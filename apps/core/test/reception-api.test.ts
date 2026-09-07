import 'reflect-metadata'
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import { sql } from 'kysely'
import request from 'supertest'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
import { AppModule } from '../src/app.module.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'
import { createTestUser, prepareTestTenant, resetAdminDatabase, testDbName } from './helpers.js'

let app: INestApplication
let tenantA: string
let tenantB: string
let tokenA: string
let tokenB: string
let registry: TenantRegistryService

const authA = () => ({ Authorization: `Bearer ${tokenA}` })
const authB = () => ({ Authorization: `Bearer ${tokenB}` })

/**
 * Danh mục seed. `002HD` là nhóm có chỉ số con — đúng hình dạng của dữ liệu
 * thật (54 nhóm, 273 chỉ số con), nơi chỉ định nhóm phải kéo theo cả con.
 */
const SEED_ANALYTES = [
  { code: '123AST', name: 'AST (GOT)', category: 'SH', unit: 'U/L', source: 'legacy' as const },
  { code: '200GLU', name: 'Glucose máu', category: 'SH', unit: 'mmol/L', source: 'legacy' as const },
  { code: '002HD', name: 'Tổng phân tích tế bào máu', category: 'CTM', source: 'legacy' as const, is_profile_head: true },
  { code: '010WBC', name: 'Bạch cầu', category: 'CTM', unit: 'G/L', source: 'legacy' as const },
  { code: '011RBC', name: 'Hồng cầu', category: 'CTM', unit: 'T/L', source: 'legacy' as const },
  { code: '034NEU%', name: 'Bạch cầu trung tính phần trăm', category: 'CTM', source: 'legacy' as const },
]

/** Dịch vụ KHÁM BỆNH — chỉ định nó phải bị từ chối, cả ở API lẫn ở database. */
const SEED_EXAM_SERVICE = {
  code: 'KB001',
  name: 'Khám nội tổng quát',
  source: 'manual' as const,
  service_kind: 'kham_benh' as const,
}

async function login(tenantCode: string, username: string, password: string): Promise<string> {
  const res = await request(app.getHttpServer())
    .post('/api/auth/login')
    .send({ tenantCode, username, password })
    .expect(201)
  return res.body.token
}

async function createPatient(token: string, body: Record<string, unknown>): Promise<string> {
  const res = await request(app.getHttpServer())
    .post('/api/reception/patients')
    .set({ Authorization: `Bearer ${token}` })
    .send(body)
    .expect(201)
  return res.body.id
}

/** Ép dãy sinh đuôi barcode về một giá trị cụ thể để dựng ca nhập nhằng. */
async function setBarcodeSerial(tenantId: string, next: number): Promise<void> {
  const db = await registry.forTenant(tenantId)
  await sql`select setval('specimen_barcode_seq', ${sql.lit(next)}, false)`.execute(db)
}

beforeAll(async () => {
  await resetAdminDatabase()
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()

  const admin = app.get(AdminDbService)
  const migrator = app.get(TenantMigratorService)
  registry = app.get(TenantRegistryService)

  tenantA = await prepareTestTenant(admin.db, migrator, 'rec-a', testDbName('lis_t_rec_a'))
  tenantB = await prepareTestTenant(admin.db, migrator, 'rec-b', testDbName('lis_t_rec_b'))
  await createTestUser(admin.db, tenantA, 'ktv-a', 'mat-khau-test-a')
  await createTestUser(admin.db, tenantB, 'ktv-b', 'mat-khau-test-b')

  for (const tenantId of [tenantA, tenantB]) {
    const db = await registry.forTenant(tenantId)
    await db
      .insertInto('analytes')
      .values(SEED_ANALYTES.map((a) => ({ ...a })) as never)
      .execute()
    await db.insertInto('analytes').values(SEED_EXAM_SERVICE as never).execute()
    await db
      .insertInto('profiles')
      .values({ code: '002HD', name: 'Tổng phân tích tế bào máu', source: 'legacy-declared' })
      .execute()
    await db
      .insertInto('profile_members')
      .values([
        { profile_code: '002HD', analyte_code: '010WBC', display_order: 1 },
        { profile_code: '002HD', analyte_code: '011RBC', display_order: 2 },
        { profile_code: '002HD', analyte_code: '034NEU%', display_order: 3 },
      ])
      .execute()
  }

  tokenA = await login('rec-a', 'ktv-a', 'mat-khau-test-a')
  tokenB = await login('rec-b', 'ktv-b', 'mat-khau-test-b')
}, 180_000)

