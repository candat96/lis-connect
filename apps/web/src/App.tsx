import { useEffect, useState, type JSX } from 'react'
import { api, logout, session, type DeviceSummary, type Session } from './api.js'
import { CatalogueAnalytes } from './screens/CatalogueAnalytes.js'
import { CatalogueChannelMap } from './screens/CatalogueChannelMap.js'
import { CatalogueResultRules } from './screens/CatalogueResultRules.js'
import { DeviceDetail } from './screens/DeviceDetail.js'
import { DeviceMonitor } from './screens/DeviceMonitor.js'
import { Login } from './screens/Login.js'
import { UnknownDashedIcon, WarningTriangleIcon } from './statusMeta.js'

const REFRESH_MS = 5000

/**
 * Bốn màn hình của Web UI. Chuyển màn hình bằng state chứ không bằng router:
 * ứng dụng chỉ có bốn đích, và thêm một thư viện định tuyến vào đây sẽ đắt hơn
 * phần nó giải quyết. Danh sách máy vẫn do App polling MỘT lần và truyền xuống
 * cả ba màn hình cần nó.
 */
type View = 'devices' | 'analytes' | 'channels' | 'rules'

const VIEW_TABS: Array<{ id: View; label: string }> = [
  { id: 'devices', label: 'Theo dõi máy' },
  { id: 'analytes', label: 'Danh mục xét nghiệm' },
  { id: 'channels', label: 'Ánh xạ mã kênh' },
  { id: 'rules', label: 'Luật ghi đè kết quả' },
]

/**
 * Kết quả một vòng làm mới danh sách máy — sống ở App, không phải trong
 * DeviceMonitor, vì banner cảnh báo "máy đang mất kết nối" phải hiển thị
 * xuyên suốt mọi màn hình (kể cả khi đang xem chi tiết một máy khác), và vì
 * chỉ nên có MỘT vòng polling danh sách máy thay vì mỗi màn hình tự gọi lại.
 */
interface DevicesPollState {
  devices: DeviceSummary[]
  /** true khi lần gọi API gần nhất thất bại — khác hẳn "đang tải" hay "đã có dữ liệu". */
  refreshFailed: boolean
  loading: boolean
}

/**
 * `sessionKey` (rỗng khi chưa đăng nhập, hoặc token hiện tại) là dependency
 * duy nhất của vòng polling: đổi phiên (đăng nhập lại sau khi hết hạn, hoặc
 * đăng nhập tài khoản khác) phải khởi động lại vòng từ đầu, không tiếp tục
 * dùng token cũ đã mất hiệu lực.
 */
