import { Inject, Injectable } from '@nestjs/common'
import type { AgentToCore } from '@lis/shared'
import { TenantRegistryService } from '../tenant/tenant-registry.service.js'

@Injectable()
export class IngestService {
  private readonly deviceIdCache = new Map<string, string>()

  // `@Inject` tường minh vì bản build test dùng esbuild (qua vitest/tsx),
  // và esbuild không phát ra metadata `design:paramtypes` cho
  // `emitDecoratorMetadata` — nếu chỉ dựa vào kiểu tham số, Nest sẽ không
  // giải quyết được token và tiêm `undefined` vào `registry`.
  constructor(@Inject(TenantRegistryService) private readonly registry: TenantRegistryService) {}

  /**
   * Tìm devices.id theo device_key, tạo mới nếu chưa có.
   *
   * Giai đoạn 1: Agent là nơi khai báo máy, Core chỉ ghi nhận. Giai đoạn sau,
   * khi cấu hình máy được quản lý từ Web UI, hành vi này đổi thành từ chối máy
   * chưa đăng ký.
   */
  async ensureDevice(tenantId: string, agentId: string, deviceKey: string): Promise<string> {
    const cacheKey = `${tenantId}:${agentId}:${deviceKey}`
    const cached = this.deviceIdCache.get(cacheKey)
    if (cached) return cached

    const db = await this.registry.forTenant(tenantId)
    const row = await db
      .insertInto('devices')
      .values({
        agent_id: agentId,
        device_key: deviceKey,
        name: deviceKey,
        transport: 'tcp',
        driver: 'unknown',
      })
      .onConflict((oc) =>
        oc.columns(['agent_id', 'device_key']).doUpdateSet({ device_key: deviceKey }),
      )
      .returning('id')
      .executeTakeFirstOrThrow()

    this.deviceIdCache.set(cacheKey, row.id)
    return row.id
  }

  async handle(tenantId: string, agentId: string, message: AgentToCore): Promise<void> {
    const db = await this.registry.forTenant(tenantId)

    switch (message.type) {
      case 'hello':
        // Gateway đã xử lý hello trước khi gọi tới đây.
        return

      case 'heartbeat':
        await db
          .updateTable('agents')
          .set({ last_seen_at: new Date() })
          .where('id', '=', agentId)
          .execute()
        return

      case 'raw_message': {
        const deviceId = await this.ensureDevice(tenantId, agentId, message.deviceId)
        // Idempotent bằng ràng buộc unique trên message_id, không phải bằng
        // "kiểm tra rồi ghi" — hai bản sao đến đồng thời sẽ lọt qua kiểu đó.
        await db
          .insertInto('raw_messages')
          .values({
            message_id: message.messageId,
            device_id: deviceId,
            direction: message.direction,
            payload: Buffer.from(message.payloadBase64, 'base64'),
            sent_at: new Date(message.sentAt),
          })
          .onConflict((oc) => oc.column('message_id').doNothing())
          .execute()

        await db
          .updateTable('device_status')
          .set({ last_message_at: new Date(), updated_at: new Date() })
          .where('device_id', '=', deviceId)
          .execute()
        return
      }

      case 'connection_event': {
        const deviceId = await this.ensureDevice(tenantId, agentId, message.deviceId)
        const occurredAt = new Date(message.sentAt)

        await db
          .insertInto('connection_events')
          .values({
            message_id: message.messageId,
            device_id: deviceId,
            status: message.status,
            detail: message.detail ?? null,
            occurred_at: occurredAt,
          })
          .onConflict((oc) => oc.column('message_id').doNothing())
          .execute()

        await db
          .insertInto('device_status')
          .values({
            device_id: deviceId,
            status: message.status,
            detail: message.detail ?? null,
            updated_at: occurredAt,
          })
          .onConflict((oc) =>
            oc
              .column('device_id')
              .doUpdateSet({
                status: message.status,
                detail: message.detail ?? null,
                updated_at: occurredAt,
              })
              // Sự kiện tới muộn nhưng xảy ra sớm hơn không được ghi đè
              // trạng thái mới hơn.
              .where('device_status.updated_at', '<', occurredAt),
          )
          .execute()
        return
      }
    }
  }
}
