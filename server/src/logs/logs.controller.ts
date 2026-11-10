import { BadRequestException, Controller, Get, Query, UseGuards } from '@nestjs/common';
import { AuthGuard, Ctx, RequestContext } from '../auth/auth.guard';
import { requireLabStaff } from '../auth/roles';

export interface AgentLogView {
  id: string;
  at: string;
  agentId: string;
  instrumentId: string | null;
  instrumentName: string | null;
  /** in = máy → LIS, out = LIS → máy, event = sự kiện kết nối, agent = thông báo của agent. */
  direction: 'in' | 'out' | 'event' | 'agent';
  session: string | null;
  /** Byte nguyên văn dạng hex (chỉ với in/out). */
  hex: string | null;
  note: string | null;
}

const DIRECTIONS = ['in', 'out', 'event', 'agent'];
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const DIGITS = /^[0-9]+$/;

/** Thoát ký tự đại diện của LIKE để chuỗi người dùng gõ được so theo nghĩa đen. */
function escapeLike(text: string): string {
  return text.split('\\').join('\\\\').split('%').join('\\%').split('_').join('\\_');
}

/** Log agent đẩy lên (dữ liệu thô đi/về với máy + sự kiện). Server giữ 14 ngày. */
@Controller('api/agent-logs')
@UseGuards(AuthGuard)
export class LogsController {
  /**
   * Mới nhất trước, tối đa `limit` dòng (mặc định 300, tối đa 1000). `beforeId` để xem tiếp dòng cũ hơn;
   * `afterId` để chỉ lấy dòng mới hơn dòng đã có (tự làm mới).
   */
  @Get()
  async list(
    @Ctx() ctx: RequestContext,
    @Query('agentId') agentId?: string,
    @Query('instrumentId') instrumentId?: string,
    @Query('direction') direction?: string,
    @Query('q') q?: string,
    @Query('beforeId') beforeId?: string,
    @Query('afterId') afterId?: string,
    @Query('limit') limitText?: string,
  ): Promise<AgentLogView[]> {
    requireLabStaff(ctx);
    const conditions: string[] = [];
    const params: unknown[] = [];
    const add = (sql: string, value: unknown) => {
      params.push(value);
      conditions.push(sql.replace('?', '$' + params.length));
    };

    if (agentId) {
      if (!UUID.test(agentId)) throw new BadRequestException('agentId không hợp lệ');
      add('l.agent_id = ?', agentId);
    }
    if (instrumentId) {
      if (!UUID.test(instrumentId)) throw new BadRequestException('instrumentId không hợp lệ');
      add('l.instrument_id = ?', instrumentId);
    }
    if (direction) {
      const wanted = direction.split(',').filter((d) => DIRECTIONS.includes(d));
      if (wanted.length === 0) throw new BadRequestException('Chiều log không hợp lệ');
      add('l.direction = ANY(?)', wanted);
    }
    if (q?.trim()) {
      // Tìm trong ghi chú và trong nội dung byte (so trên chuỗi hex của từ khoá, vd barcode).
      const needle = q.trim();
      params.push('%' + escapeLike(needle) + '%', '%' + Buffer.from(needle, 'utf8').toString('hex').toUpperCase() + '%');
      conditions.push('(l.note ILIKE $' + (params.length - 1) + " ESCAPE '\\' OR l.hex LIKE $" + params.length + ')');
    }
    if (beforeId) {
      if (!DIGITS.test(beforeId)) throw new BadRequestException('beforeId không hợp lệ');
      add('l.id < ?', beforeId);
    }
    if (afterId) {
      if (!DIGITS.test(afterId)) throw new BadRequestException('afterId không hợp lệ');
      add('l.id > ?', afterId);
    }
    const limit = Math.min(1000, Math.max(1, Number(limitText) || 300));
    const where = conditions.length > 0 ? 'WHERE ' + conditions.join(' AND ') : '';

    const rows: Record<string, unknown>[] = await ctx.db.query(
      'SELECT l.id, l.at, l.agent_id, l.instrument_id, i.name AS instrument_name, l.direction, l.session, l.hex, l.note ' +
      'FROM agent_log l LEFT JOIN instrument i ON i.id = l.instrument_id ' + where + ' ORDER BY l.id DESC LIMIT ' + limit,
      params);

    return rows.map((r) => ({
      id: String(r.id), at: (r.at as Date).toISOString(), agentId: r.agent_id as string,
      instrumentId: r.instrument_id as string | null, instrumentName: r.instrument_name as string | null,
      direction: r.direction as AgentLogView['direction'], session: r.session as string | null,
      hex: r.hex as string | null, note: r.note as string | null,
    }));
  }
}
