import { useEffect, useRef, useState, type JSX } from 'react'
import type { AgentConfig } from '../main/config-store.js'
import { api, type AgentStatus, type TrafficLine } from './api.js'
import { MAX_LINES_PER_DEVICE, hexPretty, hexToText } from './tabs/TerminalTab.js'

/** Nạp lại danh sách máy định kỳ để phát hiện máy bị gỡ khỏi cấu hình trong
 * lúc cửa sổ này vẫn đang mở (điều 4/4 của spec Task 23). */
const CONFIG_REFRESH_MS = 5000
/** Cùng chu kỳ với dải trạng thái ở cửa sổ chính (App.tsx). */
const STATUS_REFRESH_MS = 2000

type ConnectionStatus = 'connected' | 'disconnected' | 'error' | 'removed' | 'loading'

function statusLabel(status: ConnectionStatus): string {
  switch (status) {
    case 'connected':
      return 'Đang kết nối'
    case 'error':
      return 'Lỗi kết nối'
    case 'removed':
      return 'Không còn trong cấu hình'
    case 'loading':
      return 'Đang kiểm tra...'
    default:
      return 'Mất kết nối'
  }
}

function statusDotClass(status: ConnectionStatus): string {
  if (status === 'connected') return 'dot dot-green'
  if (status === 'loading') return 'dot dot-muted'
  if (status === 'removed') return 'dot dot-amber'
  return 'dot dot-red'
}

function statusItemClass(status: ConnectionStatus): string {
  if (status === 'connected') return 'status-item device-window-status is-ok'
  if (status === 'loading') return 'status-item device-window-status'
  if (status === 'removed') return 'status-item device-window-status is-warn'
  return 'status-item device-window-status is-bad'
}

/**
 * Cửa sổ theo dõi riêng cho MỘT máy (spec Task 23) — mở từ nút "Mở cửa sổ
 * theo dõi" ở tab Máy xét nghiệm. Chỉ hiện: tên máy, đèn trạng thái, luồng
 * byte vào/ra, nút text/hex, nút xoá màn hình. Không có ba tab, không có form
 * cấu hình — đây là cửa sổ để NHÌN, không phải để sửa (xem `App.tsx`, nơi
 * tham số `?device=` quyết định dựng component này thay vì layout ba tab).
 *
 * Bộ đệm 500 dòng của cửa sổ này độc lập hoàn toàn với bộ đệm ở tab Terminal
 * thô của cửa sổ chính lẫn bộ đệm của mọi cửa sổ theo dõi khác — mỗi cửa sổ
 * tự lọc kênh `agent:traffic` theo `deviceKey` của riêng nó.
 */
export function DeviceWindow({ deviceKey }: { deviceKey: string }): JSX.Element {
  const [config, setConfig] = useState<AgentConfig | null>(null)
  const [status, setStatus] = useState<AgentStatus | null>(null)
  const [lines, setLines] = useState<TrafficLine[]>([])
  const [showHex, setShowHex] = useState(false)
  const bottomRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let cancelled = false
    const load = (): void => {
      void api
        .getConfig()
        .then((next) => {
          if (!cancelled) setConfig(next)
        })
        .catch(() => {
          // Không tải được cấu hình không có nghĩa máy đã bị gỡ — giữ trạng
          // thái đã biết trước đó, lần làm mới kế tiếp sẽ thử lại.
        })
    }
    load()
    const timer = setInterval(load, CONFIG_REFRESH_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  useEffect(() => {
    let cancelled = false
    const load = (): void => {
      void api
        .status()
        .then((next) => {
          if (!cancelled) setStatus(next)
        })
        .catch(() => {
          // Tương tự: không rõ trạng thái không phải là "đã mất kết nối".
        })
    }
    load()
    const timer = setInterval(load, STATUS_REFRESH_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  // Lắng nghe kênh `agent:traffic` sẵn có, chỉ giữ lại dòng của đúng máy này
  // — huỷ đăng ký khi unmount để không rò rỉ listener (yêu cầu kỹ thuật).
  useEffect(() => {
    return api.onTraffic((line) => {
      if (line.deviceKey !== deviceKey) return
      setLines((current) => [...current, line].slice(-MAX_LINES_PER_DEVICE))
    })
  }, [deviceKey])

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [lines])

  const device = config?.devices.find((d) => d.deviceKey === deviceKey) ?? null
  const isRemoved = config !== null && device === null
  const deviceName = device?.name ?? deviceKey

  useEffect(() => {
    document.title = `${deviceName} — Theo dõi máy`
  }, [deviceName])

  const statusOf = status?.devices.find((d) => d.deviceKey === deviceKey)?.status
  const connectionStatus: ConnectionStatus = isRemoved
    ? 'removed'
    : status === null
      ? 'loading'
      : statusOf === 'connected'
        ? 'connected'
        : statusOf === 'error'
          ? 'error'
          : 'disconnected'

  return (
    <div className="device-window">
      <header className="device-window-header">
        <div className="device-window-title">
          <h1>{deviceName}</h1>
          <span className="device-window-key">{deviceKey}</span>
        </div>
        <span className={statusItemClass(connectionStatus)}>
          <span className={statusDotClass(connectionStatus)} />
          <span className="value">{statusLabel(connectionStatus)}</span>
        </span>
      </header>

      <div className="device-window-body">
        {isRemoved && (
          <p className="message message-warn" role="status" aria-live="polite">
            Máy <strong>{deviceKey}</strong> đã bị gỡ khỏi cấu hình ở tab &quot;Máy xét nghiệm&quot;.
            Cửa sổ này vẫn hiện log cũ, nhưng sẽ không còn nhận byte mới cho tới khi máy được khai
            báo lại.
          </p>
        )}

        <div className="terminal-toolbar">
          <label className="checkbox">
            <input
              type="checkbox"
              checked={showHex}
              onChange={(e) => setShowHex(e.target.checked)}
            />
            <span>Hiện dạng hex</span>
          </label>
          <button type="button" onClick={() => setLines([])}>
            Xoá màn hình
          </button>
          <span className="terminal-count">{lines.length} dòng</span>
        </div>

        <pre className="terminal device-window-terminal">
          {lines.length === 0 && (
            <div className="terminal-empty">
              {isRemoved
                ? 'Chưa có log cũ nào cho máy này.'
                : `Đang chờ dữ liệu từ ${deviceName}...`}
            </div>
          )}
          {lines.map((line, index) => (
            <div
              key={index}
              className={
                line.direction === 'inbound' ? 'terminal-line line-in' : 'terminal-line line-out'
              }
            >
              <span className="ts">{new Date(line.at).toLocaleTimeString('vi-VN')}</span>
              <span className="dir">{line.direction === 'inbound' ? '<<' : '>>'}</span>
              <span className="payload">{showHex ? hexPretty(line.hex) : hexToText(line.hex)}</span>
            </div>
          ))}
          <div ref={bottomRef} />
        </pre>
      </div>
    </div>
  )
}
