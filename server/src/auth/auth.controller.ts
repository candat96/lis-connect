import { Body, Controller, Get, HttpCode, Post, UseGuards } from '@nestjs/common';
import { IsNotEmpty, IsString, MaxLength } from 'class-validator';
import { AuthGuard, Ctx, RequestContext } from './auth.guard';
import { AuthService, Session, toSessionUser } from './auth.service';

class LoginDto {
  @IsString() @IsNotEmpty({ message: 'Chưa nhập mã phòng khám' }) @MaxLength(64) clinicCode: string;
  @IsString() @IsNotEmpty({ message: 'Chưa nhập tên đăng nhập' }) @MaxLength(128) username: string;
  @IsString() @IsNotEmpty({ message: 'Chưa nhập mật khẩu' }) @MaxLength(256) password: string;
}

@Controller('api/auth')
export class AuthController {
  constructor(private readonly auth: AuthService) {}

  @Post('login')
  @HttpCode(200)
  login(@Body() body: LoginDto): Promise<Session> {
    return this.auth.login(body.clinicCode, body.username, body.password);
  }

  /** Tài khoản + phòng khám của phiên hiện tại (web gọi khi mở lại trang). */
  @Get('me')
  @UseGuards(AuthGuard)
  me(@Ctx() ctx: RequestContext): Omit<Session, 'token'> {
    return { clinic: { code: ctx.clinic.code, name: ctx.clinic.name }, user: toSessionUser(ctx.user) };
  }
}
