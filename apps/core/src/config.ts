export interface CoreConfig {
  port: number
  adminDatabaseUrl: string
  tenantDatabaseUrlTemplate: string
  corsOrigins: string[]
  /** Kết nối thẳng tới Postgres, bỏ qua PgBouncer. Chỉ CLI quản trị dùng, vì
   *  CREATE DATABASE không chạy được qua transaction pooling. */
  directDatabaseUrlTemplate: string
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
  return {
    port: Number(env.CORE_PORT ?? 3000),
    adminDatabaseUrl,
    tenantDatabaseUrlTemplate,
    corsOrigins,
    directDatabaseUrlTemplate,
  }
}
