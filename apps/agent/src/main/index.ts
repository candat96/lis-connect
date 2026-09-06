import { join } from 'node:path'
import { hostname } from 'node:os'
import { BrowserWindow, Menu, Tray, app, nativeImage } from 'electron'
import { ConfigStore } from './config-store.js'
import { CoreClient } from './core-client.js'
import { DeviceManager } from './device-manager.js'
import { registerIpc } from './ipc.js'
import { LocalDb } from './local-db.js'
import { Outbox } from './outbox.js'

const AGENT_VERSION = app.getVersion()
/** Chu kỳ kiểm tra hàng đợi có vượt trần cảnh báo hay không. */
const QUEUE_CHECK_INTERVAL_MS = 5 * 60_000

/**
 * Đường dẫn tới tài nguyên tĩnh (icon...) khác nhau giữa chế độ dev và bản
 * đóng gói: lúc dev, `__dirname` là `out/main` nên tài nguyên nằm ở
 * `../../build/<name>` (kế bên `apps/agent/build/`); khi đóng gói,
 * `electron-builder.yml` sao các tệp này vào `resources/` qua `extraResources`
 * nên phải đọc từ `process.resourcesPath`.
 */
function resolveAsset(name: string): string {
  return app.isPackaged
    ? join(process.resourcesPath, name)
    : join(__dirname, '../../build', name)
}

let window: BrowserWindow | null = null
let tray: Tray | null = null
let coreClient: CoreClient | null = null
/**
 * Cửa sổ theo dõi riêng cho từng máy (spec Task 23): mở "Máy xét nghiệm" →
 * nút "Mở cửa sổ theo dõi" → một BrowserWindow độc lập, không phải tab con
 * của cửa sổ chính. Map mã máy → cửa sổ để mở lại thì đưa lên trước
 * (`focus()`) thay vì mở trùng; dọn khỏi map khi cửa sổ đóng.
 */
const deviceWindows = new Map<string, BrowserWindow>()

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

/**
 * Nạp cùng một giao diện renderer cho cửa sổ chính lẫn cửa sổ theo dõi máy,
 * chỉ khác tham số `device`. Renderer đọc tham số này (query string ở URL dev,
 * hoặc tuỳ chọn `search` của `loadFile` ở bản dựng) để tự quyết định dựng chế
 * độ ba tab hay chế độ cửa sổ đơn — tiến trình main không biết gì về nội dung
 * giao diện, chỉ truyền tham số.
 */
function loadRenderer(target: BrowserWindow, deviceKey?: string): void {
  if (process.env.ELECTRON_RENDERER_URL) {
    const url = new URL(process.env.ELECTRON_RENDERER_URL)
    if (deviceKey) url.searchParams.set('device', deviceKey)
    void target.loadURL(url.toString())
  } else if (deviceKey) {
    void target.loadFile(join(__dirname, '../renderer/index.html'), {
      search: `device=${encodeURIComponent(deviceKey)}`,
    })
  } else {
    void target.loadFile(join(__dirname, '../renderer/index.html'))
  }
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
    icon: resolveAsset('icon.png'),
    webPreferences: { preload: join(__dirname, '../preload/index.js'), sandbox: false },
  })
  window.on('closed', () => {
    window = null
  })

  loadRenderer(window)
}

/**
 * Mở cửa sổ theo dõi riêng cho một máy (nút "Mở cửa sổ theo dõi" ở tab Máy
 * xét nghiệm). Máy đã có cửa sổ thì đưa cửa sổ đó lên trước thay vì mở trùng.
 * Cửa sổ này KHÔNG có `parent` — cố ý, để kỹ thuật viên kéo tự do sang màn
 * hình khác hoặc xếp cạnh cửa sổ chính, không bị neo theo cửa sổ chính.
 */
function openDeviceWindow(deviceKey: string): void {
  const existing = deviceWindows.get(deviceKey)
  if (existing) {
    existing.focus()
    return
  }

  const deviceWindow = new BrowserWindow({
    // 480px rộng để 3-4 cửa sổ xếp vừa cạnh nhau trên màn hình 1920px.
    width: 480,
    height: 640,
    title: `Theo dõi máy — ${deviceKey}`,
    icon: resolveAsset('icon.png'),
    webPreferences: { preload: join(__dirname, '../preload/index.js'), sandbox: false },
  })
  deviceWindow.on('closed', () => {
    deviceWindows.delete(deviceKey)
  })

  loadRenderer(deviceWindow, deviceKey)
  deviceWindows.set(deviceKey, deviceWindow)
}

app.whenReady().then(async () => {
  registerIpc({
    configStore,
    deviceManager,
    outbox,
    getCoreClient: () => coreClient,
    restartCoreClient,
    getWindow: () => window,
    openDeviceWindow,
  })

  await deviceManager.applyConfig(configStore.load().devices)
  restartCoreClient()

  checkQueueThreshold()
  setInterval(checkQueueThreshold, QUEUE_CHECK_INTERVAL_MS).unref()

  createWindow()
  tray = new Tray(nativeImage.createFromPath(resolveAsset('tray.png')))
  tray.setToolTip('LIS Connect Agent')
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: 'Mở cửa sổ', click: createWindow },
      { type: 'separator' },
      { label: 'Thoát', click: () => app.quit() },
    ]),
  )
  // Khay hệ thống là cách duy nhất mở lại cửa sổ sau khi đóng (xem
  // `window-all-closed` bên dưới) — click trái mở nhanh, click phải hiện menu
  // ngữ cảnh (setContextMenu ở trên) kèm lối thoát ứng dụng thật sự.
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
