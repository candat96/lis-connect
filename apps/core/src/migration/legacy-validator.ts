import { buildAliasIndex, resolveAnalyteCode, type AliasIndex } from './legacy-alias.js'
import type { LegacyDroppedRow, LegacySource } from './legacy-source.js'

export interface ChannelConflict {
  insId: number
  channelCode: string
  candidates: string[]
}

export interface OrphanRef {
  kind: 'ins-test' | 'his-map' | 'result-mapping' | 'profile-member'
  /** Mô tả chỗ tham chiếu, để người đọc báo cáo lần ra được dòng nguồn. */
  ref: string
  analyteCode: string
  /**
   * Vì sao không phân giải được:
   * - `unknown`: mã không có trong danh mục chính, cũng không có bí danh nào dùng được;
   * - `ambiguous`: có bí danh nhưng trỏ tới nhiều mã chính khác nhau — không được tự chọn.
   */
  resolution: 'unknown' | 'ambiguous'
  /** Với `ambiguous`: các mã chính mà bí danh trỏ tới. */
  candidates?: string[]
}

/**
 * Một bí danh trùng tên với một mã chính đã tồn tại trong danh mục.
 *
 * Có 7 trường hợp trên dữ liệu thật, và hai bên là xét nghiệm khác hẳn nhau:
 * `000CD` mã chính là *Cấy dịch tìm vi khuẩn/nấm*, còn bí danh cùng tên trỏ tới *Cấy đờm*.
 * Quy tắc phân giải (mã chính thắng, không tra bí danh) đã đúng và không đổi; nhưng
 * kỹ thuật viên phải nhìn thấy 7 chỗ này để xác nhận đúng là hai xét nghiệm riêng biệt.
 */
export interface ShadowedAlias {
  alias: string
  /** Tên xét nghiệm của mã chính trùng tên — cái sẽ được dùng. */
  primaryName: string
  /** Mã chính mà bí danh trỏ tới (cái KHÔNG được dùng). */
  aliasTargetCode: string
  aliasTargetName: string
}

/**
 * Một bí danh trỏ tới nhiều mã chính khác nhau.
 *
 * Trên dữ liệu thật có đúng một: `000CDNT` trỏ tới cả *cấy dịch ngực (T)* lẫn *cấy dịch
 * não tủy*. Bí danh này KHÔNG tự phân giải được: `resolveAnalyteCode` trả `ambiguous`,
 * nơi khác trong hệ bỏ qua và ghi lý do. Phân biệt dịch màng phổi với dịch não tủy
 * không phải việc phần mềm được đoán.
 */
export interface AmbiguousAlias {
  alias: string
  candidates: { code: string; name: string }[]
  /** Luôn `false` — có mặt để người đọc báo cáo không phải suy diễn. */
  autoResolvable: false
}

export interface ValidationReport {
  channelConflicts: ChannelConflict[]
  orphans: OrphanRef[]
  profilesWithoutMembers: string[]
  wildcardCodes: string[]
  duplicateAnalyteCodes: string[]
  /** Mọi dòng bị bộ đọc loại bỏ, kèm tệp nguồn, khoá dòng và lý do tiếng Việt. */
  droppedSourceRows: LegacyDroppedRow[]
  /** Bí danh trùng tên với một mã chính đã tồn tại — cần kỹ thuật viên xác nhận. */
  shadowedAliases: ShadowedAlias[]
  /** Bí danh trỏ tới nhiều mã chính — không tự phân giải được. */
  ambiguousAliases: AmbiguousAlias[]
}

/**
 * Phát hiện mọi chỗ dữ liệu nguồn không nhất quán.
 *
 * Lớp này CHỈ phát hiện — không sửa, không bỏ dòng, không quyết. Hệ cũ gỡ nhập nhằng
 * mã kênh bằng `TOP 1` theo thứ tự alphabet và không báo cho ai; đó là cách kết quả
 * nồng độ cồn có thể về nhầm ô LDL. Ở đây mọi xung đột đều nổi lên để người quyết.
 */
