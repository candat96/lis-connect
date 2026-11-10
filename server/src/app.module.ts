import { Module } from '@nestjs/common';
import { JwtModule } from '@nestjs/jwt';
import { AgentsController } from './agents/agents.controller';
import { AuthController } from './auth/auth.controller';
import { LogsController } from './logs/logs.controller';
import { AuthGuard } from './auth/auth.guard';
import { AuthService } from './auth/auth.service';
import { CatalogController } from './catalog/catalog.controller';
import { APP_CONFIG, AppConfig, loadConfig } from './config';
import { InstrumentsController } from './instruments/instruments.controller';
import { ResultsController } from './results/results.controller';
import { ResultsService } from './results/results.service';
import { TenancyService } from './tenancy/tenancy.service';
import { UsersController } from './users/users.controller';

const configProvider = { provide: APP_CONFIG, useFactory: loadConfig };

@Module({
  imports: [
    JwtModule.registerAsync({
      extraProviders: [configProvider],
      inject: [APP_CONFIG],
      // Phiên làm việc một ca: hết hạn sau 12 giờ, phải đăng nhập lại.
      useFactory: (config: AppConfig) => ({ secret: config.jwtSecret, signOptions: { expiresIn: '12h' } }),
    }),
  ],
  controllers: [AuthController, ResultsController, CatalogController, InstrumentsController, AgentsController, LogsController, UsersController],
  providers: [configProvider, TenancyService, AuthService, AuthGuard, ResultsService],
})
export class AppModule {}