afterAll(async () => {
  await app?.close()
})

describe('bệnh nhân', () => {
  it('tạo bệnh nhân chỉ có tuổi (không có ngày sinh) và tự sinh mã bệnh nhân', async () => {
    const res = await request(app.getHttpServer())
      .post('/api/reception/patients')
      .set(authA())
      .send({ fullName: 'Nguyễn Văn Tuổi', sex: 'nam', ageYears: 67, phone: '0912345678' })
      .expect(201)

    expect(res.body.patientCode).toMatch(/^BN\d+$/)
    expect(res.body.ageYears).toBe(67)
    expect(res.body.birthDate).toBeNull()
  })

  it('từ chối bệnh nhân không có bất kỳ thông tin tuổi nào', async () => {
    await request(app.getHttpServer())
      .post('/api/reception/patients')
      .set(authA())
      .send({ fullName: 'Không Rõ Tuổi', sex: 'nu' })
      .expect(400)
  })

  it('tìm theo tên, mã bệnh nhân và số điện thoại, phân trang phía máy chủ', async () => {
    await createPatient(tokenA, { fullName: 'Trần Thị Tìm Kiếm', sex: 'nu', ageYears: 30, phone: '0900000001' })
    await createPatient(tokenA, { fullName: 'Lê Văn Tìm Kiếm', sex: 'nam', ageYears: 40, phone: '0900000002' })

    const byName = await request(app.getHttpServer())
      .get('/api/reception/patients')
      .query({ q: 'Tìm Kiếm', limit: 1 })
      .set(authA())
      .expect(200)
    expect(byName.body.total).toBe(2)
    expect(byName.body.items).toHaveLength(1)

    const byPhone = await request(app.getHttpServer())
      .get('/api/reception/patients')
      .query({ q: '0900000002' })
      .set(authA())
      .expect(200)
    expect(byPhone.body.total).toBe(1)
    expect(byPhone.body.items[0].fullName).toBe('Lê Văn Tìm Kiếm')

    const code = byPhone.body.items[0].patientCode
    const byCode = await request(app.getHttpServer())
      .get('/api/reception/patients')
      .query({ q: code })
      .set(authA())
      .expect(200)
    expect(byCode.body.total).toBe(1)
  })

  it('sửa được bệnh nhân', async () => {
    const id = await createPatient(tokenA, { fullName: 'Phạm Văn Sửa', sex: 'nam', ageYears: 20 })
    const res = await request(app.getHttpServer())
      .patch(`/api/reception/patients/${id}`)
      .set(authA())
      .send({ phone: '0988888888', address: 'Hà Nội' })
      .expect(200)
    expect(res.body.phone).toBe('0988888888')
    expect(res.body.address).toBe('Hà Nội')
  })
})

