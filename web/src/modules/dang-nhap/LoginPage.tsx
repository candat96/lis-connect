// Đăng nhập theo thiết kế của clinic-web: ảnh nền + sóng xanh ngọc, giới thiệu bên trái, thẻ đăng nhập bên phải.
import { BankOutlined, LockOutlined, UserOutlined } from '@ant-design/icons';
import { Alert, Button, ConfigProvider, Form, Input } from 'antd';
import { useState } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import bgPhoto from '@/assets/login/bg.jpg';
import logo from '@/assets/logo.png';
import { APP_NAME } from '@/app/appName';
import { palette } from '@/app/theme/tokens';
import { authService } from '@/services';
import { useSessionStore } from '@/stores/sessionStore';

interface LoginForm {
  clinicCode: string;
  username: string;
  password: string;
}

const LAST_KEY = 'lis-last-login';
const readLast = (): Partial<LoginForm> => {
  try {
    return JSON.parse(localStorage.getItem(LAST_KEY) ?? '{}') as Partial<LoginForm>;
  } catch {
    return {};
  }
};
// Chỉ nhớ mã phòng khám và tên đăng nhập — không bao giờ nhớ mật khẩu.
const saveLast = (v: LoginForm) => {
  try {
    localStorage.setItem(LAST_KEY, JSON.stringify({ clinicCode: v.clinicCode, username: v.username }));
  } catch {
    /* bỏ qua */
  }
};
const safeNext = (n: unknown) => (typeof n === 'string' && n.startsWith('/') && !n.startsWith('//') && !n.startsWith('/dang-nhap') ? n : '/ket-qua');

/** Đường sóng của thiết kế: một hình, hai lớp lệch góc. */
const WAVE =
  'M1654.41 311.949C931.976 -113.67 250.456 -13.177 0 90.2722V601.296H1654.41C1655.96 507.18 1658.14 317.549 1654.41 311.949Z';

function Wave({ light }: { light?: boolean }) {
  return (
    <svg className={`login-wave ${light ? 'login-wave--light' : 'login-wave--deep'}`} viewBox="0 0 1656.47 601.296" preserveAspectRatio="none" aria-hidden>
      {light && (
        <defs>
          <linearGradient id="login-wave-grad" x1="1265.74" y1="165.928" x2="179.415" y2="150.092" gradientUnits="userSpaceOnUse">
            <stop style={{ stopColor: 'var(--c-hero-deep)' }} />
            <stop offset="1" style={{ stopColor: 'var(--c-hero-light)' }} />
          </linearGradient>
        </defs>
      )}
      <path d={WAVE} style={{ fill: light ? 'url(#login-wave-grad)' : 'var(--c-hero-deep)' }} />
    </svg>
  );
}

export default function LoginPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const { token, setSession } = useSessionStore();
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [last] = useState(readLast);
  const next = safeNext((location.state as { next?: unknown } | null)?.next);

  if (token) return <Navigate to={next} replace />;

  const onFinish = async (v: LoginForm) => {
    setLoading(true);
    setError(null);
    const values = { clinicCode: v.clinicCode.trim(), username: v.username.trim(), password: v.password };
    saveLast(values);
    try {
      const session = await authService.login(values.clinicCode, values.username, values.password);
      setSession(session.token, session.user, session.clinic);
      navigate(next, { replace: true });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  return (
    <div data-screen="dang-nhap" className="login-page" style={{ backgroundImage: `url(${bgPhoto})` }}>
      <Wave light />
      <Wave />

      <header className="login-top">
        <div className="login-brand" aria-label={APP_NAME}>
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 17 }}>
            <img src={logo} alt="" aria-hidden width={56} height={56} style={{ objectFit: 'contain' }} />
            <span style={{ fontSize: 34, fontWeight: 700, letterSpacing: 0.3, lineHeight: 1 }}>{APP_NAME}</span>
          </span>
        </div>
      </header>

      <main className="login-body">
        <section className="login-hero">
          <h1>
            Quản lý xét nghiệm
            <br />
            kết nối thẳng với máy
          </h1>
          <p>Tiếp nhận mẫu, nhận kết quả tự động từ máy xét nghiệm, duyệt và trả kết quả — đồng bộ theo thời gian thực giữa các phòng khám.</p>
        </section>

        {/* Thẻ đăng nhập dùng màu xanh ngọc của thiết kế cho nút / viền chọn */}
        <ConfigProvider theme={{ token: { colorPrimary: palette.heroDeep, borderRadius: 12 } }}>
          <section className="login-card">
            <div className="login-card__head">
              <img src={logo} alt="" aria-hidden width={86} height={86} style={{ objectFit: 'contain' }} />
              <h2>Đăng nhập</h2>
              <p>Dùng tài khoản do quản trị phòng khám cấp.</p>
            </div>

            {error && <Alert type="error" showIcon message={error} style={{ marginBottom: 16 }} role="alert" />}

            <Form<LoginForm>
              layout="vertical"
              onFinish={onFinish}
              size="large"
              initialValues={{ clinicCode: last.clinicCode ?? '', username: last.username ?? '' }}
              // Dấu * đỏ sau nhãn như thiết kế
              requiredMark={(label, { required }) => (
                <>
                  {label}
                  {required && <span className="login-req">*</span>}
                </>
              )}
            >
              <Form.Item name="clinicCode" label="Mã phòng khám" rules={[{ required: true, whitespace: true, message: 'Nhập mã phòng khám' }]}>
                <Input variant="filled" prefix={<BankOutlined />} placeholder="VD: PKSTL" autoComplete="organization" autoCapitalize="none" autoFocus={!last.clinicCode} />
              </Form.Item>
              <Form.Item name="username" label="Tên đăng nhập" rules={[{ required: true, whitespace: true, message: 'Nhập tên đăng nhập' }]}>
                <Input variant="filled" prefix={<UserOutlined />} autoComplete="username" autoCapitalize="none" autoFocus={!!last.clinicCode && !last.username} />
              </Form.Item>
              <Form.Item name="password" label="Mật khẩu" rules={[{ required: true, message: 'Nhập mật khẩu' }]} style={{ marginBottom: 32 }}>
                <Input.Password variant="filled" prefix={<LockOutlined />} autoComplete="current-password" autoFocus={!!last.clinicCode && !!last.username} />
              </Form.Item>
              <Button type="primary" htmlType="submit" block loading={loading}>
                Đăng nhập
              </Button>
            </Form>
          </section>
        </ConfigProvider>
      </main>
    </div>
  );
}
