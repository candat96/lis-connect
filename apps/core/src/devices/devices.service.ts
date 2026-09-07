import { Inject, Injectable, NotFoundException } from '@nestjs/common'
import { TenantRegistryService } from '../tenant/tenant-registry.service.js'

export interface DeviceSummary {
  id: string
  deviceKey: string
  name: string
  transport: string
  driver: string
  /**
   * BỐN giá trị: ba trạng thái do Agent báo ('connected' | 'disconnected' |
   * 'error') và 'never-connected' cho máy CHƯA TỪNG có Agent nào nối vào.
   *
   * Máy giữ chỗ do công cụ di trú danh mục tạo ra rơi đúng vào nhóm cuối. Trước
   * đây chúng bị trả về 'disconnected', nên ngay sau khi di trú màn hình theo
   * dõi bật banner đỏ "20/20 MÁY ĐANG MẤT KẾT NỐI" dù chưa cài Agent nào —
   * dạy kỹ thuật viên bỏ qua đúng cảnh báo dùng để bắt sự cố thật.
   */
  status: string
  lastMessageAt: string | null
  /** Máy có đang được bật để Agent kết nối hay không (khái niệm của hệ MỚI). */
  enabled: boolean
  /**
   * Cờ `Active` của hệ LIS CŨ: bệnh viện còn dùng máy này không.
   * `null` = máy không đến từ hệ cũ nên không có thông tin. Đây là nhãn thứ ba,
   * độc lập với `enabled` và `hasPortConfig` — không được dùng thay cho nhau.
   */
  legacyActive: boolean | null
  /** Máy có cấu hình cổng trong hệ cũ hay không (từ `InterfacePara`). */
  hasPortConfig: boolean
  /**
   * Máy đã từng gửi kết quả về hay chưa.
   *
   * NGUỒN SỰ THẬT DUY NHẤT là `device_status.last_message_at`. Cột
   * `devices.has_sent_results` từng tồn tại song song, do bộ ghi di trú đặt
   * `false` cứng, và đã bị bỏ ở migration 004 vì hai định nghĩa cùng một tên là
   * cách chắc chắn để người sau sửa nhầm chỗ.
   */
  hasSentResults: boolean
}

export interface RawMessageView {
  id: string
  direction: string
  receivedAt: string
  parseStatus: string
  payloadBase64: string
  /** Bản đọc được của payload; ký tự điều khiển được thay bằng dấu chấm. */
  payloadText: string
}

export interface ConnectionEventView {
  id: string
  status: string
  detail: string | null
  occurredAt: string
}

const CONTROL_CHARS = new RegExp('[\u0000-\u001f\u007f]', 'g')

function toPrintableText(payload: Buffer): string {
  return payload.toString('utf8').replace(CONTROL_CHARS, '.')
}

@Injectable()
export class DevicesService {
  // `@Inject` tường minh vì bản build test dùng esbuild (qua vitest/tsx),
  // và esbuild không phát ra metadata `design:paramtypes` cho
  // `emitDecoratorMetadata` — nếu chỉ dựa vào kiểu tham số, Nest sẽ không
  // giải quyết được token và tiêm `undefined` vào `registry`.
  constructor(@Inject(TenantRegistryService) private readonly registry: TenantRegistryService) {}

  async list(tenantId: string): Promise<DeviceSummary[]> {
    const db = await this.registry.forTenant(tenantId)
    const rows = await db
      .selectFrom('devices')
      .leftJoin('device_status', 'device_status.device_id', 'devices.id')
      .select([
        'devices.id',
        'devices.device_key',
        'devices.name',
        'devices.transport',
        'devices.driver',
        'devices.enabled',
        'devices.has_port_config',
        'devices.legacy_active',
        'device_status.status',
        'device_status.last_message_at',
      ])
      .orderBy('devices.name')
      .execute()

    return rows.map((r) => ({
      id: r.id,
      deviceKey: r.device_key,
      name: r.name,
      transport: r.transport,
      driver: r.driver,
      // Không có dòng `device_status` nghĩa là chưa Agent nào từng báo cáo về
      // máy này — "chưa từng kết nối", KHÁC HẲN "mất kết nối".
      status: r.status ?? 'never-connected',
      lastMessageAt: r.last_message_at?.toISOString() ?? null,
      enabled: r.enabled,
      legacyActive: r.legacy_active,
      hasPortConfig: r.has_port_config,
      hasSentResults: r.last_message_at != null,
    }))
  }

  private async assertDeviceBelongsToTenant(tenantId: string, deviceId: string): Promise<void> {
    const db = await this.registry.forTenant(tenantId)
    const found = await db
      .selectFrom('devices')
      .select('id')
      .where('id', '=', deviceId)
      .executeTakeFirst()
    if (!found) throw new NotFoundException('không tìm thấy máy xét nghiệm')
  }

  async rawMessages(tenantId: string, deviceId: string, limit: number): Promise<RawMessageView[]> {
    await this.assertDeviceBelongsToTenant(tenantId, deviceId)
    const db = await this.registry.forTenant(tenantId)
    const rows = await db
      .selectFrom('raw_messages')
      .selectAll()
      .where('device_id', '=', deviceId)
      .orderBy('received_at', 'desc')
      .limit(limit)
      .execute()

    return rows.map((r) => ({
      id: r.id,
      direction: r.direction,
      receivedAt: r.received_at.toISOString(),
      parseStatus: r.parse_status,
      payloadBase64: r.payload.toString('base64'),
      payloadText: toPrintableText(r.payload),
    }))
  }

  async connectionEvents(
    tenantId: string,
    deviceId: string,
    limit: number,
  ): Promise<ConnectionEventView[]> {
    await this.assertDeviceBelongsToTenant(tenantId, deviceId)
    const db = await this.registry.forTenant(tenantId)
    const rows = await db
      .selectFrom('connection_events')
      .selectAll()
      .where('device_id', '=', deviceId)
      .orderBy('occurred_at', 'desc')
      .limit(limit)
      .execute()

    return rows.map((r) => ({
      id: r.id,
      status: r.status,
      detail: r.detail,
      occurredAt: r.occurred_at.toISOString(),
    }))
  }
}
