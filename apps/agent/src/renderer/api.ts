import type { AgentConfig, DeviceConfig } from '../main/config-store.js'

export interface SerialPortInfo {
  path: string
  manufacturer: string | null
  friendlyName: string | null
}

export interface AgentStatus {
  coreConnected: boolean
  queueDepth: number
  /** true nếu hàng đợi vượt trần cảnh báo — không có nghĩa mất dữ liệu, chỉ cần người kiểm tra. */
  queueOverThreshold: boolean
  devices: Array<{ deviceKey: string; name: string; status: string }>
}

export interface TrafficLine {
  deviceKey: string
  direction: 'inbound' | 'outbound'
  hex: string
  at: string
}

export const api = {
  getConfig: () => window.agent.invoke<AgentConfig>('agent:get-config'),
  saveCoreSettings: (payload: { coreUrl: string; agentKey: string; proxyUrl: string | null }) =>
    window.agent.invoke<AgentConfig>('agent:save-core-settings', payload),
  listSerialPorts: () => window.agent.invoke<SerialPortInfo[]>('agent:list-serial-ports'),
  upsertDevice: (device: DeviceConfig) =>
    window.agent.invoke<AgentConfig>('agent:upsert-device', device),
  removeDevice: (deviceKey: string) =>
    window.agent.invoke<AgentConfig>('agent:remove-device', deviceKey),
  testConnection: (device: DeviceConfig) =>
    window.agent.invoke<{ ok: boolean; detail: string }>('agent:test-connection', device),
  openDeviceWindow: (deviceKey: string) =>
    window.agent.invoke<void>('agent:open-device-window', deviceKey),
  status: () => window.agent.invoke<AgentStatus>('agent:status'),
  onTraffic: (handler: (line: TrafficLine) => void) =>
    window.agent.on('agent:traffic', (payload) => handler(payload as TrafficLine)),
  onCoreStatus: (handler: (status: string) => void) =>
    window.agent.on('agent:core-status', (payload) => handler(payload as string)),
}
