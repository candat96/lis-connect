import type { ColumnType, Generated } from 'kysely'

export type TenantStatus = 'active' | 'suspended'
export type AgentKeyStatus = 'active' | 'revoked'

export interface TenantsTable {
  id: Generated<string>
  code: string
  name: string
  /** Tên database nghiệp vụ của tenant, ghép vào TENANT_DATABASE_URL_TEMPLATE. */
  database_name: string
  status: TenantStatus
  created_at: Generated<Date>
}

export interface AgentKeysTable {
  id: Generated<string>
  tenant_id: string
  /** Chỉ lưu hash; khoá gốc chỉ hiện một lần lúc cấp phát. */
  key_hash: string
  agent_label: string
  status: AgentKeyStatus
  last_seen_at: ColumnType<Date | null, Date | null | undefined, Date | null>
  created_at: Generated<Date>
}

export interface TenantMigrationsTable {
  tenant_id: string
  migration_name: string
  applied_at: Generated<Date>
}

export interface AdminDatabase {
  tenants: TenantsTable
  agent_keys: AgentKeysTable
  tenant_migrations: TenantMigrationsTable
}
