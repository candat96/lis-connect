// Thiết lập phòng khám: tạo CSDL của phòng khám và tài khoản Quản trị đầu tiên — cần mã thiết lập của máy chủ.
import { BankOutlined, KeyOutlined, LockOutlined, UserOutlined } from '@ant-design/icons';
import { Alert, Button, Col, ConfigProvider, Form, Input, Row } from 'antd';
import { useState, type KeyboardEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import bgPhoto from '@/assets/login/bg.jpg';
import logo from '@/assets/logo.png';
import { APP_NAME } from '@/app/appName';
import { palette } from '@/app/theme/tokens';
import { setupService, type SetupClinicInput } from '@/services';
import { saveLastLogin } from '@/modules/dang-nhap/lastLogin';

// Thứ tự ô nhập: Enter ở ô nào thì sang ô kế, ở ô cuối thì gửi.
const FIELD_ORDER: (keyof SetupClinicInput)[] = ['setupKey', 'code', 'name', 'adminFullName', 'adminUsername', 'adminPassword'];

export default function SetupPage() {
  const navigate = useNavigate();
  const [form] = Form.useForm<SetupClinicInput>();
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const onEnter = (field: keyof SetupClinicInput) => (e: KeyboardEvent<HTMLInputElement>) => {
    const next = FIELD_ORDER[FIELD_ORDER.indexOf(field) + 1];
    if (!next) return; // ô cuối: để form tự gửi
    e.preventDefault();
    form.getFieldInstance(next)?.focus();
  };

  const onFinish = async (v: SetupClinicInput) => {
    setLoading(true);
    setError(null);
    const input: SetupClinicInput = {
      setupKey: v.setupKey.trim(),
      code: v.code.trim().toLowerCase(),
      name: v.name.trim(),
      adminFullName: v.adminFullName.trim(),
      adminUsername: v.adminUsername.trim(),
      adminPassword: v.adminPassword,
    };
    try {
      const created = await setupService.createClinic(input);
      saveLastLogin(created.code, input.adminUsername);
      navigate('/dang-nhap', { replace: true });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  return (
    <div data-screen="thiet-lap" className="login-page" style={{ backgroundImage: `url(${bgPhoto})` }}>
      <header className="login-top">
        <div className="login-brand" aria-label={APP_NAME}>
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 17 }}>
            <img src={logo} alt="" aria-hidden width={56} height={56} style={{ objectFit: 'contain' }} />
            <span style={{ fontSize: 34, fontWeight: 700, letterSpacing: 0.3, lineHeight: 1 }}>{APP_NAME}</span>
          </span>
        </div>
      </header>

      <main className="setup-body">
        <ConfigProvider theme={{ token: { colorPrimary: palette.heroDeep, borderRadius: 12 } }}>
          <section className="login-card setup-card">
            <div className="setup-card__head">
              <h2>Thiết lập phòng khám</h2>
              <p>Tạo cơ sở dữ liệu của phòng khám và tài khoản Quản trị đầu tiên.</p>
            </div>

            {error && <Alert type="error" showIcon message={error} style={{ marginBottom: 16 }} role="alert" />}

            <Form<SetupClinicInput>
              form={form}
              layout="vertical"
              onFinish={onFinish}
              requiredMark={(label, { required }) => (
                <>
                  {label}
                  {required && <span className="login-req">*</span>}
                </>
              )}
            >
              <Form.Item
                name="setupKey"
                label="Mã thiết lập"
                extra="Do người cài máy chủ cung cấp (biến SETUP_KEY, hoặc dòng in trong log lúc máy chủ khởi động)."
                rules={[{ required: true, whitespace: true, message: 'Nhập mã thiết lập' }]}
              >
                <Input.Password variant="filled" prefix={<KeyOutlined />} autoComplete="off" autoFocus onPressEnter={onEnter('setupKey')} />
              </Form.Item>

              <Row gutter={24}>
                <Col xs={24} md={12}>
                  <h3 className="setup-group">Phòng khám</h3>
                  <Form.Item
                    name="code"
                    label="Mã phòng khám"
                    extra="Nhân viên gõ mã này khi đăng nhập."
                    rules={[
                      { required: true, whitespace: true, message: 'Nhập mã phòng khám' },
                      { pattern: /^\s*[A-Za-z0-9-]{2,32}\s*$/, message: 'Chỉ gồm chữ không dấu, số, gạch ngang (2–32 ký tự)' },
                    ]}
                  >
                    <Input variant="filled" prefix={<BankOutlined />} placeholder="vd: pkthu" autoCapitalize="none" autoComplete="off" maxLength={32} onPressEnter={onEnter('code')} />
                  </Form.Item>
                  <Form.Item name="name" label="Tên phòng khám" rules={[{ required: true, whitespace: true, message: 'Nhập tên phòng khám' }]}>
                    <Input variant="filled" placeholder="vd: Phòng khám Thử" autoComplete="organization" maxLength={200} onPressEnter={onEnter('name')} />
                  </Form.Item>
                </Col>
                <Col xs={24} md={12}>
                  <h3 className="setup-group">Tài khoản Quản trị</h3>
                  <Form.Item name="adminFullName" label="Họ tên" rules={[{ required: true, whitespace: true, message: 'Nhập họ tên' }]}>
                    <Input variant="filled" autoComplete="name" maxLength={200} onPressEnter={onEnter('adminFullName')} />
                  </Form.Item>
                  <Form.Item name="adminUsername" label="Tên đăng nhập" rules={[{ required: true, whitespace: true, message: 'Nhập tên đăng nhập' }]}>
                    <Input variant="filled" prefix={<UserOutlined />} autoCapitalize="none" autoComplete="username" maxLength={64} onPressEnter={onEnter('adminUsername')} />
                  </Form.Item>
                  <Form.Item
                    name="adminPassword"
                    label="Mật khẩu"
                    rules={[
                      { required: true, message: 'Nhập mật khẩu' },
                      { min: 8, message: 'Mật khẩu tối thiểu 8 ký tự' },
                    ]}
                  >
                    <Input.Password variant="filled" prefix={<LockOutlined />} autoComplete="new-password" />
                  </Form.Item>
                </Col>
              </Row>

              <div className="setup-actions">
                <Link to="/dang-nhap">Về trang đăng nhập</Link>
                <Button type="primary" htmlType="submit" size="large" loading={loading}>
                  {loading ? 'Đang tạo phòng khám…' : 'Tạo phòng khám'}
                </Button>
              </div>
            </Form>
          </section>
        </ConfigProvider>
      </main>
    </div>
  );
}
