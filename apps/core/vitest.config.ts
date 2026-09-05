import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    include: ['src/**/*.test.ts', 'test/**/*.test.ts'],
    hookTimeout: 30_000,
    testTimeout: 30_000,
    // Test tích hợp dùng Postgres thật; chạy tuần tự để tránh giẫm chân nhau.
    fileParallelism: false,
  },
})
