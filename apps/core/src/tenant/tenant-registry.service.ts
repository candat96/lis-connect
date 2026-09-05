import { Injectable, type OnModuleDestroy } from '@nestjs/common'
import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import type { AdminDatabase } from '../admin-db/schema.js'
import type { TenantDatabase } from './tenant-schema.js'

interface Entry {
  db: Kysely<TenantDatabase>
  lastUsedAt: number
}

/**
 * Cửa duy nhất để lấy connection tới DB nghiệp vụ của một tenant.
 *
 * Pool được tạo lười và đóng lại khi nhàn rỗi: với hàng chục tenant, mỗi tenant
 * một pool đầy đủ sẽ ăn hết số kết nối Postgres cho phép.
 */
@Injectable()
export class TenantRegistryService implements OnModuleDestroy {
  private readonly entries = new Map<string, Entry>()

  constructor(
    private readonly admin: Kysely<AdminDatabase>,
    private readonly urlTemplate: string,
  ) {}

  async forTenant(tenantId: string): Promise<Kysely<TenantDatabase>> {
    const cached = this.entries.get(tenantId)
    if (cached) {
      cached.lastUsedAt = Date.now()
      return cached.db
    }

    const tenant = await this.admin
      .selectFrom('tenants')
      .select(['database_name', 'status'])
      .where('id', '=', tenantId)
      .executeTakeFirst()

    if (!tenant) throw new Error(`không tìm thấy tenant ${tenantId}`)
    if (tenant.status !== 'active') throw new Error(`tenant ${tenantId} đang tạm ngưng`)

    const url = this.urlTemplate.replace('{db}', tenant.database_name)
    const db = new Kysely<TenantDatabase>({
      dialect: new PostgresDialect({
        pool: new pg.Pool({ connectionString: url, max: 4, idleTimeoutMillis: 10_000 }),
      }),
    })
    this.entries.set(tenantId, { db, lastUsedAt: Date.now() })
    return db
  }

  /** Đóng các pool không được dùng trong `maxIdleMs` mili giây. */
  async evictIdle(maxIdleMs: number): Promise<number> {
    const cutoff = Date.now() - maxIdleMs
    const stale = [...this.entries.entries()].filter(([, entry]) => entry.lastUsedAt <= cutoff)
    for (const [tenantId, entry] of stale) {
      this.entries.delete(tenantId)
      await entry.db.destroy()
    }
    return stale.length
  }

  async closeAll(): Promise<void> {
    const all = [...this.entries.values()]
    this.entries.clear()
    await Promise.all(all.map((entry) => entry.db.destroy()))
  }

  async onModuleDestroy(): Promise<void> {
    await this.closeAll()
  }
}
