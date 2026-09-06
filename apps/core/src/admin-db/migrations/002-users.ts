import { type Kysely, sql } from 'kysely'

/**
 * Bảng `users` — tài khoản đăng nhập Web UI cho kỹ thuật viên xét nghiệm.
 *
 * `username` KHÔNG unique toàn cục — hai bệnh viện khác nhau có thể có
 * người dùng cùng tên đăng nhập (vd cả hai đều có "admin"). Chỉ unique
 * TRONG PHẠM VI một tenant, nên ràng buộc unique là (tenant_id, username).
 */
export async function up(db: Kysely<unknown>): Promise<void> {
  await db.schema
    .createTable('users')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('tenant_id', 'uuid', (c) => c.notNull().references('tenants.id').onDelete('cascade'))
    .addColumn('username', 'varchar(64)', (c) => c.notNull())
    .addColumn('password_hash', 'text', (c) => c.notNull())
    .addColumn('full_name', 'text', (c) => c.notNull())
    .addColumn('role', 'varchar(32)', (c) => c.notNull())
    .addColumn('status', 'varchar(16)', (c) => c.notNull().defaultTo('active'))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createIndex('users_tenant_id_username_key')
    .on('users')
    .columns(['tenant_id', 'username'])
    .unique()
    .execute()
}

export async function down(db: Kysely<unknown>): Promise<void> {
  await db.schema.dropTable('users').execute()
}
