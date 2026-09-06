import { randomUUID } from 'node:crypto'
import type { Kysely, Transaction } from 'kysely'
import type { TenantDatabase } from '../tenant/tenant-schema.js'
import { buildAliasIndex, resolveAnalyteCode, type AliasIndex } from './legacy-alias.js'
import { resolveChannel, type DecisionFile } from './legacy-decisions.js'
import type { LegacySource } from './legacy-source.js'

/** Hostname của bản ghi Agent giữ chỗ do lệnh di trú tạo ra. */
export const MIGRATION_AGENT_HOSTNAME = 'di-tru-he-cu'

export interface SkippedRow {
  table: string
  ref: string
  reason: string
}

export interface MigrationOutcome {
  counts: Record<string, { read: number; written: number; skipped: number }>
  /** Mọi dòng KHÔNG được ghi, kèm lý do đọc được. `read = written + skipped` luôn đúng. */
  skipped: SkippedRow[]
  /** Dòng ĐƯỢC ghi nhưng có sai lệch ý nghĩa cần người vận hành biết. */
  notes: SkippedRow[]
}

/**
 * Tìm hoặc tạo bản ghi Agent giữ chỗ cho việc di trú.
 *
 * Bảng `devices` yêu cầu `agent_id`, nhưng lúc di trú chưa có Agent nào chạy. Bản ghi
 * này chỉ để giữ khoá ngoại: khi Agent thật kết nối lần đầu nó tạo bản ghi riêng, và
 * máy sẽ được gán lại ở khối 2B khi kỹ thuật viên khai báo cổng.
 *
 * Dùng lại bản ghi cũ nếu đã có, để chạy lệnh lần thứ hai không sinh thêm một bộ máy
 * trùng lặp dưới một agent khác.
 */
export async function ensureMigrationAgent(db: Kysely<TenantDatabase>): Promise<string> {
  const existing = await db
    .selectFrom('agents')
    .select('id')
    .where('hostname', '=', MIGRATION_AGENT_HOSTNAME)
    .orderBy('created_at')
    .executeTakeFirst()
  if (existing) return existing.id

  const created = await db
    .insertInto('agents')
    .values({
      agent_key_id: randomUUID(),
      hostname: MIGRATION_AGENT_HOSTNAME,
      agent_version: 'migration',
    })
    .returning('id')
    .executeTakeFirstOrThrow()
  return created.id
}

/** Lý do dùng lại nhiều chỗ — giữ một chuỗi duy nhất để báo cáo đọc nhất quán. */
const ALREADY_PRESENT = 'đã có sẵn trong database từ lần chạy trước'

/**
 * Ghi toàn bộ danh mục hệ cũ vào database nghiệp vụ của một cơ sở.
 *
 * Mọi thao tác nằm trong MỘT giao dịch. Hỏng giữa chừng thì không để lại gì — một danh
 * mục di trú được một nửa còn tệ hơn không di trú, vì người vận hành sẽ tưởng là xong.
 *
 * Chạy lại được: mọi câu ghi đều `ON CONFLICT DO NOTHING` và đếm theo dòng thực sự
 * được ghi, nên lần chạy thứ hai không thêm gì và báo cáo nói rõ vì sao.
 */
export async function writeLegacyCatalogue(
  db: Kysely<TenantDatabase>,
  src: LegacySource,
  decisions: DecisionFile,
  agentId: string,
): Promise<MigrationOutcome> {
  const skipped: SkippedRow[] = []
  const notes: SkippedRow[] = []
  const counts: MigrationOutcome['counts'] = {}
  const bump = (table: string, field: 'read' | 'written' | 'skipped', by = 1): void => {
    counts[table] ??= { read: 0, written: 0, skipped: 0 }
    counts[table][field] += by
  }
  const skip = (table: string, ref: string, reason: string): void => {
    skipped.push({ table, ref, reason })
    bump(table, 'skipped')
  }

  const aliases = buildAliasIndex(src)

  return db.transaction().execute(async (trx) => {
    const analyteCodes = await writeAnalytes(trx, src, { bump, skip })
    const deviceIdByInsId = await writeDevices(trx, src, agentId, bump)

    await writeChannelMaps(trx, src, decisions, {
      analyteCodes,
      aliases,
      deviceIdByInsId,
      bump,
      skip,
      notes,
    })
    await writeProfiles(trx, src, { analyteCodes, aliases, bump, skip, notes })
    await writeResultMappings(trx, src, { analyteCodes, aliases, deviceIdByInsId, bump, skip, notes })
    await writeHisMaps(trx, src, { analyteCodes, aliases, bump, skip, notes })

    return { counts, skipped, notes }
  })
}

