import { CanActivate, createParamDecorator, ExecutionContext, Injectable, UnauthorizedException } from '@nestjs/common';
import { JwtService } from '@nestjs/jwt';
import type { Request } from 'express';
import { DataSource } from 'typeorm';
import { AppUser } from '../entities';
import { Clinic } from '../tenancy/master.entities';
import { TenancyService } from '../tenancy/tenancy.service';
import { TokenPayload } from './auth.service';

/** Ngữ cảnh của một request đã đăng nhập: phòng khám, CSDL của phòng khám, tài khoản. */
export interface RequestContext {
  clinic: Clinic;
  db: DataSource;
  user: AppUser;
}

const CONTEXT_KEY = 'lisContext';

/**
 * Bắt buộc đăng nhập (header `Authorization: Bearer <token>`). Token xác định luôn phòng khám —
 * mọi truy vấn của request chỉ chạy trên CSDL của phòng khám đó. Tài khoản bị ngừng sau khi đã
 * đăng nhập thì request kế tiếp bị từ chối ngay.
 */
@Injectable()
export class AuthGuard implements CanActivate {
  constructor(private readonly jwt: JwtService, private readonly tenancy: TenancyService) {}

  async canActivate(context: ExecutionContext): Promise<boolean> {
    const request = context.switchToHttp().getRequest<Request>();
    const header = request.headers.authorization ?? '';
    const token = header.startsWith('Bearer ') ? header.slice(7) : '';

    let payload: TokenPayload;
    try {
      payload = await this.jwt.verifyAsync<TokenPayload>(token);
    } catch {
      throw new UnauthorizedException('Phiên đăng nhập hết hạn — đăng nhập lại');
    }

    const clinic = await this.tenancy.findClinicById(payload.clinicId);
    if (!clinic) throw new UnauthorizedException('Phiên đăng nhập không hợp lệ');

    const db = await this.tenancy.dataSourceFor(clinic);
    const user = await db.getRepository(AppUser).findOneBy({ id: payload.sub });
    if (!user || !user.active) throw new UnauthorizedException('Tài khoản đã ngừng hoạt động');

    (request as unknown as Record<string, unknown>)[CONTEXT_KEY] = { clinic, db, user } satisfies RequestContext;
    return true;
  }
}

/** Lấy <see cref="RequestContext"/> trong controller: `@Ctx() ctx: RequestContext`. */
export const Ctx = createParamDecorator((_: unknown, context: ExecutionContext): RequestContext =>
  (context.switchToHttp().getRequest<Request>() as unknown as Record<string, RequestContext>)[CONTEXT_KEY]);
