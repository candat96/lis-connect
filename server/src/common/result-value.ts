// Đọc, làm tròn và tính cờ cho giá trị kết quả — chuyển nguyên quy tắc từ Lis.Core.Results
// (ResultValue, ComparatorParsing, ValueRounding, FlagCalculator, QualitativeMapper) để web và
// app .NET / đường máy gửi cho cùng một kết quả.

export type ResultFlag = 'None' | 'Low' | 'High' | 'CriticalLow' | 'CriticalHigh' | 'Abnormal';

const COMPARATORS = ['<=', '>=', '<', '>'];
/** Số thập phân như decimal.TryParse (AllowDecimalPoint | AllowLeadingSign, Invariant): "12", "12.5", ".5", "5.", "-3". */
const DECIMAL = /^[+-]?(\d+\.?\d*|\.\d+)$/;
const TITER = /^(\d+)\s*[:/]\s*(\d+)$/;

/** Tách dấu so sánh đứng đầu (chuỗi đã cắt khoảng trắng) và phần còn lại. */
export function stripComparator(trimmed: string): { comparator: string | null; body: string } {
  for (const candidate of COMPARATORS) {
    if (trimmed.startsWith(candidate)) return { comparator: candidate, body: trimmed.slice(candidate.length).trim() };
  }
  return { comparator: null, body: trimmed };
}

export interface ParsedValue {
  /** Số để so ngưỡng / tính cờ; null khi không đọc được. */
  number: number | null;
  comparator: string | null;
  /** Chuỗi rỗng hoặc không phải số / hiệu giá (vd ký hiệu lỗi của máy "***.*") — KHÔNG phải giá trị 0. */
  isEmpty: boolean;
}

/** Phân tích chuỗi kết quả thô: số (dấu phẩy coi như dấu chấm), có thể kèm dấu so sánh; hoặc hiệu giá "1:4", "1/4". */
export function parseValue(raw: string | null | undefined): ParsedValue {
  const trimmed = (raw ?? '').trim();
  if (trimmed.length === 0) return { number: null, comparator: null, isEmpty: true };

  const { comparator, body } = stripComparator(trimmed);

  const titer = TITER.exec(body);
  if (titer) {
    const denominator = Number(titer[2]);
    if (denominator === 0) return { number: null, comparator, isEmpty: true };
    return { number: Number(titer[1]) / denominator, comparator, isEmpty: false };
  }

  const normalized = body.replace(/,/g, '.');
  if (DECIMAL.test(normalized)) return { number: Number(normalized), comparator, isEmpty: false };
  return { number: null, comparator: null, isEmpty: true };
}

/**
 * Làm tròn "half away from zero" trên CHUỖI thập phân (không qua số thực, để 2.675 → 2.68 chứ không
 * phải 2.67) và giữ đúng số chữ số lẻ, kể cả số 0 ở cuối ("4.8" với 2 số lẻ → "4.80").
 */
function roundDecimalString(text: string, decimals: number): string {
  const negative = text.startsWith('-');
  const unsigned = text.replace(/^[+-]/, '');
  const [intPart = '', fracPart = ''] = unsigned.split('.');
  const digits = (intPart || '0') + fracPart.padEnd(decimals + 1, '0').slice(0, decimals + 1);
  let scaled = BigInt(digits.slice(0, -1)); // giá trị × 10^decimals, bỏ chữ số quyết định làm tròn
  if (Number(digits.slice(-1)) >= 5) scaled += 1n;

  const padded = scaled.toString().padStart(decimals + 1, '0');
  const result = decimals === 0 ? padded : `${padded.slice(0, -decimals)}.${padded.slice(-decimals)}`;
  return negative && scaled !== 0n ? `-${result}` : result;
}

/** Làm tròn chuỗi giá trị theo số lẻ, giữ dấu so sánh đứng đầu. Chuỗi không phải số trả về nguyên văn. */
export function roundValue(raw: string, decimals: number | null): string {
  if (decimals === null) return raw;
  const { comparator, body } = stripComparator(raw.trim());
  const normalized = body.replace(/,/g, '.');
  if (!DECIMAL.test(normalized)) return raw;
  return (comparator ?? '') + roundDecimalString(normalized, decimals);
}

/**
 * Chuỗi số sẽ lưu: có số lẻ thì làm tròn (giữ dấu so sánh); không có số lẻ thì vẫn đổi dấu phẩy thành
 * dấu chấm ("4,8" → "4.8") — trừ hiệu giá ("1:4", "1/4") giữ nguyên như gõ.
 */
export function normalizeNumber(trimmed: string, parsed: ParsedValue, decimals: number | null): string {
  if (decimals !== null) return roundValue(trimmed, decimals);
  const body = trimmed.slice(parsed.comparator?.length ?? 0).trim();
  if (body.includes(':') || body.includes('/')) return trimmed;
  return (parsed.comparator ?? '') + body.replace(/,/g, '.');
}

export interface FlagRange {
  low: number | null;
  high: number | null;
  criticalLow: number | null;
  criticalHigh: number | null;
}

/**
 * Cờ từ giá trị số và khoảng tham chiếu. Cờ nguy kịch luôn thắng cờ cao/thấp thường. Giá trị kèm dấu
 * so sánh (">500") so trực tiếp số đó. Không bao giờ trả 'Abnormal' — cờ đó chỉ do quy đổi định tính.
 */
export function computeFlag(value: ParsedValue, range: FlagRange | null): ResultFlag {
  if (value.isEmpty || value.number === null || range === null) return 'None';
  const n = value.number;
  if (range.criticalLow !== null && n <= range.criticalLow) return 'CriticalLow';
  if (range.criticalHigh !== null && n >= range.criticalHigh) return 'CriticalHigh';
  if (range.low !== null && n < range.low) return 'Low';
  if (range.high !== null && n > range.high) return 'High';
  return 'None';
}

export interface QualitativeRule {
  instrumentId: string | null;
  rawValue: string;
  displayValue: string;
  flag: ResultFlag | null;
}

/**
 * Quy đổi giá trị định tính thô ("pos"/"neg") sang chữ hiển thị và cờ ghi đè. So không phân biệt hoa thường,
 * bỏ khoảng trắng hai đầu; luật đúng máy ưu tiên hơn luật chung. Không có luật khớp → giữ nguyên, không cờ.
 */
export function applyQualitative(raw: string, instrumentId: string | null, rules: QualitativeRule[]): { display: string; flag: ResultFlag | null } {
  const needle = raw.trim().toLowerCase();
  const matched = rules
    .filter((r) => r.rawValue.trim().toLowerCase() === needle && (r.instrumentId === null || r.instrumentId === instrumentId))
    .sort((a, b) => Number(a.instrumentId === null) - Number(b.instrumentId === null))[0];
  return matched ? { display: matched.displayValue, flag: matched.flag } : { display: raw, flag: null };
}
