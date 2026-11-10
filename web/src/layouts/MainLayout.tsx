import { ApiOutlined, AppstoreOutlined, DownOutlined, ExperimentOutlined, LogoutOutlined, MenuFoldOutlined, MenuUnfoldOutlined, ShopOutlined } from '@ant-design/icons';
import { useQueryClient } from '@tanstack/react-query';
import { Avatar, Button, Dropdown, Flex, Grid, Layout, Menu, Spin, Typography } from 'antd';
import { Suspense, useState } from 'react';
import { Navigate, Outlet, useLocation, useNavigate } from 'react-router-dom';
import logo from '@/assets/logo.png';
import { useSessionStore } from '@/stores/sessionStore';

// Các màn đã có trên web. Màn chưa chuyển (Tiếp nhận, Tài khoản, Cài đặt) vẫn dùng app Windows.
export const NAV = [
  { key: '/ket-qua', label: 'Kết quả', icon: <ExperimentOutlined /> },
  { key: '/danh-muc', label: 'Danh mục', icon: <AppstoreOutlined /> },
  { key: '/may', label: 'Máy & map kênh', icon: <ApiOutlined /> },
];

const ROLE_LABEL: Record<string, string> = {
  'quan-tri': 'Quản trị', 'ky-thuat-vien': 'Kỹ thuật viên', 'tiep-nhan': 'Tiếp nhận', 'truong-khoa': 'Trưởng khoa',
};

function initials(name: string): string {
  const parts = name.trim().split(/\s+/);
  return (parts.length > 1 ? parts[0][0] + parts[parts.length - 1][0] : name.slice(0, 2)).toUpperCase();
}

/** Khung làm việc theo clinic-web: thanh trên (logo, phòng khám, tài khoản) + menu trái + vùng nội dung. */
export function MainLayout() {
  const { token, user, clinic, clear } = useSessionStore();
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const screens = Grid.useBreakpoint();
  const [collapsed, setCollapsed] = useState(false);

  if (!token || !user) return <Navigate to="/dang-nhap" replace state={{ next: pathname }} />;

  // useBreakpoint trả {} ở lần render đầu: chỉ coi là màn hẹp khi đã đo xong
  const isNarrow = screens.lg === false;
  const siderCollapsed = isNarrow || collapsed;

  const logout = () => {
    clear();
    queryClient.clear(); // không để dữ liệu của phiên trước lọt sang người đăng nhập sau
    navigate('/dang-nhap', { replace: true });
  };

  return (
    <Layout style={{ minHeight: '100%' }}>
      <Layout.Header style={{ borderBottom: '1px solid var(--c-border)', display: 'flex', alignItems: 'center', gap: 16 }}>
        <Flex align="center" gap={10} style={{ cursor: 'pointer' }} onClick={() => navigate('/ket-qua')}>
          <img src={logo} alt="" aria-hidden width={32} height={32} style={{ objectFit: 'contain' }} />
          <Typography.Text strong style={{ fontSize: 16, fontWeight: 700, whiteSpace: 'nowrap' }}>
            {clinic?.name ?? 'Phòng khám'}
          </Typography.Text>
        </Flex>
        <Typography.Text type="secondary" style={{ fontWeight: 600, whiteSpace: 'nowrap' }}>/ Xét nghiệm</Typography.Text>
        <div style={{ flex: 1 }} />
        <Typography.Text type="secondary"><ShopOutlined /> {clinic?.code.toUpperCase()}</Typography.Text>
        <Dropdown trigger={['click']} menu={{ items: [{ key: 'logout', icon: <LogoutOutlined />, danger: true, label: 'Đăng xuất', onClick: logout }] }}>
          <Button type="text" style={{ height: 44 }} aria-label="Tài khoản">
            <Flex align="center" gap={8}>
              <Avatar size={30} style={{ background: 'var(--c-brand-tint)', color: 'var(--c-brand-hover)', fontWeight: 700 }}>
                {initials(user.fullName)}
              </Avatar>
              <Flex vertical align="start" style={{ lineHeight: 1.2 }}>
                <span style={{ fontWeight: 600 }}>{user.fullName}</span>
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {user.roles.map((r) => ROLE_LABEL[r] ?? r).join(', ')}
                </Typography.Text>
              </Flex>
              <DownOutlined style={{ fontSize: 10 }} />
            </Flex>
          </Button>
        </Dropdown>
      </Layout.Header>
      <Layout>
        <Layout.Sider width={240} collapsedWidth={64} collapsed={siderCollapsed} style={{ borderRight: '1px solid var(--c-border)' }}>
          <Menu
            mode="inline"
            className="module-menu"
            items={NAV}
            selectedKeys={NAV.filter((n) => pathname.startsWith(n.key)).map((n) => n.key)}
            onClick={({ key }) => navigate(key)}
            style={{ borderInlineEnd: 'none', padding: 8 }}
          />
          {!isNarrow && (
            <Button type="text" icon={collapsed ? <MenuUnfoldOutlined /> : <MenuFoldOutlined />} onClick={() => setCollapsed((c) => !c)}
              style={{ margin: 8 }} aria-label={collapsed ? 'Mở rộng menu' : 'Thu gọn menu'} />
          )}
        </Layout.Sider>
        <Layout.Content className="module-content" style={{ padding: 24, minWidth: 0 }}>
          <Suspense fallback={<Spin style={{ display: 'block', marginTop: 48 }} />}>
            <Outlet />
          </Suspense>
        </Layout.Content>
      </Layout>
    </Layout>
  );
}
