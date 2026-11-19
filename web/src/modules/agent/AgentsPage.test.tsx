import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '@/app/providers/AppProviders';
import { agentService, type Agent } from '@/services';
import { useSessionStore } from '@/stores/sessionStore';
import AgentsPage from './AgentsPage';

// antd cần matchMedia (Row/Col responsive) — jsdom không có sẵn.
beforeEach(() => {
  window.matchMedia ??= ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  })) as typeof window.matchMedia;
  vi.restoreAllMocks();
  useSessionStore.getState().setSession('tok', { id: 'u1', username: 'admin', fullName: 'Quản trị', title: null, roles: ['quan-tri'] }, { code: 'pk', name: 'PK' });
});

const CODE = 'LIS1.aHR0cDovL3NlcnZlcgpwawpzZWNyZXQ';
const inThirtyMinutes = () => new Date(Date.now() + 30 * 60_000).toISOString();

const agent = (over: Partial<Agent>): Agent => ({
  id: 'a1', name: 'Máy tính XN tầng 2', workstationCode: 'W-a1', lastSeenAt: null, online: false, instruments: [],
  paired: false, pairedAt: null, machineName: null, version: null, pairingExpiresAt: null,
  ...over,
});

const renderPage = () =>
  render(
    <AppProviders>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <AgentsPage />
      </MemoryRouter>
    </AppProviders>,
  );

describe('AgentsPage', () => {
  it('thêm agent chỉ cần tên → hiện mã ghép nối và nút tải tệp cài, rồi báo khi agent kết nối', async () => {
    const expiresAt = inThirtyMinutes();
    const list = vi.spyOn(agentService, 'list').mockResolvedValue([]);
    vi.spyOn(agentService, 'installerInfo').mockResolvedValue({ available: true, version: '1.2.0', sha256: 'ab', size: 10 });
    const create = vi.spyOn(agentService, 'create').mockResolvedValue({ id: 'a1', pairing: { code: CODE, expiresAt } });
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Thêm agent/ }));
    const form = await screen.findByRole('dialog', { name: 'Thêm agent' });
    expect(within(form).queryByLabelText(/Mã máy trạm/)).toBeNull();
    fireEvent.change(within(form).getByLabelText(/Tên agent/), { target: { value: '  Máy tính XN tầng 2 ' } });
    list.mockResolvedValue([agent({ pairingExpiresAt: expiresAt })]);
    fireEvent.click(within(form).getByRole('button', { name: /Lưu/ }));

    const dialog = await screen.findByRole('dialog', { name: /Ghép nối "Máy tính XN tầng 2"/ });
    expect(create).toHaveBeenCalledWith({ name: 'Máy tính XN tầng 2' });
    expect(within(dialog).getByDisplayValue(CODE)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /Tải tệp cài/ })).toBeEnabled();
    expect(await within(dialog).findByText(/1\.2\.0/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Đang chờ agent/)).toBeInTheDocument();

    list.mockResolvedValue([
      agent({ paired: true, pairedAt: new Date().toISOString(), online: true, lastSeenAt: new Date().toISOString(), machineName: 'PC-XN-02', version: '1.2.0' }),
    ]);
    expect(await within(dialog).findByText(/Agent đã kết nối/)).toBeInTheDocument();
    expect(within(dialog).getByText(/PC-XN-02/)).toBeInTheDocument();
  });

  it('server chưa có tệp cài → hướng dẫn AGENT_DIST_DIR, nút tải bị tắt', async () => {
    vi.spyOn(agentService, 'list').mockResolvedValue([]);
    vi.spyOn(agentService, 'installerInfo').mockResolvedValue({ available: false, version: null, sha256: null, size: null });
    vi.spyOn(agentService, 'create').mockResolvedValue({ id: 'a1', pairing: { code: CODE, expiresAt: inThirtyMinutes() } });
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Thêm agent/ }));
    const form = await screen.findByRole('dialog', { name: 'Thêm agent' });
    fireEvent.change(within(form).getByLabelText(/Tên agent/), { target: { value: 'XN' } });
    fireEvent.click(within(form).getByRole('button', { name: /Lưu/ }));

    const dialog = await screen.findByRole('dialog', { name: /Ghép nối "XN"/ });
    expect(await within(dialog).findByText(/AGENT_DIST_DIR/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /Tải tệp cài/ })).toBeDisabled();
  });

  it('bảng hiện trạng thái ghép nối, máy tính và phiên bản; không còn cột mã máy trạm', async () => {
    vi.spyOn(agentService, 'list').mockResolvedValue([
      agent({ id: 'a1', name: 'Đang chạy', paired: true, pairedAt: '2026-11-18T01:00:00Z', online: true, lastSeenAt: new Date().toISOString(), machineName: 'PC-01', version: '1.2.0' }),
      agent({ id: 'a2', name: 'Chờ mã', pairingExpiresAt: inThirtyMinutes() }),
      agent({ id: 'a3', name: 'Hết mã' }),
    ]);
    renderPage();

    expect(await screen.findByText('Đang kết nối')).toBeInTheDocument();
    expect(screen.getByText('Chờ ghép nối')).toBeInTheDocument();
    expect(screen.getByText('Chưa ghép nối')).toBeInTheDocument();
    expect(screen.getByText(/PC-01/)).toBeInTheDocument();
    expect(screen.queryByText('Mã máy trạm')).toBeNull();
    expect(screen.getAllByRole('button', { name: /Ghép nối lại/ })).toHaveLength(3);
  });
});
