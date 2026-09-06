import { Inject, Injectable, Logger, type OnModuleDestroy } from '@nestjs/common'
import {
  PROTOCOL_VERSION,
  ProtocolError,
  parseAgentToCore,
  type AgentToCore,
  type CoreToAgent,
} from '@lis/shared'
import type { IncomingMessage, Server as HttpServer } from 'node:http'
import { WebSocketServer, type WebSocket } from 'ws'
import { AgentKeyService } from '../tenant/agent-key.service.js'
import { TenantContext } from '../tenant/tenant-context.js'
import { TenantRegistryService } from '../tenant/tenant-registry.service.js'
import { IngestService } from '../ingest/ingest.service.js'
import type { AgentSession } from './agent-session.js'

const CLOSE_BAD_PROTOCOL = 4400
const CLOSE_UNAUTHORIZED = 4401

/**
 * Cố đọc messageId từ phong bì thô, tách rời khỏi việc kiểm tra phần thân
 * bằng zod — để một message có `payloadBase64`/`deviceId` sai vẫn nack đúng
 * `messageId` của nó thay vì rơi về 'unknown'. Chỉ khi JSON không parse nổi
 * hoặc không có `messageId` dạng chuỗi mới trả 'unknown'.
 */
function extractMessageId(text: string): string {
  try {
    const parsed = JSON.parse(text) as unknown
    if (
      parsed !== null &&
      typeof parsed === 'object' &&
      'messageId' in parsed &&
      typeof (parsed as { messageId: unknown }).messageId === 'string'
    ) {
      return (parsed as { messageId: string }).messageId
    }
  } catch {
    // JSON không đọc nổi — không còn cách nào lấy messageId.
  }
  return 'unknown'
}

@Injectable()
export class AgentGateway implements OnModuleDestroy {
  private readonly logger = new Logger(AgentGateway.name)
  private readonly sessions = new Map<WebSocket, AgentSession>()
  private wss: WebSocketServer | null = null

  // `@Inject` tường minh vì bản build test dùng esbuild (qua vitest/tsx), và
  // esbuild không phát ra metadata `design:paramtypes` cho
  // `emitDecoratorMetadata` — nếu chỉ dựa vào kiểu tham số, Nest sẽ không giải
  // quyết được token và lặng lẽ tiêm `undefined`, chỉ nổ ở lần gọi đầu tiên.
  constructor(
    @Inject(AgentKeyService) private readonly keys: AgentKeyService,
    @Inject(TenantRegistryService) private readonly registry: TenantRegistryService,
    @Inject(IngestService) private readonly ingest: IngestService,
  ) {}

  /**
   * Gắn server WebSocket vào HTTP server của Nest ở đường dẫn /agent.
   *
   * Không dùng `WsAdapter` của `@nestjs/platform-ws`: adapter đó chiếm toàn bộ
   * đường nâng cấp WebSocket của ứng dụng, trong khi giai đoạn sau còn cần một
   * kênh WebSocket riêng cho Web UI ở đường dẫn khác. Tự gắn `WebSocketServer`
   * vào đúng path `/agent` giữ chỗ cho việc đó.
   */
  attach(server: HttpServer): void {
    this.wss = new WebSocketServer({ server, path: '/agent' })
    this.wss.on('connection', (socket, request) => {
      void this.onConnection(socket, request)
    })
  }

  connectedAgents(): AgentSession[] {
    return [...this.sessions.values()]
  }

  private send(socket: WebSocket, message: CoreToAgent): void {
    socket.send(JSON.stringify(message))
  }

  private async onConnection(socket: WebSocket, request: IncomingMessage): Promise<void> {
    // Việc xác thực khoá bên dưới đi qua một round-trip DB thật (bất đồng bộ),
    // trong khi client có thể gửi `hello` ngay khi `open` bắn — nghĩa là
    // trước khi hàm này await xong. Nếu ta chỉ gắn listener 'message' SAU khi
    // await xác thực, message đó tới trong lúc chưa có listener nào và bị
    // `ws` lặng lẽ bỏ qua (EventEmitter không đệm sự kiện không ai lắng nghe).
    // Vì vậy phải gắn listener 'message'/'close' ĐỒNG BỘ ngay từ đầu, và xếp
    // hàng mọi message đến trước khi xác thực xong.
    let session: AgentSession | null = null
    let closedBeforeAuth = false
    const pending: string[] = []

    socket.on('message', (data) => {
      const text = data.toString()
      if (session) {
        void this.onMessage(session, text)
      } else {
        pending.push(text)
      }
    })
    socket.on('close', () => {
      closedBeforeAuth = true
      if (session) this.sessions.delete(socket)
    })

    // Khoá Agent nằm ở query string (?key=...) vì WebSocket của trình duyệt
    // không cho đặt header tuỳ ý (tab Terminal thô trong Agent Console dùng
    // lại chính lớp client này). Toàn bộ đường truyền là wss nên khoá không đi
    // trên mạng dạng rõ — nhưng reverse proxy đặt trước Core TUYỆT ĐỐI KHÔNG
    // được log query string của request, nếu không khoá sẽ lộ ra access log.
    const url = new URL(request.url ?? '/', 'http://localhost')
    const key = url.searchParams.get('key')

    const verified = key ? await this.keys.verify(key) : null
    if (!verified) {
      socket.close(CLOSE_UNAUTHORIZED, 'khoá agent không hợp lệ')
      return
    }
    // Client có thể đã đóng kết nối trong lúc ta còn chờ xác thực.
    if (closedBeforeAuth) return

    session = {
      tenantId: verified.tenantId,
      agentKeyId: verified.keyId,
      agentId: null,
      hostname: null,
      socket,
    }
    this.sessions.set(socket, session)

    for (const text of pending) {
      void this.onMessage(session, text)
    }
  }

