import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '@/app/providers/AppProviders';
import { ApiError, instrumentService, rawMessageService, type RawMessageDetail, type RawMessageRow } from '@/services';
import { useSessionStore } from '@/stores/sessionStore';
import RawMessagesPage from './RawMessagesPage';

const login = (roles: string[]) =>
  useSessionStore.getState().setSession('tok', { id: 'u1', username: 'u', fullName: 'Người thử', title: null, roles }, { code: 'pk', name: 'PK' });

beforeEach(() => {
  window.matchMedia ??= ((query: string) => ({
    matches: false, media: query, onchange: null,
    addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false,
  })) as typeof window.matchMedia;
  vi.restoreAllMocks();
  login(['quan-tri']);
  vi.spyOn(instrumentService, 'list').mockResolvedValue([
    { id: 'i1', name: 'BC-5380 Thử' } as never,
  ]);
});

// "MSH|...<CR>PID|..." — hex của bản tin HL7 ngắn.
const hexOf = (text: string) => Array.from(new TextEncoder().encode(text)).map((b) => b.toString(16).padStart(2, '0')).join('').toUpperCase();
const HL7 = 'MSH|^~\\&|BC-5380\rOBX|1|NM|WBC||7.2\r';

const row = (over: Partial<RawMessageRow> = {}): RawMessageRow => ({
  id: 'm1', instrumentId: 'i1', instrumentName: 'BC-5380 Thử', seq: 1, session: 's1',
  startedAt: '2026-11-19T08:00:00.000Z', completedAt: '2026-11-19T08:00:01.000Z', cursor: '2026-11-19T08:00:01.0000000Z|m1',
  partial: false, size: 120, decodeStatus: 'loi', decoderVersion: '0.4.0', summary: { error: 'Thiếu đoạn OBR' },
  ...over,
});

const detail = (over: Partial<RawMessageDetail> = {}): RawMessageDetail => ({
  id: 'm1', instrumentId: 'i1', instrumentName: 'BC-5380 Thử', agentId: 'a1', seq: 1, session: 's1',
  startedAt: '2026-11-19T08:00:00.000Z', completedAt: '2026-11-19T08:00:01.000Z', storedAt: '2026-11-19T08:00:02.000Z',
  partial: false, size: HL7.length, hex: hexOf(HL7), hexTruncated: false, protocolFamily: 'hl7-mllp', model: 'BC-5380',
  decodeStatus: 'da-giai-ma', decodeStartedAt: null, decoderVersion: '0.4.0+abc', decodedAt: '2026-11-19T08:00:03.000Z',
  decodeResult: { readings: 1, stored: 1 },
  decodes: [{ kind: 'tu-dong', actorName: null, at: '2026-11-19T08:00:03.000Z', decoderVersion: '0.4.0+abc', status: 'da-giai-ma', result: { readings: 1, stored: 1 } }],
  ...over,
});

// jsdom: antd gán cùng aria-labelledby cho mọi dialog — tìm theo tiêu đề.
const findDialog = async (title: RegExp) => (await screen.findByText(title, { selector: '.ant-modal-title' })).closest('[role="dialog"]') as HTMLElement;

const renderPage = (path = '/ban-tin') =>
  render(
    <AppProviders>
      <MemoryRouter initialEntries={[path]} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <RawMessagesPage />
      </MemoryRouter>
    </AppProviders>,
  );

