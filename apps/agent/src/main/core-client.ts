import { EventEmitter } from 'node:events'
import { PROTOCOL_VERSION, newMessageId, type CoreToAgent } from '@lis/shared'
import WebSocket from 'ws'
import type { Outbox } from './outbox.js'

export type CoreClientStatus = 'connected' | 'disconnected' | 'unauthorized'

export interface CoreClientOptions {
  url: string
  agentKey: string
  agentVersion: string
  hostname: string
  outbox: Outbox
  initialBackoffMs?: number
  maxBackoffMs?: number
  /** Số message gửi mỗi lượt bơm hàng đợi. */
  batchSize?: number
}

const CLOSE_UNAUTHORIZED = 4401
const HEARTBEAT_INTERVAL_MS = 30_000

/**
 * Kênh nối lên Core.
 *
 * Backoff có trần và KHÔNG BAO GIỜ bỏ cuộc: máy xét nghiệm chạy cả đêm, Agent
 * phải còn sống lúc 3 giờ sáng. Ngoại lệ duy nhất là khoá sai — thử lại vô hạn
 * với khoá sai chỉ tạo rác trong log Core và không bao giờ thành công.
 */
export class CoreClient {
  private readonly emitter = new EventEmitter()
  private socket: WebSocket | null = null
  private backoffMs: number
  private readonly initialBackoffMs: number
  private readonly maxBackoffMs: number
  private readonly batchSize: number
  private reconnectTimer: NodeJS.Timeout | null = null
  private heartbeatTimer: NodeJS.Timeout | null = null
  private stopped = false
  private helloAcked = false
  /** rowId của các message đã gửi và đang chờ ack, tra theo messageId. */
  private readonly inFlight = new Map<string, number>()

  constructor(private readonly options: CoreClientOptions) {
    this.initialBackoffMs = options.initialBackoffMs ?? 1000
    this.maxBackoffMs = options.maxBackoffMs ?? 30_000
    this.batchSize = options.batchSize ?? 20
    this.backoffMs = this.initialBackoffMs
  }

  isConnected(): boolean {
    return this.socket?.readyState === WebSocket.OPEN && this.helloAcked
  }

  on(event: 'status', handler: (status: CoreClientStatus) => void): void {
    this.emitter.on(event, handler)
  }

  start(): void {
    this.stopped = false
    this.connect()
  }

  async stop(): Promise<void> {
    this.stopped = true
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer)
    if (this.heartbeatTimer) clearInterval(this.heartbeatTimer)
    this.reconnectTimer = null
    this.heartbeatTimer = null
    this.socket?.close()
    this.socket = null
  }

  private scheduleReconnect(): void {
    if (this.stopped) return
    this.reconnectTimer = setTimeout(() => this.connect(), this.backoffMs)
    this.backoffMs = Math.min(this.backoffMs * 2, this.maxBackoffMs)
  }

  private connect(): void {
    if (this.stopped) return
    this.helloAcked = false
    this.inFlight.clear()

    const url = `${this.options.url}?key=${encodeURIComponent(this.options.agentKey)}`
    const socket = new WebSocket(url)
    this.socket = socket

    socket.on('open', () => {
      // Không reset backoff ở đây: bắt tay WebSocket xong không có nghĩa là phiên
      // hợp lệ — Core có thể mở socket rồi vẫn từ chối (quá tải, đang triển khai
      // bản mới, giới hạn tải...). Chỉ reset khi 'hello' thật sự được ack.
      socket.send(
        JSON.stringify({
          type: 'hello',
          messageId: newMessageId(),
          sentAt: new Date().toISOString(),
          protocolVersion: PROTOCOL_VERSION,
          agentVersion: this.options.agentVersion,
          hostname: this.options.hostname,
        }),
      )
    })

    socket.on('message', (data) => this.onMessage(data.toString()))

    socket.on('close', (code) => {
      this.socket = null
      this.helloAcked = false
      if (this.heartbeatTimer) clearInterval(this.heartbeatTimer)
      this.heartbeatTimer = null

      if (code === CLOSE_UNAUTHORIZED) {
        this.emitter.emit('status', 'unauthorized')
        // Không hẹn kết nối lại: khoá sai thì thử lại bao nhiêu cũng vô ích.
        return
      }
      this.emitter.emit('status', 'disconnected')
      this.scheduleReconnect()
    })

    socket.on('error', () => {
      // 'close' luôn theo sau 'error'; xử lý ở đó cho gọn.
    })
  }

  private onMessage(text: string): void {
    let message: CoreToAgent
    try {
      message = JSON.parse(text) as CoreToAgent
    } catch {
      return
    }

    if (message.type === 'ack') {
      if (!this.helloAcked) {
        this.helloAcked = true
        // Chỉ reset backoff khi phiên thật sự thành công (hello đã được ack) —
        // không phải khi socket vừa mở.
        this.backoffMs = this.initialBackoffMs
        this.emitter.emit('status', 'connected')
        this.startHeartbeat()
        this.pump()
        return
      }
      const rowId = this.inFlight.get(message.messageId)
      if (rowId !== undefined) {
        this.options.outbox.ack(rowId)
        this.inFlight.delete(message.messageId)
      }
      this.pump()
      return
    }

    if (message.type === 'nack') {
      const rowId = this.inFlight.get(message.messageId)
      this.inFlight.delete(message.messageId)
      // Message hỏng vĩnh viễn thì bỏ khỏi hàng đợi, nếu không nó chặn mọi
      // message phía sau mãi mãi.
      if (rowId !== undefined && !message.retryable) this.options.outbox.ack(rowId)
      this.pump()
    }
  }

  private startHeartbeat(): void {
    this.heartbeatTimer = setInterval(() => {
      if (!this.isConnected()) return
      this.socket?.send(
        JSON.stringify({
          type: 'heartbeat',
          messageId: newMessageId(),
          sentAt: new Date().toISOString(),
          queueDepth: this.options.outbox.depth(),
        }),
      )
    }, HEARTBEAT_INTERVAL_MS)
  }

  /** Đẩy các message đang chờ lên Core. Gọi lại được nhiều lần, an toàn. */
  pump(): void {
    if (!this.isConnected()) return
    const pending = this.options.outbox.peek(this.batchSize)
    for (const entry of pending) {
      if (this.inFlight.has(entry.message.messageId)) continue
      this.inFlight.set(entry.message.messageId, entry.rowId)
      this.socket?.send(JSON.stringify(entry.message))
    }
  }
}
