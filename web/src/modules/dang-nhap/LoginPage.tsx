// Đăng nhập: chữ to, ô nhập lớn, thao tác bằng bàn phím (Enter chuyển ô → Enter đăng nhập). Bố cục như his-web.
import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Form, Input, Typography, type InputRef } from 'antd';
import { BankOutlined, LockOutlined, UserOutlined } from '@ant-design/icons';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import dayjs from 'dayjs';
import { authService } from '@/services';
import { useSessionStore } from '@/stores/sessionStore';
import { tokens } from '@/app/theme/tokens';

const LAST_KEY = 'lis-last-login';
const BIG = 17;
const FIELD_HEIGHT = 48;

interface LoginForm {
  clinicCode: string;
  username: string;
  password: string;
}

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

function useClock() {
  const [now, setNow] = useState(() => dayjs());
  useEffect(() => {
    const t = window.setInterval(() => setNow(dayjs()), 15_000);
    return () => window.clearInterval(t);
  }, []);
  return now;
}

export default function LoginPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const { token, setSession } = useSessionStore();
  const now = useClock();
  const [form] = Form.useForm<LoginForm>();
  const usernameRef = useRef<InputRef>(null);
  const passwordRef = useRef<InputRef>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [capsLock, setCapsLock] = useState(false);
  const [last] = useState(readLast);
  const next = safeNext((location.state as { next?: unknown } | null)?.next);

  useEffect(() => {
    if (last.clinicCode && last.username) passwordRef.current?.focus();
  }, [last]);

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
      form.setFieldValue('password', '');
      passwordRef.current?.focus();
    } finally {
      setLoading(false);
    }
  };

  const onKey = (e: React.KeyboardEvent) => setCapsLock(e.getModifierState?.('CapsLock') ?? false);
  const label = (text: string) => <span style={{ fontSize: BIG - 1, fontWeight: 600 }}>{text}</span>;

  return (
    <div style={{ minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center', background: tokens.colors.bg, padding: tokens.space.lg }}>
      <div data-screen="dang-nhap" className="login-shell">
        <section className="login-brand" style={{ background: tokens.colors.primaryStrong, color: tokens.colors.surface }}>
          <div style={{ fontSize: 44, fontWeight: 800, letterSpacing: 2, lineHeight: 1 }}>LIS</div>
          <div style={{ fontSize: 22, fontWeight: 600, marginTop: tokens.space.md, lineHeight: 1.35 }}>Hệ thống quản lý xét nghiệm</div>
          <div style={{ marginTop: 'auto' }}>
            <div style={{ fontSize: 56, fontWeight: 700, lineHeight: 1, fontVariantNumeric: 'tabular-nums' }}>{now.format('HH:mm')}</div>
            <div style={{ fontSize: BIG, marginTop: tokens.space.sm, textTransform: 'capitalize' }}>{now.locale('vi').format('dddd, DD/MM/YYYY')}</div>
          </div>
        </section>

        <section className="login-form" style={{ background: tokens.colors.surface }}>
          <Typography.Title level={1} style={{ fontSize: 30, margin: 0 }}>Đăng nhập</Typography.Title>
          <Typography.Paragraph style={{ fontSize: BIG, color: tokens.colors.textSecondary, marginTop: tokens.space.sm }}>
            Nhập mã phòng khám, tên đăng nhập và mật khẩu, nhấn <kbd>Enter</kbd> để vào hệ thống.
          </Typography.Paragraph>

          <Form form={form} layout="vertical" requiredMark={false} onFinish={onFinish} size="large" onKeyDown={onKey} onKeyUp={onKey}
            initialValues={{ clinicCode: last.clinicCode ?? '', username: last.username ?? '' }}>
            <Form.Item name="clinicCode" label={label('Mã phòng khám')} rules={[{ required: true, whitespace: true, message: 'Nhập mã phòng khám' }]}>
              <Input prefix={<BankOutlined />} autoFocus={!last.clinicCode} autoComplete="organization" autoCapitalize="none" style={{ height: FIELD_HEIGHT, fontSize: BIG }}
                onPressEnter={(e) => { e.preventDefault(); usernameRef.current?.focus(); }} />
            </Form.Item>
            <Form.Item name="username" label={label('Tên đăng nhập')} rules={[{ required: true, whitespace: true, message: 'Nhập tên đăng nhập' }]}>
              <Input ref={usernameRef} prefix={<UserOutlined />} autoFocus={!!last.clinicCode && !last.username} autoComplete="username" autoCapitalize="none"
                style={{ height: FIELD_HEIGHT, fontSize: BIG }} onPressEnter={(e) => { e.preventDefault(); passwordRef.current?.focus(); }} />
            </Form.Item>
            <Form.Item name="password" label={label('Mật khẩu')} rules={[{ required: true, message: 'Nhập mật khẩu' }]}
              extra={capsLock ? <span style={{ color: tokens.colors.warningText, fontWeight: 600 }}>Caps Lock đang bật</span> : undefined}>
              <Input.Password ref={passwordRef} prefix={<LockOutlined />} autoComplete="current-password" style={{ height: FIELD_HEIGHT, fontSize: BIG }} />
            </Form.Item>
            {error && <Alert type="error" showIcon message={<span style={{ fontSize: BIG - 1 }}>{error}</span>} style={{ marginBottom: tokens.space.lg }} role="alert" />}
            <Button type="primary" htmlType="submit" block loading={loading} style={{ height: FIELD_HEIGHT + 4, fontSize: BIG + 1, fontWeight: 600 }}>
              Đăng nhập
            </Button>
          </Form>
        </section>
      </div>
    </div>
  );
}
