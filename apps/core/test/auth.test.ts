import 'reflect-metadata'
import jwt from 'jsonwebtoken'
import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { runAdminMigrations } from '../src/admin-db/migrator.js'
import type { AdminDatabase } from '../src/admin-db/schema.js'
import { AuthService, hashPassword } from '../src/auth/auth.service.js'
import { testDbName } from './helpers.js'

const ROOT_URL = 'postgres://lis:lis@localhost:55432/postgres'
const DB_NAME = testDbName('lis_admin_auth_test')
const JWT_SECRET = 'segredo-de-teste-nao-usar-em-producao'

let db: Kysely<AdminDatabase>
let auth: AuthService
let tenantId: string

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
  auth = new AuthService(db, JWT_SECRET)

  const tenant = await db
    .insertInto('tenants')
    .values({ code: 'bva', name: 'Bệnh viện A', database_name: testDbName('lis_t_auth_bva'), status: 'active' })
    .returningAll()
    .executeTakeFirstOrThrow()
  tenantId = tenant.id

  await db
    .insertInto('users')
    .values({
      tenant_id: tenantId,
      username: 'ktv1',
      password_hash: hashPassword('mat-khau-dung'),
      full_name: 'Nguyễn Văn A',
      role: 'ky_thuat_vien',
      status: 'active',
    })
    .execute()
}, 60_000)

afterAll(async () => {
  await db.destroy()
})

