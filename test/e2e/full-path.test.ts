import 'reflect-metadata'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import request from 'supertest'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AdminDbService } from '../../apps/core/src/admin-db/admin-db.service.js'
import { AgentGateway } from '../../apps/core/src/agent-gateway/agent.gateway.js'
import { AppModule } from '../../apps/core/src/app.module.js'
import { AgentKeyService } from '../../apps/core/src/tenant/agent-key.service.js'
import { TenantMigratorService } from '../../apps/core/src/tenant/tenant-migrator.service.js'
import { createTestUser, prepareTestTenant, resetAdminDatabase } from '../../apps/core/test/helpers.js'
import { CoreClient } from '../../apps/agent/src/main/core-client.js'
import { DeviceManager } from '../../apps/agent/src/main/device-manager.js'
import { LocalDb } from '../../apps/agent/src/main/local-db.js'
import { Outbox } from '../../apps/agent/src/main/outbox.js'
import { AstmSimulator } from '../../tools/simulator/src/simulator.js'

let app: INestApplication
let corePort: number
let tenantId: string
let agentKey: string
let webToken: string
let dir: string
let db: LocalDb
let outbox: Outbox
let manager: DeviceManager
let client: CoreClient
let sim: AstmSimulator
let simPort: number

async function waitFor(check: () => Promise<boolean> | boolean, timeoutMs = 20_000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (await check()) return
    await new Promise((r) => setTimeout(r, 100))
  }
  throw new Error('hết giờ chờ điều kiện')
}

beforeAll(async () => {
  await resetAdminDatabase()

  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()
  await app.listen(0)
  app.get(AgentGateway).attach(app.getHttpServer())
  corePort = (app.getHttpServer().address() as { port: number }).port

  tenantId = await prepareTestTenant(
    app.get(AdminDbService).db,
    app.get(TenantMigratorService),
    'e2e',
    'lis_t_e2e',
  )
  agentKey = (await app.get(AgentKeyService).issue(tenantId, 'PC E2E')).plainKey

  await createTestUser(app.get(AdminDbService).db, tenantId, 'ktv-e2e', 'mat-khau-e2e')
  const loginRes = await request(app.getHttpServer())
    .post('/api/auth/login')
    .send({ tenantCode: 'e2e', username: 'ktv-e2e', password: 'mat-khau-e2e' })
    .expect(201)
  webToken = loginRes.body.token

  sim = new AstmSimulator({ misbehaviour: 'none' })
  simPort = await sim.start(0)

  dir = mkdtempSync(join(tmpdir(), 'lis-e2e-'))
  db = LocalDb.open(join(dir, 'agent.sqlite'))
  outbox = new Outbox(db)
  manager = new DeviceManager(outbox, () => client?.pump())
  client = new CoreClient({
    url: `ws://localhost:${corePort}/agent`,
    agentKey,
    agentVersion: '0.1.0',
    hostname: 'E2E-PC',
    outbox,
    initialBackoffMs: 100,
  })
  client.start()
  await waitFor(() => client.isConnected())

  await manager.applyConfig([
    {
      deviceKey: 'may-e2e',
      name: 'Máy E2E',
      driver: 'astm-e1394',
      enabled: true,
      transport: { kind: 'tcp', role: 'client', host: '127.0.0.1', port: simPort },
    },
  ])
  // `statusOf` phản ánh sự kiện 'connect' phía Agent (client TCP); sự kiện
  // 'connection' phía máy giả lập (server TCP) không đảm bảo xảy ra trước đó,
  // nên còn phải chờ `sim.hasClient()` để tránh gọi sendResults() khi socket
  // phía máy giả lập chưa kịp gán.
  await waitFor(() => manager.statusOf('may-e2e') === 'connected' && sim.hasClient())
}, 180_000)

afterAll(async () => {
  await client.stop()
  await manager.stopAll()
  await sim.stop()
  db.close()
  rmSync(dir, { recursive: true, force: true })
  await app.close()
})

describe('đường đi đầu-cuối: máy giả lập → Agent → Core → API', () => {
  it('kết quả từ máy xuất hiện trong API đọc của đúng tenant', async () => {
    await sim.sendResults([{ sid: 'E2E001', testCode: 'GLU', value: '5.4', unit: 'mmol/L' }])

    await waitFor(async () => {
      const res = await request(app.getHttpServer())
        .get('/api/devices')
        .set('Authorization', `Bearer ${webToken}`)
      return res.body.length > 0
    })

    const devices = await request(app.getHttpServer())
      .get('/api/devices')
      .set('Authorization', `Bearer ${webToken}`)
      .expect(200)
    const device = devices.body[0]
    expect(device.deviceKey).toBe('may-e2e')
    expect(device.status).toBe('connected')

    await waitFor(async () => {
      const res = await request(app.getHttpServer())
        .get(`/api/devices/${device.id}/raw-messages`)
        .set('Authorization', `Bearer ${webToken}`)
      return res.body.some((m: { payloadText: string }) => m.payloadText.includes('E2E001'))
    })
  })

  it('hàng đợi của Agent về 0 sau khi Core xác nhận', async () => {
    await waitFor(() => outbox.depth() === 0)
    expect(outbox.depth()).toBe(0)
  })

  it('mất kết nối Core rồi nối lại thì không mất và không nhân đôi kết quả', async () => {
    const device = (
      await request(app.getHttpServer()).get('/api/devices').set('Authorization', `Bearer ${webToken}`)
    ).body[0]

    const before = (
      await request(app.getHttpServer())
        .get(`/api/devices/${device.id}/raw-messages?limit=500`)
        .set('Authorization', `Bearer ${webToken}`)
    ).body.length

    // Ngắt Agent khỏi Core, gửi kết quả trong lúc mất kết nối.
    await client.stop()
    await sim.sendResults([{ sid: 'E2E002', testCode: 'URE', value: '6.1', unit: 'mmol/L' }])
    await waitFor(() => outbox.depth() > 0)

    // Nối lại.
    client = new CoreClient({
      url: `ws://localhost:${corePort}/agent`,
      agentKey,
      agentVersion: '0.1.0',
      hostname: 'E2E-PC',
      outbox,
      initialBackoffMs: 100,
    })
    client.start()
    await waitFor(() => client.isConnected())
    await waitFor(() => outbox.depth() === 0)

    const after = (
      await request(app.getHttpServer())
        .get(`/api/devices/${device.id}/raw-messages?limit=500`)
        .set('Authorization', `Bearer ${webToken}`)
    ).body

    expect(after.length).toBeGreaterThan(before)
    expect(after.some((m: { payloadText: string }) => m.payloadText.includes('E2E002'))).toBe(true)

    // Không có message nào bị ghi hai lần.
    //
    // KHÔNG so `id` (raw_messages.id, khoá chính gen_random_uuid()) — nó
    // LUÔN LUÔN duy nhất theo định nghĩa, kể cả khi hai bản ghi có cùng nội
    // dung nhưng `id` khác nhau; so vậy là một khẳng định rỗng, không bảo vệ
    // được gì (đã kiểm chứng: chèn thủ công một bản sao cùng nội dung với
    // `id` khác làm khẳng định theo nội dung dưới đây ĐỎ đúng như mong đợi —
    // xem report). Đếm theo NỘI DUNG: đúng một bản ghi chứa 'E2E002'.
    const e2e002Count = after.filter((m: { payloadText: string }) =>
      m.payloadText.includes('E2E002'),
    ).length
    expect(e2e002Count).toBe(1)
  }, 60_000)
})
