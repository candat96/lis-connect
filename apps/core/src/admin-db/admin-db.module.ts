import { Global, Module } from '@nestjs/common'
import { AdminDbService } from './admin-db.service.js'

@Global()
@Module({ providers: [AdminDbService], exports: [AdminDbService] })
export class AdminDbModule {}
