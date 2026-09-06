import { Global, Module } from '@nestjs/common'
import { AdminDbService } from '../admin-db/admin-db.service.js'
import { loadConfig } from '../config.js'
import { AuthController } from './auth.controller.js'
import { AuthService } from './auth.service.js'
import { JwtTenantGuard } from './jwt-tenant.guard.js'

@Global()
@Module({
  controllers: [AuthController],
  providers: [
    {
      provide: AuthService,
      inject: [AdminDbService],
      useFactory: (admin: AdminDbService) => new AuthService(admin.db, loadConfig().jwtSecret),
    },
    JwtTenantGuard,
  ],
  exports: [AuthService, JwtTenantGuard],
})
export class AuthModule {}
