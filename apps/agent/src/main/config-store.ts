import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'

export interface SerialConfig {
  kind: 'serial'
  path: string
  baudRate: number
  dataBits: 5 | 6 | 7 | 8
  parity: 'none' | 'even' | 'odd'
  stopBits: 1 | 2
}

export interface TcpConfig {
  kind: 'tcp'
  /** 'client': Agent nối tới máy. 'server': Agent lắng nghe, máy nối vào. */
  role: 'client' | 'server'
  host: string
  port: number
}

export interface FileConfig {
  kind: 'file'
  directory: string
  /** Mẫu glob của tên file kết quả, ví dụ '*.txt'. */
  pattern: string
  deleteAfterRead: boolean
}

export type TransportConfig = SerialConfig | TcpConfig | FileConfig

export interface DeviceConfig {
  deviceKey: string
  name: string
  driver: string
  enabled: boolean
  transport: TransportConfig
}

export interface AgentConfig {
  coreUrl: string
  agentKey: string
  proxyUrl: string | null
  hisPort: number
  devices: DeviceConfig[]
}

const DEFAULT_CONFIG: AgentConfig = {
  coreUrl: '',
  agentKey: '',
  proxyUrl: null,
  hisPort: 8080,
  devices: [],
}

/**
 * Cấu hình cục bộ của Agent, lưu dạng JSON.
 *
 * Chọn JSON thay vì SQLite vì người triển khai cần mở được bằng Notepad khi hỗ trợ
 * qua điện thoại và chép được sang máy khác. SQLite chỉ dùng cho hàng đợi và log.
 */
export class ConfigStore {
  constructor(private readonly filePath: string) {}

  load(): AgentConfig {
    if (!existsSync(this.filePath)) return { ...DEFAULT_CONFIG, devices: [] }
    try {
      const parsed = JSON.parse(readFileSync(this.filePath, 'utf8')) as Partial<AgentConfig>
      return {
        coreUrl: parsed.coreUrl ?? DEFAULT_CONFIG.coreUrl,
        agentKey: parsed.agentKey ?? DEFAULT_CONFIG.agentKey,
        proxyUrl: parsed.proxyUrl ?? null,
        hisPort: parsed.hisPort ?? DEFAULT_CONFIG.hisPort,
        devices: parsed.devices ?? [],
      }
    } catch {
      // File hỏng không được làm Agent không khởi động được: nó vẫn phải chạy lên
      // để người dùng mở cửa sổ cấu hình và nhập lại.
      return { ...DEFAULT_CONFIG, devices: [] }
    }
  }

  save(config: AgentConfig): void {
    mkdirSync(dirname(this.filePath), { recursive: true })
    // Ghi ra file tạm rồi đổi tên: mất điện giữa chừng không để lại file hỏng.
    const tempPath = `${this.filePath}.tmp`
    writeFileSync(tempPath, JSON.stringify(config, null, 2), 'utf8')
    renameSync(tempPath, this.filePath)
  }

  upsertDevice(device: DeviceConfig): void {
    const config = this.load()
    const index = config.devices.findIndex((d) => d.deviceKey === device.deviceKey)
    if (index >= 0) config.devices[index] = device
    else config.devices.push(device)
    this.save(config)
  }

  removeDevice(deviceKey: string): void {
    const config = this.load()
    config.devices = config.devices.filter((d) => d.deviceKey !== deviceKey)
    this.save(config)
  }
}