describe('RawMessagesPage', () => {
  it('mặc định lọc "Cần chú ý"; chuyển "Tất cả" thì lọc được theo trạng thái', async () => {
    const list = vi.spyOn(rawMessageService, 'list').mockResolvedValue([row()]);
    renderPage();

    expect(await screen.findByText('Thiếu đoạn OBR')).toBeInTheDocument();
    expect(list).toHaveBeenCalledWith(expect.objectContaining({ attention: true, status: undefined }));

    fireEvent.click(screen.getByText('Tất cả'));
    await waitFor(() => expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ attention: false })));
    expect(screen.getByRole('combobox', { name: 'Lọc theo trạng thái' })).toBeInTheDocument();
  });

  it('Enter mở bản tin đang chọn: chữ đọc được, kết quả giải mã, lịch sử giải mã', async () => {
    vi.spyOn(rawMessageService, 'list').mockResolvedValue([row(), row({ id: 'm2', cursor: 'x|m2', summary: { error: 'Lỗi thứ hai' } })]);
    const get = vi.spyOn(rawMessageService, 'get').mockResolvedValue(detail({ id: 'm2' }));
    renderPage();

    await screen.findByText('Lỗi thứ hai');
    const list = screen.getByLabelText(/Danh sách bản tin/);
    fireEvent.keyDown(list, { key: 'ArrowDown' });
    fireEvent.keyDown(list, { key: 'Enter' });

    await waitFor(() => expect(get).toHaveBeenCalledWith('m2'));
    const content = await screen.findByLabelText('Nội dung bản tin');
    expect(content.textContent).toContain('MSH|^~\\&|BC-5380<CR>');
    expect(content.textContent).toContain('OBX|1|NM|WBC||7.2<CR>');
    expect(screen.getByText('Kết quả ghi:')).toBeInTheDocument();
    expect(screen.getByText('Tự động')).toBeInTheDocument();
    expect(screen.getByText('Hệ thống')).toBeInTheDocument();
  });

  it('bản tin lỗi: báo ngày máy chủ sẽ xoá; dòng chưa ghép đã gán ra giá trị khác được liệt kê', async () => {
    vi.spyOn(rawMessageService, 'list').mockResolvedValue([row()]);
    vi.spyOn(rawMessageService, 'get').mockResolvedValue(detail({
      decodeStatus: 'loi', decodeResult: { error: 'Thiếu đoạn OBR', resolvedMismatch: [{ barcode: 'MAU01', channel: 'GLU', oldValue: '5.1', newValue: '7.4' }] },
    }));
    renderPage('/ban-tin?id=m1');

    expect(await screen.findByText(/sẽ xoá bản tin này ngày 17\/02\/2027/)).toBeInTheDocument();
    expect(screen.getByText(/1 dòng Chưa ghép đã gán cho người bệnh/)).toBeInTheDocument();
    expect(screen.getByText('7.4')).toBeInTheDocument();
  });

  it('Giải mã thử hiện bảng kết quả, không gọi giải mã lại', async () => {
    vi.spyOn(rawMessageService, 'list').mockResolvedValue([row()]);
    vi.spyOn(rawMessageService, 'get').mockResolvedValue(detail());
    const preview = vi.spyOn(rawMessageService, 'preview').mockResolvedValue({
      readings: [{ barcode: 'MAU01', isQc: false, items: [{ channelCode: 'WBC', rawValue: '7.2', unit: '10^9/L', flag: 'N' }] }],
      queries: [], notes: [], abandoned: false,
    });
    const redecode = vi.spyOn(rawMessageService, 'redecode');
    renderPage('/ban-tin?id=m1');

    fireEvent.click(await screen.findByRole('button', { name: /Giải mã thử/ }));
    const table = await screen.findByLabelText('Kết quả giải mã thử');
    expect(within(table).getByText('MAU01')).toBeInTheDocument();
    expect(within(table).getByText('10^9/L')).toBeInTheDocument();
    expect(preview).toHaveBeenCalledWith('m1');
    expect(redecode).not.toHaveBeenCalled();
  });

  it('Giải mã lại hỏi xác nhận, nói rõ mẫu đã duyệt không bị ghi đè', async () => {
    vi.spyOn(rawMessageService, 'list').mockResolvedValue([row()]);
    vi.spyOn(rawMessageService, 'get').mockResolvedValue(detail());
    const redecode = vi.spyOn(rawMessageService, 'redecode').mockResolvedValue({
      count: 1, stored: 0, pendingReplacement: 1, unmatched: 0, qc: 0, unchanged: 3, conflicts: 0, olderThanCurrent: 0, resolvedMismatch: 0, busy: 0, errors: 0,
    });
    renderPage('/ban-tin?id=m1');

    fireEvent.click(await screen.findByRole('button', { name: /^.*Giải mã lại$/ }));
    const dialog = await findDialog(/Giải mã lại bản tin này/);
    expect(within(dialog).getByText(/Mẫu đã duyệt chỉ nhận giá trị chờ thay, không bị ghi đè/)).toBeInTheDocument();
    expect(redecode).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Giải mã lại' }));
    await waitFor(() => expect(redecode).toHaveBeenCalledWith({ ids: ['m1'] }));
    expect(await screen.findByText(/Đã giải mã lại 1 bản tin: 1 chờ thay · 3 giữ nguyên/)).toBeInTheDocument();
  });

  it('giải mã lại theo khoảng của một máy: quá 500 bản tin thì hiện lỗi máy chủ', async () => {
    vi.spyOn(rawMessageService, 'list').mockResolvedValue([]);
    const redecode = vi.spyOn(rawMessageService, 'redecode').mockRejectedValue(new ApiError('Tối đa 500 bản tin mỗi lần — thu hẹp khoảng thời gian', 400));
    renderPage('/ban-tin?instrumentId=i1');

    fireEvent.click(await screen.findByRole('button', { name: /Giải mã lại theo khoảng/ }));
    const dialog = await findDialog(/Giải mã lại theo khoảng thời gian/);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Giải mã lại' }));

    await waitFor(() => expect(redecode).toHaveBeenCalledWith(expect.objectContaining({ instrumentId: 'i1' })));
    expect(await within(dialog).findByText('Tối đa 500 bản tin mỗi lần — thu hẹp khoảng thời gian')).toBeInTheDocument();
  });

  it('kỹ thuật viên xem được nhưng không thấy nút giải mã thử / giải mã lại', async () => {
    login(['ky-thuat-vien']);
    vi.spyOn(rawMessageService, 'list').mockResolvedValue([row()]);
    vi.spyOn(rawMessageService, 'get').mockResolvedValue(detail());
    renderPage('/ban-tin?id=m1');

    expect(await screen.findByLabelText('Nội dung bản tin')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Giải mã thử/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /Giải mã lại/ })).toBeNull();
  });
});
