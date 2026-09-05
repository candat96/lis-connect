import { EventEmitter } from 'node:events'
import { newMessageId } from '@lis/shared'
import type { DeviceConfig } from './config-store.js'
import { driverRegistry } from './drivers/registry.js'
import type { Driver } from './drivers/types.js'
import type { Outbox } from './outbox.js'
import { createTransport } from './transports/factory.js'
import type { Transport, TransportStatus } from './transports/types.js'

interface RunningDevice {
  config: DeviceConfig
  transport: Transport
  driver: Driver
  status: TransportStatus['status']
  onStatus: (status: TransportStatus) => void
  onData: (chunk: Buffer) => void
  closed: boolean
}

export interface TrafficEvent {
  deviceKey: string
  direction: 'inbound' | 'outbound'
  data: Buffer
}

/**
 * Nơi duy nhất ba lớp gặp nhau: transport chuyển byte, driver hiểu giao thức,
 * outbox giữ message cho tới khi Core xác nhận. DeviceManager chỉ nối chúng lại
 * và không chứa logic giao thức nào của riêng nó (không ENQ/ACK/checksum/khung
 * tin ở đây — tất cả nằm trong driver).
 */
export class DeviceManager {
  private readonly emitter = new EventEmitter()
  private readonly devices = new Map<string, RunningDevice>()

  constructor(
    private readonly outbox: Outbox,
    /** Gọi mỗi khi có message mới, để CoreClient bơm hàng đợi ngay. */
    private readonly notifyOutboxChanged: () => void,
  ) {}

  on(event: 'traffic', handler: (event: TrafficEvent) => void): void {
    this.emitter.on(event, handler)
  }

  off(event: 'traffic', handler: (event: TrafficEvent) => void): void {
    this.emitter.off(event, handler)
  }

  statusOf(deviceKey: string): TransportStatus['status'] {
    return this.devices.get(deviceKey)?.status ?? 'disconnected'
  }

  /**
   * Đồng bộ tập máy đang chạy theo cấu hình mới nhất.
   *
   * Với mỗi máy đã có, nếu cấu hình transport hoặc driver không đổi thì để
   * nguyên (tránh open() lần hai khi không cần) — nhưng nếu bất kỳ phần nào
   * đổi (đổi cổng, đổi driver, đổi tên...) thì đóng kết nối cũ triệt để trước
   * khi mở lại, vì gọi open() hai lần liên tiếp mà không close() sẽ để lại
   * socket/port cũ mồ côi vẫn phát dữ liệu vào cùng emitter, gây nhân đôi.
   */
  async applyConfig(configs: DeviceConfig[]): Promise<void> {
    const wanted = new Map(configs.filter((c) => c.enabled).map((c) => [c.deviceKey, c] as const))

    for (const [key, running] of this.devices) {
      const desired = wanted.get(key)
      const unchanged = desired !== undefined && sameDeviceConfig(running.config, desired)
      if (unchanged) continue
      await this.closeDevice(key, running)
    }

    for (const config of configs) {
      if (!config.enabled) continue
      if (this.devices.has(config.deviceKey)) continue
      await this.startDevice(config)
    }
  }

  private enqueue(message: Parameters<Outbox['enqueue']>[0]): void {
    this.outbox.enqueue(message)
    this.notifyOutboxChanged()
  }

  private async startDevice(config: DeviceConfig): Promise<void> {
    // Tên driver do kỹ thuật viên gõ tay trong cấu hình có thể sai —
    // driverRegistry.get() ném lỗi khi không tìm thấy. Một máy khai báo sai
    // tên driver không được làm sập tiến trình main: ghi nhận lỗi kết nối cho
    // riêng máy đó rồi đi tiếp với các máy còn lại.
    let driver: Driver
    try {
      driver = driverRegistry.get(config.driver)
    } catch (error) {
      this.enqueue({
        type: 'connection_event',
        messageId: newMessageId(),
        sentAt: new Date().toISOString(),
        deviceId: config.deviceKey,
        status: 'error',
        detail: error instanceof Error ? error.message : String(error),
      })
      return
    }

    const transport = createTransport(config.transport)
    const onStatus = (status: TransportStatus): void => {
      if (running.closed) return
      running.status = status.status
      this.enqueue({
        type: 'connection_event',
        messageId: newMessageId(),
        sentAt: new Date().toISOString(),
        deviceId: config.deviceKey,
        status: status.status,
        ...(status.detail !== undefined ? { detail: status.detail } : {}),
      })
      if (status.status !== 'connected') driver.reset()
    }
    const onData = (chunk: Buffer): void => {
      this.emitter.emit('traffic', {
        deviceKey: config.deviceKey,
        direction: 'inbound',
        data: chunk,
      })
      this.handleDriverEvents(running, chunk)
    }
    const running: RunningDevice = {
      config,
      transport,
      driver,
      status: 'disconnected',
      onStatus,
      onData,
      closed: false,
    }
    this.devices.set(config.deviceKey, running)

    transport.on('status', onStatus)
    transport.on('data', onData)

    try {
      await transport.open()
    } catch (error) {
      running.status = 'error'
      this.enqueue({
        type: 'connection_event',
        messageId: newMessageId(),
        sentAt: new Date().toISOString(),
        deviceId: config.deviceKey,
        status: 'error',
        detail: error instanceof Error ? error.message : String(error),
      })
    }
  }

