import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
    // Kịch bản driver báo lỗi (vd. bad-checksum) khiến simulator chờ ACK từng
    // frame tới 3s (ACK_TIMEOUT_MS) rồi mới bỏ qua frame kế tiếp — một phiên
    // ASTM nhiều dòng (H/P/O/R/L) trong tình huống này có thể mất tới 15s.
    // Mức 5000ms mặc định của vitest không đủ cho các bài test dùng socket
    // TCP thật (không mock) qua simulator.
    testTimeout: 20000,
  },
})
