import type { JSX } from 'react'
import type { DeviceSummary } from '../api.js'
import { StatusBadge, toUiStatus } from '../statusMeta.js'

function formatTime(iso: string | null): string {
  if (!iso) return 'chưa có dữ liệu'
  return new Date(iso).toLocaleString('vi-VN')
}

/**
 * Danh sách máy nhận dữ liệu (devices, loading, refreshFailed) từ App — App
 * là nơi duy nhất polling `GET /api/devices` mỗi 5 giây, để banner cảnh báo
 * ở đầu trang và bảng này luôn đồng bộ, và để không có hai vòng polling cùng
 * gọi một API.
 */
export function DeviceMonitor({
  devices,
  loading,
  refreshFailed,
  onSelect,
}: {
  devices: DeviceSummary[]
  loading: boolean
  refreshFailed: boolean
  onSelect: (id: string) => void
}): JSX.Element {
  return (
    <section className="panel panel-wide">
      <h2>Danh sách máy xét nghiệm</h2>

      {loading && <p className="loading-text">Đang tải danh sách máy...</p>}

      {!loading && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th className="col-status">Trạng thái</th>
                <th>Tên máy</th>
                <th>Mã máy</th>
                <th>Kết nối</th>
                <th>Driver</th>
                <th>Dữ liệu gần nhất</th>
              </tr>
            </thead>
            <tbody>
              {devices.map((device) => {
                const uiStatus = refreshFailed ? 'unknown' : toUiStatus(device.status)
                const isAlert = uiStatus !== 'connected'
                return (
                  <tr
                    key={device.id}
                    className={isAlert ? 'clickable row-alert' : 'clickable'}
                    onClick={() => onSelect(device.id)}
                    tabIndex={0}
                    role="button"
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') onSelect(device.id)
                    }}
                  >
                    <td className="col-status">
                      <StatusBadge status={refreshFailed ? 'unknown' : device.status} />
                    </td>
                    <td className="cell-name">{device.name}</td>
                    <td>
                      <code>{device.deviceKey}</code>
                    </td>
                    <td>{device.transport}</td>
                    <td>{device.driver}</td>
                    <td className="cell-time">{formatTime(device.lastMessageAt)}</td>
                  </tr>
                )
              })}
              {devices.length === 0 && (
                <tr>
                  <td colSpan={6} className="empty-cell">
                    Chưa có máy nào gửi dữ liệu lên. Kiểm tra Agent đã cấu hình và kết nối tới Core
                    chưa.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
