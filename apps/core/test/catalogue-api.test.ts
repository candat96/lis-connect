import 'reflect-metadata'
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import request from 'supertest'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
import { AppModule } from '../src/app.module.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'
import { createTestUser, prepareTestTenant, resetAdminDatabase } from './helpers.js'

let app: INestApplication
let tenantA: string
let tenantB: string
let deviceA: string
let deviceB: string
let tokenA: string
let tokenB: string

/**
 * Danh mục seed cho cơ sở A. Số dòng được suy ra từ chính mảng này thay vì
 * viết cứng: thêm một dòng vào đây mà quên sửa số kỳ vọng sẽ làm test đỏ vì
 * lý do sai.
 */
const SEED_ANALYTES = [
  {
    code: '123AST',
    name: 'AST (GOT)',
    category: 'SH',
    unit: 'U/L',
    decimals: 1,
    lower_limit: '5',
    higher_limit: '40',
    source: 'legacy' as const,
  },
  {
    code: '200GLU',
    name: 'Glucose máu',
    category: 'SH',
    unit: 'mmol/L',
    decimals: 2,
    source: 'legacy' as const,
  },
  // Cặp bảo vệ ràng buộc "không LIKE trên cột mã": '%' là ký tự đại diện.
  { code: '034NEU%', name: 'Bạch cầu trung tính phần trăm', category: 'CTM', source: 'legacy' as const },
  { code: '034NEUT', name: 'Bạch cầu trung tính tuyệt đối', category: 'CTM', source: 'legacy-new' as const },
  // Cặp bảo vệ tương tự cho '_' — cũng là ký tự đại diện của LIKE.
  { code: 'XNNT21_01', name: 'Nước tiểu mẫu gạch dưới', category: 'NT', source: 'legacy-new' as const },
  { code: 'XNNT21A01', name: 'Nước tiểu mẫu chữ A', category: 'NT', source: 'legacy-new' as const },
  // Ký tự đại diện nằm trong TÊN: tìm theo tên dùng ILIKE nên chuỗi người
  // dùng nhập phải được thoát, nếu không '%' và '_' sẽ khớp lung tung.
  { code: '900W1', name: 'Đo 50% ethanol', category: 'ĐL', source: 'manual' as const },
  { code: '900W2', name: 'Đo 50X ethanol', category: 'ĐL', source: 'manual' as const },
  { code: '901U1', name: 'Mẫu A_1 đặc biệt', category: 'ĐL', source: 'manual' as const },
  { code: '901U2', name: 'Mẫu AX1 đặc biệt', category: 'ĐL', source: 'manual' as const },
]

async function login(tenantCode: string, username: string, password: string): Promise<string> {
  const res = await request(app.getHttpServer())
    .post('/api/auth/login')
    .send({ tenantCode, username, password })
    .expect(201)
  return res.body.token
}

const authA = () => ({ Authorization: `Bearer ${tokenA}` })
const authB = () => ({ Authorization: `Bearer ${tokenB}` })

