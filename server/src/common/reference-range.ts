// Chọn khoảng tham chiếu cho một người bệnh — chuyển nguyên quy tắc từ Lis.Core (ReferenceRangeSelector,
// PatientAge) để web và app .NET cho cùng một kết quả.

export interface PatientFacts {
  sex: string;
  /** yyyy-MM-dd */
  dob: string | null;
  birthYear: number | null;
}

export interface ReferenceRange {
  sex: 'M' | 'F' | null;
  ageFromDays: number | null;
  ageToDays: number | null;
  instrumentId: string | null;
  low: number | null;
  high: number | null;
  criticalLow: number | null;
  criticalHigh: number | null;
  textRange: string | null;
}

const DAY_MS = 86_400_000;

function dayNumber(isoDate: string): number {
  const [year, month, day] = isoDate.slice(0, 10).split('-').map(Number);
  return Math.floor(Date.UTC(year, month - 1, day) / DAY_MS);
}

/**
 * Số ngày tuổi tại ngày `at` (yyyy-MM-dd). Có ngày sinh thì tính đúng ngày; chỉ có năm sinh thì
 * giả định sinh 01/07 năm đó; không có cả hai → -1 (không rõ tuổi).
 */
export function ageInDays(patient: PatientFacts, at: string): number {
  const dob = patient.dob ?? (patient.birthYear !== null ? `${String(patient.birthYear).padStart(4, '0')}-07-01` : null);
  return dob === null ? -1 : dayNumber(at) - dayNumber(dob);
}

/**
 * Khoảng phù hợp nhất: chỉ xét khoảng khớp tuổi, giới, máy; trong đó ưu tiên đúng máy hơn máy
 * null, rồi có giới hơn chung, rồi khoảng tuổi hẹp hơn. Không có khoảng nào khớp → null.
 */
export function selectRange<T extends ReferenceRange>(ranges: T[], patient: PatientFacts, at: string, instrumentId: string | null): T | null {
  const age = ageInDays(patient, at);

  const candidates = ranges.filter((r) => {
    // Không rõ tuổi: chỉ khớp khoảng không giới hạn tuổi nào cả.
    const matchesAge = age < 0
      ? r.ageFromDays === null && r.ageToDays === null
      : (r.ageFromDays === null || age >= r.ageFromDays) && (r.ageToDays === null || age <= r.ageToDays);
    const matchesSex = r.sex === null || r.sex === patient.sex;
    const matchesInstrument = r.instrumentId === null || r.instrumentId === instrumentId;
    return matchesAge && matchesSex && matchesInstrument;
  });
  if (candidates.length === 0) return null;

  const instrumentRank = (r: T) => (r.instrumentId !== null && r.instrumentId === instrumentId ? 0 : 1);
  const sexRank = (r: T) => (r.sex !== null ? 0 : 1);
  const ageWidth = (r: T) => (r.ageToDays ?? Number.MAX_SAFE_INTEGER / 2) - (r.ageFromDays ?? -Number.MAX_SAFE_INTEGER / 2);

  // sort của JS ổn định → cùng hạng thì giữ thứ tự gốc, như OrderBy/ThenBy bên .NET.
  return [...candidates].sort((a, b) =>
    instrumentRank(a) - instrumentRank(b) || sexRank(a) - sexRank(b) || ageWidth(a) - ageWidth(b))[0];
}

/** Khoảng tham chiếu để hiển thị: chữ nếu có, không thì "thấp – cao" / "≥ thấp" / "≤ cao" theo số lẻ của chỉ số. */
export function formatRange(text: string | null, low: number | null, high: number | null, decimals: number | null): string | null {
  if (text !== null && text.trim() !== '') return text;
  const format = (value: number) => (decimals !== null ? value.toFixed(decimals) : String(Number(value.toFixed(4))));
  if (low !== null && high !== null) return `${format(low)} – ${format(high)}`;
  if (low !== null) return `≥ ${format(low)}`;
  if (high !== null) return `≤ ${format(high)}`;
  return null;
}
