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
  /**
   * Chu kỳ thử lại khi bị từ chối vì khoá (4401). Cố tình dài và cố định
   * (không nhân đôi) — khác backoff thường. Mặc định 5 phút.
   */
  unauthorizedRetryMs?: number
  /**
   * Một message gửi đi mà không nhận được ack/nack khớp messageId trong hơn
   * chừng này bị coi là "kẹt" (thường vì Core nack với messageId không xác
   * định được, ví dụ 'unknown') và được giải phóng để thử lại. Mặc định 15s —
   * xa hơn nhiều so với một round-trip WebSocket bình thường.
   */
  inflightTimeoutMs?: number
  /** Số lần "kẹt" tối đa trước khi một dòng bị chuyển sang cách ly. Mặc định 5. */
  maxAttempts?: number
}

const CLOSE_UNAUTHORIZED = 4401
const HEARTBEAT_INTERVAL_MS = 30_000

/**
 * Kênh nối lên Core.
 *
 * Backoff có trần và KHÔNG BAO GIỜ bỏ cuộc: máy xét nghiệm chạy cả đêm, Agent
 * phải còn sống lúc 3 giờ sáng. Trường hợp khoá sai/bị thu hồi (4401) dừng
 * thử NHANH ngay lập tức (thử lại vô hạn với backoff giây/chục giây chỉ tạo
 * rác trong log Core và không bao giờ thành công với một khoá sai vĩnh viễn),
 * nhưng KHÔNG dừng vĩnh viễn: `AgentKeyService.verify` cũng trả về "không hợp
 * lệ" khi tenant đang tạm ngưng — một trạng thái THƯỜNG LÀ tạm thời (lý do
 * hành chính) — nên vẫn hẹn thử lại với chu kỳ dài (`unauthorizedRetryMs`) để
 * tự hồi phục khi khoá được kích hoạt lại, không cần khởi động lại Agent bằng
 * tay tại chỗ.
 */
export class CoreClient {
  private readonly emitter = new EventEmitter()
  private socket: WebSocket | null = null
  private backoffMs: number
  private readonly initialBackoffMs: number
  private readonly maxBackoffMs: number
  private readonly batchSize: number
  private readonly unauthorizedRetryMs: number
  private readonly inflightTimeoutMs: number
  private readonly maxAttempts: number
  private reconnectTimer: NodeJS.Timeout | null = null
  private heartbeatTimer: NodeJS.Timeout | null = null
  private inflightSweepTimer: NodeJS.Timeout | null = null
  private stopped = false
  private helloAcked = false
  /** rowId + thời điểm gửi của các message đã gửi và đang chờ ack, tra theo messageId. */
  private readonly inFlight = new Map<string, { rowId: number; sentAt: number }>()

  constructor(private readonly options: CoreClientOptions) {
    this.initialBackoffMs = options.initialBackoffMs ?? 1000
    this.maxBackoffMs = options.maxBackoffMs ?? 30_000
    this.batchSize = options.batchSize ?? 20
    this.unauthorizedRetryMs = options.unauthorizedRetryMs ?? 5 * 60_000
    this.inflightTimeoutMs = options.inflightTimeoutMs ?? 15_000
    this.maxAttempts = options.maxAttempts ?? 5
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
    if (this.inflightSweepTimer) clearInterval(this.inflightSweepTimer)
    this.reconnectTimer = null
    this.heartbeatTimer = null
    this.inflightSweepTimer = null
    this.socket?.close()
    this.socket = null
  }

  private scheduleReconnect(): void {
    if (this.stopped) return
    this.reconnectTimer = setTimeout(() => this.connect(), this.backoffMs)
    this.backoffMs = Math.min(this.backoffMs * 2, this.maxBackoffMs)
  }

