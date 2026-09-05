import { type Kysely, sql } from 'kysely'

export async function up(db: Kysely<unknown>): Promise<void> {
  await sql`create extension if not exists "pgcrypto"`.execute(db)

  await db.schema
    .createTable('agents')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('agent_key_id', 'uuid', (c) => c.notNull().unique())
    .addColumn('hostname', 'text', (c) => c.notNull())
    .addColumn('agent_version', 'text', (c) => c.notNull())
    .addColumn('last_seen_at', 'timestamptz')
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createTable('devices')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('agent_id', 'uuid', (c) => c.notNull().references('agents.id').onDelete('cascade'))
    .addColumn('device_key', 'varchar(64)', (c) => c.notNull())
    .addColumn('name', 'text', (c) => c.notNull())
    .addColumn('transport', 'varchar(16)', (c) => c.notNull())
    .addColumn('driver', 'varchar(64)', (c) => c.notNull())
    .addColumn('enabled', 'boolean', (c) => c.notNull().defaultTo(true))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .addUniqueConstraint('devices_agent_key_unique', ['agent_id', 'device_key'])
    .execute()

  await db.schema
    .createTable('raw_messages')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('message_id', 'uuid', (c) => c.notNull().unique())
    .addColumn('device_id', 'uuid', (c) => c.notNull().references('devices.id').onDelete('cascade'))
    .addColumn('direction', 'varchar(16)', (c) => c.notNull())
    .addColumn('payload', 'bytea', (c) => c.notNull())
    .addColumn('parse_status', 'varchar(16)', (c) => c.notNull().defaultTo('pending'))
    .addColumn('parse_error', 'text')
    .addColumn('received_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .addColumn('sent_at', 'timestamptz', (c) => c.notNull())
    .execute()

  await db.schema
    .createIndex('raw_messages_device_received_idx')
    .on('raw_messages')
    .columns(['device_id', 'received_at'])
    .execute()

  await db.schema
    .createTable('connection_events')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('message_id', 'uuid', (c) => c.notNull().unique())
    .addColumn('device_id', 'uuid', (c) => c.notNull().references('devices.id').onDelete('cascade'))
    .addColumn('status', 'varchar(16)', (c) => c.notNull())
    .addColumn('detail', 'text')
    .addColumn('occurred_at', 'timestamptz', (c) => c.notNull())
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createTable('device_status')
    .addColumn('device_id', 'uuid', (c) =>
      c.primaryKey().references('devices.id').onDelete('cascade'),
    )
    .addColumn('status', 'varchar(16)', (c) => c.notNull())
    .addColumn('detail', 'text')
    .addColumn('last_message_at', 'timestamptz')
    .addColumn('updated_at', 'timestamptz', (c) => c.notNull())
    .execute()
}

export async function down(db: Kysely<unknown>): Promise<void> {
  await db.schema.dropTable('device_status').execute()
  await db.schema.dropTable('connection_events').execute()
  await db.schema.dropTable('raw_messages').execute()
  await db.schema.dropTable('devices').execute()
  await db.schema.dropTable('agents').execute()
}
