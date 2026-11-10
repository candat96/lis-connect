import {
  BadRequestException, Body, ConflictException, Controller, Delete, Get, HttpCode, NotFoundException, Param, ParseUUIDPipe, Post, Put, UseGuards,
} from '@nestjs/common';
import { Type } from 'class-transformer';
import { IsBoolean, IsIn, IsInt, IsNotEmpty, IsNumber, IsOptional, IsString, Max, MaxLength, Min, ValidateNested } from 'class-validator';
import { DataSource } from 'typeorm';
import { ONLINE_WINDOW_MS } from '../agents/agents.controller';
import { AuthGuard, Ctx, RequestContext } from '../auth/auth.guard';
import { requireAdmin } from '../auth/roles';
import { Instrument } from '../entities';
import { InstrumentStatusReport } from '../tenancy/master.entities';
import { TenancyService } from '../tenancy/tenancy.service';

/** Họ giao thức đã có driver (Lis.Core.Drivers.DriverRegistry). */
export const PROTOCOL_FAMILIES = ['hl7-mllp', 'astm-e1394', 'beckman-au'];
const BAUD_RATES = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

/** Cấu hình kết nối — cùng định dạng JSON với ConnectionSettings của agent .NET (type/role/port/host/baudRate…). */
export class ConnectionDto {
  @IsIn(['none', 'serial', 'tcp-server', 'tcp-client'], { message: 'Kiểu kết nối không hợp lệ' }) kind: string;
  /** Tên cổng COM (vd COM3) khi kind = serial. */
  @IsOptional() @IsString() @MaxLength(32) portName?: string;
  @IsOptional() @IsIn(BAUD_RATES, { message: 'Tốc độ baud không hợp lệ' }) baudRate?: number;
  @IsOptional() @IsIn([7, 8], { message: 'Số bit dữ liệu phải là 7 hoặc 8' }) dataBits?: number;
  @IsOptional() @IsIn(['None', 'Even', 'Odd'], { message: 'Parity không hợp lệ' }) parity?: string;
  @IsOptional() @IsIn([1, 2], { message: 'Stop bit phải là 1 hoặc 2' }) stopBits?: number;
  @IsOptional() @IsString() @MaxLength(255) host?: string;
  @IsOptional() @IsInt() @Min(1, { message: 'Cổng TCP phải từ 1 đến 65535' }) @Max(65535, { message: 'Cổng TCP phải từ 1 đến 65535' }) tcpPort?: number;
}

class InstrumentDto {
  @IsString() @IsNotEmpty({ message: 'Nhập tên máy' }) @MaxLength(100) name: string;
  @IsOptional() @IsString() @MaxLength(100) model?: string | null;
  @IsOptional() @IsIn(PROTOCOL_FAMILIES, { message: 'Giao thức chưa được hỗ trợ' }) protocolFamily?: string | null;
  /** Mã máy trạm của agent phụ trách; null = chưa gán. */
  @IsOptional() @IsString() @MaxLength(64) workstationCode?: string | null;
  @IsBoolean() active: boolean;
  @ValidateNested() @Type(() => ConnectionDto) connection: ConnectionDto;
}

class ChannelDto {
  @IsString() @IsNotEmpty({ message: 'Nhập mã kênh máy' }) @MaxLength(64) channelCode: string;
  @IsString() @IsNotEmpty({ message: 'Nhập mã chỉ số' }) @MaxLength(64) serviceCode: string;
  @IsOptional() @IsInt() @Min(0) @Max(6) decimals?: number | null;
  @IsOptional() @IsNumber() factor?: number | null;
  @IsBoolean() keepComparator: boolean;
  @IsBoolean() sendOnQuery: boolean;
}

class DeclareDto {
  @IsString() @IsNotEmpty({ message: 'Nhập mã chỉ số' }) @MaxLength(64) serviceCode: string;
}

export interface InstrumentView {
  id: string;
  name: string;
  model: string | null;
  protocolFamily: string | null;
  workstationCode: string | null;
  active: boolean;
  connection: ConnectionDto;
  /** Mô tả ngắn kết nối: "COM3 · 9600 8N1" / "TCP chờ máy nối vào cổng 5100" / null khi chưa cấu hình. */
  connectionText: string | null;
  channelCount: number;
  /** Dịch vụ đã khai là máy chạy nhưng chưa biết mã kênh (đang chờ tự dò theo barcode). */
  pendingServiceCount: number;
  /** Mã kênh máy đã gửi mà chưa map (kết quả chưa vào phiếu). */
  unmappedCount: number;
  agent: { id: string; name: string; lastSeenAt: string | null; online: boolean } | null;
  status: InstrumentStatusReport | null;
}