  /**
   * Thử lại sau khi bị từ chối vì khoá, với chu kỳ dài cố định (không nhân
   * đôi) — khác hẳn `scheduleReconnect`, vì đây không phải sự cố mạng thoáng
   * qua mà có thể là khoá sai vĩnh viễn hoặc tenant tạm ngưng. Không đụng vào
   * `backoffMs`: nếu sau này Core từ chối phiên vì lý do khác (không phải
   * 4401), backoff thường vẫn bắt đầu lại từ đáy như bình thường.
   */
  private scheduleUnauthorizedRetry(): void {
    if (this.stopped) return
    this.reconnectTimer = setTimeout(() => this.connect(), this.unauthorizedRetryMs)
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
      if (this.inflightSweepTimer) clearInterval(this.inflightSweepTimer)
      this.heartbeatTimer = null
      this.inflightSweepTimer = null

      if (code === CLOSE_UNAUTHORIZED) {
        this.emitter.emit('status', 'unauthorized')
        // Dừng thử NHANH, nhưng không dừng vĩnh viễn — xem doc-comment của
        // lớp: 4401 cũng xảy ra khi tenant tạm ngưng, một trạng thái thường
        // là tạm thời.
        this.scheduleUnauthorizedRetry()
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
        this.startInflightSweep()
        this.pump()
        return
      }
      const entry = this.inFlight.get(message.messageId)
      if (entry !== undefined) {
        this.options.outbox.ack(entry.rowId)
        this.inFlight.delete(message.messageId)
      }
      this.pump()
      return
    }

    if (message.type === 'nack') {
      const entry = this.inFlight.get(message.messageId)
      this.inFlight.delete(message.messageId)
      // Message hỏng vĩnh viễn thì bỏ khỏi hàng đợi, nếu không nó chặn mọi
      // message phía sau mãi mãi.
      if (entry !== undefined && !message.retryable) this.options.outbox.ack(entry.rowId)
      // Nếu Core không xác định được messageId (ví dụ nack messageId:
      // 'unknown'), `entry` ở trên là undefined — dòng thật sự đã gửi vẫn
      // còn kẹt trong inFlight dưới messageId gốc của nó, không được xoá
      // cũng không được thử lại. `sweepStaleInflight` (chạy định kỳ) là
      // đường thoát cho trường hợp này: sau `inflightTimeoutMs` không có phản
      // hồi khớp, dòng đó được giải phóng để thử lại, và sau `maxAttempts`
      // lần kẹt liên tiếp thì bị cách ly.
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

  /**
   * Giải phóng các message đã gửi từ lâu mà chưa có ack/nack khớp messageId —
   * xem chú thích ở nhánh 'nack' trong onMessage. Không tự xoá dữ liệu: chỉ
   * đếm thêm một lần thử kẹt cho dòng đó, và cách ly khi vượt `maxAttempts`.
   */
  private startInflightSweep(): void {
    const period = Math.max(1000, Math.floor(this.inflightTimeoutMs / 2))
    this.inflightSweepTimer = setInterval(() => this.sweepStaleInflight(), period)
  }

  private sweepStaleInflight(): void {
    const now = Date.now()
    for (const [messageId, entry] of this.inFlight) {
      if (now - entry.sentAt < this.inflightTimeoutMs) continue
      this.inFlight.delete(messageId)
      const quarantined = this.options.outbox.recordFailedAttempt(entry.rowId, this.maxAttempts)
      if (quarantined) {
        console.warn(
          `[core-client] message rowId=${entry.rowId} bị cách ly sau ${this.maxAttempts} lần gửi không nhận được phản hồi khớp từ Core. Dữ liệu vẫn được giữ nguyên trong hàng đợi cục bộ, cần người kiểm tra thủ công.`,
        )
      }
    }
    this.pump()
  }

  /** Đẩy các message đang chờ lên Core. Gọi lại được nhiều lần, an toàn. */
  pump(): void {
    if (!this.isConnected()) return
    const pending = this.options.outbox.peek(this.batchSize)
    for (const entry of pending) {
      if (this.inFlight.has(entry.message.messageId)) continue
      this.inFlight.set(entry.message.messageId, { rowId: entry.rowId, sentAt: Date.now() })
      this.socket?.send(JSON.stringify(entry.message))
    }
  }
}
