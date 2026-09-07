import type { ColumnType, Generated } from 'kysely'

export type DeviceTransport = 'serial' | 'tcp' | 'file'
export type DeviceConnStatus = 'connected' | 'disconnected' | 'error'
export type RawDirection = 'inbound' | 'outbound'
export type RawParseStatus = 'pending' | 'parsed' | 'parse_error'

export interface AgentsTable {
  id: Generated<string>
  /** agent_keys.id ở DB quản trị. Không có khoá ngoại vì khác database. */
  agent_key_id: string
  hostname: string
  agent_version: string
  last_seen_at: ColumnType<Date | null, Date | null | undefined, Date | null>
  created_at: Generated<Date>
}

export interface DevicesTable {
  id: Generated<string>
  agent_id: string
  /** Mã do Agent đặt, duy nhất trong phạm vi một Agent. */
  device_key: string
  name: string
  transport: DeviceTransport
  driver: string
  enabled: Generated<boolean>
  created_at: Generated<Date>
  /** ID của máy trong hệ LIS cũ (bảng INS) — dùng để đối chiếu khi di trú. */
  legacy_ins_id: ColumnType<number | null, number | null | undefined, number | null>
  protocol: ColumnType<string | null, string | null | undefined, string | null>
  location: ColumnType<string | null, string | null | undefined, string | null>
  has_port_config: Generated<boolean>
  /**
   * Cờ `Instrument.Active` của hệ LIS cũ, giữ nguyên nghĩa của hệ cũ ("bệnh
   * viện còn dùng máy này không"). KHÁC HẲN `enabled` — thứ trả lời "có cho
   * Agent kết nối tới máy này không". NULL nghĩa là máy không đến từ hệ cũ.
   *
   * Không có `has_sent_results` ở đây: nguồn sự thật duy nhất cho "máy đã từng
   * gửi kết quả chưa" là `device_status.last_message_at`.
   */
  legacy_active: ColumnType<boolean | null, boolean | null | undefined, boolean | null>
}

export interface RawMessagesTable {
  id: Generated<string>
  /** messageId của Agent — khoá idempotent. */
  message_id: string
  device_id: string
  direction: RawDirection
  payload: Buffer
  parse_status: Generated<RawParseStatus>
  parse_error: ColumnType<string | null, string | null | undefined, string | null>
  received_at: Generated<Date>
  sent_at: Date
}

export interface ConnectionEventsTable {
  id: Generated<string>
  message_id: string
  device_id: string
  status: DeviceConnStatus
  detail: ColumnType<string | null, string | null | undefined, string | null>
  occurred_at: Date
  created_at: Generated<Date>
}

export interface DeviceStatusTable {
  device_id: string
  status: DeviceConnStatus
  detail: ColumnType<string | null, string | null | undefined, string | null>
  last_message_at: ColumnType<Date | null, Date | null | undefined, Date | null>
  updated_at: Date
}

export type AnalyteSource = 'legacy' | 'legacy-new' | 'manual'
export type ProfileSource = 'legacy-declared' | 'legacy-inferred' | 'manual'

export interface AnalytesTable {
  /** Mã cũ của hệ LIS đang chạy (123AST, 400TPTNT) — khoá nghiệp vụ, giữ nguyên. */
  code: string
  name: string
  name_en: ColumnType<string | null, string | null | undefined, string | null>
  category: ColumnType<string | null, string | null | undefined, string | null>
  unit: ColumnType<string | null, string | null | undefined, string | null>
  decimals: ColumnType<number | null, number | null | undefined, number | null>
  normal_range_text: ColumnType<string | null, string | null | undefined, string | null>
  /** numeric của Postgres về dưới dạng chuỗi qua `pg` — không mất độ chính xác. */
  lower_limit: ColumnType<string | null, string | number | null | undefined, string | number | null>
  higher_limit: ColumnType<string | null, string | number | null | undefined, string | number | null>
  comment_lower: ColumnType<string | null, string | null | undefined, string | null>
  comment_higher: ColumnType<string | null, string | null | undefined, string | null>
  comment_gray: ColumnType<string | null, string | null | undefined, string | null>
  criteria: ColumnType<string | null, string | null | undefined, string | null>
  result_is_comment: Generated<boolean>
  print_order: ColumnType<number | null, number | null | undefined, number | null>
  is_profile_head: Generated<boolean>
  source: AnalyteSource
  /**
   * Ranh giới "chỉ dịch vụ xét nghiệm mới được chỉ định".
   *
   * Danh mục di trú toàn là xét nghiệm nên mặc định 'xet_nghiem'; nhưng khối
   * tiếp đón về sau nhận dữ liệu từ HIS, nơi có cả dịch vụ khám bệnh. Ranh giới
   * này được CHỐT ở tầng database bằng khoá ngoại ghép từ `order_items`, không
   * chỉ bằng một phép kiểm trong service.
   */
  service_kind: Generated<ServiceKind>
  active: Generated<boolean>
  /**
   * Dòng này đã bị sửa tay từ giao diện hay chưa. Tách khỏi `source` có chủ ý:
   * `source` là NGUỒN GỐC (bảng nào của hệ cũ) và phải giữ được để lọc bớt
   * danh mục 2619 dòng; ép nó về 'manual' mỗi lần sửa là xoá vĩnh viễn nguồn gốc.
   */
  manually_edited: Generated<boolean>
  created_at: Generated<Date>
}