export interface ChannelView {
  id: string | null;
  /** null = dịch vụ đã khai, chưa biết mã kênh. */
  channelCode: string | null;
  serviceCode: string;
  serviceName: string;
  decimals: number | null;
  factor: number | null;
  keepComparator: boolean;
  sendOnQuery: boolean;
}

/** Mã kênh máy đã gửi nhưng chưa map: số lần gặp, giá trị gần nhất — để nhìn giá trị đoán ra xét nghiệm gì. */
export interface UnmappedView {
  channelCode: string;
  count: number;
  lastValue: string;
  lastUnit: string | null;
  lastAt: string;
}

@Controller('api/instruments')
@UseGuards(AuthGuard)
export class InstrumentsController {
  constructor(private readonly tenancy: TenancyService) {}

  @Get()
  async list(@Ctx() ctx: RequestContext): Promise<InstrumentView[]> {
    const [instruments, counts, agents] = await Promise.all([
      ctx.db.getRepository(Instrument).find({ order: { name: 'ASC' } }),
      ctx.db.query(
        `SELECT i.id,
                (SELECT count(*)::int FROM instrument_channel ic WHERE ic.instrument_id = i.id) AS channels,
                (SELECT count(*)::int FROM instrument_service x WHERE x.instrument_id = i.id
                   AND NOT EXISTS (SELECT 1 FROM instrument_channel ic WHERE ic.instrument_id = i.id AND ic.service_id = x.service_id)) AS pending,
                (SELECT count(DISTINCT u.channel_code)::int FROM unmatched_result u WHERE u.instrument_id = i.id
                   AND u.reason = 'kenh-chua-map' AND u.resolved_at IS NULL
                   AND NOT EXISTS (SELECT 1 FROM instrument_channel ic WHERE ic.instrument_id = i.id AND ic.channel_code = u.channel_code)) AS unmapped
         FROM instrument i`) as Promise<{ id: string; channels: number; pending: number; unmapped: number }[]>,
      this.tenancy.agentsOf(ctx.clinic.id),
    ]);
    const countById = new Map(counts.map((c) => [c.id, c]));
    const now = Date.now();

    return instruments.map((instrument) => {
      const agent = agents.find((a) => a.workstationCode.toLowerCase() === (instrument.workstationCode ?? '').toLowerCase()) ?? null;
      const online = agent?.lastSeenAt != null && now - agent.lastSeenAt.getTime() < ONLINE_WINDOW_MS;
      const connection = readConnection(instrument.connection);
      return {
        id: instrument.id, name: instrument.name, model: instrument.model, protocolFamily: instrument.protocolFamily,
        workstationCode: instrument.workstationCode, active: instrument.active,
        connection, connectionText: describeConnection(connection),
        channelCount: countById.get(instrument.id)?.channels ?? 0,
        pendingServiceCount: countById.get(instrument.id)?.pending ?? 0,
        unmappedCount: countById.get(instrument.id)?.unmapped ?? 0,
        agent: agent ? { id: agent.id, name: agent.name, lastSeenAt: agent.lastSeenAt?.toISOString() ?? null, online } : null,
        // Trạng thái chỉ đáng tin khi agent còn đang báo về.
        status: online ? agent?.lastStatus?.find((s) => s.instrumentId === instrument.id) ?? null : null,
      };
    });
  }

  @Post()
  async create(@Ctx() ctx: RequestContext, @Body() body: InstrumentDto): Promise<{ id: string }> {
    requireAdmin(ctx);
    const values = await this.validated(ctx, body);
    const [row] = await ctx.db.query(
      `INSERT INTO instrument (name, model, protocol_family, connection, workstation_code, active)
       VALUES ($1, $2, $3, $4::jsonb, $5, $6) RETURNING id`, values);
    return { id: row.id };
  }