beforeAll(async () => {
  await resetAdminDatabase()
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()

  const admin = app.get(AdminDbService)
  const migrator = app.get(TenantMigratorService)
  const registry = app.get(TenantRegistryService)

  tenantA = await prepareTestTenant(admin.db, migrator, 'cat-a', 'lis_t_cat_a')
  tenantB = await prepareTestTenant(admin.db, migrator, 'cat-b', 'lis_t_cat_b')
  await createTestUser(admin.db, tenantA, 'ktv-a', 'mat-khau-test-a')
  await createTestUser(admin.db, tenantB, 'ktv-b', 'mat-khau-test-b')

  const dbA = await registry.forTenant(tenantA)
  await dbA
    .insertInto('analytes')
    .values(SEED_ANALYTES.map((a) => ({ ...a, result_is_comment: false })))
    .execute()

  const agentA = await dbA
    .insertInto('agents')
    .values({ agent_key_id: crypto.randomUUID(), hostname: 'PC-A', agent_version: '0.1.0' })
    .returningAll()
    .executeTakeFirstOrThrow()
  deviceA = (
    await dbA
      .insertInto('devices')
      .values({
        agent_id: agentA.id,
        device_key: 'au640',
        name: 'AU640',
        transport: 'serial',
        driver: 'astm-e1394',
      })
      .returningAll()
      .executeTakeFirstOrThrow()
  ).id

  // Cơ sở B: danh mục và máy riêng — dùng để chứng minh cách ly giữa hai bệnh viện.
  const dbB = await registry.forTenant(tenantB)
  await dbB
    .insertInto('analytes')
    .values({
      code: '999BENHVIENB',
      name: 'Chỉ có ở cơ sở B',
      category: 'RIENG',
      result_is_comment: false,
      source: 'legacy',
    })
    .execute()
  const agentB = await dbB
    .insertInto('agents')
    .values({ agent_key_id: crypto.randomUUID(), hostname: 'PC-B', agent_version: '0.1.0' })
    .returningAll()
    .executeTakeFirstOrThrow()
  deviceB = (
    await dbB
      .insertInto('devices')
      .values({
        agent_id: agentB.id,
        device_key: 'may-b',
        name: 'Máy cơ sở B',
        transport: 'tcp',
        driver: 'hl7',
      })
      .returningAll()
      .executeTakeFirstOrThrow()
  ).id

  tokenA = await login('cat-a', 'ktv-a', 'mat-khau-test-a')
  tokenB = await login('cat-b', 'ktv-b', 'mat-khau-test-b')
}, 120_000)

afterAll(async () => {
  await app.close()
})

describe('GET /api/catalogue/analytes', () => {
  it('trả về danh sách kèm tổng số', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/analytes')
      .set(authA())
      .expect(200)

    expect(res.body.total).toBe(SEED_ANALYTES.length)
    expect(res.body.items.length).toBe(SEED_ANALYTES.length)
    const ast = res.body.items.find((i: { code: string }) => i.code === '123AST')
    expect(ast).toMatchObject({ name: 'AST (GOT)', unit: 'U/L', category: 'SH', source: 'legacy' })
  })

  it('trả ngưỡng dưới/trên dạng số, không phải chuỗi', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/analytes?q=123AST')
      .set(authA())
      .expect(200)

    const ast = res.body.items[0]
    expect(ast.lowerLimit).toBe(5)
    expect(ast.higherLimit).toBe(40)
    expect(typeof ast.lowerLimit).toBe('number')
  })

  it('tìm được theo mã', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/analytes?q=123AST')
      .set(authA())
      .expect(200)

    expect(res.body.total).toBe(1)
    expect(res.body.items[0].code).toBe('123AST')
  })

  it('tìm được theo tên không phân biệt hoa thường', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/analytes?q=glucose')
      .set(authA())
      .expect(200)

    expect(res.body.total).toBe(1)
    expect(res.body.items[0].code).toBe('200GLU')
  })

  it('tìm được theo tên có dấu tiếng Việt', async () => {
    const res = await request(app.getHttpServer())
      .get(`/api/catalogue/analytes?q=${encodeURIComponent('BẠCH CẦU TRUNG TÍNH')}`)
      .set(authA())
      .expect(200)

    expect(res.body.total).toBe(2)
  })

  it('tìm mã chứa ký tự phần trăm không khớp nhầm mã khác', async () => {
    const res = await request(app.getHttpServer())
      .get(`/api/catalogue/analytes?q=${encodeURIComponent('034NEU%')}`)
      .set(authA())
      .expect(200)

    expect(res.body.total).toBe(1)
    expect(res.body.items[0].code).toBe('034NEU%')
  })

  it('tìm mã chứa dấu gạch dưới không khớp nhầm mã khác', async () => {
    const res = await request(app.getHttpServer())
      .get(`/api/catalogue/analytes?q=${encodeURIComponent('XNNT21_01')}`)
      .set(authA())
      .expect(200)

    expect(res.body.total).toBe(1)
    expect(res.body.items[0].code).toBe('XNNT21_01')
  })

  it('thoát ký tự đại diện trong chuỗi tìm theo tên', async () => {
    const percent = await request(app.getHttpServer())
      .get(`/api/catalogue/analytes?q=${encodeURIComponent('50% ethanol')}`)
      .set(authA())
      .expect(200)
    expect(percent.body.items.map((i: { code: string }) => i.code)).toEqual(['900W1'])

    const underscore = await request(app.getHttpServer())
      .get(`/api/catalogue/analytes?q=${encodeURIComponent('A_1 đặc biệt')}`)
      .set(authA())
      .expect(200)
    expect(underscore.body.items.map((i: { code: string }) => i.code)).toEqual(['901U1'])
  })

  it('phân trang đúng', async () => {
    const all = await request(app.getHttpServer())
      .get('/api/catalogue/analytes?limit=100&offset=0')
      .set(authA())
      .expect(200)
    const codes = all.body.items.map((i: { code: string }) => i.code)

    const page = await request(app.getHttpServer())
      .get('/api/catalogue/analytes?limit=2&offset=2')
      .set(authA())
      .expect(200)

    expect(page.body.items).toHaveLength(2)
    // total là tổng số dòng khớp điều kiện, KHÔNG phải số dòng của trang này.
    expect(page.body.total).toBe(SEED_ANALYTES.length)
    expect(page.body.items.map((i: { code: string }) => i.code)).toEqual(codes.slice(2, 4))
  })

  it('lọc theo nhóm', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/analytes?category=CTM')
      .set(authA())
      .expect(200)

    expect(res.body.total).toBe(2)
    expect(res.body.items.every((i: { category: string }) => i.category === 'CTM')).toBe(true)
  })

  it('từ chối khi không có token', async () => {
    await request(app.getHttpServer()).get('/api/catalogue/analytes').expect(401)
  })

  it('không trả dữ liệu của cơ sở khác', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/analytes')
      .set(authB())
      .expect(200)

    expect(res.body.total).toBe(1)
    expect(res.body.items[0].code).toBe('999BENHVIENB')

    // Và tìm đích danh một mã của cơ sở A bằng token cơ sở B cũng không ra gì.
    const cross = await request(app.getHttpServer())
      .get('/api/catalogue/analytes?q=123AST')
      .set(authB())
      .expect(200)
    expect(cross.body.total).toBe(0)
  })
})

