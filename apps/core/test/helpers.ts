import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import { runAdminMigrations } from '../src/admin-db/migrator.js'
import type { AdminDatabase } from '../src/admin-db/schema.js'
import { hashPassword } from '../src/auth/auth.service.js'
import type { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'

const ROOT = 'postgres://lis:lis@localhost:55432/postgres'

/**
 * Hậu tố riêng cho lần chạy vitest này, do `vitest.config.ts` sinh ra.
 *
 * Mọi tên database mà test tạo ra phải đi qua `testDbName`. Hai tiến trình
 * vitest chạy cùng lúc trên một máy mà dùng chung tên database sẽ `DROP
 * DATABASE ... WITH (FORCE)` lên nhau, và tiến trình bị ngắt sẽ đỏ với những
 * lỗi không liên quan gì tới code đang sửa.
 */
export const TEST_DB_SUFFIX = process.env.TEST_DB_SUFFIX ?? ''

/** Gắn hậu tố của lần chạy hiện tại vào một tên database dùng trong test. */
export function testDbName(base: string): string {
  return `${base}${TEST_DB_SUFFIX}`
}

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
  // `databaseName` phải đã đi qua `testDbName` ở phía người gọi — nhiều test còn
  // tự `DROP DATABASE` bằng chính tên đó, nên hậu tố phải được gắn một lần duy
  // nhất, ở tầng test, chứ không phải ở đây.
  const dbName = databaseName
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${dbName} WITH (FORCE)`)
  await root.end()

  const tenant = await admin
    .insertInto('tenants')
    .values({ code, name: code, database_name: dbName, status: 'active' })
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
