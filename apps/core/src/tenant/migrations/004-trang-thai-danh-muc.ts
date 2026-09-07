import { type Kysely, sql } from 'kysely'

/**
 * Vá bốn chỗ đứt gãy giữa các tầng mà vòng review toàn nhánh tìm ra.
 *
 * 1. `device_analyte_map.pending_decision` / `decision_candidates` — mã kênh
 *    ĐANG XUNG ĐỘT MÀ CHƯA AI QUYẾT phải tồn tại thành một DÒNG trong database.
 *    Trước đây công cụ di trú không ghi gì cho những mã kênh này (đúng: không
 *    được đoán bừa), nhưng màn hình ánh xạ định nghĩa "kênh cần chú ý" là dòng
 *    có `analyte_code IS NULL` — kênh không có dòng nào thì không đếm được, và
 *    giao diện kết luận máy "đã gán đủ" cho đúng những máy đang thiếu kênh.
 *    Dòng giữ chỗ có `analyte_code = NULL` không phá ràng buộc
 *    `UNIQUE (device_id, channel_code)` vì mã kênh đó vốn chưa có dòng nào.
 *    `pending_decision` phân biệt hai trạng thái KHÁC NHAU:
 *      - false + analyte_code NULL: CHƯA GÁN — không ai khai, là chỗ bị quên;
 *      - true  + analyte_code NULL: CHƯA QUYẾT — hệ cũ khai nhiều xét nghiệm
 *        cho cùng mã kênh, phần mềm không được chọn hộ, đang chờ người quyết.
 *
 * 2. `devices.legacy_active` — cờ `Instrument.Active` của hệ cũ trước đây được
 *    đọc lên rồi vứt đi, còn giao diện thì hiển thị `devices.enabled` (khái niệm
 *    khác hẳn: "có cho Agent kết nối hay không", mặc định true). NULL nghĩa là
 *    máy không đến từ hệ cũ nên không có thông tin này.
 *
 * 3. Bỏ `devices.has_sent_results` — cột chết. Bộ ghi đặt `false` cứng cho cả 20
 *    máy còn API lại suy từ `device_status.last_message_at`: hai định nghĩa cho
 *    cùng một cái tên. Nguồn sự thật duy nhất từ nay là `device_status`.
 *
 * 4. `analytes.manually_edited` — trước đây mỗi lần sửa một dòng danh mục là
 *    `source` bị ép về 'manual', xoá vĩnh viễn dấu vết dòng đó đến từ bảng nào
 *    của hệ cũ. Mà `source` lại chính là thứ được hứa dùng để lọc bớt danh mục
 *    2619 dòng. Tách làm hai: `source` giữ nguồn gốc, `manually_edited` giữ vết
 *    đã sửa tay.
 */
export async function up(db: Kysely<unknown>): Promise<void> {
  await db.schema
    .alterTable('device_analyte_map')
    .addColumn('pending_decision', 'boolean', (c) => c.notNull().defaultTo(false))
    .execute()
  // Danh sách mã xét nghiệm ứng viên của mã kênh chưa quyết, giữ nguyên thứ tự
  // đã in ra trong báo cáo di trú để người quyết đối chiếu được.
  await db.schema.alterTable('device_analyte_map').addColumn('decision_candidates', 'jsonb').execute()

  await db.schema.alterTable('devices').addColumn('legacy_active', 'boolean').execute()
  await db.schema.alterTable('devices').dropColumn('has_sent_results').execute()

  await db.schema
    .alterTable('analytes')
    .addColumn('manually_edited', 'boolean', (c) => c.notNull().defaultTo(false))
    .execute()

  // Danh mục thật có 2619 dòng và màn hình quản lý lọc theo `source` và `active`;
  // không có index thì mỗi lần lọc là một lần quét toàn bảng.
  await db.schema.createIndex('analytes_source_idx').on('analytes').column('source').execute()
  await db.schema.createIndex('analytes_active_idx').on('analytes').column('active').execute()

  // Mã kênh chưa quyết là thứ màn hình ánh xạ đếm mỗi lần mở; đánh index phần
  // để không phải quét cả bảng ánh xạ của mọi máy.
  await db.schema
    .createIndex('device_analyte_map_pending_idx')
    .on('device_analyte_map')
    .column('device_id')
    .where(sql`pending_decision` as never)
    .execute()
}

export async function down(db: Kysely<unknown>): Promise<void> {
  await db.schema.dropIndex('device_analyte_map_pending_idx').execute()
  await db.schema.dropIndex('analytes_active_idx').execute()
  await db.schema.dropIndex('analytes_source_idx').execute()
  await db.schema.alterTable('analytes').dropColumn('manually_edited').execute()
  await db.schema
    .alterTable('devices')
    .addColumn('has_sent_results', 'boolean', (c) => c.notNull().defaultTo(false))
    .execute()
  await db.schema.alterTable('devices').dropColumn('legacy_active').execute()
  await db.schema.alterTable('device_analyte_map').dropColumn('decision_candidates').execute()
  await db.schema.alterTable('device_analyte_map').dropColumn('pending_decision').execute()
}
