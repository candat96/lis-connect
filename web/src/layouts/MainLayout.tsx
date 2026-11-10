import { Avatar, Button, Dropdown, Menu, Space, Tag, Typography } from 'antd';
import { ApiOutlined, AppstoreOutlined, ExperimentOutlined, UserOutlined } from '@ant-design/icons';
import { useQueryClient } from '@tanstack/react-query';
import { Navigate, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { tokens } from '@/app/theme/tokens';
import { useSessionStore } from '@/stores/sessionStore';

// Các màn đã có trên web. Màn chưa chuyển (Tiếp nhận, Tài khoản, Cài đặt) vẫn dùng app Windows.
export const NAV = [
  { key: '/ket-qua', label: 'Kết quả', icon: <ExperimentOutlined aria-hidden /> },
  { key: '/danh-muc', label: 'Danh mục', icon: <AppstoreOutlined aria-hidden /> },
  { key: '/may', label: 'Máy & map kênh', icon: <ApiOutlined aria-hidden /> },
];

// Khung làm việc chính: thanh trên (logo, menu ngang, phòng khám, tài khoản) + nội dung chiếm hết màn.
export function MainLayout() {
  const { token, user, clinic, clear } = useSessionStore();
  const location = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  if (!token || !user) return <Navigate to="/dang-nhap" replace state={{ next: location.pathname }} />;

  const logout = () => {
    clear();
    queryClient.clear(); // không để dữ liệu của phiên trước lọt sang người đăng nhập sau
    navigate('/dang-nhap', { replace: true });
  };

  return (
    <div style={{ height: '100vh', display: 'flex', flexDirection: 'column', background: tokens.colors.bg }}>
      <header
        style={{ height: tokens.layout.topBarHeight, display: 'flex', alignItems: 'center', gap: tokens.space.sm, padding: `0 ${tokens.space.md}px`,
          background: tokens.colors.surface, borderBottom: `1px solid ${tokens.colors.border}`, flex: 'none' }}
      >
        <Typography.Text strong style={{ color: tokens.colors.primaryStrong, fontSize: tokens.font.sizeXl, cursor: 'pointer' }} onClick={() => navigate('/ket-qua')}>
          LIS
        </Typography.Text>
        <nav aria-label="Menu chính" style={{ flex: 1, minWidth: 0 }}>
          <Menu
            mode="horizontal"
            selectedKeys={NAV.filter((n) => location.pathname.startsWith(n.key)).map((n) => n.key)}
            items={NAV}
            onClick={({ key }) => navigate(key)}
            style={{ borderBottom: 0, lineHeight: `${tokens.layout.topBarHeight - 2}px`, background: 'transparent', fontWeight: 600 }}
          />
        </nav>
        {clinic && <Tag color="blue" style={{ marginInlineEnd: 0 }}>{clinic.name}</Tag>}
        <Dropdown trigger={['click']} menu={{ items: [{ key: 'logout', label: 'Đăng xuất', danger: true }], onClick: logout }}>
          <Button type="text" aria-label="Tài khoản">
            <Space size={4}>
              <Avatar size="small" icon={<UserOutlined />} />
              <span>{user.fullName}</span>
            </Space>
          </Button>
        </Dropdown>
      </header>
      <main style={{ flex: 1, minHeight: 0, padding: tokens.space.md, overflow: 'hidden' }}>
        <Outlet />
      </main>
    </div>
  );
}
