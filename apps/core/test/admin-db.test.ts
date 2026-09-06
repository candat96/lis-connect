import { Kysely, PostgresDialect, sql } from 'kysely'
import pg from 'pg'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import type { AdminDatabase } from '../src/admin-db/schema.js'
import { runAdminMigrations } from '../src/admin-db/migrator.js'

const ROOT_URL = 'postgres://lis:lis@localhost:55432/postgres'
import { testDbName } from './helpers.js'

const DB_NAME = testDbName('lis_admin_test')

let db: Kysely<AdminDatabase>

async function recreateDatabase(): Promise<void> {
  const root = new pg.Client({ connectionString: ROOT_URL })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${DB_NAME} WITH (FORCE)`)
  await root.query(`CREATE DATABASE ${DB_NAME}`)
  await root.end()
}

beforeAll(async () => {
  await recreateDatabase()
  db = new Kysely<AdminDatabase>({
    dialect: new PostgresDialect({
      pool: new pg.Pool({ connectionString: `postgres://lis:lis@localhost:55432/${DB_NAME}` }),
    }),
  })
  await runAdminMigrations(db)
})

afterAll(async () => {
  await db.destroy()
})

describe('admin database', () => {
  it('creates the tenants table', async () => {
    const rows = await db.selectFrom('tenants').selectAll().execute()
    expect(rows).toEqual([])
  })

  it('stores a tenant and reads it back', async () => {
    const tenant = await db
      .insertInto('tenants')
      .values({ code: 'bvA', name: 'Bệnh viện A', database_name: testDbName('lis_t_bva'), status: 'active' })
      .returningAll()
      .executeTakeFirstOrThrow()

    expect(tenant.code).toBe('bvA')
    expect(tenant.database_name).toBe(testDbName('lis_t_bva'))
  })

  it('rejects two tenants with the same code', async () => {
    await db
      .insertInto('tenants')
      .values({ code: 'bvB', name: 'Bệnh viện B', database_name: testDbName('lis_t_bvb'), status: 'active' })
      .execute()

    await expect(
      db
        .insertInto('tenants')
        .values({ code: 'bvB', name: 'Trùng mã', database_name: testDbName('lis_t_other'), status: 'active' })
        .execute(),
    ).rejects.toThrow()
  })

  it('stores an agent key bound to a tenant', async () => {
    const tenant = await db
      .insertInto('tenants')
      .values({ code: 'bvC', name: 'Bệnh viện C', database_name: testDbName('lis_t_bvc'), status: 'active' })
      .returningAll()
      .executeTakeFirstOrThrow()

    const key = await db
      .insertInto('agent_keys')
      .values({
        tenant_id: tenant.id,
        key_hash: 'sha256:abc',
        agent_label: 'PC phòng sinh hóa',
        status: 'active',
      })
      .returningAll()
      .executeTakeFirstOrThrow()

    expect(key.tenant_id).toBe(tenant.id)
  })

  it('is idempotent when migrations run twice', async () => {
    await expect(runAdminMigrations(db)).resolves.not.toThrow()
    const count = await sql<{ n: string }>`select count(*)::text as n from tenants`.execute(db)
    expect(Number(count.rows[0]!.n)).toBeGreaterThan(0)
  })
})
