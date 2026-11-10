import 'reflect-metadata';
import { ValidationPipe } from '@nestjs/common';
import { NestFactory } from '@nestjs/core';
import { AppModule } from './app.module';
import { APP_CONFIG, AppConfig } from './config';

async function bootstrap(): Promise<void> {
  const app = await NestFactory.create(AppModule);
  // Chỉ nhận đúng các trường đã khai trong DTO; trường lạ bị từ chối.
  app.useGlobalPipes(new ValidationPipe({ whitelist: true, forbidNonWhitelisted: true, transform: true }));
  app.enableShutdownHooks();
  const config = app.get<AppConfig>(APP_CONFIG);
  await app.listen(config.port);
  console.log(`LIS server đang chạy ở cổng ${config.port}`);
}

void bootstrap();
