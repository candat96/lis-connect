import { useEffect, useState, type JSX } from 'react'
import { api, type AgentStatus } from './api.js'
import logoText from './assets/logo-text.png'
import { DeviceWindow } from './DeviceWindow.js'
import { CoreTab } from './tabs/CoreTab.js'
import { DevicesTab } from './tabs/DevicesTab.js'
import { TerminalTab } from './tabs/TerminalTab.js'
import './styles.css'

type TabKey = 'core' | 'devices' | 'terminal'

const TABS: Array<{ key: TabKey; label: string }> = [
  { key: 'core', label: 'Kết nối Core' },
  { key: 'devices', label: 'Máy xét nghiệm' },
  { key: 'terminal', label: 'Terminal thô' },
]

/**
 * Mã máy đọc từ tham số `?device=` (tiến trình main gắn vào khi nạp cửa sổ
 * theo dõi riêng — xem `main/index.ts#loadRenderer`). Đọc một lần lúc module
 * nạp: mỗi cửa sổ Electron là một lần nạp trang riêng biệt, tham số không đổi
 * trong vòng đời cửa sổ đó nên không cần theo dõi qua state/effect.
 */
const deviceKeyParam = new URLSearchParams(window.location.search).get('device')

export function App(): JSX.Element {
  // Cửa sổ theo dõi riêng của một máy: không có ba tab, không có phần cấu
  // hình — chỉ hiện terminal của đúng máy đó (spec Task 23).
  if (deviceKeyParam) {
    return <DeviceWindow deviceKey={deviceKeyParam} />
  }

  return <MainApp />
}

function MainApp(): JSX.Element {
  const [tab, setTab] = useState<TabKey>('core')
  const [status, setStatus] = useState<AgentStatus | null>(null)
  const [statusError, setStatusError] = useState(false)

  useEffect(() => {
    const refresh = (): void => {
      void api
        .status()
        .then((next) => {
          setStatus(next)
          setStatusError(false)
        })
        .catch(() => {
          setStatusError(true)
        })
    }
    refresh()
    const timer = setInterval(refresh, 2000)
    return () => clearInterval(timer)
  }, [])

  const offlineDevices = status?.devices.filter((d) => d.status !== 'connected') ?? []
  const queueDepth = status?.queueDepth ?? 0

  return (
    <div className="app">
      <header className="app-header">
        <img
          className="app-logo"
          src={logoText}
          alt="LIS Connect — Agent Console"
        />

        {/* Ba thứ phải đọc được trong một cái liếc: Core, thiết bị mất kết nối, hàng đợi. */}
        <div className="status-strip" role="status" aria-live="polite">
          <span
            className={`status-item ${
              statusError ? 'is-warn' : status?.coreConnected ? 'is-ok' : 'is-bad'
            }`}
          >
            <span
              className={
                statusError
                  ? 'dot dot-amber'
                  : status
                    ? status.coreConnected
                      ? 'dot dot-green'
                      : 'dot dot-red'
                    : 'dot dot-muted'
              }
            />
            <span className="value">
              {statusError
                ? 'Không rõ trạng thái Core'
                : status === null
                  ? 'Đang kiểm tra...'
                  : status.coreConnected
                    ? 'Đã nối Core'
                    : 'Chưa nối Core'}
            </span>
          </span>

          <span
            className={`status-item ${
              statusError
                ? 'is-warn'
                : status === null
                  ? ''
                  : offlineDevices.length > 0
                    ? 'is-bad'
                    : 'is-ok'
            }`}
          >
            <span
              className={
                statusError
                  ? 'dot dot-amber'
                  : status === null
                    ? 'dot dot-muted'
                    : offlineDevices.length > 0
                      ? 'dot dot-red'
                      : 'dot dot-green'
              }
            />
            <span className="value">
              {statusError
                ? 'Không rõ trạng thái máy'
                : status === null
                  ? 'Đang kiểm tra...'
                  : status.devices.length === 0
                    ? 'Chưa có máy nào'
                    : offlineDevices.length > 0
                      ? `${offlineDevices.length}/${status.devices.length} máy mất kết nối`
                      : `${status.devices.length}/${status.devices.length} máy đã kết nối`}
            </span>
          </span>

          <span className={`status-item ${queueDepth > 0 ? 'is-warn' : ''}`}>
            <span className="label">Hàng đợi:</span>
            <span className="value">{queueDepth}</span>
          </span>
        </div>
      </header>

      <nav className="tabs" role="tablist" aria-label="Các mục cấu hình Agent">
        {TABS.map((item) => (
          <button
            key={item.key}
            role="tab"
            aria-selected={tab === item.key}
            className={tab === item.key ? 'tab tab-active' : 'tab'}
            onClick={() => setTab(item.key)}
          >
            {item.label}
          </button>
        ))}
      </nav>

      <main className="content">
        {tab === 'core' && <CoreTab status={status} />}
        {tab === 'devices' && <DevicesTab status={status} />}
        {tab === 'terminal' && <TerminalTab />}
      </main>
    </div>
  )
}
