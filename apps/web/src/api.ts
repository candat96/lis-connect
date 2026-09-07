const BASE_URL = import.meta.env.VITE_CORE_URL ?? 'http://localhost:3000'
const SESSION_STORAGE_KEY = 'lis.session'

export interface DeviceSummary {
  id: string
  deviceKey: string
  name: string
  transport: string
  driver: string
  status: string
  lastMessageAt: string | null
  /**
   * Bốn nhãn tình trạng ĐỘC LẬP, đều do `GET /api/devices` trả về:
   *
   *   - `enabled`: hệ MỚI có cho Agent kết nối tới máy này không;
   *   - `legacyActive`: hệ CŨ đánh dấu bệnh viện còn dùng máy này không
   *     (`null` = máy không đến từ hệ cũ nên không có thông tin);
   *   - `hasPortConfig`: hệ cũ có khai cấu hình cổng cho máy này không — nằm
   *     trong database nghiệp vụ, do chính công cụ di trú của khối 2A ghi vào
   *     (bản ghi chú cũ ở đây nói cờ này "nằm ở Agent" là SAI);
   *   - `hasSentResults`: máy đã từng gửi kết quả về chưa, Core suy từ
   *     `device_status.last_message_at`.
   *
   * Vẫn khai `?` cho ba cờ boolean: gặp một Core cũ hơn không trả về thì giao
   * diện nói thẳng "chưa rõ" thay vì đoán bừa là `false` — nói máy "chưa từng
   * gửi kết quả" khi thực ra không biết sẽ đẩy kỹ thuật viên đi dò nhầm hướng.
   */
  enabled?: boolean
  legacyActive?: boolean | null
  hasPortConfig?: boolean
  hasSentResults?: boolean
}

export interface RawMessageView {
  id: string
  direction: string
  receivedAt: string
  parseStatus: string
  payloadBase64: string
  payloadText: string
}

export interface ConnectionEventView {
  id: string
  status: string
  detail: string | null
  occurredAt: string
}

export interface SessionUser {
  username: string
  fullName: string
  role: string
}

export interface SessionTenant {
  code: string
  name: string
}

export interface Session {
  token: string
  user: SessionUser
  tenant: SessionTenant
}

/**
 * Thông báo lỗi đăng nhập/hết phiên hiển thị cho người dùng. Đăng nhập sai
 * (Core trả 401) và mất kết nối máy chủ dùng hai thông báo khác nhau — nhưng
 * "sai mật khẩu" và "sai tên đăng nhập" phải dùng CHUNG một thông báo, việc
 * đó được đảm bảo ở phía Core (AuthService), không phải ở đây.
 */
export class LoginError extends Error {}

let listeners: Array<(reason: string) => void> = []

/**
 * Lưu/đọc phiên đăng nhập ở localStorage và phát tín hiệu khi phiên bị Core
 * từ chối (401) giữa lúc dùng — ví dụ token hết hạn sau 12 giờ trực. App
 * đăng ký lắng nghe để đưa người dùng về màn hình đăng nhập kèm lý do rõ
 * ràng, thay vì để các vòng polling âm thầm lỗi mãi hoặc màn hình trắng.
 */
export const session = {
  get(): Session | null {
    const raw = localStorage.getItem(SESSION_STORAGE_KEY)
    if (!raw) return null
    try {
      return JSON.parse(raw) as Session
    } catch {
      return null
    }
  },
  set(value: Session): void {
    localStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify(value))
  },
  clear(): void {
    localStorage.removeItem(SESSION_STORAGE_KEY)
  },
  /** Đăng ký lắng nghe mất phiên. Trả về hàm để huỷ đăng ký. */
  onExpired(cb: (reason: string) => void): () => void {
    listeners.push(cb)
    return () => {
      listeners = listeners.filter((l) => l !== cb)
    }
  },
}

function notifyExpired(reason: string): void {
  session.clear()
  for (const cb of listeners) cb(reason)
}

async function get<T>(path: string): Promise<T> {
  const current = session.get()
  const response = await fetch(`${BASE_URL}${path}`, {
    headers: current ? { Authorization: `Bearer ${current.token}` } : {},
  })
  if (response.status === 401) {
    notifyExpired('Phiên đăng nhập đã hết hạn hoặc không hợp lệ — vui lòng đăng nhập lại.')
    throw new Error('unauthorized')
  }
  if (!response.ok) {
    throw new Error(`gọi ${path} thất bại: ${response.status} ${response.statusText}`)
  }
  return (await response.json()) as T
}

