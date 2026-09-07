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
  pushEmptyProfileHeads(lines, validation)
  pushInferredProfiles(lines, validation)
  lines.push(
    '',
    `Mã chứa ký tự đại diện (${validation.wildcardCodes.length}): ${validation.wildcardCodes.join(', ') || 'không có'}`,
  )
  lines.push(
    `Mã bị lặp trong danh mục: ${validation.duplicateAnalyteCodes.join(', ') || 'không có'}`,
  )

  pushDroppedSourceRows(lines, validation)
  pushShadowedAliases(lines, validation)
  pushAmbiguousAliases(lines, validation)

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

/**
 * Các dòng bị BỘ ĐỌC loại trước khi tới phần ghi.
 *
 * Không in nhóm này thì báo cáo nói "ánh xạ HIS: đọc 666" trong khi tệp nguồn có 835
 * dòng — 169 dòng biến mất không lời giải thích. Nguyên tắc "không âm thầm bỏ dữ liệu"
 * phải trọn cả ở tầng người đọc, không chỉ ở tầng dữ liệu.
 */
function pushDroppedSourceRows(lines: string[], validation: ValidationReport): void {
  const dropped = validation.droppedSourceRows ?? []
  lines.push('', `Dòng bị loại ngay khi đọc tệp nguồn: ${dropped.length}`)
  if (dropped.length === 0) return

  lines.push('  Gộp theo tệp và lý do:')
  for (const [key, count] of tally(dropped.map((d) => `${d.file} — ${d.reason}`))) {
    lines.push(`    ${count.toString().padStart(5)} × ${key}`)
  }
  lines.push('  Chi tiết:')
  for (const d of dropped) {
    lines.push(`    [${d.file}] ${d.key}${d.code ? ` (mã ${d.code})` : ''} — ${d.reason}`)
  }
}

/**
 * Mã cha thật sự không có thành viên nào — mỗi mã MỘT DÒNG, kèm tên xét nghiệm.
 *
 * Trước đây in bằng `profilesWithoutMembers.join(', ')`: trên dữ liệu thật ra một
 * dòng dài hơn 3000 ký tự, chỉ toàn mã trần, không tên — không kỹ thuật viên nào
 * đọc nổi để đối chiếu. Đây là danh sách họ phải cầm đi xác nhận, nên phải đọc được.
 */
function pushEmptyProfileHeads(lines: string[], validation: ValidationReport): void {
  const empty = validation.emptyProfileHeads
  lines.push('', `Mã cha không có thành viên nào: ${empty.length}`)
  if (empty.length === 0) return
  for (const h of empty) {
    const suffix = h.inferredMembers.length > 0 ? ` — đề xuất: ${h.inferredMembers.join(', ')}` : ''
    lines.push(`  ${h.code} "${h.name}"${suffix}`)
  }
}

/**
 * Nhóm cha–con SUY RA từ thụt lề tên — đề xuất, không phải sự thật đã khai báo.
 *
 * `source='legacy-inferred'` và `confirmed=false` đã được ghi vào database; ở đây in
 * ra để kỹ thuật viên xác nhận trước khi dùng thật, đúng yêu cầu của mục 3.1/4.2 spec.
 */
function pushInferredProfiles(lines: string[], validation: ValidationReport): void {
  const inferred = validation.inferredProfiles
  lines.push(
    '',
    `Nhóm cha–con suy ra từ thụt lề tên (CẦN kỹ thuật viên xác nhận): ${inferred.length}`,
  )
  for (const p of inferred) {
    lines.push(`  ${p.code} "${p.name}" — ${p.members.length} thành viên đề xuất:`)
    for (const m of p.members) {
      lines.push(`      ${m.code} "${m.name}"`)
    }
  }
}

/**
 * Bí danh trùng tên với một mã chính đã tồn tại.
 *
 * Quy tắc phân giải (mã chính thắng) đã đúng và không đổi, nhưng kỹ thuật viên phải
 * nhìn thấy tên xét nghiệm của CẢ HAI bên để xác nhận đúng là hai xét nghiệm riêng biệt
 * — `000CD` mã chính là *cấy dịch*, còn bí danh cùng tên trỏ tới *cấy đờm*.
 */
function pushShadowedAliases(lines: string[], validation: ValidationReport): void {
  const shadowed = validation.shadowedAliases ?? []
  lines.push('', `Bí danh trùng tên với mã chính (mã chính được dùng): ${shadowed.length}`)
  for (const s of shadowed) {
    lines.push(`  ${s.alias}: DÙNG "${s.primaryName}" (mã chính ${s.alias})`)
    lines.push(`      BỎ  "${s.aliasTargetName}" (bí danh trỏ tới ${s.aliasTargetCode})`)
  }
}

/** Bí danh trỏ tới nhiều mã chính — phần mềm không được đoán, người phải quyết. */
function pushAmbiguousAliases(lines: string[], validation: ValidationReport): void {
  const ambiguous = validation.ambiguousAliases ?? []
  lines.push('', `Bí danh trỏ tới nhiều mã chính (KHÔNG tự phân giải): ${ambiguous.length}`)
  for (const a of ambiguous) {
    lines.push(`  ${a.alias} → ${a.candidates.map((c) => `${c.code} "${c.name}"`).join('  /  ')}`)
    lines.push('      mọi dòng tham chiếu tới bí danh này đều bị bỏ qua và ghi lý do')
  }
}

/** Đếm số lần xuất hiện, giữ thứ tự gặp đầu tiên. */
function tally(items: string[]): [string, number][] {
  const counts = new Map<string, number>()
  for (const item of items) counts.set(item, (counts.get(item) ?? 0) + 1)
  return [...counts.entries()]
}