export interface ProfilesTable {
  code: string
  name: string
  /**
   * 'legacy-declared': có khai báo thành viên thật ở hệ cũ.
   * 'legacy-inferred': dựng lại từ thụt lề tên, CẦN kỹ thuật viên xác nhận.
   */
  source: ProfileSource
  confirmed: Generated<boolean>
  created_at: Generated<Date>
}

export interface ProfileMembersTable {
  profile_code: string
  analyte_code: string
  display_order: Generated<number>
}

export interface DeviceAnalyteMapTable {
  id: Generated<string>
  device_id: string
  /** Mã kênh máy trả về (01, 19, GLU) — duy nhất trong phạm vi một máy. */
  channel_code: string
  analyte_code: ColumnType<string | null, string | null | undefined, string | null>
  /**
   * true: mã kênh này ĐANG XUNG ĐỘT VÀ CHƯA AI QUYẾT — hệ cũ khai nhiều xét
   * nghiệm cho cùng một mã kênh, phần mềm không được chọn hộ. Khác hẳn
   * `analyte_code IS NULL` đơn thuần ("chưa gán" — không ai khai, là chỗ bị quên).
   */
  pending_decision: Generated<boolean>
  /** Các mã xét nghiệm ứng viên của một mã kênh chưa quyết. */
  decision_candidates: ColumnType<string[] | null, string | null | undefined, string | null>
  round: ColumnType<number | null, number | null | undefined, number | null>
  allow_insert: Generated<boolean>
  omit: Generated<boolean>
  keep_higher_sign: Generated<boolean>
  keep_lower_sign: Generated<boolean>
  created_at: Generated<Date>
}

export interface ResultMappingsTable {
  id: Generated<string>
  /** NULL nghĩa là quy tắc áp dụng cho mọi máy (ví dụ: diễn giải TPHA). */
  device_id: ColumnType<string | null, string | null | undefined, string | null>
  analyte_code: string
  result_in: string
  result_out: ColumnType<string | null, string | null | undefined, string | null>
  comment: ColumnType<string | null, string | null | undefined, string | null>
  /** 0 bình thường, 1 thấp, 2 cao — giữ đúng quy ước của hệ cũ. */
  color: ColumnType<number | null, number | null | undefined, number | null>
}

export interface HisAnalyteMapTable {
  his_service_code: string
  analyte_code: string
}

export interface ReferenceRangesTable {
  id: Generated<string>
  analyte_code: string
  /** NULL nghĩa là áp dụng cho mọi máy. */
  device_id: ColumnType<string | null, string | null | undefined, string | null>
  /** NULL nghĩa là áp dụng cho mọi giới. */
  sex: ColumnType<string | null, string | null | undefined, string | null>
  age_from_days: ColumnType<number | null, number | null | undefined, number | null>
  age_to_days: ColumnType<number | null, number | null | undefined, number | null>
  lower_limit: ColumnType<string | null, string | number | null | undefined, string | number | null>
  higher_limit: ColumnType<string | null, string | number | null | undefined, string | number | null>
  gray_lower: ColumnType<string | null, string | number | null | undefined, string | number | null>
  gray_upper: ColumnType<string | null, string | number | null | undefined, string | number | null>
  created_at: Generated<Date>
}

/**
 * Một dòng nhật ký sửa đổi danh mục. `before_value`/`after_value` giữ nguyên
 * hình dạng dòng dữ liệu tại thời điểm trước và sau khi sửa (jsonb), nên đọc
 * lại được kể cả sau khi lược đồ bảng gốc thay đổi.
 */
export interface CatalogueAuditTable {
  id: Generated<string>
  changed_at: Generated<Date>
  /** users.id ở DB quản trị — không khoá ngoại vì khác database. */
  actor_user_id: string
  actor_username: string
  /** 'analyte' | 'device_analyte_map' | 'result_mapping' */
  entity: string
  entity_key: string
  /** 'create' | 'update' | 'delete' */
  action: string
  before_value: ColumnType<unknown, string | null | undefined, string | null>
  after_value: ColumnType<unknown, string | null | undefined, string | null>
}


export type ServiceKind = 'xet_nghiem' | 'kham_benh' | 'khac'
export type PatientSex = 'nam' | 'nu' | 'khac'
export type OrderStatus = 'moi' | 'da_lay_mau' | 'dang_chay' | 'co_ket_qua' | 'da_duyet' | 'huy'
export type OrderSource = 'lis' | 'his'
export type SpecimenStatus = 'cho_lay_mau' | 'da_lay_mau' | 'da_nhan' | 'da_chay' | 'huy'
export type OrderItemStatus = 'cho' | 'dang_chay' | 'co_ket_qua' | 'da_duyet' | 'huy'

