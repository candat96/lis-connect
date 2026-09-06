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