function useDevicesPolling(sessionKey: string): DevicesPollState {
  const [state, setState] = useState<DevicesPollState>({
    devices: [],
    refreshFailed: false,
    loading: true,
  })

  useEffect(() => {
    if (!sessionKey) {
      setState({ devices: [], refreshFailed: false, loading: false })
      return
    }
    let cancelled = false

    const load = (): void => {
      api
        .devices()
        .then((devices) => {
          if (cancelled) return
          setState({ devices, refreshFailed: false, loading: false })
        })
        .catch(() => {
          if (cancelled) return
          // Vòng tự làm mới bắt lỗi và chuyển sang "không rõ" — KHÔNG giữ
          // nguyên danh sách cũ như thể mọi máy vẫn đang ổn. Nếu lỗi là do
          // token hết hạn (401), `session.onExpired` (đăng ký ở App) sẽ đưa
          // người dùng về màn hình đăng nhập ngay sau đây — nhánh này chỉ lo
          // phần hiển thị "đang mất kết nối" cho các lỗi mạng/tạm thời khác.
          setState((prev) => ({ ...prev, refreshFailed: true, loading: false }))
        })
    }

    load()
    const timer = setInterval(load, REFRESH_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [sessionKey])

  return state
}

export function App(): JSX.Element {
  const [currentSession, setCurrentSession] = useState<Session | null>(session.get())
  const [view, setView] = useState<View>('devices')
  const [selectedDeviceId, setSelectedDeviceId] = useState<string | null>(null)
  const [authNotice, setAuthNotice] = useState<string | null>(null)
  const { devices, refreshFailed, loading } = useDevicesPolling(currentSession?.token ?? '')

  useEffect(() => {
    // Đăng ký MỘT lần cho toàn app: bất kỳ lời gọi API nào (không chỉ vòng
    // polling danh sách máy) nhận 401 đều phải đưa người dùng về màn hình
    // đăng nhập kèm lý do rõ ràng — không để màn hình trắng hay kẹt "đang
    // tải" khi token hết hạn giữa ca trực.
    return session.onExpired((reason) => {
      setAuthNotice(reason)
      setCurrentSession(null)
      setSelectedDeviceId(null)
      setView('devices')
    })
  }, [])

  const handleLoginSuccess = (value: Session): void => {
    setAuthNotice(null)
    setCurrentSession(value)
  }

  const handleLogout = (): void => {
    logout()
    setCurrentSession(null)
    setSelectedDeviceId(null)
    setView('devices')
  }

  if (!currentSession) {
    return <Login onSuccess={handleLoginSuccess} notice={authNotice} />
  }

  const offlineCount = devices.filter((d) => d.status !== 'connected').length

  return (
    <div className="app">
      <header className="app-header">
        <div className="brand">
          <img
            className="brand-logo"
            src="/logo-text.png"
            alt="LIS Connect — phần mềm theo dõi máy xét nghiệm"
            width={96}
            height={32}
          />
          <p className="brand-tenant">{currentSession.tenant.name}</p>
        </div>
        <div className="user-panel">
          <div className="user-info">
            <span className="user-name">{currentSession.user.fullName}</span>
            <span className="user-role">{currentSession.user.username}</span>
          </div>
          <button type="button" className="logout-button" onClick={handleLogout}>
            Đăng xuất
          </button>
        </div>
      </header>

      <nav className="app-nav" aria-label="Các màn hình">
        {VIEW_TABS.map((tab) => (
          <button
            key={tab.id}
            type="button"
            className="nav-tab"
            aria-current={view === tab.id ? 'page' : undefined}
            onClick={() => {
              setView(tab.id)
              if (tab.id !== 'devices') setSelectedDeviceId(null)
            }}
          >
            {tab.label}
          </button>
        ))}
      </nav>

      {!loading && refreshFailed && (
        <div className="alert-banner alert-banner-warn" role="status" aria-live="polite">
          <UnknownDashedIcon className="alert-banner-icon" />
          <span>
            Không rõ trạng thái các máy — mất kết nối tới máy chủ Core. Đang tự thử lại mỗi{' '}
            {REFRESH_MS / 1000} giây...
          </span>
        </div>
      )}

      {!loading && !refreshFailed && offlineCount > 0 && (
        <div className="alert-banner alert-banner-danger" role="status" aria-live="polite">
          <WarningTriangleIcon className="alert-banner-icon" />
          <span>
            {offlineCount}/{devices.length} MÁY ĐANG MẤT KẾT NỐI — kết quả từ các máy này sẽ
            không về hệ thống cho tới khi được nối lại.
          </span>
        </div>
      )}

      <main className="content">
        {view === 'devices' && !selectedDeviceId && (
          <DeviceMonitor
            devices={devices}
            loading={loading}
            refreshFailed={refreshFailed}
            onSelect={setSelectedDeviceId}
          />
        )}
        {view === 'devices' && selectedDeviceId && (
          <DeviceDetail
            deviceId={selectedDeviceId}
            device={devices.find((d) => d.id === selectedDeviceId) ?? null}
            devicesRefreshFailed={refreshFailed}
            onBack={() => setSelectedDeviceId(null)}
          />
        )}
        {view === 'analytes' && <CatalogueAnalytes />}
        {view === 'channels' && (
          <CatalogueChannelMap devices={devices} devicesLoading={loading} />
        )}
        {view === 'rules' && <CatalogueResultRules devices={devices} devicesLoading={loading} />}
      </main>
    </div>
  )
}
