import { Module } from '@nestjs/common'
import { IngestModule } from '../ingest/ingest.module.js'
import { AgentGateway } from './agent.gateway.js'

@Module({ imports: [IngestModule], providers: [AgentGateway], exports: [AgentGateway] })
export class AgentGatewayModule {}