describe('GET /api/catalogue/categories', () => {
  it('trả về danh sách nhóm không trùng, đã sắp xếp', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/categories')
      .set(authA())
      .expect(200)

    expect(res.body).toEqual([...new Set(SEED_ANALYTES.map((a) => a.category))].sort())
  })

  it('không trả nhóm của cơ sở khác', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/categories')
      .set(authB())
      .expect(200)

    expect(res.body).toEqual(['RIENG'])
  })

  it('từ chối khi không có token', async () => {
    await request(app.getHttpServer()).get('/api/catalogue/categories').expect(401)
  })
})

describe('PATCH /api/catalogue/analytes/:code', () => {
  it('sửa được đơn vị, số lẻ và ngưỡng', async () => {
    const res = await request(app.getHttpServer())
      .patch('/api/catalogue/analytes/200GLU')
      .set(authA())
      .send({ unit: 'mg/dL', decimals: 3, lowerLimit: 3.9, higherLimit: 6.4 })
      .expect(200)

    expect(res.body).toMatchObject({
      code: '200GLU',
      unit: 'mg/dL',
      decimals: 3,
      lowerLimit: 3.9,
      higherLimit: 6.4,
    })
    expect(typeof res.body.lowerLimit).toBe('number')
  })

  it('sửa được xét nghiệm có mã chứa ký tự đại diện (mã hoá trên URL)', async () => {
    const res = await request(app.getHttpServer())
      .patch(`/api/catalogue/analytes/${encodeURIComponent('034NEU%')}`)
      .set(authA())
      .send({ unit: '%' })
      .expect(200)

    expect(res.body.code).toBe('034NEU%')
    expect(res.body.unit).toBe('%')

    // Không được sửa lây sang mã hàng xóm '034NEUT'.
    const neighbour = await request(app.getHttpServer())
      .get('/api/catalogue/analytes?q=034NEUT')
      .set(authA())
      .expect(200)
    expect(neighbour.body.items[0].unit).toBeNull()
  })

  it('không cho đổi mã', async () => {
    await request(app.getHttpServer())
      .patch('/api/catalogue/analytes/200GLU')
      .set(authA())
      .send({ code: 'MA-MOI', name: 'Glucose máu' })
      .expect(200)

    const moved = await request(app.getHttpServer())
      .get('/api/catalogue/analytes?q=MA-MOI')
      .set(authA())
      .expect(200)
    expect(moved.body.total).toBe(0)

    const still = await request(app.getHttpServer())
      .get('/api/catalogue/analytes?q=200GLU')
      .set(authA())
      .expect(200)
    expect(still.body.total).toBe(1)
  })

  it('trả 400 khi không có trường nào hợp lệ để sửa', async () => {
    await request(app.getHttpServer())
      .patch('/api/catalogue/analytes/200GLU')
      .set(authA())
      .send({ khong_ton_tai: 1 })
      .expect(400)
  })

  it('trả 404 với mã không tồn tại', async () => {
    await request(app.getHttpServer())
      .patch('/api/catalogue/analytes/KHONG-CO-MA-NAY')
      .set(authA())
      .send({ unit: 'x' })
      .expect(404)
  })

  it('đánh dấu nguồn thành manual sau khi người dùng sửa', async () => {
    const fromLegacy = await request(app.getHttpServer())
      .patch('/api/catalogue/analytes/123AST')
      .set(authA())
      .send({ normalRangeText: '5 - 40 U/L' })
      .expect(200)
    expect(fromLegacy.body.source).toBe('manual')

    // Nguồn 'legacy-new' (bảng danh mục thứ hai của hệ cũ) cũng phải đổi.
    const fromLegacyNew = await request(app.getHttpServer())
      .patch('/api/catalogue/analytes/XNNT21A01')
      .set(authA())
      .send({ name: 'Nước tiểu mẫu chữ A (đã sửa)' })
      .expect(200)
    expect(fromLegacyNew.body.source).toBe('manual')
  })

  it('từ chối khi không có token', async () => {
    await request(app.getHttpServer())
      .patch('/api/catalogue/analytes/123AST')
      .send({ unit: 'x' })
      .expect(401)
  })

  it('không cho cơ sở khác sửa xét nghiệm của cơ sở này', async () => {
    await request(app.getHttpServer())
      .patch('/api/catalogue/analytes/123AST')
      .set(authB())
      .send({ unit: 'bi-sua-trom' })
      .expect(404)

    const untouched = await request(app.getHttpServer())
      .get('/api/catalogue/analytes?q=123AST')
      .set(authA())
      .expect(200)
    expect(untouched.body.items[0].unit).toBe('U/L')
  })
})

