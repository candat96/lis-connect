import { type Kysely, sql } from 'kysely'

/**
 * Khối tiếp đón: bệnh nhân, phiếu chỉ định, ống bệnh phẩm, barcode.
 *
 * ═══ Quyết định 1: barcode dán theo ỐNG, không theo phiếu ═══
 *
 * Một phiếu chỉ định thường được chia thành nhiều ống theo loại bệnh phẩm
 * (máu EDTA cho công thức máu, máu đông cho sinh hoá, nước tiểu...). Kỹ thuật
 * viên dán barcode lên từng ống rồi đưa ống vào máy; máy đọc barcode CỦA ỐNG.
 * Vì vậy `specimens` là bảng riêng và barcode nằm ở đó, không nằm ở `orders`.
 * Nếu đặt barcode ở phiếu thì hai ống của cùng một phiếu sẽ mang cùng một mã,
 * và khi máy trả kết quả về ta không biết nó đo ống nào.
 *
 * ═══ Quyết định 2: đuôi barcode là một dãy KHÔNG QUAY VÒNG THEO NGÀY ═══
 *
 * Hệ cũ sinh barcode `ddmmyy-<số>` với phần số ĐẾM LẠI TỪ ĐẦU mỗi ngày. Máy xét
 * nghiệm chỉ gửi về phần sau dấu gạch, nên hệ cũ phải tra ngược mờ
 * `SID LIKE '%-' + <đuôi>` trong 10 ngày rồi "lấy bản ghi mới nhất". Chính tài
 * liệu khảo sát ghi nhận: hai bệnh nhân trùng đuôi trong 10 ngày sẽ khớp nhầm —
 * kết quả của người này gắn sang hồ sơ người khác.
 *
 * Ở đây phần số lấy từ một SEQUENCE của Postgres, không bao giờ đặt lại, không
 * quay vòng (`no cycle`), và cột `barcode_serial` mang ràng buộc UNIQUE trên
 * TOÀN BẢNG — không giới hạn 10 ngày, 30 ngày hay bất kỳ cửa sổ nào. Nhờ vậy
 * đuôi mà máy gửi về xác định DUY NHẤT một ống, vĩnh viễn, và việc tra ngược là
 * một phép so khớp CHÍNH XÁC trên số nguyên có index, không phải `LIKE`.
 *
 * Ngày `ddmmyy` vẫn được in ở đầu barcode cho người đọc bằng mắt, nhưng nó
 * KHÔNG tham gia vào việc định danh — mất phần ngày cũng không mất tính duy nhất.
 *
 * ═══ Quyết định 3: chỉ dịch vụ xét nghiệm mới được chỉ định — chặn ở database ═══
 *
 * `analytes.service_kind` phân biệt dịch vụ xét nghiệm với dịch vụ khám bệnh.
 * Danh mục hiện tại toàn là xét nghiệm nên toàn bộ 2619 dòng được backfill
 * 'xet_nghiem'; nhưng khi tiếp đón nhận dữ liệu từ HIS (nơi có cả dịch vụ khám)
 * ranh giới phải sẵn sàng. Ranh giới đó được chốt bằng KHOÁ NGOẠI GHÉP chứ
 * không chỉ bằng lệnh `if` trong service: `order_items` có cột hằng
 * `service_kind` với CHECK ép bằng 'xet_nghiem', và khoá ngoại
 * (analyte_code, service_kind) → analytes(code, service_kind). Chỉ định một
 * dịch vụ khám bị Postgres từ chối, kể cả khi tầng ứng dụng có lỗ hổng.
 */
