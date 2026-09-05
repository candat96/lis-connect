import 'reflect-metadata'
import { NestFactory } from '@nestjs/core'
import { AgentGateway } from './agent-gateway/agent.gateway.js'
import { AppModule } from './app.module.js'
import { loadConfig } from './config.js'

async function bootstrap(): Promise<void> {
  const config = loadConfig()
  const app = await NestFactory.create(AppModule)
  app.enableCors({ origin: ['http://localhost:5173'], allowedHeaders: ['Content-Type', 'X-Tenant-Id'] })
  await app.listen(config.port)
  app.get(AgentGateway).attach(app.getHttpServer())
  console.log(`LIS Connect Core đang chạy ở cổng ${config.port}`)
}

void bootstrap()