describe('kênh máy', () => {
  it('liệt kê các kênh của một máy kèm tên xét nghiệm', async () => {
    await request(app.getHttpServer())
      .put(`/api/catalogue/devices/${deviceA}/channels/19`)
      .set(authA())
      .send({ analyteCode: '123AST', round: 1 })
      .expect(200)

    const res = await request(app.getHttpServer())
      .get(`/api/catalogue/devices/${deviceA}/channels`)
      .set(authA())
      .expect(200)

    expect(res.body).toHaveLength(1)
    expect(res.body[0]).toMatchObject({
      channelCode: '19',
      analyteCode: '123AST',
      analyteName: 'AST (GOT)',
      round: 1,
    })
  })

  it('từ chối gán hai xét nghiệm vào cùng một mã kênh', async () => {
    const res = await request(app.getHttpServer())
      .put(`/api/catalogue/devices/${deviceA}/channels/19`)
      .set(authA())
      .send({ analyteCode: '200GLU' })
      .expect(409)

    // Thông báo phải đọc được bằng tiếng Việt và nói rõ kênh nào đang trùng.
    expect(res.body.message).toContain('19')
    expect(res.body.message).toMatch(/kênh/i)
    expect(res.body.message).toContain('123AST')

    // Và ánh xạ cũ phải còn nguyên.
    const after = await request(app.getHttpServer())
      .get(`/api/catalogue/devices/${deviceA}/channels`)
      .set(authA())
      .expect(200)
    expect(after.body[0].analyteCode).toBe('123AST')
  })

  it('cho phép sửa cờ của chính ánh xạ đang có', async () => {
    const res = await request(app.getHttpServer())
      .put(`/api/catalogue/devices/${deviceA}/channels/19`)
      .set(authA())
      .send({ analyteCode: '123AST', round: 2, omit: true })
      .expect(200)

    expect(res.body).toMatchObject({ round: 2, omit: true, analyteCode: '123AST' })
  })

  it('gán được xét nghiệm có mã chứa ký tự đại diện', async () => {
    const res = await request(app.getHttpServer())
      .put(`/api/catalogue/devices/${deviceA}/channels/20`)
      .set(authA())
      .send({ analyteCode: '034NEU%' })
      .expect(200)
    expect(res.body.analyteCode).toBe('034NEU%')
  })

  it('trả 400 khi mã xét nghiệm không có trong danh mục', async () => {
    await request(app.getHttpServer())
      .put(`/api/catalogue/devices/${deviceA}/channels/21`)
      .set(authA())
      .send({ analyteCode: 'KHONG-CO-TRONG-DANH-MUC' })
      .expect(400)
  })

  it('xoá được một ánh xạ kênh', async () => {
    await request(app.getHttpServer())
      .delete(`/api/catalogue/devices/${deviceA}/channels/20`)
      .set(authA())
      .expect(204)

    const res = await request(app.getHttpServer())
      .get(`/api/catalogue/devices/${deviceA}/channels`)
      .set(authA())
      .expect(200)
    expect(res.body.map((c: { channelCode: string }) => c.channelCode)).toEqual(['19'])
  })

  it('trả 404 khi xoá ánh xạ không tồn tại', async () => {
    await request(app.getHttpServer())
      .delete(`/api/catalogue/devices/${deviceA}/channels/khong-co`)
      .set(authA())
      .expect(404)
  })

  it('trả 404 với máy của cơ sở khác', async () => {
    await request(app.getHttpServer())
      .get(`/api/catalogue/devices/${deviceB}/channels`)
      .set(authA())
      .expect(404)

    await request(app.getHttpServer())
      .put(`/api/catalogue/devices/${deviceA}/channels/99`)
      .set(authB())
      .send({ analyteCode: '999BENHVIENB' })
      .expect(404)
  })

  it('từ chối khi không có token', async () => {
    await request(app.getHttpServer())
      .get(`/api/catalogue/devices/${deviceA}/channels`)
      .expect(401)
    await request(app.getHttpServer())
      .delete(`/api/catalogue/devices/${deviceA}/channels/19`)
      .expect(401)
  })
})