  /** Sửa máy. Agent tự nhận cấu hình mới trong vòng 30 giây và mở lại kết nối nếu cấu hình đổi. */
  @Put(':id')
  @HttpCode(204)
  async update(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string, @Body() body: InstrumentDto): Promise<void> {
    requireAdmin(ctx);
    await this.mustExist(ctx.db, id);
    const values = await this.validated(ctx, body);
    await ctx.db.query(
      `UPDATE instrument SET name = $1, model = $2, protocol_family = $3, connection = $4::jsonb, workstation_code = $5, active = $6 WHERE id = $7`,
      [...values, id]);
  }

  @Delete(':id')
  @HttpCode(204)
  async remove(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string): Promise<void> {
    requireAdmin(ctx);
    await this.mustExist(ctx.db, id);
    try {
      await ctx.db.transaction(async (tx) => {
        await tx.query('DELETE FROM instrument_service WHERE instrument_id = $1', [id]);
        await tx.query('DELETE FROM instrument_channel WHERE instrument_id = $1', [id]);
        await tx.query('DELETE FROM instrument WHERE id = $1', [id]);
      });
    } catch (error) {
      // Máy đã có kết quả / log tham chiếu tới: không xoá được, chỉ ngừng dùng.
      if ((error as { code?: string }).code === '23503') {
        throw new ConflictException('Máy đã có kết quả xét nghiệm gắn với nó nên không xoá được — hãy chuyển sang "Ngừng dùng"');
      }
      throw error;
    }
  }

  /** Bảng map kênh của một máy: kênh đã map trước, rồi dịch vụ đã khai đang chờ dò mã kênh. */
  @Get(':id/channels')
  async channels(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string): Promise<ChannelView[]> {
    await this.mustExist(ctx.db, id);
    const rows: Record<string, unknown>[] = await ctx.db.query(
      `SELECT ic.id, ic.channel_code, sv.code AS service_code, sv.name AS service_name,
              ic.decimals, ic.factor::float8 AS factor, ic.keep_comparator, ic.send_on_query
       FROM instrument_channel ic JOIN service sv ON sv.id = ic.service_id
       WHERE ic.instrument_id = $1
       UNION ALL
       SELECT NULL, NULL, sv.code, sv.name, NULL, NULL, false, true
       FROM instrument_service x JOIN service sv ON sv.id = x.service_id
       WHERE x.instrument_id = $1
         AND NOT EXISTS (SELECT 1 FROM instrument_channel ic WHERE ic.instrument_id = x.instrument_id AND ic.service_id = x.service_id)
       ORDER BY 2 NULLS LAST, 3`,
      [id],
    );
    return rows.map((r) => ({
      id: r.id as string | null, channelCode: r.channel_code as string | null,
      serviceCode: r.service_code as string, serviceName: r.service_name as string,
      decimals: r.decimals as number | null, factor: r.factor as number | null,
      keepComparator: r.keep_comparator as boolean, sendOnQuery: r.send_on_query as boolean,
    }));
  }

  /** Thêm một kênh (mã kênh máy → chỉ số). Trùng mã kênh trên cùng máy → báo đang trỏ tới chỉ số nào. */
  @Post(':id/channels')
  async addChannel(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string, @Body() body: ChannelDto): Promise<{ id: string }> {
    requireAdmin(ctx);
    await this.mustExist(ctx.db, id);
    const serviceId = await this.leafServiceId(ctx.db, body.serviceCode);
    this.checkFactor(body.factor);
    try {
      const [row] = await ctx.db.query(
        `INSERT INTO instrument_channel (instrument_id, channel_code, service_id, decimals, factor, keep_comparator, send_on_query)
         VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id`,
        [id, body.channelCode.trim(), serviceId, body.decimals ?? null, body.factor ?? null, body.keepComparator, body.sendOnQuery]);
      return { id: row.id };
    } catch (error) {
      throw await this.duplicateChannel(ctx.db, error, id, body.channelCode);
    }
  }

