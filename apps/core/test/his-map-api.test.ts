import 'reflect-metadata'
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import request from 'supertest'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
import { AppModule } from '../src/app.module.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'
import { createTestUser, prepareTestTenant, resetAdminDatabase, testDbName } from './helpers.js'

/**
 * API bảng dịch mã HIS ↔ dịch vụ xét nghiệm.
 *
 * Bảng này là chỗ chỉ định của bác sĩ đi từ hệ thống bệnh viện sang LIS. Sai
 * một dòng ở đây là chỉ định chạy sang xét nghiệm khác, nên bộ test dưới đây
 * bám vào ba điều: tìm được (650 dòng thật, không tải hết về), không cho ghi
 * đè trong im lặng (409 có nói rõ đang trỏ tới đâu), và chỉ ra được xét
 * nghiệm nào HIS chưa gọi tới được.
 */

let app: INestApplication
let tenantA: string
let tenantB: string
let tokenA: string
let tokenB: string
let registry: TenantRegistryService

const SEED_ANALYTES = [
  { code: '123AST', name: 'AST (GOT)', category: 'SH', source: 'legacy' as const },
  { code: '200GLU', name: 'Glucose máu', category: 'SH', source: 'legacy' as const },
  { code: '929AG', name: 'Tỉ số A/G', category: 'SH', source: 'legacy' as const },
  // Cặp bảo vệ ràng buộc "không LIKE trần trên cột mã": '%' là ký tự đại diện.
  { code: '034NEU%', name: 'Bạch cầu trung tính phần trăm', category: 'CTM', source: 'legacy' as const },
  { code: '034NEUT', name: 'Bạch cầu trung tính tuyệt đối', category: 'CTM', source: 'legacy-new' as const },
  // Xét nghiệm CHƯA có mã HIS nào trỏ tới — câu hỏi chính của màn hình.
  { code: '400TPTNT', name: 'Tổng phân tích nước tiểu', category: 'NT', source: 'legacy' as const },
]

/** Ánh xạ seed: hai mã HIS khác nhau cùng trỏ tới 929AG là chuyện hợp lệ. */
const SEED_MAP = [
  { his_service_code: 'A/01', analyte_code: '929AG' },
  { his_service_code: 'A/02', analyte_code: '929AG' },
  { his_service_code: 'GLU01', analyte_code: '200GLU' },
  { his_service_code: 'AST01', analyte_code: '123AST' },
  { his_service_code: 'NEU%01', analyte_code: '034NEU%' },
  { his_service_code: 'NEUT01', analyte_code: '034NEUT' },
]

const authA = () => ({ Authorization: `Bearer ${tokenA}` })
const authB = () => ({ Authorization: `Bearer ${tokenB}` })

async function login(tenantCode: string, username: string, password: string): Promise<string> {
  const res = await request(app.getHttpServer())
    .post('/api/auth/login')
    .send({ tenantCode, username, password })
    .expect(201)
  return res.body.token
}

async function latestAudit(
  tenantId: string,
  entity: string,
  entityKey: string,
): Promise<
  | {
      action: string
      actor_username: string
      changed_at: Date
      before_value: any
      after_value: any
    }
  | undefined
> {
  const db = await registry.forTenant(tenantId)
  return (await db
    .selectFrom('catalogue_audit')
    .selectAll()
    .where('entity', '=', entity)
    .where('entity_key', '=', entityKey)
    .orderBy('changed_at', 'desc')
    .orderBy('id', 'desc')
    .executeTakeFirst()) as never
}

