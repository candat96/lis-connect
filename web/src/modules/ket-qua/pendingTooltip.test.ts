import { describe, expect, it } from 'vitest';
import { pendingTooltip } from './SampleResultCard';

const base = { pendingReason: null, pendingAt: '2026-11-20T02:55:00.000Z', pendingMeasuredAt: '2026-11-20T02:50:00.000Z', pendingInstrumentFlag: null };

describe('pendingTooltip', () => {
  it('máy gửi lại: nêu giờ đo và giờ ghi nhận', () => {
    expect(pendingTooltip({ ...base, pendingReason: 'Máy gửi lại kết quả cho mẫu đã duyệt' }))
      .toBe('Máy gửi lại giá trị này · đo lúc 09:50 20/11/2026 · ghi nhận lúc 09:55 20/11/2026. Bấm "Dùng" để thay giá trị hiện tại, hoặc ✕ để bỏ.');
  });

  it('giải mã lại, bản tin cũ đến sau, gán từ chưa ghép: nêu đúng nguồn', () => {
    expect(pendingTooltip({ ...base, pendingReason: 'Giải mã lại ra giá trị khác' })).toMatch(/^Giải mã lại bản tin ra giá trị này/);
    expect(pendingTooltip({ ...base, pendingReason: 'Bản tin cũ đến sau — kết quả hiện tại từ bản tin mới hơn' })).toMatch(/^Bản tin CŨ HƠN đến sau .* thường nên giữ/);
    expect(pendingTooltip({ ...base, pendingReason: 'Gán từ dòng chưa ghép' })).toMatch(/^Gán từ dòng chưa ghép/);
  });

  it('không có lý do (dữ liệu cũ): mặc định máy gửi lại, kèm cờ máy', () => {
    expect(pendingTooltip({ ...base, pendingAt: null, pendingMeasuredAt: null, pendingInstrumentFlag: 'H' }))
      .toBe('Máy gửi lại giá trị này · cờ máy: H. Bấm "Dùng" để thay giá trị hiện tại, hoặc ✕ để bỏ.');
  });
});
