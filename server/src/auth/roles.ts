import { ForbiddenException } from '@nestjs/common';
import { RequestContext } from './auth.guard';

// Mã vai trò dùng chung với app .NET (Lis.Core.Auth.Roles).
export const ROLE = { admin: 'quan-tri', reception: 'tiep-nhan', technician: 'ky-thuat-vien', head: 'truong-khoa' } as const;

/** Cài đặt hệ thống (máy, map kênh, agent): chỉ Quản trị. */
export function requireAdmin(ctx: RequestContext): void {
  if (!ctx.user.roles.includes(ROLE.admin)) throw new ForbiddenException('Chỉ tài khoản Quản trị được thay đổi cài đặt này');
}

/** Theo dõi máy, xem log: Quản trị, Kỹ thuật viên, Trưởng khoa. */
export function requireLabStaff(ctx: RequestContext): void {
  const allowed: string[] = [ROLE.admin, ROLE.technician, ROLE.head];
  if (!ctx.user.roles.some((r) => allowed.includes(r))) throw new ForbiddenException('Tài khoản không có quyền xem mục này');
}
