import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '@/app/providers/AppProviders';
import { agentService, instrumentService, type Agent, type AgentCommand, type Instrument } from '@/services';
import { useSessionStore } from '@/stores/sessionStore';
import { AgentDrawer } from './AgentDrawer';

beforeEach(() => {
  window.matchMedia ??= ((query: string) => ({
    matches: false, media: query, onchange: null,
    addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false,
  })) as typeof window.matchMedia;
  vi.restoreAllMocks();
  useSessionStore.getState().setSession('tok', { id: 'u1', username: 'admin', fullName: 'Quản trị', title: null, roles: ['quan-tri'] }, { code: 'pk', name: 'PK' });
  vi.spyOn(agentService, 'installerInfo').mockResolvedValue({ available: true, version: '0.2.0', sha256: 'ab', size: 10 });
  vi.spyOn(agentService, 'commands').mockResolvedValue([]);
});

const NAME = 'Máy tính XN tầng 2';

const agent = (over: Partial<Agent> = {}): Agent => ({
  id: 'a1', name: NAME, workstationCode: 'W-a1', lastSeenAt: new Date().toISOString(), online: true, instruments: [],
  paired: true, pairedAt: '2026-11-19T07:00:00.000Z', machineName: 'PC-XN-02', version: '0.2.0', pairingExpiresAt: null,
  channel: true, ports: null, portsScannedAt: null, pendingOrphan: 0,
  ...over,
});

const instrument = (over: Partial<Instrument> = {}): Instrument => ({
  id: 'i1', name: 'BC-5380', model: 'BC-5380', protocolFamily: 'hl7-mllp', workstationCode: 'W-a1', active: true,
  connection: { kind: 'serial', portName: 'COM3', baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 1 },
  connectionText: 'COM3 9600 8N1', channelCount: 0, pendingServiceCount: 0, unmappedCount: 0,
  agent: { id: 'a1', name: NAME, lastSeenAt: null, online: true },
  status: { state: 'Connected', text: 'Đã kết nối', lastResultAt: null, samplesReceived: 12, pendingToRetry: 0 },
  ...over,
});

const command = (over: Partial<AgentCommand>): AgentCommand => ({
  id: 'c1', name: 'scan-ports', args: null, status: 'sent', requestedBy: 'Quản trị', requestedAt: new Date().toISOString(),
  startedAt: null, finishedAt: null, result: null, error: null,
  ...over,
});

// jsdom: antd gán cùng aria-labelledby="test-id" cho mọi dialog nên không tìm theo tên được — tìm theo tiêu đề.
const findDialog = async (title: RegExp) => (await screen.findByText(title, { selector: '.ant-modal-title' })).closest('[role="dialog"]') as HTMLElement;

const renderDrawer = () =>
  render(
    <AppProviders>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <AgentDrawer agentId="a1" onClose={() => {}} />
      </MemoryRouter>
    </AppProviders>,
  );

