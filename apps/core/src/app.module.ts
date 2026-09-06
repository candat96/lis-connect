import { Module } from '@nestjs/common'
import { AdminDbModule } from './admin-db/admin-db.module.js'
import { AgentGatewayModule } from './agent-gateway/agent-gateway.module.js'
import { AuthModule } from './auth/auth.module.js'
import { CatalogueModule } from './catalogue/catalogue.module.js'
import { DevicesModule } from './devices/devices.module.js'
import { HealthModule } from './health/health.module.js'
import { IngestModule } from './ingest/ingest.module.js'
import { TenantModule } from './tenant/tenant.module.js'

@Module({
  imports: [
    AdminDbModule,
    TenantModule,
    AuthModule,
    IngestModule,
    AgentGatewayModule,
    DevicesModule,
    CatalogueModule,
    HealthModule,
  ],
})
export class AppModule {}
