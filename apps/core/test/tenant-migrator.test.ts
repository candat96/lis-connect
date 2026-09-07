import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { runAdminMigrations } from '../src/admin-db/migrator.js'
import type { AdminDatabase } from '../src/admin-db/schema.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import type { TenantDatabase } from '../src/tenant/tenant-schema.js'
import { testDbName } from './helpers.js'

const ROOT = 'postgres://lis:lis@localhost:55432/postgres'
const ADMIN_DB = testDbName('lis_admin_mig_test')
const TEMPLATE = 'postgres://lis:lis@localhost:55432/{db}'

let admin: Kysely<AdminDatabase>
let migrator: TenantMigratorService
let tenantAId: string

async function dropAndCreate(name: string): Promise<void> {
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.query(`CREATE DATABASE ${name}`)
  await root.end()
}

async function dropIfExists(name: string): Promise<void> {
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.end()
}

beforeAll(async () => {
  await dropAndCreate(ADMIN_DB)
  await dropIfExists(testDbName('lis_t_mig_a'))
  await dropIfExists(testDbName('lis_t_mig_b'))

  admin = new Kysely<AdminDatabase>({
    dialect: new PostgresDialect({
      pool: new pg.Pool({ connectionString: `postgres://lis:lis@localhost:55432/${ADMIN_DB}` }),
    }),
  })
  await runAdminMigrations(admin)

  const a = await admin
    .insertInto('tenants')
    .values({ code: 'a', name: 'A', database_name: testDbName('lis_t_mig_a'), status: 'active' })
    .returningAll()
    .executeTakeFirstOrThrow()
  tenantAId = a.id

  await admin
    .insertInto('tenants')
    .values({ code: 'b', name: 'B', database_name: testDbName('lis_t_mig_b'), status: 'active' })
    .execute()

  migrator = new TenantMigratorService(admin, TEMPLATE)
}, 60_000)

afterAll(async () => {
  await migrator.closeAll()
  await admin.destroy()
})

describe('TenantMigratorService', () => {
  it('provisions a database and creates the business tables', async () => {
    await migrator.provisionDatabase(tenantAId)

    const db = new Kysely<TenantDatabase>({
      dialect: new PostgresDialect({
        pool: new pg.Pool({ connectionString: `postgres://lis:lis@localhost:55432/${testDbName('lis_t_mig_a')}` }),
      }),
    })
    const devices = await db.selectFrom('devices').selectAll().execute()
    expect(devices).toEqual([])
    await db.destroy()
  })

  it('records applied migrations in the admin database', async () => {
    const rows = await admin
      .selectFrom('tenant_migrations')
      .selectAll()
      .where('tenant_id', '=', tenantAId)
      .execute()
    expect(rows.map((r) => r.migration_name)).toContain('001-initial')
  })

  it('is idempotent when run twice on the same tenant', async () => {
    await expect(migrator.migrateTenant(tenantAId)).resolves.not.toThrow()
  })

  it('migrateAll keeps going when one tenant fails', async () => {
    // Tenant B chưa có database và ta cố tình chỉ migrate, không provision.
    const result = await migrator.migrateAll()
    expect(result.ok).toContain(tenantAId)
    expect(result.failed.length).toBe(1)
    expect(result.failed[0]!.error).toBeTruthy()
  })
})
