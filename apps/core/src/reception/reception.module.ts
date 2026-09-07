import { Module } from '@nestjs/common'
import { ReceptionController } from './reception.controller.js'
import { ReceptionService } from './reception.service.js'

@Module({ controllers: [ReceptionController], providers: [ReceptionService] })
export class ReceptionModule {}
