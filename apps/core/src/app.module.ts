import { Module } from '@nestjs/common'
import { AdminDbModule } from './admin-db/admin-db.module.js'
import { AgentGatewayModule } from './agent-gateway/agent-gateway.module.js'
import { DevicesModule } from './devices/devices.module.js'
import { IngestModule } from './ingest/ingest.module.js'
import { TenantModule } from './tenant/tenant.module.js'

@Module({
  imports: [AdminDbModule, TenantModule, IngestModule, AgentGatewayModule, DevicesModule],
})
export class AppModule {}
