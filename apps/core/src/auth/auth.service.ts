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
const GENERIC_TOKEN_ERROR = 'token không hợp lệ hoặc đã hết hạn'

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

  // `Buffer.from(hex, 'hex')` KHÔNG ném lỗi với chuỗi hex hỏng — nó cắt lặng
  // lẽ (vd một ký tự không phải hex làm buffer ngắn lại, có thể còn 0 byte).
  // Nếu không kiểm độ dài tường minh ở đây, một hàng có `password_hash` hỏng
  // (pg_restore sai encoding, sửa tay nhầm, v.v.) sẽ khiến `expected.length`
  // không khớp `SCRYPT_KEYLEN` thật — và nếu lỡ chạy `scryptSync(..., expected.length)`
  // với độ dài đó, `actual.length === expected.length` vẫn đúng (cả hai đều
  // ngắn theo `expected`), làm timingSafeEqual so hai buffer RỖNG và luôn trả
  // `true` — tức MỌI mật khẩu đều đăng nhập được. Phải fail-closed: hàng hỏng
  // thì từ chối thẳng, không cố "so sánh cho có".
  if (salt.length !== SCRYPT_SALT_BYTES || expected.length !== SCRYPT_KEYLEN) {
    return false
  }

  const actual = scryptSync(plainPassword, salt, SCRYPT_KEYLEN)
  return timingSafeEqual(actual, expected)
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
    const token = jwt.sign(payload, this.jwtSecret, {
      expiresIn: TOKEN_TTL_SECONDS,
      algorithm: 'HS256',
    })

    return {
      token,
      user: { username: row.username, fullName: row.full_name, role: row.role },
      tenant: { code: row.tenant_code, name: row.tenant_name },
    }
  }

  /**
   * Giải mã JWT VÀ tra lại trạng thái tài khoản/bệnh viện trong DB quản trị ở
   * MỖI lần gọi — không chỉ tin chữ ký còn hạn. Token ký hợp lệ nhưng
   * `users.status`/`tenants.status` không còn "active" (tài khoản bị tạm
   * ngưng, mật khẩu vừa bị reset để thu hồi quyền truy cập vì nghi lộ, bệnh
   * viện bị tạm ngưng, hoặc tài khoản đã bị xoá) đều bị từ chối NGAY — không
   * đợi tới khi token tự hết hạn sau 12 giờ. Đây cũng là chỗ chặn để lỗi
   * "tenant tạm ngưng" ở TenantRegistryService (ném Error thường, không phải
   * HttpException) không lọt xuống dưới thành 500 — bị bắt và trả 401 ở đây
   * trước khi tới đó.
   */
  async verifyToken(token: string): Promise<AuthTokenPayload> {
    let decoded: string | jwt.JwtPayload
    try {
      // Ghim thuật toán: không cho kẻ tấn công đổi header `alg` (vd "none",
      // hoặc HS256→RS256 rồi "ký" bằng khoá công khai) để qua mặt xác minh.
      decoded = jwt.verify(token, this.jwtSecret, { algorithms: ['HS256'] })
    } catch {
      throw new UnauthorizedException(GENERIC_TOKEN_ERROR)
    }

    if (typeof decoded !== 'object' || decoded === null) {
      throw new UnauthorizedException(GENERIC_TOKEN_ERROR)
    }
    const { tenantId, userId, username, role } = decoded as Partial<AuthTokenPayload>
    if (!tenantId || !userId || !username || !role) {
      throw new UnauthorizedException(GENERIC_TOKEN_ERROR)
    }

    const row = await this.admin
      .selectFrom('users')
      .innerJoin('tenants', 'tenants.id', 'users.tenant_id')
      .select(['users.status as user_status', 'tenants.status as tenant_status'])
      .where('users.id', '=', userId)
      .where('users.tenant_id', '=', tenantId)
      .executeTakeFirst()

    if (!row || row.user_status !== 'active' || row.tenant_status !== 'active') {
      throw new UnauthorizedException(GENERIC_TOKEN_ERROR)
    }

    return { tenantId, userId, username, role }
  }
}
