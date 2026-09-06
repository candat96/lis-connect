import 'reflect-metadata'
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import { newMessageId } from '@lis/shared'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
import { AppModule } from '../src/app.module.js'
import { IngestService } from '../src/ingest/ingest.service.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'
import { prepareTestTenant, resetAdminDatabase } from './helpers.js'

let app: INestApplication
let ingest: IngestService
let registry: TenantRegistryService
let tenantId: string
let agentId: string

beforeAll(async () => {
  await resetAdminDatabase()
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()

  const admin = app.get(AdminDbService)
  const migrator = app.get(TenantMigratorService)
  registry = app.get(TenantRegistryService)
  ingest = app.get(IngestService)

  tenantId = await prepareTestTenant(admin.db, migrator, 'ing-a', 'lis_t_ing_a')

  const db = await registry.forTenant(tenantId)
  const agent = await db
    .insertInto('agents')
    .values({ agent_key_id: crypto.randomUUID(), hostname: 'PC-1', agent_version: '0.1.0' })
    .returningAll()
    .executeTakeFirstOrThrow()
  agentId = agent.id
}, 90_000)

afterAll(async () => {
  await app.close()
})

function rawMessage(messageId: string, payload: string, deviceId = 'may-sinh-hoa') {
  return {
    type: 'raw_message' as const,
    messageId,
    sentAt: new Date().toISOString(),
    deviceId,
    direction: 'inbound' as const,
    payloadBase64: Buffer.from(payload, 'utf8').toString('base64'),
  }
}

