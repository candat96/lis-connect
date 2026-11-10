import { Controller, Get, NotFoundException, Param, ParseUUIDPipe, UseGuards } from '@nestjs/common';
import { AuthGuard, Ctx, RequestContext } from '../auth/auth.guard';
import { Instrument } from '../entities';
import { InstrumentStatusReport } from '../tenancy/master.entities';
import { TenancyService } from '../tenancy/tenancy.service';

export interface InstrumentDto {
  id: string;
  name: string;
  model: string | null;
  protocolFamily: string | null;
  workstationCode: string | null;
  active: boolean;
  /** Mô tả ngắn kết nối: "COM3 · 9600" / "TCP chờ cổng 5100" / null khi chưa cấu hình. */
  connectionText: string | null;
  channelCount: number;
  /** Dịch vụ đã khai là máy chạy nhưng chưa biết mã kênh (đang chờ tự dò theo barcode). */
  pendingServiceCount: number;
  /** Agent phụ trách máy này (theo mã máy trạm) và trạng thái nó báo về lần gần nhất. */
  agent: { name: string; lastSeenAt: string | null; online: boolean } | null;
  status: InstrumentStatusReport | null;
}

export interface ChannelDto {
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

/** Agent coi là "đang chạy" khi vừa báo về trong khoảng này (agent báo mỗi 30 giây). */
const ONLINE_WINDOW_MS = 90_000;

@Controller('api/instruments')
@UseGuards(AuthGuard)
export class InstrumentsController {
  constructor(private readonly tenancy: TenancyService) {}

  @Get()
  async list(@Ctx() ctx: RequestContext): Promise<InstrumentDto[]> {
    const [instruments, counts, agents] = await Promise.all([
      ctx.db.getRepository(Instrument).find({ order: { name: 'ASC' } }),
      ctx.db.query(
        `SELECT i.id,
                (SELECT count(*)::int FROM instrument_channel ic WHERE ic.instrument_id = i.id) AS channels,
                (SELECT count(*)::int FROM instrument_service x WHERE x.instrument_id = i.id
                   AND NOT EXISTS (SELECT 1 FROM instrument_channel ic WHERE ic.instrument_id = i.id AND ic.service_id = x.service_id)) AS pending
         FROM instrument i`) as Promise<{ id: string; channels: number; pending: number }[]>,
      this.tenancy.agentsOf(ctx.clinic.id),
    ]);
    const countById = new Map(counts.map((c) => [c.id, c]));
    const now = Date.now();

    return instruments.map((instrument) => {
      const agent = agents.find((a) => a.workstationCode.toLowerCase() === (instrument.workstationCode ?? '').toLowerCase()) ?? null;
      const online = agent?.lastSeenAt != null && now - agent.lastSeenAt.getTime() < ONLINE_WINDOW_MS;
      return {
        id: instrument.id, name: instrument.name, model: instrument.model, protocolFamily: instrument.protocolFamily,
        workstationCode: instrument.workstationCode, active: instrument.active,
        connectionText: describeConnection(instrument.connection),
        channelCount: countById.get(instrument.id)?.channels ?? 0,
        pendingServiceCount: countById.get(instrument.id)?.pending ?? 0,
        agent: agent ? { name: agent.name, lastSeenAt: agent.lastSeenAt?.toISOString() ?? null, online } : null,
        // Trạng thái chỉ đáng tin khi agent còn đang báo về.
        status: online ? agent?.lastStatus?.find((s) => s.instrumentId === instrument.id) ?? null : null,
      };
    });
  }

  /** Bảng map kênh của một máy: kênh đã map trước, rồi dịch vụ đã khai đang chờ dò mã kênh. */
  @Get(':id/channels')
  async channels(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string): Promise<ChannelDto[]> {
    if (!(await ctx.db.getRepository(Instrument).existsBy({ id }))) throw new NotFoundException('Không tìm thấy máy');

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
}

/** Mô tả kết nối từ JSON cấu hình (do app/agent .NET ghi) — không trả nguyên JSON ra web. */
function describeConnection(connection: unknown): string | null {
  if (!connection || typeof connection !== 'object') return null;
  const c = Object.fromEntries(Object.entries(connection as Record<string, unknown>).map(([k, v]) => [k.toLowerCase(), v]));
  // Định dạng do ConnectionSettings.ToJson (Lis.Core) ghi: type serial|tcp, role server|client.
  const type = String(c.type ?? '').toLowerCase();
  const role = String(c.role ?? '').toLowerCase();
  if (type === 'serial') return [c.port, c.baudrate].filter(Boolean).join(' · ') || 'Cổng COM';
  if (type === 'tcp' && role === 'server') return `TCP chờ máy nối vào cổng ${c.port ?? '?'}`;
  if (type === 'tcp' && role === 'client') return `TCP nối tới ${c.host ?? '?'}:${c.port ?? '?'}`;
  return null;
}
