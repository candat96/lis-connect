import { readFileSync, writeFileSync } from 'node:fs'
import type { ValidationReport } from './legacy-validator.js'

/** Giá trị đặc biệt để người vận hành nói "bỏ hẳn mã kênh này". */
export const SKIP_MARKER = '__BO_QUA__'

export interface ChannelDecision {
  insId: number
  channelCode: string
  candidates: string[]
  /** null nghĩa là chưa ai quyết. */
  chosen: string | null
  note?: string
}

export interface DecisionFile {
  channels: ChannelDecision[]
}

export type Resolution =
  | { kind: 'keep'; analyteCode: string }
  | { kind: 'skip' }
  | { kind: 'undecided' }
  | { kind: 'no-conflict' }

/** Ghép khoá gộp máy + mã kênh, có dấu phân cách để tránh trộn lẫn (máy 1 kênh "19" != máy 11 kênh "9"). */
function channelKey(insId: number, channelCode: string): string {
  return `${insId} ${channelCode}`
}

/**
 * Sinh khung tệp quyết định từ báo cáo kiểm tra.
 *
 * Nếu truyền vào tệp cũ, mọi lựa chọn đã quyết được giữ nguyên — để chạy lại công cụ
 * sau khi dữ liệu nguồn cập nhật mà không mất công quyết lại từ đầu.
 */
export function buildDecisionTemplate(
  report: ValidationReport,
  existing?: DecisionFile,
): DecisionFile {
  const previous = new Map<string, string | null>()
  for (const c of existing?.channels ?? []) {
    previous.set(channelKey(c.insId, c.channelCode), c.chosen)
  }

  return {
    channels: report.channelConflicts.map((conflict) => ({
      insId: conflict.insId,
      channelCode: conflict.channelCode,
      candidates: conflict.candidates,
      chosen: previous.get(channelKey(conflict.insId, conflict.channelCode)) ?? null,
    })),
  }
}

export function saveDecisions(path: string, file: DecisionFile): void {
  writeFileSync(path, JSON.stringify(file, null, 2), 'utf8')
}

export function loadDecisions(path: string): DecisionFile {
  const parsed: unknown = JSON.parse(readFileSync(path, 'utf8'))
  if (!parsed || typeof parsed !== 'object' || !Array.isArray((parsed as DecisionFile).channels)) {
    throw new Error(`tệp quyết định không hợp lệ: ${path}`)
  }
  return parsed as DecisionFile
}

/**
 * Tra xem một mã kênh đang xung đột đã được quyết chưa.
 *
 * - Không tìm thấy mục nào cho (insId, channelCode): mã kênh này vốn không có xung đột
 *   -> `no-conflict`. Dòng dữ liệu này được coi như sạch, được phép di trú bình thường.
 * - Tìm thấy mục nhưng `chosen` là `null`: có xung đột nhưng CHƯA ai quyết -> `undecided`.
 *   Dòng đó KHÔNG được di trú. Tuyệt đối không tự chọn một ứng viên — đó chính là hành
 *   vi của hệ cũ mà khối này sinh ra để loại bỏ.
 *
 * Hai nghĩa "không có gì để quyết" và "có việc phải quyết nhưng chưa ai quyết" KHÔNG
 * được gộp làm một, vì người gọi rất dễ quên kiểm tra và bỏ sót toàn bộ dữ liệu sạch.
 */
export function resolveChannel(
  decisions: DecisionFile,
  insId: number,
  channelCode: string,
): Resolution {
  const entry = decisions.channels.find(
    (c) => c.insId === insId && c.channelCode === channelCode,
  )
  if (!entry) return { kind: 'no-conflict' }
  if (entry.chosen === null) return { kind: 'undecided' }
  if (entry.chosen === SKIP_MARKER) return { kind: 'skip' }
  if (!entry.candidates.includes(entry.chosen)) {
    throw new Error(
      `lựa chọn "${entry.chosen}" cho máy ${insId} kênh ${channelCode} không nằm trong danh sách ứng viên: ${entry.candidates.join(', ')}`,
    )
  }
  return { kind: 'keep', analyteCode: entry.chosen }
}