export function validateLegacySource(src: LegacySource): ValidationReport {
  const analyteCodes = new Set<string>()
  const analyteNames = new Map<string, string>()
  const duplicateSeen = new Set<string>()
  const duplicateAnalyteCodes: string[] = []
  for (const a of src.analytes) {
    if (analyteCodes.has(a.code)) {
      // Khử trùng lặp: một mã lặp ba lần vẫn chỉ là một vấn đề cần xử lý.
      if (!duplicateSeen.has(a.code)) {
        duplicateSeen.add(a.code)
        duplicateAnalyteCodes.push(a.code)
      }
      continue
    }
    analyteCodes.add(a.code)
    analyteNames.set(a.code, a.name)
  }

  // Bảng bí danh nằm ở `legacy-alias.ts` và công cụ ghi dữ liệu đã dùng nó. Bộ kiểm tra
  // PHẢI dùng đúng bảng đó, nếu không hai bộ phận báo hai con số mồ côi khác nhau về
  // cùng một dữ liệu (36 so với 14 dòng luật ghi đè).
  const aliases = buildAliasIndex(src)

  // Khoá gộp máy + mã kênh giữ ở dạng cấu trúc (Map lồng Map) từ đầu tới cuối. Nối chuỗi
  // rồi `split` để lấy lại thành phần là cách làm hỏng dữ liệu khi mã kênh chứa đúng ký
  // tự phân cách.
  const byInstrument = new Map<number, Map<string, Set<string>>>()
  for (const t of src.insTests) {
    let channels = byInstrument.get(t.insId)
    if (!channels) {
      channels = new Map<string, Set<string>>()
      byInstrument.set(t.insId, channels)
    }
    let codes = channels.get(t.channelCode)
    if (!codes) {
      codes = new Set<string>()
      channels.set(t.channelCode, codes)
    }
    codes.add(t.analyteCode)
  }

  const channelConflicts: ChannelConflict[] = []
  for (const [insId, channels] of byInstrument) {
    for (const [channelCode, candidates] of channels) {
      if (candidates.size < 2) continue
      channelConflicts.push({ insId, channelCode, candidates: [...candidates].sort() })
    }
  }

  const orphans: OrphanRef[] = []
  const addOrphan = (kind: OrphanRef['kind'], ref: string, code: string): void => {
    const resolved = resolveAnalyteCode(code, analyteCodes, aliases)
    if (resolved.kind === 'primary' || resolved.kind === 'alias') return
    if (resolved.kind === 'ambiguous') {
      orphans.push({ kind, ref, analyteCode: code, resolution: 'ambiguous', candidates: resolved.candidates })
      return
    }
    orphans.push({ kind, ref, analyteCode: code, resolution: 'unknown' })
  }

  for (const t of src.insTests) {
    addOrphan('ins-test', `máy ${t.insId} kênh ${t.channelCode}`, t.analyteCode)
  }
  for (const h of src.hisMaps) {
    addOrphan('his-map', `mã HIS ${h.hisCode}`, h.analyteCode)
  }
  for (const m of src.resultMappings) {
    addOrphan('result-mapping', `máy ${m.insId} giá trị ${m.resultIn}`, m.analyteCode)
  }
  for (const p of src.profileTests) {
    addOrphan('profile-member', `nhóm ${p.profileCode}`, p.analyteCode)
  }

  // Nhóm xét nghiệm của hệ cũ nằm ở HAI chỗ: bảng `Profile` (chỉ 2 dòng) và cờ
  // `isProfileHead` trên chính danh mục xét nghiệm (319 mã). Chỉ soi `Profile.json` thì
  // kiểm tra này vĩnh viễn trả rỗng trong khi 319 nhóm thật sự không có thành viên nào.
  const profilesWithMembers = new Set(src.profileTests.map((p) => p.profileCode))
  const profilesWithoutMembers: string[] = []
  const emptySeen = new Set<string>()
  for (const code of [
    ...src.profiles.map((p) => p.code),
    ...src.analytes.filter((a) => a.isProfileHead).map((a) => a.code),
  ]) {
    if (profilesWithMembers.has(code) || emptySeen.has(code)) continue
    emptySeen.add(code)
    profilesWithoutMembers.push(code)
  }

  // Ký tự `%` và `_` là một phần của mã, không phải ký tự đại diện. Liệt kê để cảnh báo
  // người viết truy vấn sau này không dùng LIKE.
  const wildcardSeen = new Set<string>()
  const wildcardCodes: string[] = []
  for (const a of src.analytes) {
    if (!a.code.includes('%') && !a.code.includes('_')) continue
    if (wildcardSeen.has(a.code)) continue
    wildcardSeen.add(a.code)
    wildcardCodes.push(a.code)
  }

  return {
    channelConflicts,
    orphans,
    profilesWithoutMembers,
    wildcardCodes,
    duplicateAnalyteCodes,
    droppedSourceRows: src.droppedRows ?? [],
    shadowedAliases: collectShadowedAliases(aliases, analyteCodes, analyteNames),
    ambiguousAliases: collectAmbiguousAliases(aliases, analyteCodes, analyteNames),
  }
}

/** Bí danh trùng tên với một mã chính: mã chính thắng, bí danh bị che khuất. */
function collectShadowedAliases(
  aliases: AliasIndex,
  analyteCodes: ReadonlySet<string>,
  analyteNames: ReadonlyMap<string, string>,
): ShadowedAlias[] {
  const out: ShadowedAlias[] = []
  for (const [alias, targets] of aliases) {
    if (!analyteCodes.has(alias)) continue
    for (const target of [...targets].sort()) {
      if (!analyteCodes.has(target)) continue
      out.push({
        alias,
        primaryName: analyteNames.get(alias) ?? '(không có tên)',
        aliasTargetCode: target,
        aliasTargetName: analyteNames.get(target) ?? '(không có tên)',
      })
    }
  }
  return out.sort((a, b) => a.alias.localeCompare(b.alias))
}

/** Bí danh trỏ tới nhiều mã chính: không được tự chọn, phải để người quyết. */
function collectAmbiguousAliases(
  aliases: AliasIndex,
  analyteCodes: ReadonlySet<string>,
  analyteNames: ReadonlyMap<string, string>,
): AmbiguousAlias[] {
  const out: AmbiguousAlias[] = []
  for (const [alias, targets] of aliases) {
    const usable = [...targets].filter((t) => analyteCodes.has(t)).sort()
    if (usable.length < 2) continue
    out.push({
      alias,
      candidates: usable.map((code) => ({ code, name: analyteNames.get(code) ?? '(không có tên)' })),
      autoResolvable: false,
    })
  }
  return out.sort((a, b) => a.alias.localeCompare(b.alias))
}
