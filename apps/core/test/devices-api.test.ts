import 'reflect-metadata'
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import { newMessageId } from '@lis/shared'
import request from 'supertest'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
import { AppModule } from '../src/app.module.js'
import { IngestService } from '../src/ingest/ingest.service.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'
import { createTestUser, prepareTestTenant, resetAdminDatabase, testDbName } from './helpers.js'

let app: INestApplication
let tenantA: string
let tenantB: string
let deviceAId: string
let tokenA: string
let tokenB: string

async function login(tenantCode: string, username: string, password: string): Promise<string> {
  const res = await request(app.getHttpServer())
    .post('/api/auth/login')
    .send({ tenantCode, username, password })
    .expect(201)
  return res.body.token
}

beforeAll(async () => {
  await resetAdminDatabase()
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()

  const admin = app.get(AdminDbService)
  const migrator = app.get(TenantMigratorService)
  const registry = app.get(TenantRegistryService)
  const ingest = app.get(IngestService)

  tenantA = await prepareTestTenant(admin.db, migrator, 'api-a', testDbName('lis_t_api_a'))
  tenantB = await prepareTestTenant(admin.db, migrator, 'api-b', testDbName('lis_t_api_b'))
  await createTestUser(admin.db, tenantA, 'ktv-a', 'mat-khau-test-a')
  await createTestUser(admin.db, tenantB, 'ktv-b', 'mat-khau-test-b')

  const db = await registry.forTenant(tenantA)
  const agent = await db
    .insertInto('agents')
    .values({ agent_key_id: crypto.randomUUID(), hostname: 'PC-A', agent_version: '0.1.0' })
    .returningAll()
    .executeTakeFirstOrThrow()

  await ingest.handle(tenantA, agent.id, {
    type: 'raw_message',
    messageId: newMessageId(),
    sentAt: new Date().toISOString(),
    deviceId: 'may-a',
    direction: 'inbound',
    payloadBase64: Buffer.from('R|1|^^^Glu|5.4', 'utf8').toString('base64'),
  })
  await ingest.handle(tenantA, agent.id, {
    type: 'connection_event',
    messageId: newMessageId(),
    sentAt: new Date().toISOString(),
    deviceId: 'may-a',
    status: 'connected',
  })

  deviceAId = (await db.selectFrom('devices').select('id').executeTakeFirstOrThrow()).id

  tokenA = await login('api-a', 'ktv-a', 'mat-khau-test-a')
  tokenB = await login('api-b', 'ktv-b', 'mat-khau-test-b')
}, 90_000)

afterAll(async () => {
  await app.close()
})

describe('GET /api/devices', () => {
  it('trả về máy của tenant trong header', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/devices')
      .set('Authorization', `Bearer ${tokenA}`)
      .expect(200)

    expect(res.body).toHaveLength(1)
    expect(res.body[0]).toMatchObject({ deviceKey: 'may-a', status: 'connected' })
  })

  it('không trả về máy của tenant khác', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/devices')
      .set('Authorization', `Bearer ${tokenB}`)
      .expect(200)

    expect(res.body).toEqual([])
  })

  it('từ chối request thiếu token', async () => {
    await request(app.getHttpServer()).get('/api/devices').expect(401)
  })

  it('từ chối token giả mạo/hỏng', async () => {
    await request(app.getHttpServer())
      .get('/api/devices')
      .set('Authorization', 'Bearer khong-phai-jwt-that')
      .expect(401)
  })
})

describe('GET /api/devices/:id/raw-messages', () => {
  it('trả về payload dạng base64 kèm bản đọc được', async () => {
    const res = await request(app.getHttpServer())
      .get(`/api/devices/${deviceAId}/raw-messages`)
      .set('Authorization', `Bearer ${tokenA}`)
      .expect(200)

    expect(res.body).toHaveLength(1)
    expect(res.body[0].payloadText).toBe('R|1|^^^Glu|5.4')
    expect(res.body[0].parseStatus).toBe('pending')
  })

  it('không cho tenant khác đọc message của máy này', async () => {
    await request(app.getHttpServer())
      .get(`/api/devices/${deviceAId}/raw-messages`)
      .set('Authorization', `Bearer ${tokenB}`)
      .expect(404)
  })
})

describe('GET /api/devices/:id/connection-events', () => {
  it('trả về lịch sử kết nối mới nhất trước', async () => {
    const res = await request(app.getHttpServer())
      .get(`/api/devices/${deviceAId}/connection-events`)
      .set('Authorization', `Bearer ${tokenA}`)
      .expect(200)

    expect(res.body[0]).toMatchObject({ status: 'connected' })
  })
})

describe('JwtTenantGuard — thu hồi giữa phiên (đặt CUỐI file: đổi trạng thái tenantB)', () => {
  it('bệnh viện bị tạm ngưng: token cũ (chữ ký còn hợp lệ, chưa hết hạn theo exp) bị từ chối 401 — KHÔNG phải 500', async () => {
    const admin = app.get(AdminDbService)
    await admin.db
      .updateTable('tenants')
      .set({ status: 'suspended' })
      .where('id', '=', tenantB)
      .execute()

    // Trước bản sửa round-1, TenantRegistryService ném Error thường (không
    // phải HttpException) khi tenant tạm ngưng, và guard không tra lại DB nên
    // request lọt xuống tới đó — Nest trả 500 kèm stack trace thay vì 401.
    await request(app.getHttpServer())
      .get('/api/devices')
      .set('Authorization', `Bearer ${tokenB}`)
      .expect(401)
  })
})

describe('POST /api/auth/login — request hỏng/rỗng', () => {
  it('không có body nào (không header Content-Type) trả 401, KHÔNG phải 500', async () => {
    // Không gọi .send(...) — mô phỏng đúng `curl -X POST` trần mà reviewer
    // dùng để phát hiện lỗi: @Body() trả undefined, .tenantCode trên đó từng
    // ném TypeError chưa bắt, lộ ra thành 500 kèm stack trace.
    await request(app.getHttpServer()).post('/api/auth/login').expect(401)
  })

  it('body rỗng {} trả 401', async () => {
    await request(app.getHttpServer()).post('/api/auth/login').send({}).expect(401)
  })

  it('trường lạ kiểu object/mảng (thử tiêm toán tử Mongo-style) không gây lỗi, trả 401', async () => {
    await request(app.getHttpServer())
      .post('/api/auth/login')
      .send({ tenantCode: { $ne: null }, username: ['rv-c'], password: 123 })
      .expect(401)
  })
})
