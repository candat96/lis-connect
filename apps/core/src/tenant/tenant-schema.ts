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
}
