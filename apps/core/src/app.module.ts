import { Module } from '@nestjs/common'
import { AdminDbModule } from './admin-db/admin-db.module.js'
import { TenantModule } from './tenant/tenant.module.js'

@Module({ imports: [AdminDbModule, TenantModule] })
export class AppModule {}
