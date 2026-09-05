import type { TransportConfig } from '../config-store.js'

export interface TransportStatus {
  status: 'connected' | 'disconnected' | 'error'
  detail?: string
}

export interface TransportEvents {
  data: (chunk: Buffer) => void
  status: (status: TransportStatus) => void
}

/**
 * Kênh truyền byte tới một máy xét nghiệm.
 *
 * Transport KHÔNG biết gì về giao thức: nó chỉ chuyển byte và báo trạng thái.
 * Nhờ vậy cùng một driver chạy được trên cả serial lẫn TCP.
 */
export interface Transport {
  readonly config: TransportConfig
  open(): Promise<void>
  close(): Promise<void>
  write(data: Buffer): Promise<void>
  isOpen(): boolean
  on<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void
  off<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void
}