describe('AgentDrawer', () => {
  it('agent mất kênh lệnh → các nút lệnh tắt, có lời báo', async () => {
    vi.spyOn(agentService, 'list').mockResolvedValue([agent({ channel: false, online: false })]);
    vi.spyOn(instrumentService, 'list').mockResolvedValue([]);
    renderDrawer();

    expect(await screen.findByText(/Agent đang mất kết nối/)).toBeInTheDocument();
    for (const name of [/Nạp lại cấu hình/, /Quét cổng COM/, /Gửi bù ngay/, /Khởi động lại/]) {
      expect(screen.getByRole('button', { name })).toBeDisabled();
    }
    // Thu hồi vẫn bấm được khi agent tắt (server khoá ngay, agent không nối lại được nữa).
    expect(screen.getByRole('button', { name: /Thu hồi/ })).toBeEnabled();
  });

  it('quét cổng COM → hiện "Đang chạy" rồi bảng cổng khi lệnh xong', async () => {
    const list = vi.spyOn(agentService, 'list').mockResolvedValue([agent()]);
    vi.spyOn(instrumentService, 'list').mockResolvedValue([]);
    const send = vi.spyOn(agentService, 'command').mockResolvedValue(command({ status: 'sent' }));
    const byId = vi.spyOn(agentService, 'commandById').mockResolvedValue(command({ status: 'running' }));
    renderDrawer();

    fireEvent.click(await screen.findByRole('button', { name: /Quét cổng COM/ }));
    await waitFor(() => expect(send).toHaveBeenCalledWith('a1', 'scan-ports'));
    expect(await screen.findByText('Đang chạy')).toBeInTheDocument();

    const ports = [{ name: 'COM3', description: 'USB-SERIAL CH340', usedBy: 'BC-5380', busy: false }, { name: 'COM5', description: null, usedBy: null, busy: true }];
    list.mockResolvedValue([agent({ ports, portsScannedAt: new Date().toISOString() })]);
    byId.mockResolvedValue(command({ status: 'done', result: { ports } }));

    expect(await screen.findByText('USB-SERIAL CH340', undefined, { timeout: 4000 })).toBeInTheDocument();
    // Bảng cổng theo danh sách agent, dòng tóm tắt theo lần hỏi lệnh — hai nhịp khác nhau nên chờ riêng.
    expect(await screen.findByText(/2 cổng: COM3 \(BC-5380\), COM5 bận/, undefined, { timeout: 4000 })).toBeInTheDocument();
    expect(screen.getByText(/Phần mềm khác đang giữ/)).toBeInTheDocument();
  });

  it('thu hồi phải gõ đúng tên agent mới gửi lệnh', async () => {
    vi.spyOn(agentService, 'list').mockResolvedValue([agent()]);
    vi.spyOn(instrumentService, 'list').mockResolvedValue([]);
    const send = vi.spyOn(agentService, 'command').mockResolvedValue(command({ name: 'revoke' }));
    vi.spyOn(agentService, 'commandById').mockResolvedValue(command({ name: 'revoke', status: 'done' }));
    renderDrawer();

    fireEvent.click(await screen.findByRole('button', { name: /Thu hồi/ }));
    const dialog = await findDialog(/Thu hồi agent "/);
    const ok = within(dialog).getByRole('button', { name: /Thu hồi agent/ });
    expect(ok).toBeDisabled();
    const input = within(dialog).getByRole('textbox');
    fireEvent.change(input, { target: { value: 'Máy tính XN' } });
    expect(ok).toBeDisabled();
    fireEvent.change(input, { target: { value: NAME } });
    expect(ok).toBeEnabled();
    fireEvent.click(ok);
    await waitFor(() => expect(send).toHaveBeenCalledWith('a1', 'revoke'));
  });

  it('thử cổng máy đang dùng → báo máy sẽ tạm ngắt trước khi gửi', async () => {
    vi.spyOn(agentService, 'list').mockResolvedValue([agent()]);
    vi.spyOn(instrumentService, 'list').mockResolvedValue([instrument(), instrument({ id: 'i2', name: 'Máy khác', workstationCode: 'W-zz', agent: null })]);
    const send = vi.spyOn(agentService, 'command').mockResolvedValue(command({ name: 'test-port' }));
    vi.spyOn(agentService, 'commandById').mockResolvedValue(command({ name: 'test-port', status: 'running' }));
    renderDrawer();

    const row = (await screen.findByText('BC-5380')).closest('tr')!;
    expect(screen.queryByText('Máy khác')).toBeNull();
    fireEvent.click(within(row as HTMLElement).getByRole('button', { name: /Thử cổng/ }));

    const dialog = await findDialog(/Thử cổng COM3/);
    expect(within(dialog).getByText(/Máy BC-5380 sẽ tạm ngắt trong 10 giây/)).toBeInTheDocument();
    expect(send).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: /Vẫn thử/ }));
    await waitFor(() => expect(send).toHaveBeenCalledWith('a1', 'test-port', { portName: 'COM3', baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 1, seconds: 10 }));
  });

  it('nối lại một máy gửi lệnh kèm mã máy', async () => {
    vi.spyOn(agentService, 'list').mockResolvedValue([agent()]);
    vi.spyOn(instrumentService, 'list').mockResolvedValue([instrument()]);
    const send = vi.spyOn(agentService, 'command').mockResolvedValue(command({ name: 'reconnect-instrument' }));
    vi.spyOn(agentService, 'commandById').mockResolvedValue(command({ name: 'reconnect-instrument', status: 'done' }));
    renderDrawer();

    const row = (await screen.findByText('BC-5380')).closest('tr')!;
    fireEvent.click(within(row as HTMLElement).getByRole('button', { name: /Nối lại/ }));
    await waitFor(() => expect(send).toHaveBeenCalledWith('a1', 'reconnect-instrument', { instrumentId: 'i1' }));
  });

  it('agent còn bản tin của máy không còn gán → cảnh báo', async () => {
    vi.spyOn(agentService, 'list').mockResolvedValue([agent({ pendingOrphan: 7 })]);
    vi.spyOn(instrumentService, 'list').mockResolvedValue([]);
    renderDrawer();
    expect(await screen.findByText(/Agent còn giữ 7 bản tin của máy không còn gán/)).toBeInTheDocument();
  });

  it('bản cài trên server mới hơn → có nút cập nhật', async () => {
    vi.spyOn(agentService, 'list').mockResolvedValue([agent({ version: '0.1.0' })]);
    vi.spyOn(instrumentService, 'list').mockResolvedValue([]);
    renderDrawer();
    expect(await screen.findByRole('button', { name: /Cập nhật lên 0\.2\.0/ })).toBeEnabled();
  });
});
