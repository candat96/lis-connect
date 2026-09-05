import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    // Nhiều tệp test trong apps/core dùng chung một database quản trị
    // (ADMIN_DATABASE_URL) và tự DROP/CREATE nó trong `beforeAll`. Khi chạy
    // qua workspace ở gốc repo, Vitest dựng một worker pool DÙNG CHUNG cho
    // mọi project; cấu hình `fileParallelism: false` khai trong
    // apps/core/vitest.config.ts chỉ chi phối lịch chạy nội bộ của project đó
    // chứ không co worker pool chung này về còn 1 luồng. Kết quả: hai tệp
    // test (ví dụ agent-gateway.test.ts và ingest.test.ts) có thể được giao
    // cho hai worker khác nhau và cùng DROP/CREATE database đó một lúc, gây
    // lỗi "duplicate key value violates unique constraint
    // pg_database_datname_index". Khai lại `fileParallelism: false` ở đây co
    // pool dùng chung về đúng 1 worker, buộc toàn bộ test chạy tuần tự.
    fileParallelism: false,
  },
})
