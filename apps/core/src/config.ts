export interface CoreConfig {
  port: number
  adminDatabaseUrl: string
  tenantDatabaseUrlTemplate: string
  corsOrigins: string[]
  /** Kết nối thẳng tới Postgres, bỏ qua PgBouncer. Chỉ CLI quản trị dùng, vì
   *  CREATE DATABASE không chạy được qua transaction pooling. */
  directDatabaseUrlTemplate: string
  /** Bí mật ký JWT đăng nhập Web UI. KHÔNG có giá trị mặc định trong mã — xem lý do dưới. */
  jwtSecret: string
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): CoreConfig {
  const adminDatabaseUrl = env.ADMIN_DATABASE_URL
  const tenantDatabaseUrlTemplate = env.TENANT_DATABASE_URL_TEMPLATE
  if (!adminDatabaseUrl) throw new Error('thiếu biến môi trường ADMIN_DATABASE_URL')
  if (!tenantDatabaseUrlTemplate) throw new Error('thiếu biến môi trường TENANT_DATABASE_URL_TEMPLATE')
  if (!tenantDatabaseUrlTemplate.includes('{db}')) {
    throw new Error('TENANT_DATABASE_URL_TEMPLATE phải chứa {db}')
  }
  const corsOrigins = (env.CORS_ORIGINS ?? 'http://localhost:5173')
    .split(',')
    .map((origin) => origin.trim())
    .filter((origin) => origin.length > 0)
  const directDatabaseUrlTemplate = env.DIRECT_DATABASE_URL_TEMPLATE ?? tenantDatabaseUrlTemplate

  // Bắt buộc mọi môi trường (không chỉ production) đặt JWT_SECRET tường minh — KHÔNG
  // đặt giá trị mặc định trong mã: một chuỗi mặc định "để chạy cho nhanh" ký được token
  // hợp lệ ở BẤT KỲ bản triển khai nào cũng lấy đúng chuỗi đó, tức là ai đọc được mã
  // nguồn (mã nguồn không bí mật) đều tự ký được token giả cho bất kỳ tenant nào. Yêu
  // cầu gốc chỉ bắt buộc kiểm tra này khi NODE_ENV=production; ở đây cố ý siết chặt hơn
  // (mọi môi trường) vì AuthModule — nơi phát hành token thật — được nạp trong mọi lần
  // khởi động AppModule kể cả test, nên "phải có secret" là điều kiện cần để ứng dụng
  // chạy được, không riêng gì production.
  const jwtSecret = env.JWT_SECRET
  if (!jwtSecret) {
    if (env.NODE_ENV === 'production') {
      throw new Error('thiếu biến môi trường JWT_SECRET (bắt buộc ở production)')
    }
    throw new Error('thiếu biến môi trường JWT_SECRET')
  }

  return {
    port: Number(env.CORE_PORT ?? 3000),
    adminDatabaseUrl,
    tenantDatabaseUrlTemplate,
    corsOrigins,
    directDatabaseUrlTemplate,
    jwtSecret,
  }
}
