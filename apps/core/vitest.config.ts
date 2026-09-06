import { config } from 'dotenv'
import { defineConfig } from 'vitest/config'

config({ path: new URL('.env.test', import.meta.url).pathname })

// Mỗi lần chạy `vitest` phải có bộ database riêng.
//
// `fileParallelism: false` chỉ tuần tự hoá các tệp TRONG một tiến trình vitest.
// Khi có hai tiến trình vitest cùng lúc trên một máy — hai người cùng chạy test,
// hoặc hai tác nhân tự động làm việc song song — cả hai đều đọc cùng một
// ADMIN_DATABASE_URL cố định và cùng gọi `DROP DATABASE ... WITH (FORCE)` trên
// nó. Cú FORCE ngắt thẳng kết nối của tiến trình kia, nên tiến trình đó đỏ với
// những lỗi vô nghĩa: "database does not exist", "Connection terminated
// unexpectedly". Tập ca đỏ đổi khác nhau mỗi lần chạy, và không liên quan gì tới
// đoạn code đang sửa — đúng loại tín hiệu khiến người ta đi sửa nhầm chỗ hàng giờ.
//
// Hậu tố theo tiến trình dưới đây tách hẳn hai lần chạy ra. `helpers.ts` dùng
// `TEST_DB_SUFFIX` cho tên database của từng bệnh viện, còn URL quản trị được
// vá ngay tại đây.
const suffix = `_p${process.pid}`

const adminUrl = new URL(process.env.ADMIN_DATABASE_URL ?? '')
adminUrl.pathname = `${adminUrl.pathname}${suffix}`

export default defineConfig({
  test: {
    include: ['src/**/*.test.ts', 'test/**/*.test.ts'],
    hookTimeout: 120_000,
    testTimeout: 60_000,
    // Test tích hợp dùng Postgres thật; chạy tuần tự để tránh giẫm chân nhau.
    fileParallelism: false,
    env: {
      ADMIN_DATABASE_URL: adminUrl.toString(),
      TEST_DB_SUFFIX: suffix,
    },
  },
})