describe('AuthService.login', () => {
  it('đăng nhập đúng trả về token và thông tin người dùng/cơ sở', async () => {
    const result = await auth.login('bva', 'ktv1', 'mat-khau-dung')
    expect(result.user).toEqual({ username: 'ktv1', fullName: 'Nguyễn Văn A', role: 'ky_thuat_vien' })
    expect(result.tenant).toEqual({ code: 'bva', name: 'Bệnh viện A' })

    const decoded = jwt.verify(result.token, JWT_SECRET) as Record<string, unknown>
    expect(decoded.tenantId).toBe(tenantId)
    expect(decoded.username).toBe('ktv1')
  })

  it('sai mật khẩu bị từ chối với thông báo chung', async () => {
    await expect(auth.login('bva', 'ktv1', 'sai-mat-khau')).rejects.toThrow(
      /Sai mã cơ sở, tên đăng nhập hoặc mật khẩu/,
    )
  })

  it('username không tồn tại bị từ chối với ĐÚNG cùng thông báo (không lộ username có tồn tại hay không)', async () => {
    let messageWrongPassword = ''
    let messageWrongUsername = ''
    try {
      await auth.login('bva', 'ktv1', 'sai-mat-khau')
    } catch (err) {
      messageWrongPassword = err instanceof Error ? err.message : String(err)
    }
    try {
      await auth.login('bva', 'khong-ton-tai', 'bat-ky-gi')
    } catch (err) {
      messageWrongUsername = err instanceof Error ? err.message : String(err)
    }
    expect(messageWrongUsername).toBe(messageWrongPassword)
  })

  it('mã cơ sở không tồn tại bị từ chối với cùng thông báo', async () => {
    await expect(auth.login('khong-ton-tai', 'ktv1', 'mat-khau-dung')).rejects.toThrow(
      /Sai mã cơ sở, tên đăng nhập hoặc mật khẩu/,
    )
  })

  it('tài khoản bị tạm ngưng không đăng nhập được', async () => {
    await db
      .insertInto('users')
      .values({
        tenant_id: tenantId,
        username: 'ktv-ngung',
        password_hash: hashPassword('mat-khau-dung'),
        full_name: 'Bị tạm ngưng',
        role: 'ky_thuat_vien',
        status: 'suspended',
      })
      .execute()

    await expect(auth.login('bva', 'ktv-ngung', 'mat-khau-dung')).rejects.toThrow(
      /Sai mã cơ sở, tên đăng nhập hoặc mật khẩu/,
    )
  })

  it('bệnh viện bị tạm ngưng: mọi tài khoản của nó không đăng nhập được', async () => {
    const suspendedTenant = await db
      .insertInto('tenants')
      .values({
        code: 'bvb-ngung',
        name: 'Bệnh viện tạm ngưng',
        database_name: testDbName('lis_t_auth_bvb'),
        status: 'suspended',
      })
      .returningAll()
      .executeTakeFirstOrThrow()
    await db
      .insertInto('users')
      .values({
        tenant_id: suspendedTenant.id,
        username: 'ktv2',
        password_hash: hashPassword('mat-khau-dung'),
        full_name: 'Trần Thị B',
        role: 'ky_thuat_vien',
        status: 'active',
      })
      .execute()

    await expect(auth.login('bvb-ngung', 'ktv2', 'mat-khau-dung')).rejects.toThrow(
      /Sai mã cơ sở, tên đăng nhập hoặc mật khẩu/,
    )
  })

  it('hash mật khẩu hỏng trong DB bị từ chối — KHÔNG được cho qua bất kỳ mật khẩu nào (fail-closed)', async () => {
    // `hashHex` ở đây KHÔNG PHẢI hex hợp lệ (không có ký tự hex nào) —
    // `Buffer.from('KHONG-PHAI-HEX', 'hex')` không ném lỗi, nó lặng lẽ trả về
    // buffer RỖNG (0 byte). Mô phỏng một hàng bị hỏng do pg_restore sai
    // encoding, sửa tay nhầm, hoặc bất kỳ đường ghi dữ liệu nào khác ngoài
    // hashPassword(). Bản cài đặt fail-open (gọi `scryptSync(pw, salt,
    // expected.length)` rồi so `actual.length === expected.length` trước khi
    // `timingSafeEqual`) sẽ cho MỌI mật khẩu qua: cả hai vế đều là buffer 0
    // byte, `timingSafeEqual([], [])` luôn `true`.
    await db
      .insertInto('users')
      .values({
        tenant_id: tenantId,
        username: 'ktv-hong',
        password_hash: 'scrypt:' + '00'.repeat(16) + ':KHONG-PHAI-HEX',
        full_name: 'Hash bị hỏng',
        role: 'ky_thuat_vien',
        status: 'active',
      })
      .execute()

    const mockPasswords = [
      'mat-khau-dung',
      '123456',
      '',
      'admin',
      'password',
      'a',
      'x'.repeat(200),
      'mat-khau-hoan-toan-bia-dat-8',
    ]
    for (const password of mockPasswords) {
      await expect(auth.login('bva', 'ktv-hong', password)).rejects.toThrow(
        /Sai mã cơ sở, tên đăng nhập hoặc mật khẩu/,
      )
    }
  })

  it('cùng username ở hai tenant khác nhau không xung đột (unique theo tenant, không toàn cục)', async () => {
    const otherTenant = await db
      .insertInto('tenants')
      .values({ code: 'bvc', name: 'Bệnh viện C', database_name: testDbName('lis_t_auth_bvc'), status: 'active' })
      .returningAll()
      .executeTakeFirstOrThrow()
    await db
      .insertInto('users')
      .values({
        tenant_id: otherTenant.id,
        username: 'ktv1', // trùng username với tenant "bva" ở trên — phải cho phép
        password_hash: hashPassword('mat-khau-khac'),
        full_name: 'Người trùng tên đăng nhập ở cơ sở khác',
        role: 'ky_thuat_vien',
        status: 'active',
      })
      .execute()

    const result = await auth.login('bvc', 'ktv1', 'mat-khau-khac')
    expect(result.tenant.code).toBe('bvc')
  })
})

