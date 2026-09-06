import { type Kysely, sql } from 'kysely'

export async function up(db: Kysely<unknown>): Promise<void> {
  await db.schema
    .createTable('analytes')
    // Mã cũ của hệ LIS đang chạy (123AST, 400TPTNT) là khoá nghiệp vụ.
    // Giữ nguyên để HIS và kỹ thuật viên không phải học lại.
    .addColumn('code', 'varchar(64)', (c) => c.primaryKey())
    .addColumn('name', 'text', (c) => c.notNull())
    .addColumn('name_en', 'text')
    .addColumn('category', 'varchar(64)')
    .addColumn('unit', 'varchar(32)')
    .addColumn('decimals', 'integer')
    .addColumn('normal_range_text', 'text')
    .addColumn('lower_limit', 'numeric')
    .addColumn('higher_limit', 'numeric')
    .addColumn('comment_lower', 'text')
    .addColumn('comment_higher', 'text')
    .addColumn('comment_gray', 'text')
    .addColumn('criteria', 'text')
    // false: cột kết quả giữ giá trị số, biện luận để riêng.
    // true: cột kết quả nhận luôn câu biện luận.
    .addColumn('result_is_comment', 'boolean', (c) => c.notNull().defaultTo(false))
    .addColumn('print_order', 'integer')
    .addColumn('is_profile_head', 'boolean', (c) => c.notNull().defaultTo(false))
    .addColumn('source', 'varchar(16)', (c) => c.notNull())
    .addColumn('active', 'boolean', (c) => c.notNull().defaultTo(true))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createIndex('analytes_name_idx')
    .on('analytes')
    .column('name')
    .execute()

  await db.schema
    .createTable('profiles')
    .addColumn('code', 'varchar(64)', (c) => c.primaryKey())
    .addColumn('name', 'text', (c) => c.notNull())
    // 'legacy-declared': có khai báo thành viên thật ở hệ cũ.
    // 'legacy-inferred': dựng lại từ thụt lề tên, CẦN kỹ thuật viên xác nhận.
    .addColumn('source', 'varchar(24)', (c) => c.notNull())
    .addColumn('confirmed', 'boolean', (c) => c.notNull().defaultTo(false))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createTable('profile_members')
    .addColumn('profile_code', 'varchar(64)', (c) =>
      c.notNull().references('profiles.code').onDelete('cascade'),
    )
    .addColumn('analyte_code', 'varchar(64)', (c) =>
      c.notNull().references('analytes.code').onDelete('cascade'),
    )
    .addColumn('display_order', 'integer', (c) => c.notNull().defaultTo(0))
    .addPrimaryKeyConstraint('profile_members_pk', ['profile_code', 'analyte_code'])
    .execute()

  await db.schema
    .createTable('device_analyte_map')
    // Bảng này KHÔNG tồn tại từ giai đoạn 1 — brief giả định sai. Giai đoạn 1
    // mô tả bảng này trong thiết kế nhưng quên đưa vào migration 001-initial,
    // và không task nào của giai đoạn 1 dùng tới nên không ai phát hiện. Task 1
    // của khối 2A tạo mới toàn bộ bảng thay vì ALTER như brief viết.
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('device_id', 'uuid', (c) => c.notNull().references('devices.id').onDelete('cascade'))
    // Mã kênh máy trả về (01, 19, GLU) — không phải khoá nghiệp vụ của chính nó,
    // chỉ duy nhất trong phạm vi một máy (xem UNIQUE bên dưới).
    .addColumn('channel_code', 'varchar(64)', (c) => c.notNull())
    .addColumn('analyte_code', 'varchar(64)', (c) => c.references('analytes.code'))
    .addColumn('round', 'integer')
    .addColumn('allow_insert', 'boolean', (c) => c.notNull().defaultTo(false))
    .addColumn('omit', 'boolean', (c) => c.notNull().defaultTo(false))
    .addColumn('keep_higher_sign', 'boolean', (c) => c.notNull().defaultTo(false))
    .addColumn('keep_lower_sign', 'boolean', (c) => c.notNull().defaultTo(false))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    // Thay đổi quan trọng nhất của khối 2A: hệ cũ cho phép hai xét nghiệm cùng
    // trỏ tới một mã kênh rồi gỡ nhập nhằng bằng TOP 1 theo alphabet. Trên
    // AU640, kênh 19 map cả 128LDL lẫn 244ACo (nồng độ cồn) — kết quả có thể
    // về nhầm ô mà không cảnh báo. Dữ liệu thật có 7 cặp như vậy.
    .addUniqueConstraint('device_analyte_map_channel_unique', ['device_id', 'channel_code'])
    .execute()

  await db.schema
    .createTable('result_mappings')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('device_id', 'uuid', (c) => c.notNull().references('devices.id').onDelete('cascade'))
    .addColumn('analyte_code', 'varchar(64)', (c) =>
      c.notNull().references('analytes.code').onDelete('cascade'),
    )
    .addColumn('result_in', 'text', (c) => c.notNull())
    .addColumn('result_out', 'text')
    .addColumn('comment', 'text')
    // 0 bình thường, 1 thấp, 2 cao — giữ đúng quy ước của hệ cũ
    .addColumn('color', 'integer')
    .addUniqueConstraint('result_mappings_unique', ['device_id', 'analyte_code', 'result_in'])
    .execute()

  await db.schema
    .createTable('his_analyte_map')
    .addColumn('his_service_code', 'varchar(64)', (c) => c.primaryKey())
    .addColumn('analyte_code', 'varchar(64)', (c) =>
      c.notNull().references('analytes.code').onDelete('cascade'),
    )
    .execute()

  await db.schema
    .createIndex('his_analyte_map_analyte_idx')
    .on('his_analyte_map')
    .column('analyte_code')
    .execute()

  await db.schema
    .createTable('reference_ranges')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('analyte_code', 'varchar(64)', (c) =>
      c.notNull().references('analytes.code').onDelete('cascade'),
    )
    // NULL nghĩa là áp dụng cho mọi máy
    .addColumn('device_id', 'uuid', (c) => c.references('devices.id').onDelete('cascade'))
    // NULL nghĩa là áp dụng cho mọi giới
    .addColumn('sex', 'varchar(8)')
    .addColumn('age_from_days', 'integer')
    .addColumn('age_to_days', 'integer')
    .addColumn('lower_limit', 'numeric')
    .addColumn('higher_limit', 'numeric')
    .addColumn('gray_lower', 'numeric')
    .addColumn('gray_upper', 'numeric')
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  // --- Mở rộng bảng có sẵn từ giai đoạn 1 ---

  await db.schema.alterTable('devices').addColumn('legacy_ins_id', 'integer').execute()
  await db.schema.alterTable('devices').addColumn('protocol', 'varchar(32)').execute()
  await db.schema.alterTable('devices').addColumn('location', 'varchar(64)').execute()
  // Ba nhãn hoạt động ĐỘC LẬP. Hệ cũ chỉ có một cờ `Active` và cả 20 máy đều bằng 1,
  // nên nó vô dụng. Dấu vết thật nằm ở chỗ có cấu hình cổng và từng gửi kết quả.
  await db.schema
    .alterTable('devices')
    .addColumn('has_port_config', 'boolean', (c) => c.notNull().defaultTo(false))
    .execute()
  await db.schema
    .alterTable('devices')
    .addColumn('has_sent_results', 'boolean', (c) => c.notNull().defaultTo(false))
    .execute()
}

export async function down(db: Kysely<unknown>): Promise<void> {
  for (const col of ['legacy_ins_id', 'protocol', 'location', 'has_port_config', 'has_sent_results']) {
    await db.schema.alterTable('devices').dropColumn(col).execute()
  }
  await db.schema.dropTable('reference_ranges').execute()
  await db.schema.dropTable('his_analyte_map').execute()
  await db.schema.dropTable('result_mappings').execute()
  await db.schema.dropTable('device_analyte_map').execute()
  await db.schema.dropTable('profile_members').execute()
  await db.schema.dropTable('profiles').execute()
  await db.schema.dropTable('analytes').execute()
}