export async function up(db: Kysely<unknown>): Promise<void> {
  // ---------------------------------------------------------- ranh giới dịch vụ

  await db.schema
    .alterTable('analytes')
    // 'xet_nghiem' | 'kham_benh' | 'khac'. Mặc định 'xet_nghiem' vì toàn bộ
    // danh mục di trú là xét nghiệm; dữ liệu về sau từ HIS phải khai tường minh.
    .addColumn('service_kind', 'varchar(16)', (c) => c.notNull().defaultTo('xet_nghiem'))
    .execute()

  await sql`
    alter table analytes
      add constraint analytes_service_kind_check
      check (service_kind in ('xet_nghiem', 'kham_benh', 'khac'))
  `.execute(db)

  // Đích của khoá ngoại ghép ở order_items. Không thừa với khoá chính `code`:
  // Postgres đòi đích của khoá ngoại phải là một ràng buộc unique đúng bộ cột.
  await db.schema
    .alterTable('analytes')
    .addUniqueConstraint('analytes_code_service_kind_unique', ['code', 'service_kind'])
    .execute()

  await db.schema
    .createIndex('analytes_service_kind_idx')
    .on('analytes')
    .column('service_kind')
    .execute()

  // --------------------------------------------------------------- bệnh nhân

  await db.schema
    .createTable('patients')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    // Mã bệnh nhân của chính LIS. Sinh tự động từ sequence nếu tiếp đón không
    // nhập; duy nhất trong phạm vi database của một bệnh viện.
    .addColumn('patient_code', 'varchar(32)', (c) => c.notNull().unique())
    .addColumn('full_name', 'text', (c) => c.notNull())
    // Ba cách khai tuổi, dùng cái nào có. Thực tế Việt Nam rất hay chỉ có tuổi:
    // bệnh nhân lớn tuổi không nhớ ngày sinh, giấy tờ chỉ ghi năm. Ép nhập đủ
    // ngày sinh sẽ đẻ ra hàng loạt ngày 01/01 bịa — mà khoảng tham chiếu theo
    // tuổi lại đọc chính cột này.
    .addColumn('birth_date', 'date')
    .addColumn('birth_year', 'integer')
    .addColumn('age_years', 'integer')
    // Trẻ sơ sinh: tuổi tính bằng tháng mới có nghĩa cho khoảng tham chiếu.
    .addColumn('age_months', 'integer')
    // 'nam' | 'nu' | 'khac'
    .addColumn('sex', 'varchar(8)', (c) => c.notNull())
    .addColumn('address', 'text')
    .addColumn('phone', 'varchar(32)')
    // Mã bệnh nhân bên HIS. Không unique: HIS có thể chưa trả, và một bệnh nhân
    // tự tạo tại LIS rồi mới được đối chiếu sang HIS sau.
    .addColumn('his_patient_code', 'varchar(64)')
    .addColumn('department', 'varchar(128)')
    // Đối tượng thanh toán: 'bhyt' | 'dich_vu' | 'mien_phi' | 'khac'...
    // Để varchar tự do có chủ ý — danh mục đối tượng khác nhau giữa các bệnh viện.
    .addColumn('patient_type', 'varchar(32)', (c) => c.notNull().defaultTo('dich_vu'))
    .addColumn('note', 'text')
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .addColumn('updated_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await sql`
    alter table patients
      add constraint patients_sex_check check (sex in ('nam', 'nu', 'khac'))
  `.execute(db)

  // Một bệnh nhân không có bất kỳ thông tin tuổi nào là dữ liệu không dùng được:
  // khoảng tham chiếu theo tuổi (bảng reference_ranges) sẽ không áp được, và
  // kết quả sẽ được so với ngưỡng người lớn cho cả trẻ sơ sinh.
  await sql`
    alter table patients
      add constraint patients_age_present_check
      check (birth_date is not null or birth_year is not null
             or age_years is not null or age_months is not null)
  `.execute(db)

  // Tìm theo tên là thao tác thường xuyên nhất của tiếp đón. Index trên
  // lower(full_name) để `ilike '...%'` dùng được index cho phần tiền tố.
  await sql`create index patients_full_name_lower_idx on patients (lower(full_name))`.execute(db)
  await db.schema.createIndex('patients_phone_idx').on('patients').column('phone').execute()
  await db.schema
    .createIndex('patients_his_code_idx')
    .on('patients')
    .column('his_patient_code')
    .execute()

  // Mã bệnh nhân tự sinh: BN + số thứ tự không quay vòng.
  await sql`create sequence patient_code_seq as bigint start with 1 no cycle`.execute(db)

  // ------------------------------------------------------------------- phiếu

  await db.schema
    .createTable('orders')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    // Số phiếu cho người đọc. KHÁC barcode: barcode định danh ỐNG, số phiếu
    // định danh tờ chỉ định.
    .addColumn('order_no', 'varchar(32)', (c) => c.notNull().unique())
    .addColumn('patient_id', 'uuid', (c) =>
      c.notNull().references('patients.id').onDelete('restrict'),
    )
    .addColumn('ordered_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .addColumn('ordering_doctor', 'text')
    .addColumn('diagnosis', 'text')
    .addColumn('department', 'varchar(128)')
    // 'moi' | 'da_lay_mau' | 'dang_chay' | 'co_ket_qua' | 'da_duyet' | 'huy'
    .addColumn('status', 'varchar(16)', (c) => c.notNull().defaultTo('moi'))
    // 'lis': tiếp đón tự tạo tại LIS. 'his': nhận từ hệ thống bệnh viện.
    .addColumn('source', 'varchar(8)', (c) => c.notNull().defaultTo('lis'))
    // Mã phiếu bên HIS, để đối chiếu và trả kết quả về đúng chỗ.
    .addColumn('his_order_code', 'varchar(64)')
    .addColumn('note', 'text')
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .addColumn('updated_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await sql`
    alter table orders
      add constraint orders_status_check
      check (status in ('moi', 'da_lay_mau', 'dang_chay', 'co_ket_qua', 'da_duyet', 'huy'))
  `.execute(db)

  await sql`
    alter table orders add constraint orders_source_check check (source in ('lis', 'his'))
  `.execute(db)

  await db.schema
    .createIndex('orders_patient_idx')
    .on('orders')
    .columns(['patient_id', 'ordered_at'])
    .execute()
  await db.schema.createIndex('orders_ordered_at_idx').on('orders').column('ordered_at').execute()

  await sql`create sequence order_no_seq as bigint start with 1 no cycle`.execute(db)

  // ------------------------------------------------------------- ống bệnh phẩm

  // Dãy sinh phần đuôi barcode. `no cycle` là điều kiện sống còn của toàn bộ
  // thiết kế: quay vòng nghĩa là một ngày nào đó hai ống mang cùng một đuôi, và
  // ta rơi lại đúng cái bẫy của hệ cũ. bigint nên không có ngày đó — nếu có
  // tràn thật thì Postgres sẽ BÁO LỖI khi cấp số, chứ không lặng lẽ lặp lại.
  await sql`create sequence specimen_barcode_seq as bigint start with 1 no cycle`.execute(db)

  await db.schema
    .createTable('specimens')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('order_id', 'uuid', (c) => c.notNull().references('orders.id').onDelete('cascade'))
    // Loại bệnh phẩm: 'mau_edta', 'huyet_thanh', 'nuoc_tieu', 'mau_citrat'...
    .addColumn('specimen_type', 'varchar(32)', (c) => c.notNull())
    // Barcode đầy đủ in lên ống: 'ddmmyy-<serial 7 chữ số>'.
    .addColumn('barcode', 'varchar(32)', (c) => c.notNull().unique())
    // Phần đuôi — chính là thứ máy xét nghiệm gửi về. UNIQUE trên TOÀN BẢNG,
    // không giới hạn cửa sổ thời gian: đây là ràng buộc khiến việc khớp ngược
    // theo đuôi không thể nhập nhằng.
    .addColumn('barcode_serial', 'bigint', (c) => c.notNull().unique())
    .addColumn('collected_at', 'timestamptz')
    // 'cho_lay_mau' | 'da_lay_mau' | 'da_nhan' | 'da_chay' | 'huy'
    .addColumn('status', 'varchar(16)', (c) => c.notNull().defaultTo('cho_lay_mau'))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await sql`
    alter table specimens
      add constraint specimens_status_check
      check (status in ('cho_lay_mau', 'da_lay_mau', 'da_nhan', 'da_chay', 'huy'))
  `.execute(db)

  await sql`
    alter table specimens add constraint specimens_serial_positive_check check (barcode_serial > 0)
  `.execute(db)

  // Một phiếu chỉ có MỘT ống cho mỗi loại bệnh phẩm; hai ống cùng loại trong
  // cùng phiếu chỉ là dấu hiệu bấm nhầm nút hai lần.
  await sql`
    alter table specimens
      add constraint specimens_order_type_unique unique (order_id, specimen_type)
  `.execute(db)

  await db.schema.createIndex('specimens_order_idx').on('specimens').column('order_id').execute()

  // ------------------------------------------------------- dịch vụ trong phiếu

  await db.schema
    .createTable('order_items')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('order_id', 'uuid', (c) => c.notNull().references('orders.id').onDelete('cascade'))
    // Ống chứa bệnh phẩm cho dịch vụ này. NULL nghĩa là chưa phân ống.
    .addColumn('specimen_id', 'uuid', (c) => c.references('specimens.id').onDelete('set null'))
    .addColumn('analyte_code', 'varchar(64)', (c) => c.notNull())
    // Cột hằng phục vụ khoá ngoại ghép bên dưới — xem ghi chú đầu tệp.
    .addColumn('service_kind', 'varchar(16)', (c) => c.notNull().defaultTo('xet_nghiem'))
    // Chỉ định một nhóm (ví dụ 002HD — tổng phân tích tế bào máu, 20 chỉ số con)
    // nghĩa là chỉ định cả các con. Con được ghi thành DÒNG RIÊNG, trỏ ngược về
    // mã nhóm ở đây, vì kết quả về từ máy là kết quả của từng chỉ số con.
    .addColumn('parent_analyte_code', 'varchar(64)')
    // 'cho' | 'dang_chay' | 'co_ket_qua' | 'da_duyet' | 'huy'
    .addColumn('status', 'varchar(16)', (c) => c.notNull().defaultTo('cho'))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    // Không cho một dịch vụ trùng trong cùng một phiếu — ràng buộc ở tầng
    // database, không phải chỉ ở tầng ứng dụng. Đây cũng là chỗ neo của
    // `on conflict do nothing` khi bung nhóm ra các chỉ số con: một chỉ số vừa
    // được chỉ định lẻ vừa nằm trong nhóm chỉ được tính một lần.
    .addUniqueConstraint('order_items_order_analyte_unique', ['order_id', 'analyte_code'])
    .execute()

  await sql`
    alter table order_items
      add constraint order_items_status_check
      check (status in ('cho', 'dang_chay', 'co_ket_qua', 'da_duyet', 'huy'))
  `.execute(db)

  // Chốt ranh giới "chỉ dịch vụ xét nghiệm" ở tầng database.
  await sql`
    alter table order_items
      add constraint order_items_lab_only_check check (service_kind = 'xet_nghiem')
  `.execute(db)

  await db.schema
    .alterTable('order_items')
    .addForeignKeyConstraint(
      'order_items_analyte_fk',
      ['analyte_code', 'service_kind'],
      'analytes',
      ['code', 'service_kind'],
    )
    .onDelete('restrict')
    .execute()

  await db.schema
    .alterTable('order_items')
    .addForeignKeyConstraint('order_items_parent_fk', ['parent_analyte_code'], 'profiles', ['code'])
    .onDelete('set null')
    .execute()

  await db.schema
    .createIndex('order_items_order_idx')
    .on('order_items')
    .column('order_id')
    .execute()
  await db.schema
    .createIndex('order_items_specimen_idx')
    .on('order_items')
    .column('specimen_id')
    .execute()
}

export async function down(db: Kysely<unknown>): Promise<void> {
  await db.schema.dropTable('order_items').execute()
  await db.schema.dropTable('specimens').execute()
  await db.schema.dropTable('orders').execute()
  await db.schema.dropTable('patients').execute()
  await sql`drop sequence if exists specimen_barcode_seq`.execute(db)
  await sql`drop sequence if exists order_no_seq`.execute(db)
  await sql`drop sequence if exists patient_code_seq`.execute(db)
  await db.schema.dropIndex('analytes_service_kind_idx').execute()
  await sql`alter table analytes drop constraint analytes_code_service_kind_unique`.execute(db)
  await sql`alter table analytes drop constraint analytes_service_kind_check`.execute(db)
  await db.schema.alterTable('analytes').dropColumn('service_kind').execute()
}
