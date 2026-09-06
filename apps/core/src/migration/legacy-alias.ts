import type { LegacySource } from './legacy-source.js'

/**
 * Bảng bí danh mã xét nghiệm của hệ cũ.
 *
 * `TestCodeNew` không chỉ là một danh mục thứ hai: nó còn mang cột đổi tên mã, nên
 * nhiều bảng tham chiếu (rõ nhất là `ResultMapping`) trỏ tới mã bí danh chứ không
 * phải mã chính. Không phân giải thì những dòng đó bị coi là mồ côi và mất trắng.
 *
 * Tệp này CHỈ phân giải. Việc đọc bảng bí danh từ tệp nguồn thuộc về `legacy-source.ts`.
 */

/** alias -> danh sách mã chính mà nó trỏ tới (nhiều hơn một là nhập nhằng). */
export type AliasIndex = Map<string, string[]>

export type CodeResolution =
  /** Mã có sẵn trong danh mục chính — dùng thẳng, không tra bí danh. */
  | { kind: 'primary'; code: string }
  /** Mã không có trong danh mục chính nhưng bí danh trỏ tới đúng một mã chính. */
  | { kind: 'alias'; code: string; via: string }
  /** Bí danh trỏ tới nhiều mã chính khác nhau — không được tự chọn. */
  | { kind: 'ambiguous'; candidates: string[] }
  /** Không có trong danh mục chính, cũng không có bí danh nào dùng được. */
  | { kind: 'unknown' }

/**
 * Dựng chỉ mục bí danh từ `LegacySource`.
 *
 * `analyteAliases` được bộ đọc bổ sung sau khi phần này đã viết xong; nếu bộ nguồn
 * cũ (hoặc một bộ dựng tay trong test) không có trường đó thì chỉ mục rỗng và mọi
 * thứ hoạt động y như khi chưa có bảng bí danh.
 */
export function buildAliasIndex(src: LegacySource): AliasIndex {
  const index: AliasIndex = new Map()
  for (const row of src.analyteAliases ?? []) {
    const alias = row?.alias?.trim()
    const canonical = row?.canonical?.trim()
    if (!alias || !canonical || alias === canonical) continue
    const list = index.get(alias) ?? []
    if (!list.includes(canonical)) list.push(canonical)
    index.set(alias, list)
  }
  return index
}

/**
 * Phân giải một mã tham chiếu về mã chính trong danh mục.
 *
 * Thứ tự BẮT BUỘC, không được đảo:
 *   1. mã có trong danh mục chính  -> dùng thẳng, KHÔNG tra bí danh;
 *   2. không có -> tra bảng bí danh;
 *   3. vẫn không có -> chịu.
 *
 * Có 7 bí danh trùng tên với một mã chính đã tồn tại mà hai bên là xét nghiệm khác
 * hẳn nhau (`000CD` mã chính là *cấy dịch*, bí danh trỏ tới *cấy đờm*; `1438BRCA1`
 * mã chính là *GENE BRCA1*, bí danh trỏ tới *185delAG*). Đảo thứ tự là gán nhầm tên
 * xét nghiệm cho bệnh nhân.
 *
 * Bí danh trỏ tới nhiều mã chính (`000CDNT` -> *cấy dịch ngực (T)* và *cấy dịch não
 * tủy*) thì KHÔNG tự chọn — trả về `ambiguous` để người gọi bỏ qua và ghi lý do,
 * giống hệt cách xử lý mã kênh chưa được quyết.
 */
export function resolveAnalyteCode(
  code: string,
  primary: ReadonlySet<string>,
  aliases: AliasIndex,
): CodeResolution {
  if (primary.has(code)) return { kind: 'primary', code }

  const targets = (aliases.get(code) ?? []).filter((t) => primary.has(t))
  if (targets.length === 0) return { kind: 'unknown' }
  if (targets.length > 1) return { kind: 'ambiguous', candidates: [...targets].sort() }
  return { kind: 'alias', code: targets[0]!, via: code }
}
