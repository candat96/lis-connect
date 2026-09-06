import { Kysely, Migrator, type Migration, type MigrationProvider } from 'kysely'
import type { AdminDatabase } from './schema.js'
import * as initial from './migrations/001-initial.js'
import * as users from './migrations/002-users.js'

const migrations: Record<string, Migration> = {
  '001-initial': initial,
  '002-users': users,
}

class StaticProvider implements MigrationProvider {
  async getMigrations(): Promise<Record<string, Migration>> {
    return migrations
  }
}

export async function runAdminMigrations(db: Kysely<AdminDatabase>): Promise<void> {
  const migrator = new Migrator({ db, provider: new StaticProvider() })
  const { error, results } = await migrator.migrateToLatest()
  if (error) {
    const failed = results?.find((r) => r.status === 'Error')?.migrationName ?? 'không rõ'
    throw new Error(`migration DB quản trị thất bại ở "${failed}": ${String(error)}`)
  }
}