describe('cách ly giữa hai bệnh viện', () => {
  it('token cơ sở A không đọc được bệnh nhân của cơ sở B', async () => {
    const idB = await createPatient(tokenB, { fullName: 'Bệnh Nhân Cơ Sở B', sex: 'nu', ageYears: 50 })

    await request(app.getHttpServer())
      .get(`/api/reception/patients/${idB}`)
      .set(authB())
      .expect(200)

    await request(app.getHttpServer())
      .get(`/api/reception/patients/${idB}`)
      .set(authA())
      .expect(404)
  })

  it('token cơ sở A không tìm thấy bệnh nhân của cơ sở B qua ô tìm kiếm', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/reception/patients')
      .query({ q: 'Bệnh Nhân Cơ Sở B' })
      .set(authA())
      .expect(200)
    expect(res.body.total).toBe(0)
  })

  it('token cơ sở A không tra được phiếu của cơ sở B theo barcode', async () => {
    const patientB = await createPatient(tokenB, { fullName: 'Bn Phiếu B', sex: 'nam', ageYears: 33 })
    const orderB = await request(app.getHttpServer())
      .post('/api/reception/orders')
      .set(authB())
      .send({ patientId: patientB, specimens: [{ specimenType: 'huyet_thanh', analyteCodes: ['123AST'] }] })
      .expect(201)
    const barcode = orderB.body.specimens[0].barcode

    await request(app.getHttpServer())
      .get('/api/reception/orders/by-barcode')
      .query({ barcode })
      .set(authA())
      .expect(404)
  })
})

describe('chỉ định xét nghiệm', () => {
  it('chỉ định một nhóm thì các chỉ số con vào theo', async () => {
    const patientId = await createPatient(tokenA, { fullName: 'Bn Nhóm', sex: 'nam', ageYears: 45 })
    const res = await request(app.getHttpServer())
      .post('/api/reception/orders')
      .set(authA())
      .send({
        patientId,
        specimens: [{ specimenType: 'mau_edta', analyteCodes: ['002HD'] }],
      })
      .expect(201)

    const codes = res.body.items.map((i: { analyteCode: string }) => i.analyteCode).sort()
    expect(codes).toEqual(['002HD', '010WBC', '011RBC', '034NEU%'].sort())

    const child = res.body.items.find((i: { analyteCode: string }) => i.analyteCode === '010WBC')
    expect(child.parentAnalyteCode).toBe('002HD')
    // Con phải nằm cùng ống với nhóm cha, nếu không máy đo ống nào cũng sai.
    expect(child.specimenId).toBe(res.body.specimens[0].id)
  })

  it('chỉ định lẻ một chỉ số rồi chỉ định cả nhóm chứa nó vẫn chỉ ra một dòng', async () => {
    const patientId = await createPatient(tokenA, { fullName: 'Bn Trùng Con', sex: 'nu', ageYears: 28 })
    const res = await request(app.getHttpServer())
      .post('/api/reception/orders')
      .set(authA())
      .send({
        patientId,
        specimens: [{ specimenType: 'mau_edta', analyteCodes: ['010WBC', '002HD'] }],
      })
      .expect(201)

    const wbc = res.body.items.filter((i: { analyteCode: string }) => i.analyteCode === '010WBC')
    expect(wbc).toHaveLength(1)
  })

  it('từ chối chỉ định trùng dịch vụ trong cùng một phiếu', async () => {
    const patientId = await createPatient(tokenA, { fullName: 'Bn Trùng', sex: 'nam', ageYears: 61 })
    await request(app.getHttpServer())
      .post('/api/reception/orders')
      .set(authA())
      .send({
        patientId,
        specimens: [{ specimenType: 'huyet_thanh', analyteCodes: ['123AST', '123AST'] }],
      })
      .expect(400)
  })

  it('CHỈ cho chọn dịch vụ xét nghiệm — dịch vụ khám bệnh bị từ chối', async () => {
    const patientId = await createPatient(tokenA, { fullName: 'Bn Khám', sex: 'nu', ageYears: 35 })
    const res = await request(app.getHttpServer())
      .post('/api/reception/orders')
      .set(authA())
      .send({ patientId, specimens: [{ specimenType: 'huyet_thanh', analyteCodes: ['KB001'] }] })
      .expect(400)
    expect(String(res.body.message)).toContain('KB001')
  })

  it('danh sách dịch vụ chọn được không chứa dịch vụ khám bệnh', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/reception/orderable-analytes')
      .query({ q: 'KB001' })
      .set(authA())
      .expect(200)
    expect(res.body.total).toBe(0)
  })

  it('ranh giới dịch vụ khám được chốt ở tầng database, không chỉ ở service', async () => {
    const db = await registry.forTenant(tenantA)
    const patientId = await createPatient(tokenA, { fullName: 'Bn Chốt DB', sex: 'nam', ageYears: 44 })
    const order = await request(app.getHttpServer())
      .post('/api/reception/orders')
      .set(authA())
      .send({ patientId, specimens: [{ specimenType: 'huyet_thanh', analyteCodes: ['123AST'] }] })
      .expect(201)

    await expect(
      db
        .insertInto('order_items')
        .values({ order_id: order.body.id, analyte_code: 'KB001' } as never)
        .execute(),
    ).rejects.toThrow()
  })

  it('mã xét nghiệm chứa ký tự đại diện được tìm đúng, không kéo theo mã khác', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/reception/orderable-analytes')
      .query({ q: '034NEU%' })
      .set(authA())
      .expect(200)
    expect(res.body.items.map((i: { code: string }) => i.code)).toEqual(['034NEU%'])
  })
})