// --- các hàm phụ trợ, tách ra cho mỗi bảng một khối đọc được ---

type Bump = (table: string, field: 'read' | 'written' | 'skipped', by?: number) => void
type Skip = (table: string, ref: string, reason: string) => void

interface RefContext {
  analyteCodes: Set<string>
  aliases: AliasIndex
  bump: Bump
  skip: Skip
  notes: SkippedRow[]
}

/**
 * Phân giải mã tham chiếu, tự ghi lý do bỏ qua / ghi chú bí danh.
 * Trả về mã chính để ghi, hoặc `null` nếu dòng phải bị bỏ.
 */
function resolveRef(
  ctx: RefContext,
  table: string,
  ref: string,
  rawCode: string,
): string | null {
  const resolved = resolveAnalyteCode(rawCode, ctx.analyteCodes, ctx.aliases)
  switch (resolved.kind) {
    case 'primary':
      return resolved.code
    case 'alias':
      ctx.notes.push({
        table,
        ref,
        reason: `mã ${resolved.via} là bí danh, đã quy về mã chính ${resolved.code}`,
      })
      return resolved.code
    case 'ambiguous':
      ctx.skip(
        table,
        ref,
        `bí danh ${rawCode} trỏ tới nhiều mã chính (${resolved.candidates.join(', ')}), chưa được quyết`,
      )
      return null
    case 'unknown':
      ctx.skip(table, ref, `xét nghiệm ${rawCode} không có trong danh mục`)
      return null
    default: {
      const never: never = resolved
      throw new Error(`nhánh phân giải mã chưa xử lý: ${JSON.stringify(never)}`)
    }
  }
}

async function writeAnalytes(
  trx: Transaction<TenantDatabase>,
  src: LegacySource,
  h: { bump: Bump; skip: Skip },
): Promise<Set<string>> {
  const analyteCodes = new Set<string>()
  const seen = new Set<string>()
  h.bump('analytes', 'read', src.analytes.length)

  for (const a of src.analytes) {
    if (seen.has(a.code)) {
      h.skip('analytes', a.code, 'mã bị lặp trong nguồn')
      continue
    }
    seen.add(a.code)

    const row = await trx
      .insertInto('analytes')
      .values({
        code: a.code,
        name: a.name,
        name_en: a.nameEn,
        category: a.category,
        unit: a.unit,
        normal_range_text: a.normalRangeText,
        lower_limit: a.lowerLimit,
        higher_limit: a.higherLimit,
        comment_lower: a.commentLower,
        comment_higher: a.commentHigher,
        comment_gray: a.commentGray,
        criteria: a.criteria,
        result_is_comment: a.resultIsComment,
        print_order: a.printOrder,
        is_profile_head: a.isProfileHead,
        // Danh mục hệ cũ trải trên hai bảng: 'legacy' (TestCode) và 'legacy-new'
        // (TestCodeNew). Giữ nguyên nguồn để sau này còn lần được về gốc.
        source: a.source,
      })
      .onConflict((oc) => oc.doNothing())
      .returning('code')
      .executeTakeFirst()

    // Mã vẫn tồn tại trong danh mục dù dòng này không được ghi (lần chạy trước đã ghi),
    // nên mọi tham chiếu tới nó vẫn hợp lệ.
    analyteCodes.add(a.code)
    if (row) h.bump('analytes', 'written')
    else h.skip('analytes', a.code, ALREADY_PRESENT)
  }

  return analyteCodes
}

async function writeDevices(
  trx: Transaction<TenantDatabase>,
  src: LegacySource,
  agentId: string,
  bump: Bump,
): Promise<Map<number, string>> {
  const portConfigured = new Set(src.interfaceParas.map((p) => p.insId))
  const deviceIdByInsId = new Map<number, string>()
  bump('devices', 'read', src.instruments.length)

  for (const ins of src.instruments) {
    const deviceKey = `legacy-${ins.insId}`
    const inserted = await trx
      .insertInto('devices')
      .values({
        agent_id: agentId,
        device_key: deviceKey,
        name: ins.name,
        transport: ins.conType === 'S' ? 'tcp' : 'serial',
        driver: 'unknown',
        legacy_ins_id: ins.insId,
        protocol: ins.protocol,
        location: ins.location,
        has_port_config: portConfigured.has(ins.insId),
        // Dấu vết "từng gửi kết quả" nằm ở bảng Result_OUT của hệ cũ, không có trong
        // bộ tệp trích. Để mặc định false; kỹ thuật viên bật lại ở màn hình quản lý.
        has_sent_results: false,
      })
      .onConflict((oc) => oc.doNothing())
      .returning('id')
      .executeTakeFirst()

    if (inserted) {
      deviceIdByInsId.set(ins.insId, inserted.id)
      bump('devices', 'written')
      continue
    }

    // Đã có từ lần chạy trước: lấy lại id để các bảng phía sau còn trỏ tới được.
    const existing = await trx
      .selectFrom('devices')
      .select('id')
      .where('agent_id', '=', agentId)
      .where('device_key', '=', deviceKey)
      .executeTakeFirstOrThrow()
    deviceIdByInsId.set(ins.insId, existing.id)
    bump('devices', 'skipped')
  }

  return deviceIdByInsId
}

