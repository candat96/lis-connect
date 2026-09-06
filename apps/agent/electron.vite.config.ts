import react from '@vitejs/plugin-react'
import { defineConfig, externalizeDepsPlugin } from 'electron-vite'

// `@lis/shared` không được build ra JS trước (package.json của nó trỏ
// `main` thẳng vào `src/index.ts`), nên phải loại nó khỏi danh sách phụ
// thuộc bị externalize để Vite bundle mã nguồn TypeScript của nó vào thẳng
// `out/main` và `out/preload` — nếu không, tiến trình main của Electron sẽ
// `require()` một tệp `.ts` chưa biên dịch và crash ngay khi khởi động.
// `better-sqlite3` và `serialport` là native module, vẫn phải externalize
// bình thường (không được bundle).
const EXTERNALIZE_EXCLUDE = { exclude: ['@lis/shared'] }

export default defineConfig({
  main: { plugins: [externalizeDepsPlugin(EXTERNALIZE_EXCLUDE)] },
  preload: { plugins: [externalizeDepsPlugin(EXTERNALIZE_EXCLUDE)] },
  renderer: { plugins: [react()] },
})