describe('barcode', () => {
  it('barcode trùng bị chặn ở tầng database', async () => {
    const db = await registry.forTenant(tenantA)
    const patientId = await createPatient(tokenA, { fullName: 'Bn Barcode', sex: 'nam', ageYears: 50 })
    const order = await request(app.getHttpServer())
      .post('/api/reception/orders')
      .set(authA())
      .send({ patientId, specimens: [{ specimenType: 'huyet_thanh', analyteCodes: ['123AST'] }] })
      .expect(201)

    const specimen = order.body.specimens[0]

    // Cùng barcode, cùng đuôi số: cả hai ràng buộc UNIQUE đều phải chặn.
    await expect(
      db
        .insertInto('specimens')
        .values({
          order_id: order.body.id,
          specimen_type: 'nuoc_tieu',
          barcode: specimen.barcode,
          barcode_serial: specimen.barcodeSerial,
        } as never)
        .execute(),
    ).rejects.toThrow()

    // Barcode khác nhưng ĐUÔI trùng: vẫn phải bị chặn, vì đuôi mới là thứ máy
    // gửi về và là thứ định danh ống.
    await expect(
      db
        .insertInto('specimens')
        .values({
          order_id: order.body.id,
          specimen_type: 'nuoc_tieu',
          barcode: `999999-${String(specimen.barcodeSerial).padStart(7, '0')}`,
          barcode_serial: specimen.barcodeSerial,
        } as never)
        .execute(),
    ).rejects.toThrow()
  })

  it('mỗi ống trong một phiếu mang một barcode riêng', async () => {
    const patientId = await createPatient(tokenA, { fullName: 'Bn Hai Ống', sex: 'nu', ageYears: 22 })
    const res = await request(app.getHttpServer())
      .post('/api/reception/orders')
      .set(authA())
      .send({
        patientId,
        specimens: [
          { specimenType: 'mau_edta', analyteCodes: ['002HD'] },
          { specimenType: 'huyet_thanh', analyteCodes: ['123AST', '200GLU'] },
        ],
      })
      .expect(201)

    expect(res.body.specimens).toHaveLength(2)
    const barcodes = res.body.specimens.map((s: { barcode: string }) => s.barcode)
    expect(new Set(barcodes).size).toBe(2)
  })

  it('khớp theo ĐUÔI máy gửi về: trường hợp duy nhất thì khớp đúng', async () => {
    await setBarcodeSerial(tenantA, 4001)
    const patientId = await createPatient(tokenA, { fullName: 'Bn Đuôi Duy Nhất', sex: 'nam', ageYears: 55 })
    const order = await request(app.getHttpServer())
      .post('/api/reception/orders')
      .set(authA())
      .send({ patientId, specimens: [{ specimenType: 'huyet_thanh', analyteCodes: ['123AST'] }] })
      .expect(201)

    const specimen = order.body.specimens[0]
    expect(specimen.barcodeSerial).toBe('4001')
    expect(specimen.barcode).toMatch(/^\d{6}-0004001$/)

    // Máy gửi đủ 7 chữ số
    const full = await request(app.getHttpServer())
      .get('/api/reception/orders/by-barcode')
      .query({ barcode: '0004001' })
      .set(authA())
      .expect(200)
    expect(full.body.id).toBe(order.body.id)
    expect(full.body.patient.id).toBe(patientId)

    // Máy cắt số 0 đứng đầu
    const stripped = await request(app.getHttpServer())
      .get('/api/reception/orders/by-barcode')
      .query({ barcode: '4001' })
      .set(authA())
      .expect(200)
    expect(stripped.body.id).toBe(order.body.id)

    // Máy gửi cả barcode đầy đủ
    const whole = await request(app.getHttpServer())
      .get('/api/reception/orders/by-barcode')
      .query({ barcode: specimen.barcode })
      .set(authA())
      .expect(200)
    expect(whole.body.id).toBe(order.body.id)
  })

  it('khớp theo ĐUÔI: nhập nhằng thì TỪ CHỐI, tuyệt đối không lấy bản ghi mới nhất', async () => {
    // Dựng đúng cái bẫy của hệ cũ: hai ống có đuôi lồng nhau (0005678 và
    // 0095678). Máy cắt bớt và gửi về '5678'.
    await setBarcodeSerial(tenantA, 5678)
    const p1 = await createPatient(tokenA, { fullName: 'Bn Nhập Nhằng Một', sex: 'nam', ageYears: 31 })
    const o1 = await request(app.getHttpServer())
      .post('/api/reception/orders')
      .set(authA())
      .send({ patientId: p1, specimens: [{ specimenType: 'huyet_thanh', analyteCodes: ['123AST'] }] })
      .expect(201)
    expect(o1.body.specimens[0].barcodeSerial).toBe('5678')

    await setBarcodeSerial(tenantA, 95678)
    const p2 = await createPatient(tokenA, { fullName: 'Bn Nhập Nhằng Hai', sex: 'nu', ageYears: 32 })
    const o2 = await request(app.getHttpServer())
      .post('/api/reception/orders')
      .set(authA())
      .send({ patientId: p2, specimens: [{ specimenType: 'huyet_thanh', analyteCodes: ['200GLU'] }] })
      .expect(201)
    expect(o2.body.specimens[0].barcodeSerial).toBe('95678')

    const res = await request(app.getHttpServer())
      .get('/api/reception/orders/by-barcode')
      .query({ barcode: '5678' })
      .set(authA())
      .expect(409)
    const message = String(res.body.message)
    expect(message).toContain('nhập nhằng')
    // Thông báo phải nói rõ có mấy ống khớp để người xử lý biết đường tra tay.
    expect(message).toContain('2')

    // Đuôi đầy đủ vẫn phân biệt được hai ống — nhập nhằng chỉ do máy cắt bớt.
    const exact1 = await request(app.getHttpServer())
      .get('/api/reception/orders/by-barcode')
      .query({ barcode: '0005678' })
      .set(authA())
      .expect(200)
    expect(exact1.body.id).toBe(o1.body.id)

    const exact2 = await request(app.getHttpServer())
      .get('/api/reception/orders/by-barcode')
      .query({ barcode: '0095678' })
      .set(authA())
      .expect(200)
    expect(exact2.body.id).toBe(o2.body.id)
  })

  it('đuôi quá ngắn bị từ chối thay vì đoán', async () => {
    await request(app.getHttpServer())
      .get('/api/reception/orders/by-barcode')
      .query({ barcode: '12' })
      .set(authA())
      .expect(400)
  })

  it('đuôi không khớp ống nào trả 404 với thông báo rõ ràng', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/reception/orders/by-barcode')
      .query({ barcode: '8888888' })
      .set(authA())
      .expect(404)
    expect(String(res.body.message)).toContain('8888888')
  })
})