beforeAll(async () => {
  await resetAdminDatabase()
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()

  const admin = app.get(AdminDbService)
  const migrator = app.get(TenantMigratorService)
  registry = app.get(TenantRegistryService)

  tenantA = await prepareTestTenant(admin.db, migrator, 'his-a', testDbName('lis_t_his_a'))
  tenantB = await prepareTestTenant(admin.db, migrator, 'his-b', testDbName('lis_t_his_b'))
  await createTestUser(admin.db, tenantA, 'ktv-a', 'mat-khau-test-a')
  await createTestUser(admin.db, tenantB, 'ktv-b', 'mat-khau-test-b')

  const dbA = await registry.forTenant(tenantA)
  await dbA
    .insertInto('analytes')
    .values(SEED_ANALYTES.map((a) => ({ ...a, result_is_comment: false })))
    .execute()
  await dbA.insertInto('his_analyte_map').values(SEED_MAP).execute()

  const dbB = await registry.forTenant(tenantB)
  await dbB
    .insertInto('analytes')
    .values({
      code: '999BENHVIENB',
      name: 'Chỉ có ở cơ sở B',
      source: 'legacy',
      result_is_comment: false,
    })
    .execute()
  await dbB
    .insertInto('his_analyte_map')
    .values({ his_service_code: 'RIENG-B', analyte_code: '999BENHVIENB' })
    .execute()

  tokenA = await login('his-a', 'ktv-a', 'mat-khau-test-a')
  tokenB = await login('his-b', 'ktv-b', 'mat-khau-test-b')
}, 120_000)

afterAll(async () => {
  await app.close()
})

describe('GET /api/catalogue/his-map', () => {
  it('liệt kê kèm tổng số và tên xét nghiệm đích', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/his-map')
      .set(authA())
      .expect(200)

    expect(res.body.total).toBe(SEED_MAP.length)
    const row = res.body.items.find((i: { hisServiceCode: string }) => i.hisServiceCode === 'A/01')
    expect(row).toMatchObject({ analyteCode: '929AG', analyteName: 'Tỉ số A/G' })
  })

  it('phân trang phía máy chủ, không trả hết một lượt', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/his-map?limit=2&offset=0')
      .set(authA())
      .expect(200)

    expect(res.body.items.length).toBe(2)
    expect(res.body.total).toBe(SEED_MAP.length)

    const trang2 = await request(app.getHttpServer())
      .get('/api/catalogue/his-map?limit=2&offset=2')
      .set(authA())
      .expect(200)
    const codes1 = res.body.items.map((i: { hisServiceCode: string }) => i.hisServiceCode)
    const codes2 = trang2.body.items.map((i: { hisServiceCode: string }) => i.hisServiceCode)
    expect(codes1).not.toEqual(codes2)
  })

  it('tìm được theo mã HIS', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/his-map?q=GLU01')
      .set(authA())
      .expect(200)
    expect(res.body.total).toBe(1)
    expect(res.body.items[0].analyteCode).toBe('200GLU')
  })

  it('tìm được theo mã xét nghiệm đích', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/his-map?q=929AG')
      .set(authA())
      .expect(200)
    expect(res.body.total).toBe(2)
  })

  it('tìm được theo TÊN xét nghiệm đích', async () => {
    const res = await request(app.getHttpServer())
      .get(`/api/catalogue/his-map?q=${encodeURIComponent('Glucose')}`)
      .set(authA())
      .expect(200)
    expect(res.body.total).toBe(1)
    expect(res.body.items[0].hisServiceCode).toBe('GLU01')
  })

  it('không để ký tự đại diện của LIKE lọt vào truy vấn', async () => {
    // '034NEU%' và '034NEUT' chỉ khác nhau ở ký tự cuối. Không thoát '%' thì
    // tìm '034NEU%' sẽ kéo về cả hai dòng.
    const res = await request(app.getHttpServer())
      .get(`/api/catalogue/his-map?q=${encodeURIComponent('034NEU%')}`)
      .set(authA())
      .expect(200)
    expect(res.body.total).toBe(1)
    expect(res.body.items[0].hisServiceCode).toBe('NEU%01')
  })
})

