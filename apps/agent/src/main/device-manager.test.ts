import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { AstmSimulator } from '../../../../tools/simulator/src/simulator.js'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { DeviceConfig } from './config-store.js'
import { DeviceManager } from './device-manager.js'
import { LocalDb } from './local-db.js'
import { Outbox } from './outbox.js'

let dir: string
let db: LocalDb
let outbox: Outbox
let manager: DeviceManager
let sim: AstmSimulator
let simPort: number

async function waitFor(check: () => boolean, timeoutMs = 8000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (check()) return
    await new Promise((r) => setTimeout(r, 30))
  }
  throw new Error('hết giờ chờ điều kiện')
}

function deviceConfig(port: number): DeviceConfig {
  return {
    deviceKey: 'may-sinh-hoa',
    name: 'Máy sinh hóa',
    driver: 'astm-e1394',
    enabled: true,
    transport: { kind: 'tcp', role: 'client', host: '127.0.0.1', port },
  }
}

beforeEach(async () => {
  dir = mkdtempSync(join(tmpdir(), 'lis-dm-'))
  db = LocalDb.open(join(dir, 'agent.sqlite'))
  outbox = new Outbox(db)
  manager = new DeviceManager(outbox, () => undefined)

  sim = new AstmSimulator({ misbehaviour: 'none' })
  simPort = await sim.start(0)
})

afterEach(async () => {
  await manager.stopAll()
  await sim.stop()
  db.close()
  rmSync(dir, { recursive: true, force: true })
})

describe('DeviceManager', () => {
  it('mở kết nối tới máy và ghi connection event vào hàng đợi', async () => {
    await manager.applyConfig([deviceConfig(simPort)])

    await waitFor(() => outbox.depth() > 0)
    const messages = outbox.peek(10).map((e) => e.message)
    expect(messages.some((m) => m.type === 'connection_event')).toBe(true)
  })

  it('đưa raw message vào hàng đợi khi máy gửi kết quả', async () => {
    await manager.applyConfig([deviceConfig(simPort)])
    await waitFor(() => manager.statusOf('may-sinh-hoa') === 'connected')
    await waitFor(() => sim.hasClient())

    await sim.sendResults([{ sid: 'SID001', testCode: 'GLU', value: '5.4', unit: 'mmol/L' }])

    await waitFor(() => outbox.peek(50).some((e) => e.message.type === 'raw_message'))
    const raw = outbox.peek(50).find((e) => e.message.type === 'raw_message')
    expect(raw).toBeDefined()
    if (raw?.message.type !== 'raw_message') throw new Error('sai loại message')
    const payload = Buffer.from(raw.message.payloadBase64, 'base64').toString('latin1')
    expect(payload).toContain('SID001')
  })

  it('phát sự kiện traffic cho tab Terminal thô', async () => {
    const traffic: Array<{ deviceKey: string; direction: string }> = []
    manager.on('traffic', (event) => traffic.push(event))

    await manager.applyConfig([deviceConfig(simPort)])
    await waitFor(() => manager.statusOf('may-sinh-hoa') === 'connected')
    await waitFor(() => sim.hasClient())
    await sim.sendResults([{ sid: 'SID002', testCode: 'URE', value: '6.1', unit: 'mmol/L' }])

    await waitFor(() => traffic.some((t) => t.direction === 'inbound'))
    expect(traffic.some((t) => t.direction === 'outbound')).toBe(true)
  })

  it('không mở máy bị tắt (enabled=false)', async () => {
    await manager.applyConfig([{ ...deviceConfig(simPort), enabled: false }])
    await new Promise((r) => setTimeout(r, 300))
    expect(manager.statusOf('may-sinh-hoa')).toBe('disconnected')
  })

  it('applyConfig lần hai đóng máy đã bị gỡ khỏi cấu hình', async () => {
    await manager.applyConfig([deviceConfig(simPort)])
    await waitFor(() => manager.statusOf('may-sinh-hoa') === 'connected')

    await manager.applyConfig([])
    expect(manager.statusOf('may-sinh-hoa')).toBe('disconnected')
  })

  it('testConnection báo ok khi mở được cổng', async () => {
    const result = await manager.testConnection(deviceConfig(simPort))
    expect(result.ok).toBe(true)
  })

  it('testConnection báo lỗi kèm lý do khi không mở được', async () => {
    const result = await manager.testConnection(deviceConfig(1))
    expect(result.ok).toBe(false)
    expect(result.detail.length).toBeGreaterThan(0)
  })

  it('mở thất bại lần đầu, tự thử lại theo backoff và cuối cùng kết nối được', async () => {
    // Đóng máy giả lập ngay để lần mở đầu tiên chắc chắn thất bại
    // (ECONNREFUSED), rồi mở lại ở đúng cổng đó sau một nhịp ngắn — mô phỏng
    // máy xét nghiệm chưa bật lúc Agent khởi động.
    await sim.stop()
    manager = new DeviceManager(outbox, () => undefined, {
      initialBackoffMs: 50,
      maxBackoffMs: 200,
    })

    const config = deviceConfig(simPort)
    await manager.applyConfig([config])
    expect(manager.statusOf('may-sinh-hoa')).not.toBe('connected')

    setTimeout(() => {
      void (async () => {
        sim = new AstmSimulator({ misbehaviour: 'none' })
        await sim.start(simPort)
      })()
    }, 100)

    await waitFor(() => manager.statusOf('may-sinh-hoa') === 'connected', 10_000)
  })

  it('thiết bị bị gỡ khỏi cấu hình thì không còn hẹn giờ thử lại nào chạy nữa', async () => {
    await sim.stop()
    manager = new DeviceManager(outbox, () => undefined, {
      initialBackoffMs: 50,
      maxBackoffMs: 200,
    })

    await manager.applyConfig([deviceConfig(simPort)])
    // Cho vài vòng backoff trôi qua trong lúc máy vẫn chưa bật.
    await new Promise((r) => setTimeout(r, 300))

    await manager.applyConfig([])

    // Bật lại máy giả lập ở đúng cổng: nếu còn timer mồ côi nào từ thiết bị đã
    // gỡ, nó sẽ mở kết nối và statusOf sẽ không còn đứng yên ở 'disconnected'
    // (khoá ngoài `devices` map thì statusOf trả 'disconnected' mặc định —
    // nên ta kiểm bằng cách chờ một khoảng đủ dài rồi khẳng định không có kết
    // nối "ma" nào được set up: applyConfig([]) phải làm devices rỗng).
    sim = new AstmSimulator({ misbehaviour: 'none' })
    await sim.start(simPort)
    await new Promise((r) => setTimeout(r, 400))
    expect(manager.statusOf('may-sinh-hoa')).toBe('disconnected')
    expect(sim.hasClient()).toBe(false)
  })

  it('vẫn đưa raw message vào hàng đợi khi driver báo checksum sai', async () => {
    await sim.stop()
    sim = new AstmSimulator({ misbehaviour: 'bad-checksum' })
    simPort = await sim.start(0)

    await manager.applyConfig([deviceConfig(simPort)])
    await waitFor(() => manager.statusOf('may-sinh-hoa') === 'connected')
    await waitFor(() => sim.hasClient())
    await sim.sendResults([{ sid: 'SID003', testCode: 'CRE', value: '88', unit: 'umol/L' }])

    await waitFor(() => outbox.peek(50).some((e) => e.message.type === 'raw_message'))
    // Dữ liệu hỏng vẫn phải được lưu: không bao giờ vứt byte từ máy xét nghiệm.
    expect(outbox.peek(50).some((e) => e.message.type === 'raw_message')).toBe(true)
  })
})
