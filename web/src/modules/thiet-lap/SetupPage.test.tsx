import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import SetupPage from './SetupPage';

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
  localStorage.clear();
  vi.restoreAllMocks();
});

const renderPage = () =>
  render(
    <MemoryRouter initialEntries={['/thiet-lap']} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <Routes>
        <Route path="/thiet-lap" element={<SetupPage />} />
        <Route path="/dang-nhap" element={<div>trang đăng nhập</div>} />
      </Routes>
    </MemoryRouter>,
  );

const fill = (label: string, value: string) => fireEvent.change(screen.getByLabelText(new RegExp(label)), { target: { value } });

const fillAll = () => {
  fill('Mã thiết lập', ' ma-thiet-lap ');
  fill('Mã phòng khám', ' PKThu ');
  fill('Tên phòng khám', 'Phòng khám Thử');
  fill('Họ tên', 'Quản trị Mẫu');
  fill('Tên đăng nhập', 'admin');
  fill('^Mật khẩu', 'matkhau-thu-1');
  fill('Nhập lại mật khẩu', 'matkhau-thu-1');
};

const jsonResponse = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

describe('SetupPage', () => {
  it('gửi đúng dữ liệu (cắt khoảng trắng, mã chữ thường) rồi về đăng nhập với mã điền sẵn', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse(201, { code: 'pkthu' }));
    renderPage();
    fillAll();
    fireEvent.click(screen.getByRole('button', { name: /Tạo phòng khám/ }));

    await screen.findByText('trang đăng nhập');
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/setup/clinic');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(init?.body as string)).toEqual({
      setupKey: 'ma-thiet-lap',
      code: 'pkthu',
      name: 'Phòng khám Thử',
      adminFullName: 'Quản trị Mẫu',
      adminUsername: 'admin',
      adminPassword: 'matkhau-thu-1',
    });
    expect(JSON.parse(localStorage.getItem('lis-last-login')!)).toEqual({ clinicCode: 'pkthu', username: 'admin' });
  });

  it('hiện lỗi máy chủ ngay tại form và ở lại trang', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse(403, { statusCode: 403, message: 'Mã thiết lập không đúng' }));
    renderPage();
    fillAll();
    fireEvent.click(screen.getByRole('button', { name: /Tạo phòng khám/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Mã thiết lập không đúng');
    expect(screen.queryByText('trang đăng nhập')).not.toBeInTheDocument();
    expect(localStorage.getItem('lis-last-login')).toBeNull();
  });

  it('không gửi khi mã phòng khám sai dạng hoặc mật khẩu ngắn', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch');
    renderPage();
    fillAll();
    fill('Mã phòng khám', 'phòng khám');
    fill('^Mật khẩu', '1234567');
    fireEvent.click(screen.getByRole('button', { name: /Tạo phòng khám/ }));

    expect(await screen.findByText('Chỉ gồm chữ không dấu, số, gạch ngang (2–32 ký tự)')).toBeInTheDocument();
    expect(await screen.findByText('Mật khẩu tối thiểu 8 ký tự')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('không gửi khi hai lần nhập mật khẩu không khớp', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch');
    renderPage();
    fillAll();
    fill('Nhập lại mật khẩu', 'matkhau-thu-2');
    fireEvent.click(screen.getByRole('button', { name: /Tạo phòng khám/ }));

    expect(await screen.findByText('Hai lần nhập mật khẩu không khớp')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('Enter ở một ô chuyển sang ô kế, không gửi form', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch');
    renderPage();
    const key = screen.getByLabelText(/Mã thiết lập/);
    key.focus();
    fireEvent.keyDown(key, { key: 'Enter', code: 'Enter', keyCode: 13 });

    await waitFor(() => expect(screen.getByLabelText(/Mã phòng khám/)).toHaveFocus());
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