  private handleDriverEvents(running: RunningDevice, chunk: Buffer): void {
    for (const event of running.driver.feed(chunk)) {
      if (event.kind === 'reply') {
        void running.transport.write(event.data).catch(() => undefined)
        this.emitter.emit('traffic', {
          deviceKey: running.config.deviceKey,
          direction: 'outbound',
          data: event.data,
        })
        continue
      }

      // Cả message parse được ('message') lẫn dữ liệu hỏng ('error') đều được
      // đẩy lên Core dưới dạng raw_message: không bao giờ vứt byte từ máy xét
      // nghiệm, dù frame lạc hay rác không parse nổi.
      this.enqueue({
        type: 'raw_message',
        messageId: newMessageId(),
        sentAt: new Date().toISOString(),
        deviceId: running.config.deviceKey,
        direction: 'inbound',
        payloadBase64: event.raw.toString('base64'),
      })
    }
  }

  /**
   * Mở thử kết nối rồi đóng ngay, phục vụ nút "Test kết nối" trong giao diện.
   *
   * Nếu máy này đang chạy thật (đã có kết nối trong `devices`), KHÔNG mở thêm
   * một kết nối song song: với transport 'tcp' role 'client' hay 'serial',
   * một cổng serial vật lý chỉ cho một tiến trình mở cùng lúc và mở thêm ở
   * chế độ 'server' TCP sẽ tranh cổng đang lắng nghe với kết nối hiện có, gây
   * lỗi giả hoặc làm rớt kết nối thật đang chạy. Vì vậy khi máy đang chạy,
   * trả kết quả dựa trên trạng thái hiện tại thay vì mở thêm kết nối.
   */
  async testConnection(config: DeviceConfig): Promise<{ ok: boolean; detail: string }> {
    const running = this.devices.get(config.deviceKey)
    if (running && sameDeviceConfig(running.config, config)) {
      if (running.status === 'connected') {
        return { ok: true, detail: 'máy đang kết nối' }
      }
      return {
        ok: false,
        detail: 'máy đang được cấu hình chạy nhưng chưa kết nối được, không thử mở thêm',
      }
    }

    const transport = createTransport(config.transport)
    try {
      await transport.open()
      await transport.close()
      return { ok: true, detail: 'mở kết nối thành công' }
    } catch (error) {
      return { ok: false, detail: error instanceof Error ? error.message : String(error) }
    }
  }

  async stopAll(): Promise<void> {
    for (const [key, running] of this.devices) await this.closeDevice(key, running)
  }

  /**
   * Đóng một máy triệt để: gỡ listener khỏi transport TRƯỚC khi đóng socket.
   *
   * Transport đóng socket không đồng bộ — sự kiện 'status' (disconnected) có
   * thể phát ra sau khi close() đã resolve (ví dụ sau khi test đã dọn dẹp
   * xong DB). Nếu không gỡ listener, sự kiện muộn đó vẫn gọi enqueue() và ghi
   * vào một Outbox/DB có thể đã bị đóng. Gỡ trước khi đóng loại bỏ hoàn toàn
   * nguy cơ này, đồng thời tránh sự kiện muộn ghi đè status của máy đã bị gỡ.
   */
  private async closeDevice(key: string, running: RunningDevice): Promise<void> {
    running.closed = true
    running.transport.off('status', running.onStatus)
    running.transport.off('data', running.onData)
    this.devices.delete(key)
    await running.transport.close()
  }
}

function sameDeviceConfig(a: DeviceConfig, b: DeviceConfig): boolean {
  return a.driver === b.driver && JSON.stringify(a.transport) === JSON.stringify(b.transport)
}
