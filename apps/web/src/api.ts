const BASE_URL = import.meta.env.VITE_CORE_URL ?? 'http://localhost:3000'
const TENANT_STORAGE_KEY = 'lis.tenantId'

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

/**
 * Giai đoạn 1 chưa có đăng nhập: tenant lấy từ ô nhập và lưu ở localStorage.
 * Giai đoạn 2 thay bằng JWT — giữ toàn bộ phần này ở một tệp để lúc đó chỉ phải
 * sửa một chỗ.
 */
export const tenant = {
  get: (): string => localStorage.getItem(TENANT_STORAGE_KEY) ?? '',
  set: (value: string): void => localStorage.setItem(TENANT_STORAGE_KEY, value),
}

async function get<T>(path: string): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    headers: { 'X-Tenant-Id': tenant.get() },
  })
  if (!response.ok) {
    throw new Error(`gọi ${path} thất bại: ${response.status} ${response.statusText}`)
  }
  return (await response.json()) as T
}

export const api = {
  devices: () => get<DeviceSummary[]>('/api/devices'),
  rawMessages: (deviceId: string) =>
    get<RawMessageView[]>(`/api/devices/${deviceId}/raw-messages?limit=100`),
  connectionEvents: (deviceId: string) =>
    get<ConnectionEventView[]>(`/api/devices/${deviceId}/connection-events?limit=100`),
}
