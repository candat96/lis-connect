import { Injectable, type OnModuleDestroy, type OnModuleInit } from '@nestjs/common'
import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import { loadConfig } from '../config.js'
import { runAdminMigrations } from './migrator.js'
import type { AdminDatabase } from './schema.js'

@Injectable()
export class AdminDbService implements OnModuleInit, OnModuleDestroy {
  readonly db: Kysely<AdminDatabase>

  constructor() {
    const config = loadConfig()
    this.db = new Kysely<AdminDatabase>({
      dialect: new PostgresDialect({
        pool: new pg.Pool({ connectionString: config.adminDatabaseUrl, max: 10 }),
      }),
    })
  }

  async onModuleInit(): Promise<void> {
    await runAdminMigrations(this.db)
  }

  async onModuleDestroy(): Promise<void> {
    await this.db.destroy()
  }
}
