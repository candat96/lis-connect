import { type Kysely, sql } from 'kysely'

export async function up(db: Kysely<unknown>): Promise<void> {
  await sql`create extension if not exists "pgcrypto"`.execute(db)

  await db.schema
    .createTable('tenants')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('code', 'varchar(64)', (c) => c.notNull().unique())
    .addColumn('name', 'text', (c) => c.notNull())
    .addColumn('database_name', 'varchar(63)', (c) => c.notNull().unique())
    .addColumn('status', 'varchar(16)', (c) => c.notNull().defaultTo('active'))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createTable('agent_keys')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('tenant_id', 'uuid', (c) => c.notNull().references('tenants.id').onDelete('cascade'))
    .addColumn('key_hash', 'text', (c) => c.notNull().unique())
    .addColumn('agent_label', 'text', (c) => c.notNull())
    .addColumn('status', 'varchar(16)', (c) => c.notNull().defaultTo('active'))
    .addColumn('last_seen_at', 'timestamptz')
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createTable('tenant_migrations')
    .addColumn('tenant_id', 'uuid', (c) => c.notNull().references('tenants.id').onDelete('cascade'))
    .addColumn('migration_name', 'text', (c) => c.notNull())
    .addColumn('applied_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .addPrimaryKeyConstraint('tenant_migrations_pk', ['tenant_id', 'migration_name'])
    .execute()
}

export async function down(db: Kysely<unknown>): Promise<void> {
  await db.schema.dropTable('tenant_migrations').execute()
  await db.schema.dropTable('agent_keys').execute()
  await db.schema.dropTable('tenants').execute()
}