export interface PatientsTable {
  id: Generated<string>
  /** Mã bệnh nhân của LIS, sinh từ sequence nếu tiếp đón không nhập. */
  patient_code: string
  full_name: string
  /**
   * Ba cách khai tuổi vì thực tế Việt Nam rất hay chỉ có tuổi, không có ngày
   * sinh. Ràng buộc CHECK ở database đòi có ÍT NHẤT MỘT trong bốn cột tuổi.
   */
  birth_date: ColumnType<string | null, string | null | undefined, string | null>
  birth_year: ColumnType<number | null, number | null | undefined, number | null>
  age_years: ColumnType<number | null, number | null | undefined, number | null>
  age_months: ColumnType<number | null, number | null | undefined, number | null>
  sex: PatientSex
  address: ColumnType<string | null, string | null | undefined, string | null>
  phone: ColumnType<string | null, string | null | undefined, string | null>
  /** Mã bệnh nhân bên HIS — không unique, HIS có thể chưa trả. */
  his_patient_code: ColumnType<string | null, string | null | undefined, string | null>
  department: ColumnType<string | null, string | null | undefined, string | null>
  patient_type: Generated<string>
  note: ColumnType<string | null, string | null | undefined, string | null>
  created_at: Generated<Date>
  updated_at: Generated<Date>
}

export interface OrdersTable {
  id: Generated<string>
  /** Số phiếu cho người đọc — KHÁC barcode, thứ định danh từng ống. */
  order_no: string
  patient_id: string
  ordered_at: Generated<Date>
  ordering_doctor: ColumnType<string | null, string | null | undefined, string | null>
  diagnosis: ColumnType<string | null, string | null | undefined, string | null>
  department: ColumnType<string | null, string | null | undefined, string | null>
  status: Generated<OrderStatus>
  source: Generated<OrderSource>
  his_order_code: ColumnType<string | null, string | null | undefined, string | null>
  note: ColumnType<string | null, string | null | undefined, string | null>
  created_at: Generated<Date>
  updated_at: Generated<Date>
}

/**
 * Ống bệnh phẩm. Barcode nằm ở ĐÂY chứ không ở `orders`: kỹ thuật viên dán mã
 * lên từng ống và máy xét nghiệm đọc mã của ống. Một phiếu chia thành nhiều ống
 * theo loại bệnh phẩm.
 */
export interface SpecimensTable {
  id: Generated<string>
  order_id: string
  specimen_type: string
  /** Mã đầy đủ in lên ống: `ddmmyy-<serial>`. */
  barcode: string
  /**
   * Phần đuôi barcode — chính là thứ máy xét nghiệm gửi về.
   *
   * Lấy từ sequence không quay vòng và mang UNIQUE trên TOÀN BẢNG, nên khớp
   * ngược theo đuôi là phép so khớp CHÍNH XÁC trên số nguyên, không phải `LIKE`
   * mờ trong cửa sổ 10 ngày như hệ cũ. `bigint` về từ `pg` dưới dạng chuỗi.
   */
  barcode_serial: ColumnType<string, string | number | bigint, never>
  collected_at: ColumnType<Date | null, Date | null | undefined, Date | null>
  status: Generated<SpecimenStatus>
  created_at: Generated<Date>
}

export interface OrderItemsTable {
  id: Generated<string>
  order_id: string
  specimen_id: ColumnType<string | null, string | null | undefined, string | null>
  analyte_code: string
  /**
   * Cột hằng, luôn bằng 'xet_nghiem'. Tồn tại để làm nửa còn lại của khoá ngoại
   * ghép (analyte_code, service_kind) → analytes(code, service_kind): Postgres
   * từ chối chỉ định một dịch vụ khám bệnh, kể cả khi tầng ứng dụng có lỗ hổng.
   */
  service_kind: Generated<'xet_nghiem'>
  /** Mã nhóm đã bung ra dòng này; NULL nếu được chỉ định lẻ. */
  parent_analyte_code: ColumnType<string | null, string | null | undefined, string | null>
  status: Generated<OrderItemStatus>
  created_at: Generated<Date>
}

export interface TenantDatabase {
  agents: AgentsTable
  devices: DevicesTable
  raw_messages: RawMessagesTable
  connection_events: ConnectionEventsTable
  device_status: DeviceStatusTable
  analytes: AnalytesTable
  profiles: ProfilesTable
  profile_members: ProfileMembersTable
  device_analyte_map: DeviceAnalyteMapTable
  result_mappings: ResultMappingsTable
  his_analyte_map: HisAnalyteMapTable
  reference_ranges: ReferenceRangesTable
  catalogue_audit: CatalogueAuditTable
  patients: PatientsTable
  orders: OrdersTable
  specimens: SpecimensTable
  order_items: OrderItemsTable
}
