import { randomBytes, scryptSync, timingSafeEqual } from 'node:crypto'
import { Injectable, UnauthorizedException } from '@nestjs/common'
import jwt from 'jsonwebtoken'
import type { Kysely } from 'kysely'
import type { AdminDatabase } from '../admin-db/schema.js'

const SCRYPT_KEYLEN = 64
const SCRYPT_SALT_BYTES = 16

/**
 * Một ca trực điển hình ở phòng xét nghiệm kéo dài tối đa 12 giờ; hết ca thì
 * bắt đăng nhập lại thay vì để token sống vô thời hạn (token bị lộ chỉ có
 * giá trị trong đúng một ca).
 */
const TOKEN_TTL_SECONDS = 12 * 60 * 60

const GENERIC_LOGIN_ERROR = 'Sai mã cơ sở, tên đăng nhập hoặc mật khẩu'

export interface AuthTokenPayload {
  tenantId: string
  userId: string
  username: string
  role: string
}

export interface LoginResult {
  token: string
  user: { username: string; fullName: string; role: string }
  tenant: { code: string; name: string }
}

/** Băm mật khẩu bằng scrypt (node:crypto có sẵn — không cài thêm native module). */
export function hashPassword(plainPassword: string): string {
  const salt = randomBytes(SCRYPT_SALT_BYTES)
  const derived = scryptSync(plainPassword, salt, SCRYPT_KEYLEN)
  return `scrypt:${salt.toString('hex')}:${derived.toString('hex')}`
}

function verifyPassword(plainPassword: string, stored: string): boolean {
  const parts = stored.split(':')
  if (parts.length !== 3 || parts[0] !== 'scrypt') return false
  const saltHex = parts[1]
  const hashHex = parts[2]
  if (!saltHex || !hashHex) return false

  let salt: Buffer
  let expected: Buffer
  try {
    salt = Buffer.from(saltHex, 'hex')
    expected = Buffer.from(hashHex, 'hex')
  } catch {
    return false
  }
  const actual = scryptSync(plainPassword, salt, expected.length)
  return actual.length === expected.length && timingSafeEqual(actual, expected)
}

// Hash "giả" cố định, tính một lần lúc nạp module. Khi không tìm thấy tenant
// hoặc username, login() vẫn chạy verifyPassword() với hash này để thời gian
// phản hồi gần giống hệt trường hợp tìm thấy nhưng sai mật khẩu — không để
// lộ qua độ trễ việc tenant/username có tồn tại hay không.
const DUMMY_HASH = hashPassword(randomBytes(24).toString('hex'))

@Injectable()
export class AuthService {
  constructor(
    private readonly admin: Kysely<AdminDatabase>,
    private readonly jwtSecret: string,
  ) {}

  async login(tenantCode: string, username: string, password: string): Promise<LoginResult> {
    if (!tenantCode || !username || !password) {
      throw new UnauthorizedException(GENERIC_LOGIN_ERROR)
    }

    const row = await this.admin
      .selectFrom('users')
      .innerJoin('tenants', 'tenants.id', 'users.tenant_id')
      .select([
        'users.id as user_id',
        'users.username as username',
        'users.password_hash as password_hash',
        'users.full_name as full_name',
        'users.role as role',
        'users.status as user_status',
        'tenants.id as tenant_id',
        'tenants.code as tenant_code',
        'tenants.name as tenant_name',
        'tenants.status as tenant_status',
      ])
      .where('tenants.code', '=', tenantCode)
      .where('users.username', '=', username)
      .executeTakeFirst()

    const passwordOk = verifyPassword(password, row?.password_hash ?? DUMMY_HASH)

    if (!row || row.user_status !== 'active' || row.tenant_status !== 'active' || !passwordOk) {
      throw new UnauthorizedException(GENERIC_LOGIN_ERROR)
    }

    const payload: AuthTokenPayload = {
      tenantId: row.tenant_id,
      userId: row.user_id,
      username: row.username,
      role: row.role,
    }
    const token = jwt.sign(payload, this.jwtSecret, { expiresIn: TOKEN_TTL_SECONDS })

    return {
      token,
      user: { username: row.username, fullName: row.full_name, role: row.role },
      tenant: { code: row.tenant_code, name: row.tenant_name },
    }
  }

  verifyToken(token: string): AuthTokenPayload {
    let decoded: string | jwt.JwtPayload
    try {
      decoded = jwt.verify(token, this.jwtSecret)
    } catch {
      throw new UnauthorizedException('token không hợp lệ hoặc đã hết hạn')
    }

    if (typeof decoded !== 'object' || decoded === null) {
      throw new UnauthorizedException('token không hợp lệ hoặc đã hết hạn')
    }
    const { tenantId, userId, username, role } = decoded as Partial<AuthTokenPayload>
    if (!tenantId || !userId || !username || !role) {
      throw new UnauthorizedException('token không hợp lệ hoặc đã hết hạn')
    }
    return { tenantId, userId, username, role }
  }
}
