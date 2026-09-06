import { useEffect, useState, type JSX } from 'react'
import type {
  DeviceConfig,
  FileConfig,
  SerialConfig,
  TcpConfig,
  TransportConfig,
} from '../../main/config-store.js'
import { api, type AgentStatus, type SerialPortInfo } from '../api.js'

const EMPTY_DEVICE: DeviceConfig = {
  deviceKey: '',
  name: '',
  driver: 'astm-e1394',
  enabled: true,
  transport: { kind: 'serial', path: '', baudRate: 9600, dataBits: 8, parity: 'none', stopBits: 1 },
}

function statusLabel(status: string): string {
  if (status === 'connected') return 'Đang kết nối'
  if (status === 'error') return 'Lỗi'
  return 'Mất kết nối'
}

function statusBadgeClass(status: string): string {
  if (status === 'connected') return 'badge badge-ok'
  if (status === 'error') return 'badge badge-bad'
  return 'badge badge-bad'
}

export function DevicesTab({ status }: { status: AgentStatus | null }): JSX.Element {
  const [loading, setLoading] = useState(true)
  const [devices, setDevices] = useState<DeviceConfig[]>([])
  const [ports, setPorts] = useState<SerialPortInfo[]>([])
  const [draft, setDraft] = useState<DeviceConfig>(EMPTY_DEVICE)
  const [saving, setSaving] = useState(false)
  const [testing, setTesting] = useState(false)
  const [testResult, setTestResult] = useState<{ ok: boolean; text: string } | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionMessage, setActionMessage] = useState<{ kind: 'bad'; text: string } | null>(null)

  useEffect(() => {
    void Promise.all([api.getConfig(), api.listSerialPorts()])
      .then(([config, portList]) => {
        setDevices(config.devices)
        setPorts(portList)
      })
      .catch(() => {
        setLoadError(
          'Không tải được danh sách máy hoặc cổng COM. Xem tab Terminal thô để biết chi tiết, hoặc khởi động lại Agent.',
        )
      })
      .finally(() => {
        setLoading(false)
      })
  }, [])

  const updateTransport = (transport: TransportConfig): void =>
    setDraft((current) => ({ ...current, transport }))

  const save = async (): Promise<void> => {
    setSaving(true)
    setActionMessage(null)
    try {
      const config = await api.upsertDevice(draft)
      setDevices(config.devices)
      setDraft(EMPTY_DEVICE)
      setTestResult(null)
    } catch {
      setActionMessage({
        kind: 'bad',
        text: 'Lưu máy thất bại. Máy chưa được lưu, kiểm tra lại thông tin rồi thử lại.',
      })
    } finally {
      setSaving(false)
    }
  }

  const remove = async (deviceKey: string): Promise<void> => {
    setActionMessage(null)
    try {
      const config = await api.removeDevice(deviceKey)
      setDevices(config.devices)
    } catch {
      setActionMessage({
        kind: 'bad',
        text: 'Xoá máy thất bại. Máy vẫn còn trong danh sách, hãy thử lại.',
      })
    }
  }

  const test = async (): Promise<void> => {
    setTesting(true)
    setTestResult(null)
    try {
      const result = await api.testConnection(draft)
      setTestResult({ ok: result.ok, text: result.ok ? `Thành công: ${result.detail}` : `Thất bại: ${result.detail}` })
    } catch {
      setTestResult({
        ok: false,
        text: 'Không thực hiện được lệnh test kết nối (lỗi Agent, không phải lỗi máy). Xem tab Terminal thô để biết chi tiết.',
      })
    } finally {
      setTesting(false)
    }
  }

  return (
    <section>
      <div className="panel">
        <h2>Máy xét nghiệm đã khai báo</h2>

        {actionMessage && (
          <p className="message message-bad" role="status" aria-live="polite">
            {actionMessage.text}
          </p>
        )}

        {loading ? (
          <p className="loading-text">Đang tải danh sách máy...</p>
        ) : loadError ? (
          <p className="message message-bad" role="status" aria-live="polite">
            {loadError}
          </p>
        ) : (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Tên</th>
                  <th>Mã máy</th>
                  <th>Kết nối</th>
                  <th>Trạng thái</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {devices.map((device) => {
                  const current =
                    status?.devices.find((d) => d.deviceKey === device.deviceKey)?.status ??
                    'disconnected'
                  const isProblem = current !== 'connected'
                  return (
                    <tr key={device.deviceKey} className={isProblem ? 'row-alert' : ''}>
                      <td>{device.name}</td>
                      <td>{device.deviceKey}</td>
                      <td>{device.transport.kind}</td>
                      <td>
                        <span className={statusBadgeClass(current)}>
                          <span className={current === 'connected' ? 'dot dot-green' : 'dot dot-red'} />
                          {statusLabel(current)}
                        </span>
                      </td>
                      <td>
                        <button className="danger" onClick={() => void remove(device.deviceKey)}>
                          Xoá
                        </button>
                      </td>
                    </tr>
                  )
                })}
                {devices.length === 0 && (
                  <tr>
                    <td colSpan={5} className="empty-cell">
                      Chưa khai báo máy nào. Điền biểu mẫu bên dưới để thêm máy đầu tiên.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div className="panel">
        <h2>Thêm hoặc sửa máy</h2>

        <label className="field">
          <span>Mã máy (không dấu, dùng để nhận diện)</span>
          <input
            value={draft.deviceKey}
            placeholder="may-sinh-hoa"
            onChange={(e) => setDraft({ ...draft, deviceKey: e.target.value })}
          />
        </label>

        <label className="field">
          <span>Tên hiển thị</span>
          <input
            value={draft.name}
            placeholder="Máy sinh hóa BS-240"
            onChange={(e) => setDraft({ ...draft, name: e.target.value })}
          />
        </label>

        <label className="field">
          <span>Kiểu kết nối</span>
          <select
            value={draft.transport.kind}
            onChange={(e) => {
              const kind = e.target.value as TransportConfig['kind']
              if (kind === 'serial') {
                updateTransport({
                  kind: 'serial',
                  path: ports[0]?.path ?? '',
                  baudRate: 9600,
                  dataBits: 8,
                  parity: 'none',
                  stopBits: 1,
                })
              } else if (kind === 'tcp') {
                updateTransport({ kind: 'tcp', role: 'client', host: '192.168.1.50', port: 9100 })
              } else {
                updateTransport({
                  kind: 'file',
                  directory: '',
                  pattern: '*.txt',
                  deleteAfterRead: false,
                })
              }
            }}
          >
            <option value="serial">Serial (cổng COM)</option>
            <option value="tcp">TCP/IP</option>
            <option value="file">Thư mục file</option>
          </select>
        </label>

        {draft.transport.kind === 'serial' && (
          <SerialFields transport={draft.transport} ports={ports} onChange={updateTransport} />
        )}

        {draft.transport.kind === 'tcp' && (
          <TcpFields transport={draft.transport} onChange={updateTransport} />
        )}

        {draft.transport.kind === 'file' && (
          <FileFields transport={draft.transport} onChange={updateTransport} />
        )}

        <div className="actions">
          <button disabled={testing} onClick={() => void test()}>
            {testing ? 'Đang thử...' : 'Test kết nối'}
          </button>
          <button className="primary" disabled={saving} onClick={() => void save()}>
            {saving ? 'Đang lưu...' : 'Lưu máy'}
          </button>
        </div>

        {testResult && (
          <p
            className={testResult.ok ? 'message message-ok' : 'message message-bad'}
            role="status"
            aria-live="polite"
          >
            {testResult.text}
          </p>
        )}
      </div>
    </section>
  )
}

/*
 * Ba component con dưới đây nhận `transport` đã thu hẹp kiểu cụ thể (SerialConfig /
 * TcpConfig / FileConfig) thay vì union `TransportConfig`. Khai báo union thì việc
 * lan truyền qua onChange đóng gói bên trong JSX điều kiện (draft.transport.kind === '...')
 * khiến TypeScript không giữ được phần thu hẹp kiểu bên trong closure, mỗi lần spread
 * `{ ...draft.transport, kind: '...' }` lại thiếu các trường riêng của từng loại kết nối.
 * Tách thành hàm nhận tham số kiểu cụ thể giải quyết việc đó mà không đổi hành vi.
 */

function SerialFields({
  transport,
  ports,
  onChange,
}: {
  transport: SerialConfig
  ports: SerialPortInfo[]
  onChange: (transport: SerialConfig) => void
}): JSX.Element {
  return (
    <>
      <label className="field">
        <span>Cổng COM</span>
        <select
          value={transport.path}
          onChange={(e) => onChange({ ...transport, path: e.target.value })}
        >
          <option value="">-- chọn cổng --</option>
          {ports.map((port) => (
            <option key={port.path} value={port.path}>
              {port.path}
              {port.friendlyName ? ` — ${port.friendlyName}` : ''}
            </option>
          ))}
        </select>
        {ports.length === 0 && (
          <span className="loading-text">
            Không tìm thấy cổng COM nào. Cắm cáp máy rồi bấm lại tab này.
          </span>
        )}
      </label>
      <label className="field">
        <span>Baudrate</span>
        <select
          value={transport.baudRate}
          onChange={(e) => onChange({ ...transport, baudRate: Number(e.target.value) })}
        >
          {[1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200].map((rate) => (
            <option key={rate} value={rate}>
              {rate}
            </option>
          ))}
        </select>
      </label>
    </>
  )
}

function TcpFields({
  transport,
  onChange,
}: {
  transport: TcpConfig
  onChange: (transport: TcpConfig) => void
}): JSX.Element {
  return (
    <>
      <label className="field">
        <span>Vai trò</span>
        <select
          value={transport.role}
          onChange={(e) =>
            onChange({ ...transport, role: e.target.value as 'client' | 'server' })
          }
        >
          <option value="client">Agent nối tới máy</option>
          <option value="server">Máy nối vào Agent</option>
        </select>
      </label>
      <label className="field">
        <span>Địa chỉ IP</span>
        <input
          value={transport.host}
          onChange={(e) => onChange({ ...transport, host: e.target.value })}
        />
      </label>
      <label className="field">
        <span>Cổng</span>
        <input
          type="number"
          value={transport.port}
          onChange={(e) => onChange({ ...transport, port: Number(e.target.value) })}
        />
      </label>
    </>
  )
}

function FileFields({
  transport,
  onChange,
}: {
  transport: FileConfig
  onChange: (transport: FileConfig) => void
}): JSX.Element {
  return (
    <>
      <label className="field">
        <span>Thư mục</span>
        <input
          value={transport.directory}
          placeholder="D:\ketqua"
          onChange={(e) => onChange({ ...transport, directory: e.target.value })}
        />
      </label>
      <label className="field">
        <span>Mẫu tên file</span>
        <input
          value={transport.pattern}
          onChange={(e) => onChange({ ...transport, pattern: e.target.value })}
        />
      </label>
      <label className="field checkbox">
        <input
          type="checkbox"
          checked={transport.deleteAfterRead}
          onChange={(e) => onChange({ ...transport, deleteAfterRead: e.target.checked })}
        />
        <span>Xoá file sau khi đọc</span>
      </label>
    </>
  )
}