async function writeChannelMaps(
  trx: Transaction<TenantDatabase>,
  src: LegacySource,
  decisions: DecisionFile,
  ctx: RefContext & { deviceIdByInsId: Map<number, string> },
): Promise<void> {
  const table = 'device_analyte_map'
  ctx.bump(table, 'read', src.insTests.length)
  const usedChannels = new Set<string>()

  for (const t of src.insTests) {
    const ref = `máy ${t.insId} kênh ${t.channelCode} → ${t.analyteCode}`

    const deviceId = ctx.deviceIdByInsId.get(t.insId)
    if (!deviceId) {
      ctx.skip(table, ref, 'máy không có trong danh sách')
      continue
    }

    const analyteCode = resolveRef(ctx, table, ref, t.analyteCode)
    if (analyteCode === null) continue

    // Bốn nhánh được liệt kê tường minh. `no-conflict` nghĩa là mã kênh này vốn không
    // có xung đột nào — dữ liệu sạch, di trú bình thường. Gộp nó với `undecided` là
    // âm thầm vứt gần như toàn bộ danh mục.
    const decision = resolveChannel(decisions, t.insId, t.channelCode)
    switch (decision.kind) {
      case 'no-conflict':
        break
      case 'keep':
        if (decision.analyteCode !== t.analyteCode) {
          ctx.skip(table, ref, `người vận hành chọn ${decision.analyteCode}`)
          continue
        }
        break
      case 'skip':
        ctx.skip(table, ref, 'người vận hành quyết bỏ mã kênh này')
        continue
      case 'undecided':
        ctx.skip(table, ref, 'mã kênh có nhiều xét nghiệm, chưa được quyết')
        continue
      default: {
        const never: never = decision
        throw new Error(`nhánh quyết định chưa xử lý: ${JSON.stringify(never)}`)
      }
    }

    // Một mã kênh chỉ được thuộc về một xét nghiệm. Nếu hai dòng nguồn khác nhau cùng
    // quy về một (máy, kênh) sau khi phân giải bí danh, dòng thứ hai phải nêu lý do
    // chứ không được để ON CONFLICT nuốt mất.
    const channelKey = `${t.insId} ${t.channelCode}`
    if (usedChannels.has(channelKey)) {
      ctx.skip(table, ref, 'mã kênh đã được một dòng khác trong nguồn dùng')
      continue
    }
    usedChannels.add(channelKey)

    const inserted = await trx
      .insertInto(table)
      .values({
        device_id: deviceId,
        channel_code: t.channelCode,
        analyte_code: analyteCode,
        round: t.round,
        allow_insert: t.allowInsert,
        omit: t.omit,
        keep_higher_sign: t.keepHigherSign,
        keep_lower_sign: t.keepLowerSign,
      })
      .onConflict((oc) => oc.doNothing())
      .returning('id')
      .executeTakeFirst()

    if (inserted) ctx.bump(table, 'written')
    else ctx.skip(table, ref, ALREADY_PRESENT)
  }
}