describe('AuthService.verifyToken', () => {
  it('giải mã lại đúng payload đã ký', async () => {
    const { token } = await auth.login('bva', 'ktv1', 'mat-khau-dung')
    const payload = await auth.verifyToken(token)
    expect(payload.tenantId).toBe(tenantId)
    expect(payload.username).toBe('ktv1')
    expect(payload.role).toBe('ky_thuat_vien')
  })

  it('ném lỗi với token rác', async () => {
    await expect(auth.verifyToken('khong-phai-jwt')).rejects.toThrow()
  })

  it('ném lỗi với token ký bằng bí mật khác', async () => {
    const forged = jwt.sign(
      { tenantId, userId: 'x', username: 'ktv1', role: 'ky_thuat_vien' },
      'bi-mat-khac',
    )
    await expect(auth.verifyToken(forged)).rejects.toThrow()
  })

  it('ném lỗi với token đã hết hạn', async () => {
    const expired = jwt.sign(
      { tenantId, userId: 'x', username: 'ktv1', role: 'ky_thuat_vien' },
      JWT_SECRET,
      { expiresIn: -10 },
    )
    await expect(auth.verifyToken(expired)).rejects.toThrow()
  })

  it('ném lỗi với token ký bằng alg "none" (không chữ ký)', async () => {
    const header = Buffer.from(JSON.stringify({ alg: 'none', typ: 'JWT' })).toString('base64url')
    const payload = Buffer.from(
      JSON.stringify({ tenantId, userId: 'x', username: 'ktv1', role: 'ky_thuat_vien' }),
    ).toString('base64url')
    await expect(auth.verifyToken(`${header}.${payload}.`)).rejects.toThrow()
  })

  it('token còn hạn nhưng tài khoản vừa bị tạm ngưng phải mất hiệu lực NGAY (không đợi hết hạn 12 giờ)', async () => {
    await db
      .insertInto('users')
      .values({
        tenant_id: tenantId,
        username: 'ktv-thu-hoi',
        password_hash: hashPassword('mat-khau-dung'),
        full_name: 'Sắp bị thu hồi',
        role: 'ky_thuat_vien',
        status: 'active',
      })
      .execute()

    const { token } = await auth.login('bva', 'ktv-thu-hoi', 'mat-khau-dung')
    // Token vẫn còn hạn và vẫn xác minh được ngay sau khi phát hành.
    await expect(auth.verifyToken(token)).resolves.toMatchObject({ username: 'ktv-thu-hoi' })

    // Kịch bản thật: kỹ thuật viên nghỉ việc / laptop mất / nghi lộ mật khẩu
    // — quản trị tạm ngưng tài khoản (hoặc reset-password, cũng đổi
    // password_hash và không ảnh hưởng tới kiểm tra này vì ta kiểm cả status).
    await db
      .updateTable('users')
      .set({ status: 'suspended' })
      .where('tenant_id', '=', tenantId)
      .where('username', '=', 'ktv-thu-hoi')
      .execute()

    // Token cũ (vẫn còn hạn theo `exp`, chữ ký vẫn hợp lệ) phải bị từ chối
    // NGAY LẬP TỨC — không phải đợi tới khi hết hạn tự nhiên.
    await expect(auth.verifyToken(token)).rejects.toThrow(/token không hợp lệ hoặc đã hết hạn/)
  })

  it('token còn hạn nhưng bệnh viện vừa bị tạm ngưng phải mất hiệu lực NGAY', async () => {
    const t = await db
      .insertInto('tenants')
      .values({
        code: 'bvd-thu-hoi',
        name: 'Bệnh viện sắp tạm ngưng',
        database_name: testDbName('lis_t_auth_bvd'),
        status: 'active',
      })
      .returningAll()
      .executeTakeFirstOrThrow()
    await db
      .insertInto('users')
      .values({
        tenant_id: t.id,
        username: 'ktv3',
        password_hash: hashPassword('mat-khau-dung'),
        full_name: 'Lê Văn D',
        role: 'ky_thuat_vien',
        status: 'active',
      })
      .execute()

    const { token } = await auth.login('bvd-thu-hoi', 'ktv3', 'mat-khau-dung')
    await db.updateTable('tenants').set({ status: 'suspended' }).where('id', '=', t.id).execute()

    await expect(auth.verifyToken(token)).rejects.toThrow(/token không hợp lệ hoặc đã hết hạn/)
  })
})