describe('luật ghi đè kết quả', () => {
  let ruleId: string

  it('tạo, sửa, xoá được luật', async () => {
    const created = await request(app.getHttpServer())
      .post('/api/catalogue/result-rules')
      .set(authA())
      .send({
        deviceId: deviceA,
        analyteCode: '123AST',
        resultIn: 'neg',
        resultOut: 'Âm tính',
        color: 0,
      })
      .expect(201)

    expect(created.body).toMatchObject({
      deviceId: deviceA,
      analyteCode: '123AST',
      resultIn: 'neg',
      resultOut: 'Âm tính',
    })
    ruleId = created.body.id

    const updated = await request(app.getHttpServer())
      .put(`/api/catalogue/result-rules/${ruleId}`)
      .set(authA())
      .send({ resultOut: 'Âm tính (đã sửa)', comment: 'ghi chú' })
      .expect(200)
    expect(updated.body).toMatchObject({ resultOut: 'Âm tính (đã sửa)', comment: 'ghi chú' })

    await request(app.getHttpServer())
      .delete(`/api/catalogue/result-rules/${ruleId}`)
      .set(authA())
      .expect(204)

    await request(app.getHttpServer())
      .delete(`/api/catalogue/result-rules/${ruleId}`)
      .set(authA())
      .expect(404)
  })

  it('từ chối luật trùng (máy, xét nghiệm, giá trị vào)', async () => {
    const first = await request(app.getHttpServer())
      .post('/api/catalogue/result-rules')
      .set(authA())
      .send({ deviceId: deviceA, analyteCode: '123AST', resultIn: 'pos', resultOut: 'Dương tính' })
      .expect(201)

    const dup = await request(app.getHttpServer())
      .post('/api/catalogue/result-rules')
      .set(authA())
      .send({ deviceId: deviceA, analyteCode: '123AST', resultIn: 'pos', resultOut: 'Khác' })
      .expect(409)
    expect(dup.body.message).toMatch(/đã có|trùng/i)
    expect(dup.body.message).toContain('123AST')

    // Luật chung (không gắn máy) trùng nhau cũng phải là 409, không phải 500.
    await request(app.getHttpServer())
      .post('/api/catalogue/result-rules')
      .set(authA())
      .send({ analyteCode: '123AST', resultIn: 'chung', resultOut: 'Luật chung' })
      .expect(201)
    await request(app.getHttpServer())
      .post('/api/catalogue/result-rules')
      .set(authA())
      .send({ analyteCode: '123AST', resultIn: 'chung', resultOut: 'Luật chung 2' })
      .expect(409)

    expect(first.body.id).toBeTruthy()
  })

  it('liệt kê luật của máy kèm luật áp dụng cho mọi máy', async () => {
    const res = await request(app.getHttpServer())
      .get(`/api/catalogue/devices/${deviceA}/result-rules`)
      .set(authA())
      .expect(200)

    const byIn = Object.fromEntries(
      res.body.map((r: { resultIn: string; deviceId: string | null }) => [r.resultIn, r.deviceId]),
    )
    expect(byIn['pos']).toBe(deviceA)
    expect(byIn['chung']).toBeNull()
    expect(res.body[0].analyteName).toBe('AST (GOT)')
  })

  it('trả 409 khi sửa luật thành một bộ khoá đã tồn tại', async () => {
    const rules = await request(app.getHttpServer())
      .get(`/api/catalogue/devices/${deviceA}/result-rules`)
      .set(authA())
      .expect(200)
    const common = rules.body.find((r: { resultIn: string }) => r.resultIn === 'chung')
    const onDevice = rules.body.find((r: { resultIn: string }) => r.resultIn === 'pos')

    await request(app.getHttpServer())
      .put(`/api/catalogue/result-rules/${onDevice.id}`)
      .set(authA())
      .send({ deviceId: null, resultIn: common.resultIn })
      .expect(409)
  })

  it('trả 400 khi thiếu mã xét nghiệm hoặc giá trị vào', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/result-rules')
      .set(authA())
      .send({ analyteCode: '123AST' })
      .expect(400)
    await request(app.getHttpServer())
      .post('/api/catalogue/result-rules')
      .set(authA())
      .send({ resultIn: 'x' })
      .expect(400)
  })

  it('trả 400 khi mã xét nghiệm không có trong danh mục', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/result-rules')
      .set(authA())
      .send({ analyteCode: 'KHONG-CO', resultIn: 'x' })
      .expect(400)
  })

  it('trả 400 khi deviceId không phải uuid', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/result-rules')
      .set(authA())
      .send({ deviceId: 'khong-phai-uuid', analyteCode: '123AST', resultIn: 'y' })
      .expect(400)
  })

  it('không cho cơ sở khác đọc hoặc sửa luật của cơ sở này', async () => {
    await request(app.getHttpServer())
      .get(`/api/catalogue/devices/${deviceA}/result-rules`)
      .set(authB())
      .expect(404)

    const rules = await request(app.getHttpServer())
      .get(`/api/catalogue/devices/${deviceA}/result-rules`)
      .set(authA())
      .expect(200)

    await request(app.getHttpServer())
      .put(`/api/catalogue/result-rules/${rules.body[0].id}`)
      .set(authB())
      .send({ resultOut: 'bi-sua-trom' })
      .expect(404)

    await request(app.getHttpServer())
      .delete(`/api/catalogue/result-rules/${rules.body[0].id}`)
      .set(authB())
      .expect(404)
  })

  it('từ chối khi không có token', async () => {
    await request(app.getHttpServer())
      .get(`/api/catalogue/devices/${deviceA}/result-rules`)
      .expect(401)
    await request(app.getHttpServer())
      .post('/api/catalogue/result-rules')
      .send({ analyteCode: '123AST', resultIn: 'z' })
      .expect(401)
  })
})
