import { Module } from '@nestjs/common'
import { AdminDbModule } from './admin-db/admin-db.module.js'

@Module({ imports: [AdminDbModule] })
export class AppModule {}
