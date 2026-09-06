import { useEffect, useState, type JSX } from 'react'
import { api, tenant, type DeviceSummary } from './api.js'
import { DeviceDetail } from './screens/DeviceDetail.js'
import { DeviceMonitor } from './screens/DeviceMonitor.js'
import { UnknownDashedIcon, WarningTriangleIcon } from './statusMeta.js'

const REFRESH_MS = 5000

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

function useDevicesPolling(tenantId: string): DevicesPollState {
  const [state, setState] = useState<DevicesPollState>({
    devices: [],
    refreshFailed: false,
    loading: true,
  })

  useEffect(() => {
    if (!tenantId) {
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
          // nguyên danh sách cũ như thể mọi máy vẫn đang ổn.
          setState((prev) => ({ ...prev, refreshFailed: true, loading: false }))
        })
    }

    load()
    const timer = setInterval(load, REFRESH_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [tenantId])

  return state
}

export function App(): JSX.Element {
  const [tenantId, setTenantId] = useState(tenant.get())
  const [selectedDeviceId, setSelectedDeviceId] = useState<string | null>(null)
  const { devices, refreshFailed, loading } = useDevicesPolling(tenantId)

  const applyTenant = (value: string): void => {
    tenant.set(value)
    setTenantId(value)
    setSelectedDeviceId(null)
  }

  const offlineCount = devices.filter((d) => d.status !== 'connected').length

  return (
    <div className="app">
      <header className="app-header">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">
            LIS
          </span>
          <div className="brand-text">
            <h1>LIS Connect</h1>
            <p>Theo dõi máy xét nghiệm</p>
          </div>
        </div>
        <label className="tenant-picker">
          <span>Mã cơ sở (tenant)</span>
          <input
            value={tenantId}
            placeholder="dán tenant id"
            onChange={(e) => applyTenant(e.target.value)}
          />
        </label>
      </header>

      {tenantId && !loading && refreshFailed && (
        <div className="alert-banner alert-banner-warn" role="status" aria-live="polite">
          <UnknownDashedIcon className="alert-banner-icon" />
          <span>
            Không rõ trạng thái các máy — mất kết nối tới máy chủ Core. Đang tự thử lại mỗi{' '}
            {REFRESH_MS / 1000} giây...
          </span>
        </div>
      )}

      {tenantId && !loading && !refreshFailed && offlineCount > 0 && (
        <div className="alert-banner alert-banner-danger" role="status" aria-live="polite">
          <WarningTriangleIcon className="alert-banner-icon" />
          <span>
            {offlineCount}/{devices.length} MÁY ĐANG MẤT KẾT NỐI — kết quả từ các máy này sẽ
            không về hệ thống cho tới khi được nối lại.
          </span>
        </div>
      )}

      <main className="content">
        {!tenantId && <p className="empty">Nhập tenant id để xem dữ liệu.</p>}
        {tenantId && !selectedDeviceId && (
          <DeviceMonitor
            devices={devices}
            loading={loading}
            refreshFailed={refreshFailed}
            onSelect={setSelectedDeviceId}
          />
        )}
        {tenantId && selectedDeviceId && (
          <DeviceDetail
            deviceId={selectedDeviceId}
            device={devices.find((d) => d.id === selectedDeviceId) ?? null}
            devicesRefreshFailed={refreshFailed}
            onBack={() => setSelectedDeviceId(null)}
          />
        )}
      </main>
    </div>
  )
}
