// Vitest tự động dò `vitest.workspace.ts` ở gốc repo bất kể cờ `--config` được
// truyền vào, khiến `vitest run --config vitest.e2e.config.ts` âm thầm bỏ qua
// `test.include` của config đó và chạy lại đúng bộ test của `pnpm test`.
// File rỗng này được truyền qua `--workspace` trong script `test:e2e` để tắt
// việc dò tìm đó, buộc Vitest chỉ dùng đúng `vitest.e2e.config.ts`.
export default []
