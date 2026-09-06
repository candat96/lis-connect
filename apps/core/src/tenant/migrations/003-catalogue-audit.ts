import { type Kysely, sql } from 'kysely'

/**
 * Nhật ký sửa đổi danh mục.
 *
 * Biết một dòng "đã sửa tay" (`analytes.source = 'manual'`) là chưa đủ để truy
 * vết một sự cố lâm sàng: danh mục quyết định kết quả trả về hệ thống bệnh
 * viện, nên phải biết AI sửa, LÚC NÀO, TỪ giá trị gì SANG giá trị gì.
 *
 * Bảng nằm trong database của từng bệnh viện, cùng chỗ với dữ liệu nó ghi vết —
 * không tách sang DB quản trị, để một lần khôi phục database của một cơ sở kéo
 * theo đúng nhật ký của chính cơ sở đó.
 */
export async function up(db: Kysely<unknown>): Promise<void> {
  await db.schema
    .createTable('catalogue_audit')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('changed_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    // users.id ở DB quản trị. Không có khoá ngoại vì khác database — và cũng
    // không nên có: xoá một tài khoản không được phép xoá vết họ đã sửa gì.
    .addColumn('actor_user_id', 'uuid', (c) => c.notNull())
    // Chép lại tên đăng nhập tại thời điểm sửa: tài khoản có thể bị đổi tên
    // hoặc gỡ, nhật ký thì phải đọc được mãi về sau.
    .addColumn('actor_username', 'text', (c) => c.notNull())
    // 'analyte' | 'device_analyte_map' | 'result_mapping'
    .addColumn('entity', 'varchar(32)', (c) => c.notNull())
    // Khoá nghiệp vụ của dòng bị sửa: mã xét nghiệm, "<deviceId>:<channelCode>",
    // hoặc id của luật ghi đè.
    .addColumn('entity_key', 'text', (c) => c.notNull())
    // 'create' | 'update' | 'delete'
    .addColumn('action', 'varchar(16)', (c) => c.notNull())
    // NULL khi tạo mới.
    .addColumn('before_value', 'jsonb')
    // NULL khi xoá.
    .addColumn('after_value', 'jsonb')
    .execute()

  await db.schema
    .createIndex('catalogue_audit_entity_idx')
    .on('catalogue_audit')
    .columns(['entity', 'entity_key'])
    .execute()

  await db.schema
    .createIndex('catalogue_audit_changed_at_idx')
    .on('catalogue_audit')
    .column('changed_at')
    .execute()
}

export async function down(db: Kysely<unknown>): Promise<void> {
  await db.schema.dropTable('catalogue_audit').execute()
}
