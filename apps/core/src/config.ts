export interface CoreConfig {
  port: number
  adminDatabaseUrl: string
  tenantDatabaseUrlTemplate: string
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): CoreConfig {
  const adminDatabaseUrl = env.ADMIN_DATABASE_URL
  const tenantDatabaseUrlTemplate = env.TENANT_DATABASE_URL_TEMPLATE
  if (!adminDatabaseUrl) throw new Error('thiếu biến môi trường ADMIN_DATABASE_URL')
  if (!tenantDatabaseUrlTemplate) throw new Error('thiếu biến môi trường TENANT_DATABASE_URL_TEMPLATE')
  if (!tenantDatabaseUrlTemplate.includes('{db}')) {
    throw new Error('TENANT_DATABASE_URL_TEMPLATE phải chứa {db}')
  }
  return {
    port: Number(env.CORE_PORT ?? 3000),
    adminDatabaseUrl,
    tenantDatabaseUrlTemplate,
  }
}
