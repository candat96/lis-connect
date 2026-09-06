import { Body, Controller, Inject, Post } from '@nestjs/common'
import { AuthService, type LoginResult } from './auth.service.js'

interface LoginRequestBody {
  tenantCode?: unknown
  username?: unknown
  password?: unknown
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

@Controller('api/auth')
export class AuthController {
  // `@Inject` tường minh — xem ghi chú trong jwt-tenant.guard.ts.
  constructor(@Inject(AuthService) private readonly auth: AuthService) {}

  @Post('login')
  login(@Body() body: LoginRequestBody): Promise<LoginResult> {
    return this.auth.login(asString(body.tenantCode), asString(body.username), asString(body.password))
  }
}
