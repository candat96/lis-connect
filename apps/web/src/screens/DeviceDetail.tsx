import { useEffect, useState, type JSX } from 'react'
import { api, type ConnectionEventView, type DeviceSummary, type RawMessageView } from '../api.js'
import { StatusBadge } from '../statusMeta.js'

const REFRESH_MS = 5000
/**
 * Core đã giới hạn `limit=100` ở API, nhưng chốt thêm một trần hiển thị ở
 * client — phòng khi giới hạn API đổi trong tương lai — vì đây là màn hình
 * có thể mở liên tục cả ngày và không được để bộ nhớ trình duyệt phình to.
 * Mỗi vòng làm mới THAY THẾ toàn bộ danh sách cũ chứ không nối thêm, nên
 * dung lượng luôn bị chặn bởi trần này.
 */
const MAX_DISPLAYED_MESSAGES = 200

type LoadState = 'loading' | 'ok' | 'error'

export function DeviceDetail({
  deviceId,
  device,
  devicesRefreshFailed,
  onBack,
}: {
  deviceId: string
  device: DeviceSummary | null
  /**
   * true khi vòng polling `GET /api/devices` ở App thất bại gần nhất — huy
   * hiệu trạng thái của máy này (đọc từ `device`, vốn là kết quả lần fetch
   * TRƯỚC ĐÓ) phải bị ép về "không rõ" trong trường hợp này, giống hệt cách
   * DeviceMonitor xử lý, để không hiển thị "Đang kết nối" cũ như thể máy vẫn
   * ổn trong khi banner đầu trang đang báo mất kết nối tới Core.
   */
  devicesRefreshFailed: boolean
  onBack: () => void
}): JSX.Element {
  const [messages, setMessages] = useState<RawMessageView[]>([])
  const [messagesState, setMessagesState] = useState<LoadState>('loading')
  const [events, setEvents] = useState<ConnectionEventView[]>([])
  const [eventsState, setEventsState] = useState<LoadState>('loading')
  const [showHex, setShowHex] = useState(false)

  useEffect(() => {
    setMessagesState('loading')
    setEventsState('loading')
    let cancelled = false

    const load = (): void => {
      api
        .rawMessages(deviceId)
        .then((data) => {
          if (cancelled) return
          setMessages(data.slice(0, MAX_DISPLAYED_MESSAGES))
          setMessagesState('ok')
        })
        .catch(() => {
          // Bắt lỗi rõ ràng thay vì để bảng đứng im — kỹ thuật viên cần biết
          // dữ liệu đang hiển thị có còn cập nhật hay không.
          if (!cancelled) setMessagesState('error')
        })

      api
        .connectionEvents(deviceId)
        .then((data) => {
          if (cancelled) return
          setEvents(data)
          setEventsState('ok')
        })
        .catch(() => {
          if (!cancelled) setEventsState('error')
        })
    }

    load()
    const timer = setInterval(load, REFRESH_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [deviceId])

  return (
    <section className="panel panel-wide">
      <div className="detail-header">
        <button className="back-link" onClick={onBack}>
          ← Quay lại danh sách
        </button>
        <div className="detail-title">
          <h2 className="detail-name">{device?.name ?? 'Máy xét nghiệm'}</h2>
          <StatusBadge status={devicesRefreshFailed ? 'unknown' : (device?.status ?? 'unknown')} size="sm" />
        </div>
      </div>

      <h3 className="section-title">Nhật ký kết nối</h3>
      {eventsState === 'loading' && <p className="loading-text">Đang tải nhật ký kết nối...</p>}
      {eventsState === 'error' && (
        <p className="error">
          Không tải được nhật ký kết nối. Đang tự thử lại mỗi {REFRESH_MS / 1000} giây.
        </p>
      )}
      {eventsState === 'ok' && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Thời điểm</th>
                <th>Trạng thái</th>
                <th>Chi tiết</th>
              </tr>
            </thead>
            <tbody>
              {events.map((event) => (
                <tr key={event.id}>
                  <td className="cell-time">{new Date(event.occurredAt).toLocaleString('vi-VN')}</td>
                  <td>
                    <StatusBadge status={event.status} size="sm" />
                  </td>
                  <td>{event.detail ?? '—'}</td>
                </tr>
              ))}
              {events.length === 0 && (
                <tr>
                  <td colSpan={3} className="empty-cell">
                    Chưa có sự kiện kết nối nào được ghi nhận cho máy này.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      <div className="actions">
        <h3 className="section-title section-title-inline">Dữ liệu thô nhận từ máy</h3>
        <label className="checkbox">
          <input type="checkbox" checked={showHex} onChange={(e) => setShowHex(e.target.checked)} />
          <span>Hiện dạng hex</span>
        </label>
      </div>

      {messagesState === 'error' && (
        <p className="error">
          Không tải được dữ liệu thô — nội dung bên dưới có thể đã cũ. Đang tự thử lại mỗi{' '}
          {REFRESH_MS / 1000} giây.
        </p>
      )}

      <pre className="terminal" aria-live="off">
        {messagesState === 'loading' && <div className="terminal-empty">Đang tải dữ liệu thô...</div>}
        {messagesState !== 'loading' &&
          messages.map((message) => (
            <div key={message.id} className={message.direction === 'inbound' ? 'terminal-line line-in' : 'terminal-line line-out'}>
              <span className="ts">{new Date(message.receivedAt).toLocaleTimeString('vi-VN')}</span>
              <span className="dir">{message.direction === 'inbound' ? '«' : '»'}</span>
              <span className="status">[{message.parseStatus}]</span>
              <span className="payload">
                {showHex
                  ? atob(message.payloadBase64)
                      .split('')
                      .map((c) => c.charCodeAt(0).toString(16).padStart(2, '0'))
                      .join(' ')
                      .toUpperCase()
                  : message.payloadText}
              </span>
            </div>
          ))}
        {messagesState === 'ok' && messages.length === 0 && (
          <div className="terminal-empty">Chưa nhận được dữ liệu nào từ máy này.</div>
        )}
      </pre>
    </section>
  )
}
