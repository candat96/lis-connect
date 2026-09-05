import { join } from 'node:path'
import { BrowserWindow, Tray, app, nativeImage } from 'electron'
import { ConfigStore } from './config-store.js'

let window: BrowserWindow | null = null
let tray: Tray | null = null

export const configStore = new ConfigStore(join(app.getPath('userData'), 'config.json'))

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

app.whenReady().then(() => {
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
