// Bản tin thô: câu tóm tắt tiếng Việt cho kết quả giải mã, cỡ bản tin, ngày bị dọn.
import dayjs, { type Dayjs } from 'dayjs';
import type { DecodeKind, DecodeResult, DecodeStatus, RedecodeTotals } from '@/services';

/** Máy chủ giữ bản tin thô 90 ngày kể từ lúc lưu. */
export const KEEP_DAYS = 90;

const n = (v: number | undefined) => v ?? 0;

/** Các con số khác 0 của một lượt giải mã, theo thứ tự đọc tự nhiên. */
function countParts(r: DecodeResult): string[] {
  const parts: string[] = [];
  if (n(r.readings) > 0) parts.push(`${r.readings} mẫu`);
  if (n(r.stored) > 0) parts.push(`ghi ${r.stored} kết quả`);
  if (n(r.pendingReplacement) > 0) parts.push(`${r.pendingReplacement} chờ thay`);
  if (n(r.unmatched) > 0) parts.push(`${r.unmatched} chưa ghép`);
  if (n(r.qc) > 0) parts.push(`${r.qc} kết quả QC`);
  if (n(r.unchanged) > 0) parts.push(`${r.unchanged} giữ nguyên`);
  if (n(r.conflicts) > 0) parts.push(`${r.conflicts} xung đột chờ thay`);
  if (n(r.olderThanCurrent) > 0) parts.push(`${r.olderThanCurrent} cũ hơn kết quả hiện có`);
  return parts;
}

/** Một dòng mô tả kết quả giải mã cho bảng; bản tin đang chờ/đang giải mã trả chuỗi rỗng. */
export function summarizeDecode(status: DecodeStatus, r: DecodeResult | null): string {
  switch (status) {
    case 'loi':
      return r?.error || r?.note || 'Lỗi không rõ';
    case 'can-xem-lai':
      return r?.note || r?.error || 'Giải mã bị gián đoạn — xem rồi giải mã lại';
    case 'chi-luu':
      return 'Máy chỉ nghe hoặc chưa chọn giao thức — chỉ lưu';
    case 'cho-giai-ma':
    case 'dang-giai-ma':
      return '';
    default: {
      const head: string[] = [];
      const mismatch = r?.resolvedMismatch?.length ?? 0;
      if (mismatch > 0) head.push(`${mismatch} dòng chưa ghép đã gán ra giá trị khác`);
      if (r?.warnings?.length) head.push(`Cảnh báo: ${r.warnings.join('; ')}`);
      const parts = [...head, ...(r ? countParts(r) : [])];
      if (parts.length > 0) return parts.join(' · ');
      return status === 'khong-co-ket-qua' ? 'Không có kết quả (câu hỏi chỉ định, thăm dò, byte rời…)' : 'Đã giải mã';
    }
  }
}

/** Tóm tắt phản hồi "giải mã lại" (một bản tin hoặc theo khoảng). */
export function redecodeText(t: RedecodeTotals): string {
  const parts: string[] = [];
  if (t.stored > 0) parts.push(`ghi ${t.stored} kết quả`);
  if (t.pendingReplacement > 0) parts.push(`${t.pendingReplacement} chờ thay`);
  if (t.unmatched > 0) parts.push(`${t.unmatched} chưa ghép`);
  if (t.qc > 0) parts.push(`${t.qc} kết quả QC`);
  if (t.unchanged > 0) parts.push(`${t.unchanged} giữ nguyên`);
  if (t.conflicts > 0) parts.push(`${t.conflicts} xung đột chờ thay`);
  if (t.olderThanCurrent > 0) parts.push(`${t.olderThanCurrent} cũ hơn kết quả hiện có`);
  if (t.resolvedMismatch > 0) parts.push(`${t.resolvedMismatch} dòng chưa ghép đã gán ra giá trị khác`);
  if (t.busy > 0) parts.push(`${t.busy} đang được giải mã, bỏ qua`);
  if (t.errors > 0) parts.push(`${t.errors} lỗi`);
  return `Đã giải mã lại ${t.count} bản tin: ${parts.length > 0 ? parts.join(' · ') : 'không có thay đổi'}`;
}

export const decodeKindLabel = (kind: DecodeKind): string => (kind === 'giai-ma-lai' ? 'Giải mã lại' : 'Tự động');

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/** Ngày máy chủ dọn bản tin (lưu + 90 ngày). */
export const purgeDate = (storedAt: string): Dayjs => dayjs(storedAt).add(KEEP_DAYS, 'day');

/** Câu xác nhận dùng chung cho mọi lần giải mã lại. */
export const REDECODE_SAFETY = 'Mẫu đã duyệt chỉ nhận giá trị chờ thay, không bị ghi đè. Kết quả đã có mà khác cũng vào chờ thay để người duyệt chọn. Chỉ số còn trống trên mẫu chưa duyệt được ghi thẳng; dòng Chưa ghép / QC chưa xử lý của chính bản tin này được thay bằng kết quả mới.';