export async function login(tenantCode: string, username: string, password: string): Promise<Session> {
  let response: Response
  try {
    response = await fetch(`${BASE_URL}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ tenantCode, username, password }),
    })
  } catch {
    throw new LoginError('Không kết nối được tới máy chủ Core — kiểm tra mạng rồi thử lại.')
  }

  if (response.status === 401) {
    throw new LoginError('Sai mã cơ sở, tên đăng nhập hoặc mật khẩu.')
  }
  if (!response.ok) {
    throw new LoginError('Máy chủ Core đang gặp sự cố — thử lại sau ít phút.')
  }

  const value = (await response.json()) as Session
  session.set(value)
  return value
}

export function logout(): void {
  session.clear()
}

export const api = {
  devices: () => get<DeviceSummary[]>('/api/devices'),
  rawMessages: (deviceId: string) =>
    get<RawMessageView[]>(`/api/devices/${deviceId}/raw-messages?limit=100`),
  connectionEvents: (deviceId: string) =>
    get<ConnectionEventView[]>(`/api/devices/${deviceId}/connection-events?limit=100`),
}

/* ------------------------------------------------------------------ danh mục
 *
 * Ba màn hình quản lý danh mục (xét nghiệm, ánh xạ mã kênh, luật ghi đè) dùng
 * chung lớp gọi API dưới đây. Khác với phần theo dõi máy vốn chỉ đọc, ở đây có
 * ghi — nên mọi lỗi phải giữ được cả MÃ HTTP lẫn CÂU GIẢI THÍCH từ Core:
 *
 *   - 409 khi gán trùng mã kênh không phải là "lỗi hệ thống" mà là một quy tắc
 *     nghiệp vụ; giao diện cần đọc lại câu của Core ("kênh này đang gán cho
 *     xét nghiệm X, hãy xoá ánh xạ cũ trước") để chỉ đường cho người dùng thay
 *     vì hiện "Lỗi 409".
 *   - 400 mang tên trường sai, 404 mang mã không tìm thấy — đều là câu tiếng
 *     Việt đã viết sẵn ở Core, ném đi rồi thay bằng "có lỗi xảy ra" là phí.
 */

/** Lỗi API còn giữ mã HTTP và câu giải thích của Core để giao diện dùng lại. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

export interface AnalyteView {
  code: string
  name: string
  nameEn: string | null
  category: string | null
  unit: string | null
  decimals: number | null
  normalRangeText: string | null
  lowerLimit: number | null
  higherLimit: number | null
  commentLower: string | null
  commentHigher: string | null
  commentGray: string | null
  criteria: string | null
  resultIsComment: boolean
  printOrder: number | null
  isProfileHead: boolean
  /**
   * BA giá trị, không phải hai: 'legacy' (bảng danh mục thứ nhất của hệ cũ),
   * 'legacy-new' (bảng danh mục thứ hai), 'manual' (đã sửa tay từ giao diện
   * này). Giá trị lạ vẫn phải hiển thị được thay vì làm hỏng màn hình, nên
   * kiểu ở đây là `string` chứ không phải union đóng.
   */
  source: string
  active: boolean
}

export interface AnalyteSearchResult {
  items: AnalyteView[]
  /** Tổng số dòng khớp bộ lọc, độc lập với limit/offset. */
  total: number
}

export interface AnalytePatch {
  unit?: string | null
  decimals?: number | null
  lowerLimit?: number | null
  higherLimit?: number | null
  commentLower?: string | null
  commentHigher?: string | null
  commentGray?: string | null
  printOrder?: number | null
}

export interface ChannelMapView {
  id: string
  deviceId: string
  channelCode: string
  analyteCode: string | null
  analyteName: string | null
  /**
   * BA trạng thái, không phải hai. `unassigned` là "quên khai"; `undecided` là
   * "hệ cũ khai nhiều xét nghiệm cho cùng mã kênh, đang chờ người quyết". Gộp
   * lại thì màn hình lại nói "đã gán đủ" cho đúng những máy có xung đột.
   * Core cũ hơn không trả trường này: coi như suy từ `analyteCode`.
   */
  state?: 'assigned' | 'unassigned' | 'undecided'
  /** Nhãn tiếng Việt do Core dựng sẵn cho `state`. */
  stateLabel?: string
  /** Với `undecided`: các mã xét nghiệm ứng viên của hệ cũ. */
  decisionCandidates?: string[]
  round: number | null
  allowInsert: boolean
  omit: boolean
  keepHigherSign: boolean
  keepLowerSign: boolean
}

export interface ChannelMapPayload {
  analyteCode: string
  round?: number | null
  allowInsert?: boolean
  omit?: boolean
  keepHigherSign?: boolean
  keepLowerSign?: boolean
}

export interface ResultRuleView {
  id: string
  /** null nghĩa là luật CHUNG, áp cho mọi máy — không phải "chưa gán máy". */
  deviceId: string | null
  scope: 'device' | 'common'
  /** Nhãn tiếng Việt do Core dựng sẵn cho `scope`. */
  scopeLabel: string
  analyteCode: string
  analyteName: string | null
  resultIn: string
  resultOut: string | null
  comment: string | null
  /** 0 bình thường / 1 thấp / 2 cao, theo quy ước hệ cũ. */
  color: number | null
}

export interface ResultRulePayload {
  deviceId?: string | null
  analyteCode?: string
  resultIn?: string
  resultOut?: string | null
  comment?: string | null
  color?: number | null
}

/**
 * Đọc câu giải thích của Core từ thân phản hồi lỗi. Nest trả
 * `{ statusCode, message, error }` với `message` là chuỗi hoặc mảng chuỗi.
 * Không đọc được thì mới lùi về câu chung chung có kèm mã HTTP.
 */
async function readErrorMessage(response: Response, path: string): Promise<string> {
  try {
    const body = (await response.json()) as { message?: unknown }
    if (typeof body.message === 'string' && body.message.trim() !== '') return body.message
    if (Array.isArray(body.message)) {
      const parts = body.message.filter((m): m is string => typeof m === 'string')
      if (parts.length > 0) return parts.join('; ')
    }
  } catch {
    // Thân phản hồi không phải JSON — dùng câu dự phòng bên dưới.
  }
  return `Máy chủ từ chối yêu cầu (${response.status}) khi gọi ${path}.`
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const current = session.get()
  const headers: Record<string, string> = {}
  if (current) headers.Authorization = `Bearer ${current.token}`
  if (body !== undefined) headers['Content-Type'] = 'application/json'

  let response: Response
  try {
    response = await fetch(`${BASE_URL}${path}`, {
      method,
      headers,
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    })
  } catch {
    // Mất mạng/không tới được Core: phân biệt hẳn với "máy chủ từ chối", vì
    // cách xử lý của người dùng khác nhau (kiểm tra mạng vs. sửa dữ liệu nhập).
    throw new ApiError(0, 'Không kết nối được tới máy chủ Core — kiểm tra mạng rồi thử lại.')
  }

  if (response.status === 401) {
    notifyExpired('Phiên đăng nhập đã hết hạn hoặc không hợp lệ — vui lòng đăng nhập lại.')
    throw new ApiError(401, 'Phiên đăng nhập đã hết hạn.')
  }
  if (!response.ok) {
    throw new ApiError(response.status, await readErrorMessage(response, path))
  }
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

/**
 * Mã xét nghiệm và mã kênh là mã nghiệp vụ của hệ cũ: có mã chứa `%`, `_`,
 * dấu cách và cả `/`. Phải mã hoá từng đoạn đường dẫn, nếu không `%` sẽ bị
 * hiểu là mở đầu một escape sequence và request hỏng ngay ở tầng HTTP.
 */
function segment(value: string): string {
  return encodeURIComponent(value)
}

export const catalogueApi = {
  searchAnalytes: (params: {
    q: string
    category: string
    limit: number
    offset: number
  }): Promise<AnalyteSearchResult> => {
    const search = new URLSearchParams()
    if (params.q.trim() !== '') search.set('q', params.q.trim())
    if (params.category !== '') search.set('category', params.category)
    search.set('limit', String(params.limit))
    search.set('offset', String(params.offset))
    return request<AnalyteSearchResult>('GET', `/api/catalogue/analytes?${search.toString()}`)
  },

  categories: (): Promise<string[]> => request<string[]>('GET', '/api/catalogue/categories'),

  updateAnalyte: (code: string, patch: AnalytePatch): Promise<AnalyteView> =>
    request<AnalyteView>('PATCH', `/api/catalogue/analytes/${segment(code)}`, patch),

  channels: (deviceId: string): Promise<ChannelMapView[]> =>
    request<ChannelMapView[]>('GET', `/api/catalogue/devices/${segment(deviceId)}/channels`),

  putChannel: (
    deviceId: string,
    channelCode: string,
    payload: ChannelMapPayload,
  ): Promise<ChannelMapView> =>
    request<ChannelMapView>(
      'PUT',
      `/api/catalogue/devices/${segment(deviceId)}/channels/${segment(channelCode)}`,
      payload,
    ),

  deleteChannel: (deviceId: string, channelCode: string): Promise<void> =>
    request<void>(
      'DELETE',
      `/api/catalogue/devices/${segment(deviceId)}/channels/${segment(channelCode)}`,
    ),

  resultRules: (deviceId: string): Promise<ResultRuleView[]> =>
    request<ResultRuleView[]>('GET', `/api/catalogue/devices/${segment(deviceId)}/result-rules`),

  createResultRule: (payload: ResultRulePayload): Promise<ResultRuleView> =>
    request<ResultRuleView>('POST', '/api/catalogue/result-rules', payload),

  updateResultRule: (id: string, payload: ResultRulePayload): Promise<ResultRuleView> =>
    request<ResultRuleView>('PUT', `/api/catalogue/result-rules/${segment(id)}`, payload),

  deleteResultRule: (id: string): Promise<void> =>
    request<void>('DELETE', `/api/catalogue/result-rules/${segment(id)}`),
}

/** Câu hiển thị được cho mọi lỗi lọt tới giao diện, kể cả lỗi không lường trước. */
export function errorMessageOf(err: unknown): string {
  if (err instanceof ApiError) return err.message
  if (err instanceof Error && err.message !== '') return err.message
  return 'Đã xảy ra lỗi không xác định.'
}
