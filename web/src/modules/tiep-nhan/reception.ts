// Tiếp nhận tối giản: đọc ngày/năm sinh người gõ, tìm dịch vụ theo mã, câu tóm tắt.
import dayjs from 'dayjs';
import type { CatalogService } from '@/services';

export type Birth = { dob: string; birthYear?: undefined } | { birthYear: number; dob?: undefined };

const THIS_YEAR = () => new Date().getFullYear();

/**
 * "1985" → năm sinh; "12/03/1985", "12-03-1985", "12031985" → ngày sinh (dd/MM/yyyy).
 * Sai, rỗng, tương lai, trước 1900 → null.
 */
export function parseBirth(text: string): Birth | null {
  const t = text.trim();
  if (/^\d{4}$/.test(t)) {
    const y = Number(t);
    return y >= 1900 && y <= THIS_YEAR() ? { birthYear: y } : null;
  }
  const m = /^(\d{1,2})[/\-.]?(\d{1,2})[/\-.]?(\d{4})$/.exec(t);
  if (!m) return null;
  const [, d, mo, y] = m;
  const iso = `${y}-${mo.padStart(2, '0')}-${d.padStart(2, '0')}`;
  // Không dùng plugin parse chặt: kiểm ngày thật bằng cách so lại chuỗi sau khi dayjs chuẩn hoá (31/02 → 03/03 thì lệch).
  const date = dayjs(iso);
  if (!date.isValid() || date.format('YYYY-MM-DD') !== iso || date.isAfter(dayjs()) || Number(y) < 1900) return null;
  return { dob: iso };
}

/** Tìm dịch vụ/panel theo mã (không phân biệt hoa thường), chỉ dịch vụ đang dùng; con của panel không chọn riêng ở đây. */
export function findServiceByCode(services: CatalogService[], code: string): CatalogService | null {
  const wanted = code.trim().toUpperCase();
  if (!wanted) return null;
  return services.find((s) => s.active && s.code.toUpperCase() === wanted) ?? null;
}

/** "N" → Nam, "U"/"F" → Nữ, "K"/"O" → Khác; nhận cả chữ đầy đủ. */
export function parseSex(text: string): 'M' | 'F' | 'O' | null {
  const t = text.trim().toUpperCase();
  if (['N', 'M', 'NAM'].includes(t)) return 'M';
  if (['U', 'F', 'NU', 'NỮ'].includes(t)) return 'F';
  if (['K', 'O', 'KHAC', 'KHÁC'].includes(t)) return 'O';
  return null;
}

export const SEX_LABEL: Record<'M' | 'F' | 'O', string> = { M: 'Nam', F: 'Nữ', O: 'Khác' };
