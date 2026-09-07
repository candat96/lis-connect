import { Injectable } from '@nestjs/common'
import {
  Kysely,
  Migrator,
  PostgresDialect,
  type Migration,
  type MigrationProvider,
} from 'kysely'
import pg from 'pg'
import type { AdminDatabase } from '../admin-db/schema.js'
import { assertValidDatabaseName } from './database-name.js'
import * as initial from './migrations/001-initial.js'
import * as catalogue from './migrations/002-catalogue.js'
import * as catalogueAudit from './migrations/003-catalogue-audit.js'
import * as catalogueState from './migrations/004-trang-thai-danh-muc.js'
import type { TenantDatabase } from './tenant-schema.js'

const migrations: Record<string, Migration> = {
  '001-initial': initial,
  '002-catalogue': catalogue,
  '003-catalogue-audit': catalogueAudit,
  '004-trang-thai-danh-muc': catalogueState,
}

class StaticProvider implements MigrationProvider {
  async getMigrations(): Promise<Record<string, Migration>> {
    return migrations
  }
}

@Injectable()
export class TenantMigratorService {
  private readonly pools = new Map<string, Kysely<TenantDatabase>>()

  constructor(
    private readonly admin: Kysely<AdminDatabase>,
    private readonly urlTemplate: string,
  ) {}

  /** URL kết nối tới DB nghiệp vụ của một tenant, trên cùng một Postgres host. */
  private async urlFor(tenantId: string): Promise<{ url: string; databaseName: string }> {
    const tenant = await this.admin
      .selectFrom('tenants')
      .select(['database_name'])
      .where('id', '=', tenantId)
      .executeTakeFirst()
    if (!tenant) throw new Error(`không tìm thấy tenant ${tenantId}`)
    assertValidDatabaseName(tenant.database_name)
    return {
      url: this.urlTemplate.replace('{db}', () => tenant.database_name),
      databaseName: tenant.database_name,
    }
  }

  private async connect(tenantId: string): Promise<Kysely<TenantDatabase>> {
    const existing = this.pools.get(tenantId)
    if (existing) return existing
    const { url } = await this.urlFor(tenantId)
    const db = new Kysely<TenantDatabase>({
      dialect: new PostgresDialect({ pool: new pg.Pool({ connectionString: url, max: 4 }) }),
    })
    this.pools.set(tenantId, db)
    return db
  }

  /** Tạo database của tenant nếu chưa có, rồi chạy migration. */
  async provisionDatabase(tenantId: string): Promise<void> {
    const { url, databaseName } = await this.urlFor(tenantId)
    const rootUrl = new URL(url)
    rootUrl.pathname = '/postgres'

    const root = new pg.Client({ connectionString: rootUrl.toString() })
    await root.connect()
    try {
      const exists = await root.query('select 1 from pg_database where datname = $1', [databaseName])
      if (exists.rowCount === 0) {
        // CREATE DATABASE không nhận tham số bind; databaseName đã được
        // assertValidDatabaseName kiểm ở urlFor() phía trên.
        await root.query(`CREATE DATABASE ${databaseName}`)
      }
    } finally {
      await root.end()
    }

    await this.migrateTenant(tenantId)
  }

  async migrateTenant(tenantId: string): Promise<void> {
    const db = await this.connect(tenantId)
    const migrator = new Migrator({ db, provider: new StaticProvider() })
    const { error, results } = await migrator.migrateToLatest()
    if (error) {
      const failed = results?.find((r) => r.status === 'Error')?.migrationName ?? 'không rõ'
      throw new Error(`migration tenant ${tenantId} thất bại ở "${failed}": ${String(error)}`)
    }
    for (const result of results ?? []) {
      if (result.status !== 'Success') continue
      await this.admin
        .insertInto('tenant_migrations')
        .values({ tenant_id: tenantId, migration_name: result.migrationName })
        .onConflict((oc) => oc.columns(['tenant_id', 'migration_name']).doNothing())
        .execute()
    }
  }

  /**
   * Chạy migration cho mọi tenant đang hoạt động. Một tenant hỏng không được
   * chặn các tenant còn lại, nên lỗi được gom lại chứ không ném ra ngay.
   */
  async migrateAll(): Promise<{ ok: string[]; failed: Array<{ tenantId: string; error: string }> }> {
    const tenants = await this.admin
      .selectFrom('tenants')
      .select('id')
      .where('status', '=', 'active')
      .execute()

    const ok: string[] = []
    const failed: Array<{ tenantId: string; error: string }> = []
    for (const tenant of tenants) {
      try {
        await this.migrateTenant(tenant.id)
        ok.push(tenant.id)
      } catch (error) {
        failed.push({ tenantId: tenant.id, error: error instanceof Error ? error.message : String(error) })
      }
    }
    return { ok, failed }
  }

  async closeAll(): Promise<void> {
    await Promise.all([...this.pools.values()].map((db) => db.destroy()))
    this.pools.clear()
  }
}
