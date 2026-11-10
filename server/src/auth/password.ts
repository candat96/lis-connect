import { randomBytes, timingSafeEqual } from 'node:crypto';
import { argon2id } from 'hash-wasm';

// Mật khẩu băm Argon2id, chuỗi lưu CSDL: argon2id$<m KiB>$<t>$<p>$<salt b64>$<hash b64>
// — cùng định dạng với app .NET (Lis.Data.Auth.PasswordHasher) để tài khoản cũ đăng nhập được trên web.

const PREFIX = 'argon2id';
const MAX_MEMORY_KIB = 1024 * 1024;
const MAX_ITERATIONS = 100;
const MAX_PARALLELISM = 64;

/** Hash hợp lệ về định dạng nhưng không khớp mật khẩu nào — giữ thời gian xử lý ổn định khi tên đăng nhập không tồn tại. */
export const DUMMY_HASH = `argon2id$65536$3$1$${Buffer.alloc(16).toString('base64')}$${Buffer.alloc(32).toString('base64')}`;

/** Kiểm mật khẩu so với chuỗi băm đã lưu. Hash hỏng / tham số vô lý → false (không ném lỗi). */
export async function verifyPassword(password: string, stored: string): Promise<boolean> {
  const parts = stored.split('$');
  if (parts.length !== 6 || parts[0] !== PREFIX) return false;

  const [memoryKib, iterations, parallelism] = [parts[1], parts[2], parts[3]].map((p) => (/^\d+$/.test(p) ? Number(p) : NaN));
  if (!(memoryKib > 0 && memoryKib <= MAX_MEMORY_KIB) || !(iterations > 0 && iterations <= MAX_ITERATIONS)
    || !(parallelism > 0 && parallelism <= MAX_PARALLELISM)) {
    return false;
  }

  const salt = Buffer.from(parts[4], 'base64');
  const expected = Buffer.from(parts[5], 'base64');
  if (salt.length < 8 || expected.length < 4) return false;

  const actual = await argon2id({
    password, salt, parallelism, iterations, memorySize: memoryKib, hashLength: expected.length, outputType: 'binary',
  });
  return actual.length === expected.length && timingSafeEqual(Buffer.from(actual), expected);
}

/** Mật khẩu tối thiểu 8 ký tự (cùng quy tắc với app .NET). Trả về câu báo lỗi, hoặc null nếu hợp lệ. */
export function passwordProblem(password: string): string | null {
  return password.length < 8 ? 'Mật khẩu tối thiểu 8 ký tự' : null;
}

/** Băm mật khẩu với salt ngẫu nhiên mới — cùng tham số và định dạng với Lis.Data.Auth.PasswordHasher (64 MB, 3 lượt, 1 luồng). */
export async function hashPassword(password: string): Promise<string> {
  const memoryKib = 64 * 1024;
  const iterations = 3;
  const parallelism = 1;
  const salt = randomBytes(16);
  const hash = await argon2id({ password, salt, parallelism, iterations, memorySize: memoryKib, hashLength: 32, outputType: 'binary' });
  return [PREFIX, memoryKib, iterations, parallelism, salt.toString('base64'), Buffer.from(hash).toString('base64')].join('$');
}
