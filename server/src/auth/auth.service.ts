import { HttpException, HttpStatus, Injectable, UnauthorizedException } from '@nestjs/common';
import { JwtService } from '@nestjs/jwt';
import { AppUser } from '../entities';
import { TenancyService } from '../tenancy/tenancy.service';
import { DUMMY_HASH, verifyPassword } from './password';

export interface SessionUser {
  id: string;
  username: string;
  fullName: string;
  title: string | null;
  roles: string[];
}

export interface Session {
  token: string;
  clinic: { code: string; name: string };
  user: SessionUser;
}

/** Nội dung token phiên: tài khoản + phòng khám. */
export interface TokenPayload {
  sub: string;
  clinicId: string;
}

const MAX_FAILED_ATTEMPTS = 5;
const LOCK_MINUTES = 15;
const INVALID = 'Sai mã phòng khám, tên đăng nhập hoặc mật khẩu';

/**
 * Đăng nhập web theo phòng khám. Cùng quy tắc với app .NET: khoá 15 phút sau 5 lần sai liên tiếp,
 * chặn tài khoản ngừng hoạt động, không lộ "tên đăng nhập/phòng khám không tồn tại" qua thông báo
 * hay thời gian xử lý.
 */
@Injectable()
export class AuthService {
  constructor(private readonly tenancy: TenancyService, private readonly jwt: JwtService) {}

  async login(clinicCode: string, username: string, password: string): Promise<Session> {
    const clinic = await this.tenancy.findClinicByCode(clinicCode);
    if (!clinic) {
      await verifyPassword(password, DUMMY_HASH);
      throw new UnauthorizedException(INVALID);
    }

    const users = (await this.tenancy.dataSourceFor(clinic)).getRepository(AppUser);
    const user = await users
      .createQueryBuilder('u')
      .addSelect('u.passwordHash')
      .where('u.username = :username', { username: username.trim() })
      .getOne();
    if (!user) {
      await verifyPassword(password, DUMMY_HASH);
      throw new UnauthorizedException(INVALID);
    }

    const now = new Date();
    if (user.lockedUntil && user.lockedUntil > now) {
      const minutes = Math.max(1, Math.ceil((user.lockedUntil.getTime() - now.getTime()) / 60_000));
      throw new HttpException(`Tài khoản tạm khoá do nhập sai nhiều lần — thử lại sau ${minutes} phút`, HttpStatus.TOO_MANY_REQUESTS);
    }
    if (!user.active) throw new UnauthorizedException('Tài khoản đã ngừng hoạt động');

    if (!(await verifyPassword(password, user.passwordHash))) {
      // Tăng và (nếu tới ngưỡng) khoá nguyên tử trong một câu UPDATE — an toàn khi nhiều nơi đăng nhập sai cùng lúc.
      await users.query(
        `UPDATE app_user
         SET failed_count = failed_count + 1,
             locked_until = CASE WHEN failed_count + 1 >= $2 THEN now() + make_interval(mins => $3) ELSE locked_until END
         WHERE id = $1`,
        [user.id, MAX_FAILED_ATTEMPTS, LOCK_MINUTES],
      );
      throw new UnauthorizedException(INVALID);
    }

    await users.update(user.id, { failedCount: 0, lockedUntil: null, lastLoginAt: now });

    const payload: TokenPayload = { sub: user.id, clinicId: clinic.id };
    return {
      token: await this.jwt.signAsync(payload),
      clinic: { code: clinic.code, name: clinic.name },
      user: toSessionUser(user),
    };
  }
}

export function toSessionUser(user: AppUser): SessionUser {
  return { id: user.id, username: user.username, fullName: user.fullName, title: user.title, roles: user.roles };
}
