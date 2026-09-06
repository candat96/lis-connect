import { CanActivate, ExecutionContext, Inject, Injectable, UnauthorizedException } from '@nestjs/common'
import { TenantContext } from '../tenant/tenant-context.js'
import { AuthService } from './auth.service.js'

interface TenantRequest {
  headers: Record<string, string | undefined>
  tenantId?: string
  userId?: string
  username?: string
  role?: string
}

const BEARER_PREFIX = 'Bearer '

/**
 * Thay thế DevTenantGuard (Task 19): tenant không còn do client tự khai qua
 * header X-Tenant-Id nữa — phải đến từ một JWT hợp lệ do AuthService phát
 * hành lúc đăng nhập.
 */
@Injectable()
export class JwtTenantGuard implements CanActivate {
  // `@Inject` tường minh vì bản build test dùng esbuild (qua vitest/tsx), và
  // esbuild không phát metadata `design:paramtypes` cho `emitDecoratorMetadata`
  // — nếu chỉ dựa vào kiểu tham số, Nest sẽ không giải quyết được token và
  // tiêm `undefined` vào `auth`.
  constructor(@Inject(AuthService) private readonly auth: AuthService) {}

  async canActivate(context: ExecutionContext): Promise<boolean> {
    const request = context.switchToHttp().getRequest<TenantRequest>()
    const header = request.headers['authorization']
    if (!header || !header.startsWith(BEARER_PREFIX)) {
      throw new UnauthorizedException('thiếu token đăng nhập')
    }
    const token = header.slice(BEARER_PREFIX.length)
    // `verifyToken` tra lại DB mỗi lần (xem ghi chú trong auth.service.ts) —
    // guard vì vậy phải async, không còn là một phép kiểm chữ ký thuần đồng bộ.
    const payload = await this.auth.verifyToken(token)

    request.tenantId = payload.tenantId
    request.userId = payload.userId
    request.username = payload.username
    request.role = payload.role
    return true
  }
}

/** Bọc handler trong ngữ cảnh tenant. Dùng ở controller. */
export function withTenant<T>(tenantId: string, fn: () => Promise<T>): Promise<T> {
  return TenantContext.run(tenantId, fn)
}
