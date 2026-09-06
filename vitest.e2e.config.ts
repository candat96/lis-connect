import { config } from 'dotenv'
import { defineConfig } from 'vitest/config'

config({ path: new URL('apps/core/.env.test', import.meta.url).pathname })

export default defineConfig({
  test: {
    include: ['test/e2e/**/*.test.ts'],
    hookTimeout: 180_000,
    testTimeout: 120_000,
    fileParallelism: false,
  },
})
