import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import { runAdminMigrations } from '../src/admin-db/migrator.js'
import type { AdminDatabase } from '../src/admin-db/schema.js'
import { hashPassword } from '../src/auth/auth.service.js'
import type { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'

const ROOT = 'postgres://lis:lis@localhost:55432/postgres'

/**
 * Xoá và tạo lại database quản trị dùng cho test, sau đó chạy migration. Đọc
 * tên từ ADMIN_DATABASE_URL. Migration không còn tự chạy lúc `app.init()`
 * (Task 18), nên các test tích hợp phải tự gọi ở đây.
 */
export async function resetAdminDatabase(): Promise<void> {
  const url = new URL(process.env.ADMIN_DATABASE_URL ?? '')
  const name = url.pathname.slice(1)
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.query(`CREATE DATABASE ${name}`)
  await root.end()

  const db = new Kysely<AdminDatabase>({
    dialect: new PostgresDialect({
      pool: new pg.Pool({ connectionString: process.env.ADMIN_DATABASE_URL }),
    }),
  })
  await runAdminMigrations(db)
  await db.destroy()
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

/**
 * Tạo một người dùng đăng nhập được cho test — mật khẩu test cố định, không
 * dùng cho gì khác ngoài việc gọi `POST /api/auth/login` trong bộ test tích
 * hợp lấy token thật.
 */
export async function createTestUser(
  admin: Kysely<AdminDatabase>,
  tenantId: string,
  username: string,
  password: string,
): Promise<void> {
  await admin
    .insertInto('users')
    .values({
      tenant_id: tenantId,
      username,
      password_hash: hashPassword(password),
      full_name: username,
      role: 'ky_thuat_vien',
      status: 'active',
    })
    .execute()
}
