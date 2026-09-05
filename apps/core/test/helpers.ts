import type { Kysely } from 'kysely'
import pg from 'pg'
import type { AdminDatabase } from '../src/admin-db/schema.js'
import type { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'

const ROOT = 'postgres://lis:lis@localhost:55432/postgres'

/** Xoá và tạo lại database quản trị dùng cho test. Đọc tên từ ADMIN_DATABASE_URL. */
export async function resetAdminDatabase(): Promise<void> {
  const url = new URL(process.env.ADMIN_DATABASE_URL ?? '')
  const name = url.pathname.slice(1)
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.query(`CREATE DATABASE ${name}`)
  await root.end()
}

/** Tạo tenant, tạo database nghiệp vụ và chạy migration. Trả về tenantId. */
export async function prepareTestTenant(
  admin: Kysely<AdminDatabase>,
  migrator: TenantMigratorService,
  code: string,
  databaseName: string,
): Promise<string> {
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${databaseName} WITH (FORCE)`)
  await root.end()

  const tenant = await admin
    .insertInto('tenants')
    .values({ code, name: code, database_name: databaseName, status: 'active' })
    .returningAll()
    .executeTakeFirstOrThrow()
  await migrator.provisionDatabase(tenant.id)
  return tenant.id
}
