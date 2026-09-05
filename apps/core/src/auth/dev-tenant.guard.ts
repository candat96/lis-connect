import { CanActivate, ExecutionContext, Injectable, UnauthorizedException } from '@nestjs/common'
import { TenantContext } from '../tenant/tenant-context.js'

/**
 * Xác thực tạm cho giai đoạn 1: lấy tenant từ header X-Tenant-Id.
 *
 * Guard này CHỦ Ý không chạy ở production. Giai đoạn 2 thay bằng JWT thật; cho tới
 * lúc đó, một bản triển khai thật không được phép chấp nhận tenant do client tự khai.
 */
@Injectable()
export class DevTenantGuard implements CanActivate {
  canActivate(context: ExecutionContext): boolean {
    if (process.env.NODE_ENV === 'production') {
      throw new Error('DevTenantGuard không được dùng ở production')
    }
    const request = context.switchToHttp().getRequest<{ headers: Record<string, string> }>()
    const tenantId = request.headers['x-tenant-id']
    if (!tenantId) throw new UnauthorizedException('thiếu header X-Tenant-Id')
    ;(request as unknown as { tenantId: string }).tenantId = tenantId
    return true
  }
}

/** Bọc handler trong ngữ cảnh tenant. Dùng ở controller. */
export function withTenant<T>(tenantId: string, fn: () => Promise<T>): Promise<T> {
  return TenantContext.run(tenantId, fn)
}