describe('GET /api/catalogue/his-map/unmapped-analytes', () => {
  it('chỉ ra xét nghiệm CHƯA có mã HIS nào trỏ tới', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/his-map/unmapped-analytes')
      .set(authA())
      .expect(200)

    const codes = res.body.items.map((i: { code: string }) => i.code)
    expect(codes).toContain('400TPTNT')
    expect(codes).not.toContain('200GLU')
    expect(res.body.total).toBe(1)
  })

  it('có phân trang và tìm kiếm riêng', async () => {
    const res = await request(app.getHttpServer())
      .get(`/api/catalogue/his-map/unmapped-analytes?q=${encodeURIComponent('nước tiểu')}&limit=10`)
      .set(authA())
      .expect(200)
    expect(res.body.total).toBe(1)
    expect(res.body.items[0].name).toContain('nước tiểu')
  })
})

describe('GET /api/catalogue/his-map/summary', () => {
  it('đếm được số ánh xạ và số xét nghiệm HIS chưa gọi tới được', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/his-map/summary')
      .set(authA())
      .expect(200)

    expect(res.body).toMatchObject({
      mappingCount: SEED_MAP.length,
      analyteCount: SEED_ANALYTES.length,
      mappedAnalyteCount: SEED_ANALYTES.length - 1,
      unmappedAnalyteCount: 1,
    })
  })
})

describe('POST /api/catalogue/his-map', () => {
  it('thêm được một ánh xạ mới', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/his-map')
      .set(authA())
      .send({ hisServiceCode: 'TPTNT01', analyteCode: '400TPTNT' })
      .expect(201)

    const res = await request(app.getHttpServer())
      .get('/api/catalogue/his-map?q=TPTNT01')
      .set(authA())
      .expect(200)
    expect(res.body.items[0]).toMatchObject({
      analyteCode: '400TPTNT',
      analyteName: 'Tổng phân tích nước tiểu',
    })

    // Dọn lại để các test sau vẫn thấy đúng một xét nghiệm chưa ánh xạ.
    await request(app.getHttpServer())
      .delete('/api/catalogue/his-map/TPTNT01')
      .set(authA())
      .expect(204)
  })

  it('mã HIS trùng trả 409 và nói rõ đang trỏ tới xét nghiệm nào', async () => {
    const res = await request(app.getHttpServer())
      .post('/api/catalogue/his-map')
      .set(authA())
      .send({ hisServiceCode: 'GLU01', analyteCode: '123AST' })
      .expect(409)

    expect(res.body.message).toContain('GLU01')
    expect(res.body.message).toContain('200GLU')
    expect(res.body.message).toContain('Glucose máu')
  })

  it('mã xét nghiệm không có trong danh mục trả 400, không phải 500', async () => {
    const res = await request(app.getHttpServer())
      .post('/api/catalogue/his-map')
      .set(authA())
      .send({ hisServiceCode: 'KHONGCO01', analyteCode: 'KHONG-TON-TAI' })
      .expect(400)
    expect(res.body.message).toContain('KHONG-TON-TAI')
  })
})

describe('PUT/DELETE /api/catalogue/his-map/:hisServiceCode', () => {
  it('sửa được xét nghiệm đích của một mã HIS', async () => {
    const res = await request(app.getHttpServer())
      .put('/api/catalogue/his-map/AST01')
      .set(authA())
      .send({ analyteCode: '200GLU' })
      .expect(200)
    expect(res.body).toMatchObject({ hisServiceCode: 'AST01', analyteCode: '200GLU' })

    await request(app.getHttpServer())
      .put('/api/catalogue/his-map/AST01')
      .set(authA())
      .send({ analyteCode: '123AST' })
      .expect(200)
  })

  it('sửa mã HIS không tồn tại trả 404', async () => {
    await request(app.getHttpServer())
      .put('/api/catalogue/his-map/KHONG-CO-MA-NAY')
      .set(authA())
      .send({ analyteCode: '123AST' })
      .expect(404)
  })

  it('xoá được và xoá lần hai trả 404', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/his-map')
      .set(authA())
      .send({ hisServiceCode: 'TAM01', analyteCode: '123AST' })
      .expect(201)
    await request(app.getHttpServer())
      .delete('/api/catalogue/his-map/TAM01')
      .set(authA())
      .expect(204)
    await request(app.getHttpServer())
      .delete('/api/catalogue/his-map/TAM01')
      .set(authA())
      .expect(404)
  })

  it('mã HIS chứa ký tự đặc biệt vẫn xoá được qua URL đã mã hoá', async () => {
    await request(app.getHttpServer())
      .delete(`/api/catalogue/his-map/${encodeURIComponent('NEU%01')}`)
      .set(authA())
      .expect(204)
    await request(app.getHttpServer())
      .post('/api/catalogue/his-map')
      .set(authA())
      .send({ hisServiceCode: 'NEU%01', analyteCode: '034NEU%' })
      .expect(201)
  })
})

