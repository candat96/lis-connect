import { BadRequestException, Body, ConflictException, Controller, Get, HttpCode, NotFoundException, Param, ParseUUIDPipe, Post, Put, UseGuards } from '@nestjs/common';
import { ArrayNotEmpty, IsArray, IsBoolean, IsIn, IsNotEmpty, IsOptional, IsString, Matches, MaxLength } from 'class-validator';
import { AuthGuard, Ctx, RequestContext } from '../auth/auth.guard';
import { hashPassword, passwordProblem, verifyPassword } from '../auth/password';
import { requireAdmin, ROLE } from '../auth/roles';
import { AppUser } from '../entities';

const ROLES = Object.values(ROLE);

class CreateUserDto {
  @IsString() @IsNotEmpty({ message: 'Nhập tên đăng nhập' }) @MaxLength(64)
  @Matches(/^[A-Za-z0-9._-]+$/, { message: 'Tên đăng nhập chỉ gồm chữ không dấu, số, dấu chấm, gạch ngang, gạch dưới' })
  username: string;
  @IsString() @IsNotEmpty({ message: 'Nhập họ tên' }) @MaxLength(100) fullName: string;
  @IsOptional() @IsString() @MaxLength(100) title?: string | null;
  @IsArray() @ArrayNotEmpty({ message: 'Chọn ít nhất một vai trò' }) @IsIn(ROLES, { each: true, message: 'Vai trò không hợp lệ' }) roles: string[];
  @IsString() @MaxLength(256) password: string;
}

class UpdateUserDto {
  @IsString() @IsNotEmpty({ message: 'Nhập họ tên' }) @MaxLength(100) fullName: string;
  @IsOptional() @IsString() @MaxLength(100) title?: string | null;
  @IsArray() @ArrayNotEmpty({ message: 'Chọn ít nhất một vai trò' }) @IsIn(ROLES, { each: true, message: 'Vai trò không hợp lệ' }) roles: string[];
  @IsBoolean() active: boolean;
  /** Có giá trị = đặt lại mật khẩu (đồng thời mở khoá tài khoản). */
  @IsOptional() @IsString() @MaxLength(256) newPassword?: string | null;
}

class ChangeOwnPasswordDto {
  @IsString() @IsNotEmpty({ message: 'Nhập mật khẩu hiện tại' }) @MaxLength(256) currentPassword: string;
  @IsString() @MaxLength(256) newPassword: string;
}

export interface UserView {
  id: string;
  username: string;
  fullName: string;
  title: string | null;
  roles: string[];
  active: boolean;
  /** Đang bị tạm khoá do nhập sai mật khẩu nhiều lần (tới thời điểm này). */
  lockedUntil: string | null;
  lastLoginAt: string | null;
}

/**
 * Nhân sự (tài khoản đăng nhập) của phòng khám — cùng bảng `app_user` và cùng quy tắc với app .NET:
 * mật khẩu tối thiểu 8 ký tự, luôn phải còn ít nhất một Quản trị đang hoạt động. Chỉ Quản trị được quản lý.
 */
@Controller('api/users')
@UseGuards(AuthGuard)
export class UsersController {
  @Get()
  async list(@Ctx() ctx: RequestContext): Promise<UserView[]> {
    requireAdmin(ctx);
    const users = await ctx.db.getRepository(AppUser).find({ order: { username: 'ASC' } });
    const now = new Date();
    return users.map((u) => ({
      id: u.id, username: u.username, fullName: u.fullName, title: u.title, roles: u.roles, active: u.active,
      lockedUntil: u.lockedUntil && u.lockedUntil > now ? u.lockedUntil.toISOString() : null,
      lastLoginAt: u.lastLoginAt?.toISOString() ?? null,
    }));
  }

  @Post()
  async create(@Ctx() ctx: RequestContext, @Body() body: CreateUserDto): Promise<{ id: string }> {
    requireAdmin(ctx);
    this.checkPassword(body.password);
    try {
      const [row] = await ctx.db.query(
        'INSERT INTO app_user (username, full_name, title, password_hash, roles) VALUES ($1, $2, $3, $4, $5) RETURNING id',
        [body.username.trim(), body.fullName.trim(), body.title?.trim() || null, await hashPassword(body.password), [...new Set(body.roles)]]);
      return { id: row.id };
    } catch (error) {
      if ((error as { code?: string }).code === '23505') throw new ConflictException('Tên đăng nhập đã tồn tại');
      throw error;
    }
  }

  @Put(':id')
  @HttpCode(204)
  async update(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string, @Body() body: UpdateUserDto): Promise<void> {
    requireAdmin(ctx);
    const roles = [...new Set(body.roles)];
    let passwordHash: string | null = null;
    if (body.newPassword) {
      this.checkPassword(body.newPassword);
      passwordHash = await hashPassword(body.newPassword);
    }

    // Kiểm "còn ít nhất một Quản trị đang hoạt động" và ghi UPDATE trong cùng giao dịch, khoá hàng toàn bộ
    // quản trị đang hoạt động — hai người cùng lúc mỗi bên khoá một quản trị khác nhau không thể cùng lọt.
    await ctx.db.transaction(async (tx) => {
      const admins: { id: string }[] = await tx.query('SELECT id FROM app_user WHERE active = true AND $1 = ANY(roles) FOR UPDATE', [ROLE.admin]);
      const wasActiveAdmin = admins.some((a) => a.id === id);
      const staysActiveAdmin = body.active && roles.includes(ROLE.admin);
      if (wasActiveAdmin && !staysActiveAdmin && admins.length <= 1) {
        throw new ConflictException('Phải còn ít nhất một tài khoản Quản trị đang hoạt động — không thể khoá hoặc bỏ vai trò Quản trị của tài khoản này');
      }

      const [, affected] = passwordHash === null
        ? await tx.query('UPDATE app_user SET full_name = $1, title = $2, roles = $3, active = $4 WHERE id = $5',
          [body.fullName.trim(), body.title?.trim() || null, roles, body.active, id])
        // Đặt lại mật khẩu thì mở luôn khoá tạm (người dùng quên mật khẩu thường đã nhập sai nhiều lần).
        : await tx.query(
          'UPDATE app_user SET full_name = $1, title = $2, roles = $3, active = $4, password_hash = $5, failed_count = 0, locked_until = NULL WHERE id = $6',
          [body.fullName.trim(), body.title?.trim() || null, roles, body.active, passwordHash, id]);
      if (affected === 0) throw new NotFoundException('Không tìm thấy tài khoản');
    });
  }

  /** Tự đổi mật khẩu của mình (mọi vai trò). Phải nhập đúng mật khẩu hiện tại. */
  @Post('me/password')
  @HttpCode(204)
  async changeOwnPassword(@Ctx() ctx: RequestContext, @Body() body: ChangeOwnPasswordDto): Promise<void> {
    this.checkPassword(body.newPassword);
    const me = await ctx.db.getRepository(AppUser).createQueryBuilder('u').addSelect('u.passwordHash').where('u.id = :id', { id: ctx.user.id }).getOneOrFail();
    if (!(await verifyPassword(body.currentPassword, me.passwordHash))) throw new BadRequestException('Mật khẩu hiện tại không đúng');
    await ctx.db.query('UPDATE app_user SET password_hash = $1 WHERE id = $2', [await hashPassword(body.newPassword), me.id]);
  }

  private checkPassword(password: string): void {
    const problem = passwordProblem(password);
    if (problem) throw new BadRequestException(problem);
  }
}
