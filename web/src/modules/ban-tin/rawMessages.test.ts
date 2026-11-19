import { describe, expect, it } from 'vitest';
import { formatSize, purgeDate, redecodeText, summarizeDecode } from './rawMessages';

describe('summarizeDecode', () => {
  it('đã giải mã: chỉ nêu các con số khác 0', () => {
    expect(summarizeDecode('da-giai-ma', { readings: 2, stored: 18, pendingReplacement: 1, unmatched: 3, qc: 0 }))
      .toBe('2 mẫu · ghi 18 kết quả · 1 chờ thay · 3 chưa ghép');
    expect(summarizeDecode('da-giai-ma', { readings: 1, stored: 0, qc: 4 })).toBe('1 mẫu · 4 kết quả QC');
  });

  it('giải mã lại: có kết quả giữ nguyên, xung đột, bản tin cũ hơn', () => {
    expect(summarizeDecode('da-giai-ma', { readings: 1, stored: 0, unchanged: 9, conflicts: 1, olderThanCurrent: 2 }))
      .toBe('1 mẫu · 9 giữ nguyên · 1 xung đột chờ thay · 2 cũ hơn kết quả hiện có');
  });

  it('cảnh báo và dòng chưa ghép đã gán ra giá trị khác được đưa lên đầu', () => {
    expect(summarizeDecode('khong-co-ket-qua', { readings: 0, warnings: ['Cấu hình không khớp, bỏ cả mẫu'] }))
      .toBe('Cảnh báo: Cấu hình không khớp, bỏ cả mẫu');
    expect(summarizeDecode('da-giai-ma', { readings: 1, stored: 2, resolvedMismatch: [{ barcode: 'M1', channel: 'GLU', oldValue: '5', newValue: '7' }] }))
      .toBe('1 dòng chưa ghép đã gán ra giá trị khác · 1 mẫu · ghi 2 kết quả');
  });

  it('lỗi / cần xem lại / chỉ lưu / không có kết quả / đang chờ', () => {
    expect(summarizeDecode('loi', { error: 'Driver ném lỗi: thiếu khung' })).toBe('Driver ném lỗi: thiếu khung');
    expect(summarizeDecode('loi', null)).toBe('Lỗi không rõ');
    expect(summarizeDecode('can-xem-lai', { note: 'Giải mã bị gián đoạn 3 lần' })).toBe('Giải mã bị gián đoạn 3 lần');
    expect(summarizeDecode('can-xem-lai', null)).toBe('Giải mã bị gián đoạn — xem rồi giải mã lại');
    expect(summarizeDecode('chi-luu', null)).toBe('Máy chỉ nghe hoặc chưa chọn giao thức — chỉ lưu');
    expect(summarizeDecode('khong-co-ket-qua', { readings: 0 })).toBe('Không có kết quả (câu hỏi chỉ định, thăm dò, byte rời…)');
    expect(summarizeDecode('cho-giai-ma', null)).toBe('');
    expect(summarizeDecode('dang-giai-ma', null)).toBe('');
  });
});

describe('redecodeText', () => {
  it('tóm tắt kết quả giải mã lại', () => {
    expect(redecodeText({ count: 3, stored: 2, pendingReplacement: 1, unmatched: 0, qc: 0, unchanged: 5, conflicts: 0, olderThanCurrent: 1, resolvedMismatch: 0, busy: 0, errors: 1 }))
      .toBe('Đã giải mã lại 3 bản tin: ghi 2 kết quả · 1 chờ thay · 5 giữ nguyên · 1 cũ hơn kết quả hiện có · 1 lỗi');
    expect(redecodeText({ count: 1, stored: 0, pendingReplacement: 0, unmatched: 0, qc: 0, unchanged: 0, conflicts: 0, olderThanCurrent: 0, resolvedMismatch: 0, busy: 0, errors: 0 }))
      .toBe('Đã giải mã lại 1 bản tin: không có thay đổi');
  });
});

describe('formatSize / purgeDate', () => {
  it('cỡ bản tin', () => {
    expect(formatSize(512)).toBe('512 B');
    expect(formatSize(2048)).toBe('2.0 KB');
    expect(formatSize(3 * 1024 * 1024)).toBe('3.0 MB');
  });

  it('ngày bị xoá = ngày lưu + 90 ngày', () => {
    expect(purgeDate('2026-11-19T08:00:00.000Z').toISOString()).toBe('2027-02-17T08:00:00.000Z');
  });
});
