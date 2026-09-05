import { Global, Module } from '@nestjs/common'
import { AdminDbService } from '../admin-db/admin-db.service.js'
import { loadConfig } from '../config.js'
import { AgentKeyService } from './agent-key.service.js'
import { TenantMigratorService } from './tenant-migrator.service.js'
import { TenantRegistryService } from './tenant-registry.service.js'

@Global()
@Module({
  providers: [
    {
      provide: TenantRegistryService,
      inject: [AdminDbService],
      useFactory: (admin: AdminDbService) =>
        new TenantRegistryService(admin.db, loadConfig().tenantDatabaseUrlTemplate),
    },
    {
      provide: TenantMigratorService,
      inject: [AdminDbService],
      useFactory: (admin: AdminDbService) =>
        new TenantMigratorService(admin.db, loadConfig().tenantDatabaseUrlTemplate),
    },
    {
      provide: AgentKeyService,
      inject: [AdminDbService],
      useFactory: (admin: AdminDbService) => new AgentKeyService(admin.db),
    },
  ],
  exports: [TenantRegistryService, TenantMigratorService, AgentKeyService],
})
export class TenantModule {}