  @Put(':id/channels/:channelId')
  @HttpCode(204)
  async updateChannel(
    @Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string,
    @Param('channelId', new ParseUUIDPipe()) channelId: string, @Body() body: ChannelDto,
  ): Promise<void> {
    requireAdmin(ctx);
    const serviceId = await this.leafServiceId(ctx.db, body.serviceCode);
    this.checkFactor(body.factor);
    try {
      const [, affected] = await ctx.db.query(
        `UPDATE instrument_channel SET channel_code = $1, service_id = $2, decimals = $3, factor = $4, keep_comparator = $5, send_on_query = $6
         WHERE id = $7 AND instrument_id = $8`,
        [body.channelCode.trim(), serviceId, body.decimals ?? null, body.factor ?? null, body.keepComparator, body.sendOnQuery, channelId, id]);
      if (affected === 0) throw new NotFoundException('Không tìm thấy kênh');
    } catch (error) {
      throw await this.duplicateChannel(ctx.db, error, id, body.channelCode);
    }
  }

  @Delete(':id/channels/:channelId')
  @HttpCode(204)
  async removeChannel(
    @Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string, @Param('channelId', new ParseUUIDPipe()) channelId: string,
  ): Promise<void> {
    requireAdmin(ctx);
    await ctx.db.query('DELETE FROM instrument_channel WHERE id = $1 AND instrument_id = $2', [channelId, id]);
  }

  /** Khai một chỉ số là máy này chạy được khi chưa biết mã kênh — hệ thống tự dò mã kênh theo barcode. */
  @Post(':id/declared')
  @HttpCode(204)
  async declare(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string, @Body() body: DeclareDto): Promise<void> {
    requireAdmin(ctx);
    await this.mustExist(ctx.db, id);
    const serviceId = await this.leafServiceId(ctx.db, body.serviceCode);
    await ctx.db.query('INSERT INTO instrument_service (instrument_id, service_id) VALUES ($1, $2) ON CONFLICT DO NOTHING', [id, serviceId]);
  }

  @Delete(':id/declared/:serviceCode')
  @HttpCode(204)
  async undeclare(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string, @Param('serviceCode') serviceCode: string): Promise<void> {
    requireAdmin(ctx);
    await ctx.db.query(
      'DELETE FROM instrument_service WHERE instrument_id = $1 AND service_id IN (SELECT id FROM service WHERE lower(code) = lower($2))', [id, serviceCode]);
  }

  /** Mã kênh máy đã gửi mà chưa map (mới nhất trước). */
  @Get(':id/unmapped')
  async unmapped(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string): Promise<UnmappedView[]> {
    const rows: Record<string, unknown>[] = await ctx.db.query(
      `SELECT * FROM (
         SELECT DISTINCT ON (u.channel_code) u.channel_code,
                (count(*) OVER (PARTITION BY u.channel_code))::int AS count, u.value, u.unit, u.received_at
         FROM unmatched_result u
         WHERE u.instrument_id = $1 AND u.reason = 'kenh-chua-map' AND u.resolved_at IS NULL
           AND NOT EXISTS (SELECT 1 FROM instrument_channel ic WHERE ic.instrument_id = u.instrument_id AND ic.channel_code = u.channel_code)
         ORDER BY u.channel_code, u.received_at DESC) x
       ORDER BY received_at DESC`,
      [id]);
    return rows.map((r) => ({
      channelCode: r.channel_code as string, count: r.count as number, lastValue: r.value as string,
      lastUnit: r.unit as string | null, lastAt: (r.received_at as Date).toISOString(),
    }));
  }

  // ---- dùng chung ----

  private async mustExist(db: DataSource, id: string): Promise<void> {
    if (!(await db.getRepository(Instrument).existsBy({ id }))) throw new NotFoundException('Không tìm thấy máy');
  }

  /** Kiểm và chuẩn hoá dữ liệu máy → tham số SQL [name, model, family, connectionJson, workstationCode, active]. */
  private async validated(ctx: RequestContext, body: InstrumentDto): Promise<unknown[]> {
    const connection = writeConnection(body.connection);
    const workstationCode = body.workstationCode?.trim() || null;
    if (workstationCode) {
      const agents = await this.tenancy.agentsOf(ctx.clinic.id);
      if (!agents.some((a) => a.workstationCode.toLowerCase() === workstationCode.toLowerCase())) {
        throw new BadRequestException(`Không có agent nào dùng mã máy trạm ${workstationCode}`);
      }
    }
    return [body.name.trim(), body.model?.trim() || null, body.protocolFamily || null, connection === null ? null : JSON.stringify(connection),
      workstationCode, body.active];
  }