  private async onMessage(session: AgentSession, text: string): Promise<void> {
    let message: AgentToCore
    try {
      message = parseAgentToCore(JSON.parse(text) as unknown)
    } catch (error) {
      // Message hỏng không được làm đứt kết nối: Agent còn hàng đợi phía sau
      // cần tiếp tục đẩy lên. Chỉ báo lỗi cho riêng message này.
      //
      // Vẫn cố đọc messageId từ phong bì trước khi kiểm phần thân: pump() bên
      // Agent chỉ lấy 20 message đầu hàng đợi theo thứ tự và bỏ qua message
      // đang inFlight — nếu nack trả về 'unknown' cho một message thật sự có
      // messageId hợp lệ, Agent tra inFlight.get('unknown') ra undefined,
      // không xoá được khỏi outbox lẫn không biết để ack, và 20 message hỏng
      // đầu hàng đợi sẽ chặn TOÀN BỘ kết quả phía sau vĩnh viễn trong khi
      // queueDepth vẫn tăng, không ai được báo. Chỉ dùng 'unknown' khi JSON
      // không đọc nổi hoặc phong bì không mang messageId dạng chuỗi.
      const reason = error instanceof ProtocolError ? error.message : 'message không đọc được'
      this.send(session.socket, {
        type: 'nack',
        messageId: extractMessageId(text),
        reason,
        retryable: false,
      })
      return
    }

    if (message.type === 'hello') {
      if (message.protocolVersion !== PROTOCOL_VERSION) {
        session.socket.close(
          CLOSE_BAD_PROTOCOL,
          `phiên bản giao thức không khớp: Core dùng ${PROTOCOL_VERSION}`,
        )
        return
      }
      try {
        await TenantContext.run(session.tenantId, async () => {
          const db = await this.registry.forTenant(session.tenantId)
          const agent = await db
            .insertInto('agents')
            .values({
              agent_key_id: session.agentKeyId,
              hostname: message.hostname,
              agent_version: message.agentVersion,
              last_seen_at: new Date(),
            })
            .onConflict((oc) =>
              oc.column('agent_key_id').doUpdateSet({
                hostname: message.hostname,
                agent_version: message.agentVersion,
                last_seen_at: new Date(),
              }),
            )
            .returning('id')
            .executeTakeFirstOrThrow()
          session.agentId = agent.id
          session.hostname = message.hostname
        })
      } catch (error) {
        // Lỗi hạ tầng (mất kết nối DB, vi phạm ràng buộc...) không được để
        // rơi thành unhandled rejection: onMessage được gọi kiểu
        // `void this.onMessage(...)` từ listener 'message', nên một promise
        // rejected ở đây không ai bắt sẽ tuỳ cấu hình Node mà hạ cả tiến
        // trình Core — ảnh hưởng tới MỌI Agent đang kết nối, không riêng
        // socket này. Hạ xuống thành nack, coi là lỗi thoáng qua nên
        // retryable: true, và không đóng kết nối.
        this.logger.error(`xử lý hello thất bại: ${String(error)}`)
        this.send(session.socket, {
          type: 'nack',
          messageId: message.messageId,
          reason: error instanceof Error ? error.message : 'lỗi không xác định',
          retryable: true,
        })
        return
      }
      this.send(session.socket, { type: 'ack', messageId: message.messageId })
      return
    }

    const agentId = session.agentId
    if (!agentId) {
      this.send(session.socket, {
        type: 'nack',
        messageId: message.messageId,
        reason: 'phải gửi hello trước',
        retryable: true,
      })
      return
    }

    try {
      await TenantContext.run(session.tenantId, () =>
        this.ingest.handle(session.tenantId, agentId, message),
      )
      this.send(session.socket, { type: 'ack', messageId: message.messageId })
    } catch (error) {
      this.logger.error(`xử lý message thất bại: ${String(error)}`)
      this.send(session.socket, {
        type: 'nack',
        messageId: message.messageId,
        reason: error instanceof Error ? error.message : 'lỗi không xác định',
        retryable: true,
      })
    }
  }

  async onModuleDestroy(): Promise<void> {
    for (const session of this.sessions.values()) session.socket.terminate()
    this.sessions.clear()
    await new Promise<void>((resolve) => {
      if (!this.wss) {
        resolve()
        return
      }
      this.wss.close(() => resolve())
    })
  }
}
