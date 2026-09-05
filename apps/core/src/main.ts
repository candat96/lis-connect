import 'reflect-metadata'
import { NestFactory } from '@nestjs/core'
import { AppModule } from './app.module.js'
import { loadConfig } from './config.js'

async function bootstrap(): Promise<void> {
  const config = loadConfig()
  const app = await NestFactory.create(AppModule)
  await app.listen(config.port)
  console.log(`LIS Connect Core đang chạy ở cổng ${config.port}`)
}

void bootstrap()
