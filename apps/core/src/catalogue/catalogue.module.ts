import { Module } from '@nestjs/common'
import { CatalogueController } from './catalogue.controller.js'
import { CatalogueService } from './catalogue.service.js'

@Module({ controllers: [CatalogueController], providers: [CatalogueService] })
export class CatalogueModule {}
