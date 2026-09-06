import type { ValidationReport } from './legacy-validator.js'
import type { MigrationOutcome } from './legacy-writer.js'

/** Dựng báo cáo tiếng Việt để người vận hành cầm đi đối chiếu với hệ cũ. */
export function formatReport(
  validation: ValidationReport,
  outcome: MigrationOutcome | null,
): string {
  const lines: string[] = []

  lines.push('=== KIỂM TRA DỮ LIỆU NGUỒN ===', '')
  lines.push(`Mã kênh có nhiều xét nghiệm: ${validation.channelConflicts.length}`)
  for (const c of validation.channelConflicts) {
    lines.push(`  máy ${c.insId} kênh ${c.channelCode}: ${c.candidates.join(' hoặc ')}`)
  }
  lines.push('', `Tham chiếu tới xét nghiệm không tồn tại: ${validation.orphans.length}`)
  for (const o of validation.orphans) {
    lines.push(`  [${o.kind}] ${o.ref} → ${o.analyteCode}`)
  }
  lines.push(
    '',
    `Nhóm không có thành viên: ${validation.profilesWithoutMembers.join(', ') || 'không có'}`,
  )
  lines.push(
    `Mã chứa ký tự đại diện (${validation.wildcardCodes.length}): ${validation.wildcardCodes.join(', ') || 'không có'}`,
  )
  lines.push(
    `Mã bị lặp trong danh mục: ${validation.duplicateAnalyteCodes.join(', ') || 'không có'}`,
  )

  if (!outcome) {
    lines.push(
      '',
      '=== CHƯA GHI VÀO DATABASE ===',
      'Chạy lại với --apply sau khi đã điền tệp quyết định.',
    )
    return lines.join('\n')
  }

  lines.push('', '=== ĐỐI CHIẾU SAU KHI GHI ===', '')
  lines.push('Bảng                      Đọc    Ghi    Bỏ qua')
  for (const [table, c] of Object.entries(outcome.counts)) {
    lines.push(
      `${table.padEnd(24)} ${String(c.read).padStart(5)}  ${String(c.written).padStart(5)}  ${String(c.skipped).padStart(6)}`,
    )
  }

  // Gộp lý do lại để bảng đối chiếu đọc được ngay cả khi có hàng trăm dòng bị bỏ;
  // danh sách chi tiết đầy đủ vẫn nằm ngay bên dưới, không cắt bớt dòng nào.
  if (outcome.skipped.length > 0) {
    lines.push('', 'Tổng hợp lý do bỏ qua:')
    for (const [reason, count] of tally(outcome.skipped.map((s) => `${s.table}: ${s.reason}`))) {
      lines.push(`  ${count.toString().padStart(5)} × ${reason}`)
    }
    lines.push('', 'Chi tiết các dòng bị bỏ qua:')
    for (const s of outcome.skipped) {
      lines.push(`  [${s.table}] ${s.ref} — ${s.reason}`)
    }
  }

  if (outcome.notes.length > 0) {
    lines.push('', 'Các dòng ĐÃ ghi nhưng có sai lệch ý nghĩa, cần xem lại:')
    for (const n of outcome.notes) {
      lines.push(`  [${n.table}] ${n.ref} — ${n.reason}`)
    }
  }

  return lines.join('\n')
}

/** Đếm số lần xuất hiện, giữ thứ tự gặp đầu tiên. */
function tally(items: string[]): [string, number][] {
  const counts = new Map<string, number>()
  for (const item of items) counts.set(item, (counts.get(item) ?? 0) + 1)
  return [...counts.entries()]
}