describe('nhật ký sửa đổi', () => {
  it('ghi vết cho cả thêm, sửa và xoá', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/his-map')
      .set(authA())
      .send({ hisServiceCode: 'VET01', analyteCode: '123AST' })
      .expect(201)
    const taoMoi = await latestAudit(tenantA, 'his_analyte_map', 'VET01')
    expect(taoMoi!.action).toBe('create')
    expect(taoMoi!.actor_username).toBe('ktv-a')
    expect(taoMoi!.before_value).toBeNull()
    expect(taoMoi!.after_value.analyte_code).toBe('123AST')

    await request(app.getHttpServer())
      .put('/api/catalogue/his-map/VET01')
      .set(authA())
      .send({ analyteCode: '200GLU' })
      .expect(200)
    const sua = await latestAudit(tenantA, 'his_analyte_map', 'VET01')
    expect(sua!.action).toBe('update')
    expect(sua!.before_value.analyte_code).toBe('123AST')
    expect(sua!.after_value.analyte_code).toBe('200GLU')

    await request(app.getHttpServer())
      .delete('/api/catalogue/his-map/VET01')
      .set(authA())
      .expect(204)
    const xoa = await latestAudit(tenantA, 'his_analyte_map', 'VET01')
    expect(xoa!.action).toBe('delete')
    expect(xoa!.after_value).toBeNull()
  })

  it('không ghi vết khi thao tác bị từ chối', async () => {
    const truoc = await latestAudit(tenantA, 'his_analyte_map', 'GLU01')
    await request(app.getHttpServer())
      .post('/api/catalogue/his-map')
      .set(authA())
      .send({ hisServiceCode: 'GLU01', analyteCode: '123AST' })
      .expect(409)
    const sau = await latestAudit(tenantA, 'his_analyte_map', 'GLU01')
    expect(sau?.changed_at).toEqual(truoc?.changed_at)
  })
})

describe('cách ly giữa hai bệnh viện', () => {
  it('token cơ sở A không thấy ánh xạ của cơ sở B', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/his-map?q=RIENG-B')
      .set(authA())
      .expect(200)
    expect(res.body.total).toBe(0)

    const resB = await request(app.getHttpServer())
      .get('/api/catalogue/his-map?q=RIENG-B')
      .set(authB())
      .expect(200)
    expect(resB.body.total).toBe(1)
  })

  it('token cơ sở A không sửa và không xoá được ánh xạ của cơ sở B', async () => {
    await request(app.getHttpServer())
      .put('/api/catalogue/his-map/RIENG-B')
      .set(authA())
      .send({ analyteCode: '123AST' })
      .expect(404)
    await request(app.getHttpServer())
      .delete('/api/catalogue/his-map/RIENG-B')
      .set(authA())
      .expect(404)

    // Dòng của cơ sở B vẫn nguyên vẹn.
    const resB = await request(app.getHttpServer())
      .get('/api/catalogue/his-map?q=RIENG-B')
      .set(authB())
      .expect(200)
    expect(resB.body.items[0].analyteCode).toBe('999BENHVIENB')
  })

  it('nhật ký của mỗi bệnh viện nằm trong database của chính nó', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/his-map')
      .set(authB())
      .send({ hisServiceCode: 'VET-B', analyteCode: '999BENHVIENB' })
      .expect(201)
    expect((await latestAudit(tenantB, 'his_analyte_map', 'VET-B'))!.actor_username).toBe('ktv-b')
    expect(await latestAudit(tenantA, 'his_analyte_map', 'VET-B')).toBeUndefined()
  })
})
