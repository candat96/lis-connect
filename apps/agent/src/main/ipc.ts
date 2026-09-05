import { type BrowserWindow, ipcMain } from 'electron'
import type { AgentConfig, ConfigStore, DeviceConfig } from './config-store.js'
import type { CoreClient } from './core-client.js'
import type { DeviceManager } from './device-manager.js'
import type { Outbox } from './outbox.js'
import { listSerialPorts } from './transports/serial.js'

export interface IpcDeps {
  configStore: ConfigStore
  deviceManager: DeviceManager
  outbox: Outbox
  getCoreClient: () => CoreClient | null
  restartCoreClient: () => void
  getWindow: () => BrowserWindow | null
}

/**
 * Đăng ký toàn bộ kênh IPC nối tiến trình main với cửa sổ giao diện (renderer).
 *
 * Đây là lớp mỏng: mọi logic thật (đọc/ghi cấu hình, mở/đóng kết nối máy) đều
 * nằm ở ConfigStore/DeviceManager, ipc.ts chỉ chuyển tiếp lời gọi.
 */
export function registerIpc(deps: IpcDeps): void {
  ipcMain.handle('agent:get-config', () => deps.configStore.load())

  ipcMain.handle('agent:save-core-settings', (_event, payload: Partial<AgentConfig>) => {
    const config = deps.configStore.load()
    deps.configStore.save({
      ...config,
      coreUrl: payload.coreUrl ?? config.coreUrl,
      agentKey: payload.agentKey ?? config.agentKey,
      proxyUrl: payload.proxyUrl ?? config.proxyUrl,
    })
    deps.restartCoreClient()
    return deps.configStore.load()
  })

  ipcMain.handle('agent:list-serial-ports', () => listSerialPorts())

  ipcMain.handle('agent:upsert-device', async (_event, device: DeviceConfig) => {
    deps.configStore.upsertDevice(device)
    await deps.deviceManager.applyConfig(deps.configStore.load().devices)
    return deps.configStore.load()
  })

  ipcMain.handle('agent:remove-device', async (_event, deviceKey: string) => {
    deps.configStore.removeDevice(deviceKey)
    await deps.deviceManager.applyConfig(deps.configStore.load().devices)
    return deps.configStore.load()
  })

  ipcMain.handle('agent:test-connection', (_event, device: DeviceConfig) =>
    deps.deviceManager.testConnection(device),
  )

  ipcMain.handle('agent:status', () => ({
    coreConnected: deps.getCoreClient()?.isConnected() ?? false,
    queueDepth: deps.outbox.depth(),
    devices: deps.configStore.load().devices.map((d) => ({
      deviceKey: d.deviceKey,
      name: d.name,
      status: deps.deviceManager.statusOf(d.deviceKey),
    })),
  }))

  // Đẩy lưu lượng byte lên renderer cho tab Terminal thô.
  deps.deviceManager.on('traffic', (event) => {
    deps.getWindow()?.webContents.send('agent:traffic', {
      deviceKey: event.deviceKey,
      direction: event.direction,
      hex: event.data.toString('hex'),
      at: new Date().toISOString(),
    })
  })
}