  private async leafServiceId(db: DataSource, code: string): Promise<string> {
    const [service] = await db.query('SELECT id, is_panel FROM service WHERE lower(code) = lower($1)', [code.trim()]);
    if (!service) throw new BadRequestException(`Không có chỉ số mã ${code.trim()}`);
    if (service.is_panel) throw new BadRequestException('Kênh máy chỉ map tới chỉ số lá, không map tới dịch vụ cha (gói)');
    return service.id;
  }

  private checkFactor(factor: number | null | undefined): void {
    if (factor !== null && factor !== undefined && !(factor > 0)) throw new BadRequestException('Hệ số phải lớn hơn 0');
  }

  /** Lỗi trùng (instrument_id, channel_code) → 409 kèm tên chỉ số đang giữ mã kênh đó; lỗi khác trả nguyên. */
  private async duplicateChannel(db: DataSource, error: unknown, instrumentId: string, channelCode: string): Promise<unknown> {
    if ((error as { code?: string }).code !== '23505') return error;
    const [existing] = await db.query(
      `SELECT sv.name FROM instrument_channel ic JOIN service sv ON sv.id = ic.service_id WHERE ic.instrument_id = $1 AND ic.channel_code = $2`,
      [instrumentId, channelCode.trim()]);
    return new ConflictException(`Mã kênh ${channelCode.trim()} đã map tới chỉ số "${existing?.name ?? '?'}"`);
  }
}

/** Đọc JSON kết nối (do ConnectionSettings.ToJson của .NET hoặc web ghi) thành dạng form. */
export function readConnection(json: unknown): ConnectionDto {
  if (!json || typeof json !== 'object') return { kind: 'none' };
  const c = json as Record<string, unknown>;
  const type = String(c.type ?? '').toLowerCase();
  const int = (v: unknown) => (typeof v === 'number' ? v : typeof v === 'string' && /^\d+$/.test(v) ? Number(v) : undefined);
  if (type === 'serial') {
    return {
      kind: 'serial', portName: typeof c.port === 'string' ? c.port : undefined, baudRate: int(c.baudRate) ?? 9600,
      dataBits: int(c.dataBits) ?? 8, parity: typeof c.parity === 'string' ? c.parity : 'None', stopBits: int(c.stopBits) ?? 1,
    };
  }
  if (type === 'tcp') {
    return String(c.role ?? '').toLowerCase() === 'client'
      ? { kind: 'tcp-client', host: typeof c.host === 'string' ? c.host : undefined, tcpPort: int(c.port) }
      : { kind: 'tcp-server', tcpPort: int(c.port) };
  }
  return { kind: 'none' };
}

/** Kiểm cấu hình kết nối (cùng quy tắc ConnectionSettings.Validate) rồi trả JSON đúng định dạng agent đọc; null = chưa cấu hình. */
export function writeConnection(c: ConnectionDto): Record<string, unknown> | null {
  switch (c.kind) {
    case 'serial':
      if (!c.portName?.trim()) throw new BadRequestException('Nhập cổng COM (vd COM3)');
      return { type: 'serial', port: c.portName.trim().toUpperCase(), baudRate: c.baudRate ?? 9600, dataBits: c.dataBits ?? 8, parity: c.parity ?? 'None', stopBits: c.stopBits ?? 1 };
    case 'tcp-server':
      if (!c.tcpPort) throw new BadRequestException('Nhập cổng TCP (1–65535)');
      return { type: 'tcp', role: 'server', port: c.tcpPort };
    case 'tcp-client':
      if (!c.host?.trim()) throw new BadRequestException('Nhập địa chỉ IP của máy');
      if (!c.tcpPort) throw new BadRequestException('Nhập cổng TCP (1–65535)');
      return { type: 'tcp', role: 'client', host: c.host.trim(), port: c.tcpPort };
    default:
      return null;
  }
}

export function describeConnection(c: ConnectionDto): string | null {
  switch (c.kind) {
    case 'serial':
      return `${c.portName ?? '?'} · ${c.baudRate} ${c.dataBits}${(c.parity ?? 'N')[0]}${c.stopBits}`;
    case 'tcp-server':
      return `TCP chờ máy nối vào cổng ${c.tcpPort ?? '?'}`;
    case 'tcp-client':
      return `TCP nối tới ${c.host ?? '?'}:${c.tcpPort ?? '?'}`;
    default:
      return null;
  }
}