describe('IngestService', () => {
  it('lưu raw message và tự tạo device khi thấy device_key lạ', async () => {
    await ingest.handle(tenantId, agentId, rawMessage(newMessageId(), 'H|\\^&|||Mindray'))

    const db = await registry.forTenant(tenantId)
    const devices = await db.selectFrom('devices').selectAll().execute()
    expect(devices.length).toBe(1)
    expect(devices[0]!.device_key).toBe('may-sinh-hoa')

    const raws = await db.selectFrom('raw_messages').selectAll().execute()
    expect(raws.length).toBe(1)
    expect(raws[0]!.payload.toString('utf8')).toBe('H|\\^&|||Mindray')
    expect(raws[0]!.parse_status).toBe('pending')
  })

  it('gửi lại cùng messageId không tạo bản ghi thứ hai', async () => {
    const id = newMessageId()
    await ingest.handle(tenantId, agentId, rawMessage(id, 'R|1|^^^Glu|5.4'))
    await ingest.handle(tenantId, agentId, rawMessage(id, 'R|1|^^^Glu|5.4'))

    const db = await registry.forTenant(tenantId)
    const rows = await db
      .selectFrom('raw_messages')
      .selectAll()
      .where('message_id', '=', id)
      .execute()
    expect(rows.length).toBe(1)
  })

  it('hai messageId khác nhau với nội dung giống hệt vẫn tạo hai bản ghi', async () => {
    const db = await registry.forTenant(tenantId)
    const before = await db.selectFrom('raw_messages').selectAll().execute()

    await ingest.handle(tenantId, agentId, rawMessage(newMessageId(), 'R|2|^^^Ure|6.1'))
    await ingest.handle(tenantId, agentId, rawMessage(newMessageId(), 'R|2|^^^Ure|6.1'))

    const after = await db.selectFrom('raw_messages').selectAll().execute()
    expect(after.length).toBe(before.length + 2)
  })

  it('lưu connection event và cập nhật device_status', async () => {
    await ingest.handle(tenantId, agentId, {
      type: 'connection_event',
      messageId: newMessageId(),
      sentAt: new Date().toISOString(),
      deviceId: 'may-sinh-hoa',
      status: 'disconnected',
      detail: 'cổng COM3 bị rút',
    })

    const db = await registry.forTenant(tenantId)
    const status = await db.selectFrom('device_status').selectAll().executeTakeFirstOrThrow()
    expect(status.status).toBe('disconnected')
    expect(status.detail).toBe('cổng COM3 bị rút')
  })

  it('connection event mới hơn ghi đè trạng thái cũ', async () => {
    await new Promise((r) => setTimeout(r, 5))
    await ingest.handle(tenantId, agentId, {
      type: 'connection_event',
      messageId: newMessageId(),
      sentAt: new Date().toISOString(),
      deviceId: 'may-sinh-hoa',
      status: 'connected',
    })

    const db = await registry.forTenant(tenantId)
    const status = await db.selectFrom('device_status').selectAll().executeTakeFirstOrThrow()
    expect(status.status).toBe('connected')
    expect(status.detail).toBeNull()
  })

  it('raw_message không được đè lên trạng thái mất kết nối (C4)', async () => {
    // Tái lập đúng thứ tự xả hàng đợi đã thấy trên Postgres thật: connected
    // (t-60s) → raw_message (t-40s) → disconnected (t-20s). Trạng thái cuối
    // phải là 'disconnected' — raw_message chỉ được đụng vào last_message_at,
    // không bao giờ đụng vào updated_at, nếu không nó ghi đè bằng giờ Core
    // nhận (luôn muộn hơn occurred_at của connection_event) và làm biến mất
    // sự kiện mất kết nối trên device_status.
    const db = await registry.forTenant(tenantId)
    const device = await db
      .insertInto('devices')
      .values({
        agent_id: agentId,
        device_key: 'may-c4',
        name: 'may-c4',
        transport: 'tcp',
        driver: 'unknown',
      })
      .returning('id')
      .executeTakeFirstOrThrow()

    const now = Date.now()
    const t60 = new Date(now - 60_000).toISOString()
    const t40 = new Date(now - 40_000).toISOString()
    const t20 = new Date(now - 20_000).toISOString()

    await ingest.handle(tenantId, agentId, {
      type: 'connection_event',
      messageId: newMessageId(),
      sentAt: t60,
      deviceId: 'may-c4',
      status: 'connected',
    })

    await ingest.handle(tenantId, agentId, {
      type: 'raw_message',
      messageId: newMessageId(),
      sentAt: t40,
      deviceId: 'may-c4',
      direction: 'inbound',
      payloadBase64: Buffer.from('R|1|^^^Glu|5.4', 'utf8').toString('base64'),
    })

    await ingest.handle(tenantId, agentId, {
      type: 'connection_event',
      messageId: newMessageId(),
      sentAt: t20,
      deviceId: 'may-c4',
      status: 'disconnected',
      detail: 'mất kết nối',
    })

    const status = await db
      .selectFrom('device_status')
      .selectAll()
      .where('device_id', '=', device.id)
      .executeTakeFirstOrThrow()
    expect(status.status).toBe('disconnected')
  })

  it('raw_message tới trước khi có connection_event nào vẫn tạo dòng device_status hợp lý', async () => {
    const db = await registry.forTenant(tenantId)

    await ingest.handle(tenantId, agentId, rawMessage(newMessageId(), 'R|1|^^^Ure|6.1', 'may-c4-b'))

    const devices = await db
      .selectFrom('devices')
      .selectAll()
      .where('device_key', '=', 'may-c4-b')
      .execute()
    const status = await db
      .selectFrom('device_status')
      .selectAll()
      .where('device_id', '=', devices[0]!.id)
      .executeTakeFirstOrThrow()
    expect(status.status).toBe('connected')
    expect(status.last_message_at).not.toBeNull()
  })

  it('heartbeat cập nhật last_seen_at của agent', async () => {
    await ingest.handle(tenantId, agentId, {
      type: 'heartbeat',
      messageId: newMessageId(),
      sentAt: new Date().toISOString(),
      queueDepth: 3,
    })

    const db = await registry.forTenant(tenantId)
    const agent = await db
      .selectFrom('agents')
      .selectAll()
      .where('id', '=', agentId)
      .executeTakeFirstOrThrow()
    expect(agent.last_seen_at).not.toBeNull()
  })

  it('ghi cho tenant này không rò sang tenant khác', async () => {
    const admin = app.get(AdminDbService)
    const migrator = app.get(TenantMigratorService)
    const otherTenant = await prepareTestTenant(admin.db, migrator, 'ing-b', 'lis_t_ing_b')

    const otherDb = await registry.forTenant(otherTenant)
    const rows = await otherDb.selectFrom('raw_messages').selectAll().execute()
    expect(rows).toEqual([])
  })
})
