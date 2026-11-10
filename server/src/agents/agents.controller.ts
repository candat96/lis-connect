import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { Body, ConflictException, Controller, Delete, Get, HttpCode, NotFoundException, Param, ParseUUIDPipe, Patch, Post, UseGuards } from '@nestjs/common';
import { IsNotEmpty, IsString, Matches, MaxLength } from 'class-validator';
import { AuthGuard, Ctx, RequestContext } from '../auth/auth.guard';
import { requireAdmin, requireLabStaff } from '../auth/roles';
import { Instrument } from '../entities';
import { Agent, InstrumentStatusReport } from '../tenancy/master.entities';
import { TenancyService } from '../tenancy/tenancy.service';

class AgentDto {
  @IsString() @IsNotEmpty({ message: 'Nhập tên agent' }) @MaxLength(100) name: string;
  // Mã máy trạm: khoá nối agent với các máy xét nghiệm nó phụ trách.
  @IsString() @IsNotEmpty({ message: 'Nhập mã máy trạm' }) @MaxLength(64)
  @Matches(/^[A-Za-z0-9._-]+$/, { message: 'Mã máy trạm chỉ gồm chữ không dấu, số, dấu chấm, gạch ngang, gạch dưới' })
  workstationCode: string;
}

export interface AgentView {
  id: string;
  name: string;
  workstationCode: string;
  lastSeenAt: string | null;
  online: boolean;
  /** Các máy xét nghiệm gán cho agent này kèm trạng thái nó báo về. */
  instruments: { id: string; name: string; status: InstrumentStatusReport | null }[];
}

/** Agent coi là "đang chạy" khi vừa báo về trong khoảng này (agent báo mỗi 30 giây). */
export const ONLINE_WINDOW_MS = 90_000;

/** Khoá agent: `lisa_<id không gạch>_<bí mật hex>`; server chỉ lưu SHA-256 của bí mật (cùng cách với Lis.Server .NET). */
function newKey(agentId: string): { key: string; keyHash: string } {
  const secret = randomBytes(32).toString('hex');
  return {
    key: `lisa_${agentId.replace(/-/g, '')}_${secret}`,
    keyHash: createHash('sha256').update(secret, 'utf8').digest('hex').toUpperCase(),
  };
}

@Controller('api/agents')
@UseGuards(AuthGuard)
export class AgentsController {
  constructor(private readonly tenancy: TenancyService) {}

  private get agents() {
    return this.tenancy.master.getRepository(Agent);
  }

  @Get()
  async list(@Ctx() ctx: RequestContext): Promise<AgentView[]> {
    requireLabStaff(ctx);
    const [agents, instruments] = await Promise.all([
      this.tenancy.agentsOf(ctx.clinic.id),
      ctx.db.getRepository(Instrument).find({ order: { name: 'ASC' } }),
    ]);
    const now = Date.now();
    return agents.map((agent) => {
      const online = agent.lastSeenAt != null && now - agent.lastSeenAt.getTime() < ONLINE_WINDOW_MS;
      return {
        id: agent.id, name: agent.name, workstationCode: agent.workstationCode,
        lastSeenAt: agent.lastSeenAt?.toISOString() ?? null, online,
        instruments: instruments
          .filter((i) => (i.workstationCode ?? '').toLowerCase() === agent.workstationCode.toLowerCase())
          .map((i) => ({ id: i.id, name: i.name, status: online ? agent.lastStatus?.find((s) => s.instrumentId === i.id) ?? null : null })),
      };
    });
  }

  /** Tạo agent. Khoá chỉ trả về MỘT lần ở đây — dán vào appsettings.json của agent. */
  @Post()
  async create(@Ctx() ctx: RequestContext, @Body() body: AgentDto): Promise<{ id: string; key: string }> {
    requireAdmin(ctx);
    await this.ensureWorkstationFree(ctx.clinic.id, body.workstationCode, null);
    const id = randomUUID();
    const { key, keyHash } = newKey(id);
    await this.agents.insert({ id, clinicId: ctx.clinic.id, name: body.name.trim(), workstationCode: body.workstationCode.trim(), keyHash });
    return { id, key };
  }

  @Patch(':id')
  @HttpCode(204)
  async update(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string, @Body() body: AgentDto): Promise<void> {
    requireAdmin(ctx);
    const agent = await this.mine(ctx, id);
    await this.ensureWorkstationFree(ctx.clinic.id, body.workstationCode, id);
    const newCode = body.workstationCode.trim();
    // Đổi mã máy trạm: các máy đang gán cho agent này đi theo mã mới, không bị "mồ côi".
    if (newCode.toLowerCase() !== agent.workstationCode.toLowerCase()) {
      await ctx.db.query('UPDATE instrument SET workstation_code = $1 WHERE lower(workstation_code) = lower($2)', [newCode, agent.workstationCode]);
    }
    await this.agents.update(id, { name: body.name.trim(), workstationCode: newCode });
  }

  /** Cấp khoá mới (khoá cũ hết hiệu lực ngay) — dùng khi lộ khoá hoặc cài lại máy tính agent. */
  @Post(':id/key')
  async regenerateKey(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string): Promise<{ key: string }> {
    requireAdmin(ctx);
    await this.mine(ctx, id);
    const { key, keyHash } = newKey(id);
    await this.agents.update(id, { keyHash });
    return { key };
  }

  @Delete(':id')
  @HttpCode(204)
  async remove(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string): Promise<void> {
    requireAdmin(ctx);
    const agent = await this.mine(ctx, id);
    const assigned = await ctx.db.getRepository(Instrument).createQueryBuilder('i')
      .where('lower(i.workstationCode) = lower(:code)', { code: agent.workstationCode }).getCount();
    if (assigned > 0) {
      throw new ConflictException(`Agent đang phụ trách ${assigned} máy xét nghiệm — chuyển các máy đó sang agent khác trước khi xoá`);
    }
    await this.agents.delete(id);
  }

  /** Agent phải thuộc phòng khám của phiên đăng nhập — không đụng được agent của phòng khám khác. */
  private async mine(ctx: RequestContext, id: string): Promise<Agent> {
    const agent = await this.agents.findOneBy({ id, clinicId: ctx.clinic.id });
    if (!agent) throw new NotFoundException('Không tìm thấy agent');
    return agent;
  }

  private async ensureWorkstationFree(clinicId: string, workstationCode: string, exceptId: string | null): Promise<void> {
    const others = await this.tenancy.agentsOf(clinicId);
    if (others.some((a) => a.id !== exceptId && a.workstationCode.toLowerCase() === workstationCode.trim().toLowerCase())) {
      throw new ConflictException(`Mã máy trạm ${workstationCode.trim()} đã có agent khác dùng`);
    }
  }
}
