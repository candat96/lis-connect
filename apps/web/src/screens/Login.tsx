import { useId, useState, type FormEvent, type JSX } from 'react'
import { login, LoginError } from '../api.js'
import type { Session } from '../api.js'
import { EyeIcon, EyeOffIcon, WarningTriangleIcon } from '../statusMeta.js'

/**
 * Màn hình đăng nhập — thiết kế theo hướng dẫn từ skill ui-ux-pro-max (Task 19):
 *
 *   - Style: Minimalism & Swiss Style (nền sáng, khung rõ, ít trang trí) — cùng
 *     hướng đã dùng cho Device Monitor, giữ nhất quán hệ sinh thái sản phẩm.
 *   - Không dùng lại bảng màu "teal" mà công cụ gợi ý mặc định cho dashboard
 *     chung chung — bảng "Clinical blue + health green + alert red" mà công cụ
 *     đề xuất riêng cho y tế trùng với token xanh dương lâm sàng
 *     (--color-accent #1d4ed8) ĐÃ CÓ trong styles.css từ Task 16, nên giữ
 *     nguyên token cũ thay vì đổi màu — đúng yêu cầu "nhất quán với Device
 *     Monitor hiện có".
 *   - Lỗi đăng nhập: một thông báo DUY NHẤT cho mọi trường hợp sai (không tiết
 *     lộ mã cơ sở/tên đăng nhập có tồn tại hay không — khớp với AuthService
 *     phía Core), hiển thị trong khối role="alert" để trình đọc màn hình đọc
 *     ngay, không chỉ dựa vào viền đỏ.
 *   - Nhãn hiển thị luôn luôn (không placeholder-only), input mật khẩu có nút
 *     hiện/ẩn, autocomplete đúng chuẩn để trình quản lý mật khẩu hoạt động và
 *     KHÔNG chặn dán (yêu cầu accessible-authentication của WCAG 2.2 AA).
 *   - Banner "phiên đã hết hạn" dùng lại đúng class .alert-banner-warn đã có
 *     trong hệ thống (không tạo thêm biến thể banner mới).
 */
export function Login({
  onSuccess,
  notice,
}: {
  onSuccess: (session: Session) => void
  notice: string | null
}): JSX.Element {
  const [tenantCode, setTenantCode] = useState('')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const tenantFieldId = useId()
  const usernameFieldId = useId()
  const passwordFieldId = useId()
  const errorId = useId()

  const handleSubmit = (e: FormEvent): void => {
    e.preventDefault()
    if (submitting) return
    setError(null)
    setSubmitting(true)

    login(tenantCode.trim(), username.trim(), password)
      .then((session) => {
        onSuccess(session)
      })
      .catch((err: unknown) => {
        setError(
          err instanceof LoginError
            ? err.message
            : 'Có lỗi không xác định khi đăng nhập — thử lại sau.',
        )
      })
      .finally(() => setSubmitting(false))
  }

  return (
    <div className="login-page">
      <div className="login-card">
        <div className="login-brand">
          <img
            className="login-brand-logo"
            src="/logo-text.png"
            alt="LIS Connect — phần mềm theo dõi máy xét nghiệm"
            width={162}
            height={54}
          />
          <p>Đăng nhập theo dõi máy xét nghiệm</p>
        </div>

        {notice && (
          <div className="alert-banner alert-banner-warn login-notice" role="status" aria-live="polite">
            <WarningTriangleIcon className="alert-banner-icon" />
            <span>{notice}</span>
          </div>
        )}

        <form className="login-form" onSubmit={handleSubmit} noValidate>
          {error && (
            <div
              id={errorId}
              className="login-error"
              role="alert"
              aria-live="assertive"
            >
              {error}
            </div>
          )}

          <div className="field">
            <label htmlFor={tenantFieldId}>Mã cơ sở</label>
            <input
              id={tenantFieldId}
              name="tenantCode"
              type="text"
              autoComplete="organization"
              autoCapitalize="none"
              autoCorrect="off"
              spellCheck={false}
              placeholder="vd: bva"
              required
              value={tenantCode}
              onChange={(e) => setTenantCode(e.target.value)}
              aria-describedby={error ? errorId : undefined}
            />
          </div>

          <div className="field">
            <label htmlFor={usernameFieldId}>Tên đăng nhập</label>
            <input
              id={usernameFieldId}
              name="username"
              type="text"
              autoComplete="username"
              autoCapitalize="none"
              autoCorrect="off"
              spellCheck={false}
              required
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              aria-describedby={error ? errorId : undefined}
            />
          </div>

          <div className="field">
            <label htmlFor={passwordFieldId}>Mật khẩu</label>
            <div className="password-input">
              <input
                id={passwordFieldId}
                name="password"
                type={showPassword ? 'text' : 'password'}
                autoComplete="current-password"
                required
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                aria-describedby={error ? errorId : undefined}
              />
              <button
                type="button"
                className="password-toggle"
                onClick={() => setShowPassword((v) => !v)}
                aria-label={showPassword ? 'Ẩn mật khẩu' : 'Hiện mật khẩu'}
                aria-pressed={showPassword}
              >
                {showPassword ? <EyeOffIcon /> : <EyeIcon />}
              </button>
            </div>
          </div>

          <button type="submit" className="login-submit" disabled={submitting}>
            {submitting ? 'Đang đăng nhập...' : 'Đăng nhập'}
          </button>
        </form>
      </div>
    </div>
  )
}