async function writeProfiles(
  trx: Transaction<TenantDatabase>,
  src: LegacySource,
  ctx: RefContext,
): Promise<void> {
  ctx.bump('profiles', 'read', src.profiles.length)
  const profileCodes = new Set<string>()
  for (const p of src.profiles) {
    if (profileCodes.has(p.code)) {
      ctx.skip('profiles', p.code, 'mã nhóm bị lặp trong nguồn')
      continue
    }
    profileCodes.add(p.code)

    const inserted = await trx
      .insertInto('profiles')
      .values({ code: p.code, name: p.name, source: 'legacy-declared' })
      .onConflict((oc) => oc.doNothing())
      .returning('code')
      .executeTakeFirst()
    if (inserted) ctx.bump('profiles', 'written')
    else ctx.skip('profiles', p.code, ALREADY_PRESENT)
  }

  const table = 'profile_members'
  const headCodes = new Set(src.analytes.filter((a) => a.isProfileHead).map((a) => a.code))
  ctx.bump(table, 'read', src.profileTests.length)
  const seen = new Set<string>()
  let order = 0

  for (const m of src.profileTests) {
    const ref = `nhóm ${m.profileCode} → ${m.analyteCode}`
    if (!profileCodes.has(m.profileCode)) {
      ctx.skip(table, ref, 'nhóm không tồn tại')
      continue
    }

    const analyteCode = resolveRef(ctx, table, ref, m.analyteCode)
    if (analyteCode === null) continue

    // Hệ cũ để mã cha nằm trong chính danh sách thành viên của nó và phải lọc bằng cờ
    // mỗi lần truy vấn. Loại ngay tại đây để hệ mới không mang theo quy ước ngầm đó.
    if (headCodes.has(analyteCode)) {
      ctx.skip(table, ref, 'mã cha không phải thành viên của chính nó')
      continue
    }

    const key = `${m.profileCode} ${analyteCode}`
    if (seen.has(key)) {
      ctx.skip(table, ref, 'thành viên trùng trong nguồn')
      continue
    }
    seen.add(key)

    order += 1
    const inserted = await trx
      .insertInto(table)
      .values({ profile_code: m.profileCode, analyte_code: analyteCode, display_order: order })
      .onConflict((oc) => oc.doNothing())
      .returning('analyte_code')
      .executeTakeFirst()
    if (inserted) ctx.bump(table, 'written')
    else ctx.skip(table, ref, ALREADY_PRESENT)
  }
}

async function writeResultMappings(
  trx: Transaction<TenantDatabase>,
  src: LegacySource,
  ctx: RefContext & { deviceIdByInsId: Map<number, string> },
): Promise<void> {
  const table = 'result_mappings'
  ctx.bump(table, 'read', src.resultMappings.length)
  const seen = new Set<string>()

  for (const r of src.resultMappings) {
    const ref = `máy ${r.insId ?? 'chung'} ${r.analyteCode} ${r.resultIn}`

    const analyteCode = resolveRef(ctx, table, ref, r.analyteCode)
    if (analyteCode === null) continue

    // `insId` rỗng nghĩa là luật áp dụng cho mọi máy (diễn giải TPHA chẳng hạn).
    let deviceId: string | null = null
    if (r.insId !== null) {
      const found = ctx.deviceIdByInsId.get(r.insId)
      if (found) {
        deviceId = found
      } else {
        // Máy đã bị xoá khỏi bảng Instrument nhưng luật của nó còn nằm lại. Bỏ đi là
        // mất luật diễn giải; nên ghi thành luật chung, và nói rõ để người vận hành
        // biết phạm vi đã rộng ra.
        ctx.notes.push({
          table,
          ref,
          reason: `máy ${r.insId} không có trong danh sách máy — luật được ghi thành luật chung áp dụng cho mọi máy`,
        })
      }
    }

    const key = `${deviceId ?? 'chung'} ${analyteCode} ${r.resultIn}`
    if (seen.has(key)) {
      ctx.skip(table, ref, 'luật trùng trong nguồn')
      continue
    }
    seen.add(key)

    const inserted = await trx
      .insertInto(table)
      .values({
        device_id: deviceId,
        analyte_code: analyteCode,
        result_in: r.resultIn,
        result_out: r.resultOut,
        comment: r.comment,
        color: r.color,
      })
      .onConflict((oc) => oc.doNothing())
      .returning('id')
      .executeTakeFirst()
    if (inserted) ctx.bump(table, 'written')
    else ctx.skip(table, ref, ALREADY_PRESENT)
  }
}

async function writeHisMaps(
  trx: Transaction<TenantDatabase>,
  src: LegacySource,
  ctx: RefContext,
): Promise<void> {
  const table = 'his_analyte_map'
  ctx.bump(table, 'read', src.hisMaps.length)
  const seen = new Set<string>()

  for (const h of src.hisMaps) {
    const ref = `mã HIS ${h.hisCode} → ${h.analyteCode}`

    const analyteCode = resolveRef(ctx, table, ref, h.analyteCode)
    if (analyteCode === null) continue

    if (seen.has(h.hisCode)) {
      ctx.skip(table, ref, 'mã HIS trùng trong nguồn')
      continue
    }
    seen.add(h.hisCode)

    const inserted = await trx
      .insertInto(table)
      .values({ his_service_code: h.hisCode, analyte_code: analyteCode })
      .onConflict((oc) => oc.doNothing())
      .returning('his_service_code')
      .executeTakeFirst()
    if (inserted) ctx.bump(table, 'written')
    else ctx.skip(table, ref, ALREADY_PRESENT)
  }
}
