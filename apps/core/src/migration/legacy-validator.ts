import type { LegacySource } from './legacy-source.js'

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
}

export interface ValidationReport {
  channelConflicts: ChannelConflict[]
  orphans: OrphanRef[]
  profilesWithoutMembers: string[]
  wildcardCodes: string[]
  duplicateAnalyteCodes: string[]
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
  const duplicateAnalyteCodes: string[] = []
  for (const a of src.analytes) {
    if (analyteCodes.has(a.code)) duplicateAnalyteCodes.push(a.code)
    else analyteCodes.add(a.code)
  }

  const byChannel = new Map<string, Set<string>>()
  for (const t of src.insTests) {
    const key = `${t.insId}|${t.channelCode}`
    const set = byChannel.get(key) ?? new Set<string>()
    set.add(t.analyteCode)
    byChannel.set(key, set)
  }

  const channelConflicts: ChannelConflict[] = []
  for (const [key, candidates] of byChannel) {
    if (candidates.size < 2) continue
    const [insIdText, channelCode] = key.split('|')
    channelConflicts.push({
      insId: Number(insIdText),
      channelCode: channelCode ?? '',
      candidates: [...candidates].sort(),
    })
  }

  const orphans: OrphanRef[] = []
  for (const t of src.insTests) {
    if (!analyteCodes.has(t.analyteCode)) {
      orphans.push({
        kind: 'ins-test',
        ref: `máy ${t.insId} kênh ${t.channelCode}`,
        analyteCode: t.analyteCode,
      })
    }
  }
  for (const h of src.hisMaps) {
    if (!analyteCodes.has(h.analyteCode)) {
      orphans.push({ kind: 'his-map', ref: `mã HIS ${h.hisCode}`, analyteCode: h.analyteCode })
    }
  }
  for (const m of src.resultMappings) {
    if (!analyteCodes.has(m.analyteCode)) {
      orphans.push({
        kind: 'result-mapping',
        ref: `máy ${m.insId} giá trị ${m.resultIn}`,
        analyteCode: m.analyteCode,
      })
    }
  }
  for (const p of src.profileTests) {
    if (!analyteCodes.has(p.analyteCode)) {
      orphans.push({
        kind: 'profile-member',
        ref: `nhóm ${p.profileCode}`,
        analyteCode: p.analyteCode,
      })
    }
  }

  const profilesWithMembers = new Set(src.profileTests.map((p) => p.profileCode))
  const profilesWithoutMembers = src.profiles
    .map((p) => p.code)
    .filter((code) => !profilesWithMembers.has(code))

  // Ký tự `%` là một phần của mã, không phải ký tự đại diện. Liệt kê để cảnh báo
  // người viết truy vấn sau này không dùng LIKE.
  const wildcardCodes = src.analytes
    .map((a) => a.code)
    .filter((code) => code.includes('%') || code.includes('_'))

  return {
    channelConflicts,
    orphans,
    profilesWithoutMembers,
    wildcardCodes,
    duplicateAnalyteCodes,
  }
}
