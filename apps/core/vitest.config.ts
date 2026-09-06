import { config } from 'dotenv'
import { defineConfig } from 'vitest/config'

config({ path: new URL('.env.test', import.meta.url).pathname })

export default defineConfig({
  test: {
    include: ['src/**/*.test.ts', 'test/**/*.test.ts'],
    hookTimeout: 120_000,
    testTimeout: 60_000,
    // Test tích hợp dùng Postgres thật; chạy tuần tự để tránh giẫm chân nhau.
    fileParallelism: false,
  },
})
