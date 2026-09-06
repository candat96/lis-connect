import { join } from 'node:path'
import { hostname } from 'node:os'
import { BrowserWindow, Tray, app, nativeImage } from 'electron'
import { ConfigStore } from './config-store.js'
import { CoreClient } from './core-client.js'
import { DeviceManager } from './device-manager.js'
import { registerIpc } from './ipc.js'
import { LocalDb } from './local-db.js'
import { Outbox } from './outbox.js'

const AGENT_VERSION = app.getVersion()
/** Chu kỳ kiểm tra hàng đợi có vượt trần cảnh báo hay không. */
const QUEUE_CHECK_INTERVAL_MS = 5 * 60_000

let window: BrowserWindow | null = null
let tray: Tray | null = null
let coreClient: CoreClient | null = null

export const configStore = new ConfigStore(join(app.getPath('userData'), 'config.json'))
const localDb = LocalDb.open(join(app.getPath('userData'), 'agent.sqlite'))
const outbox = new Outbox(localDb)
const deviceManager = new DeviceManager(outbox, () => coreClient?.pump())

/**
 * Cảnh báo lớn khi hàng đợi vượt trần — KHÔNG bao giờ xoá gì (spec 6.2).
 * Kênh trạng thái (`agent:status`, đọc trực tiếp `outbox.isOverThreshold()`
 * trong ipc.ts) đã đủ để giao diện hiện cảnh báo; hàm này chỉ lo phần log,
 * để có dấu vết ngay cả khi không ai đang mở cửa sổ Agent Console.
 */
function checkQueueThreshold(): void {
  if (outbox.isOverThreshold()) {
    console.warn(
      `[outbox] hàng đợi vượt trần cảnh báo (${outbox.depth()} message) — Core có thể đã mất liên lạc lâu, cần người kiểm tra. KHÔNG tự xoá dữ liệu.`,
    )
  }
}

function restartCoreClient(): void {
  const config = configStore.load()
  void coreClient?.stop()
  coreClient = null
  if (!config.coreUrl || !config.agentKey) return

  coreClient = new CoreClient({
    url: config.coreUrl,
    agentKey: config.agentKey,
    agentVersion: AGENT_VERSION,
    hostname: hostname(),
    outbox,
  })
  coreClient.on('status', (status) => {
    window?.webContents.send('agent:core-status', status)
  })
  coreClient.start()
}

function createWindow(): void {
  if (window) {
    window.show()
    return
  }
  window = new BrowserWindow({
    width: 1100,
    height: 720,
    title: 'LIS Connect Agent',
    webPreferences: { preload: join(__dirname, '../preload/index.js'), sandbox: false },
  })
  window.on('closed', () => {
    window = null
  })

  if (process.env.ELECTRON_RENDERER_URL) {
    void window.loadURL(process.env.ELECTRON_RENDERER_URL)
  } else {
    void window.loadFile(join(__dirname, '../renderer/index.html'))
  }
}

app.whenReady().then(async () => {
  registerIpc({
    configStore,
    deviceManager,
    outbox,
    getCoreClient: () => coreClient,
    restartCoreClient,
    getWindow: () => window,
  })

  await deviceManager.applyConfig(configStore.load().devices)
  restartCoreClient()

  checkQueueThreshold()
  setInterval(checkQueueThreshold, QUEUE_CHECK_INTERVAL_MS).unref()

  createWindow()
  tray = new Tray(nativeImage.createEmpty())
  tray.setToolTip('LIS Connect Agent')
  tray.on('click', createWindow)
})

// Đóng cửa sổ KHÔNG thoát ứng dụng: main process phải chạy tiếp để giữ kết nối
// tới máy xét nghiệm. Chỉ thoát khi người dùng chọn thoát từ khay hệ thống.
app.on('window-all-closed', () => {
  // cố ý để trống
})

app.on('before-quit', () => {
  void deviceManager.stopAll()
  void coreClient?.stop()
  localDb.close()
})
