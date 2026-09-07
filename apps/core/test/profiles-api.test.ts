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
 * API nhóm xét nghiệm (profiles) và chỉ số con.
 *
 * Điều quan trọng nhất được kiểm ở đây: `legacy-declared` và `legacy-inferred`
 * KHÔNG được đối xử như nhau. Nhóm suy diễn là ĐỀ XUẤT do phần mềm dựng lại từ
 * thụt lề tên của hệ cũ; nó có thể thiếu con, có thể lẫn con không thuộc nhóm.
 * API phải trả về đủ thông tin để giao diện nói bằng CHỮ rằng nhóm đó đang chờ
 * xác nhận, và phải cho kỹ thuật viên xác nhận / sửa thành viên / đổi thứ tự.
 */

let app: INestApplication
let tenantA: string
let tenantB: string
let tokenA: string
let tokenB: string
let registry: TenantRegistryService

const SEED_ANALYTES = [
  { code: '002HD', name: 'Tổng phân tích tế bào máu ngoại vi', is_profile_head: true },
  { code: '002WBC', name: 'Số lượng bạch cầu (WBC)' },
  { code: '015RBC', name: 'Số lượng hồng cầu (RBC)' },
  { code: '016HGB', name: 'HGB' },
  { code: '400TPTNT', name: 'Tổng phân tích nước tiểu', is_profile_head: true },
  { code: '400GLU', name: 'Glucose niệu' },
  { code: '783PROGES', name: 'PROGESTERONE', is_profile_head: true },
  { code: '783PRO1', name: 'Progesterone (định lượng)' },
  // Mã cha KHÔNG có con nào — thuật toán suy diễn không bắt được (920IONDO thật).
  { code: '920IONDO', name: 'Điện giải đồ (Na, K, Cl)', is_profile_head: true },
  // Bảo vệ ràng buộc "không LIKE trần trên cột mã".
  { code: '034NEU%', name: 'Bạch cầu trung tính phần trăm', is_profile_head: true },
  { code: '034NEUT', name: 'Bạch cầu trung tính tuyệt đối', is_profile_head: true },
]

const SEED_PROFILES = [
  // Nhóm hệ cũ KHAI BÁO tường minh — đáng tin, đã xác nhận sẵn.
  { code: '400TPTNT', name: 'Tổng phân tích nước tiểu', source: 'legacy-declared' as const, confirmed: true },
  // Nhóm SUY DIỄN — đề xuất, chờ người xác nhận.
  { code: '002HD', name: 'Tổng phân tích tế bào máu ngoại vi', source: 'legacy-inferred' as const, confirmed: false },
  { code: '783PROGES', name: 'PROGESTERONE', source: 'legacy-inferred' as const, confirmed: false },
  // Có dòng nhóm nhưng RỖNG — khác hẳn "không có dòng nhóm nào".
  { code: '034NEU%', name: 'Nhóm rỗng', source: 'legacy-inferred' as const, confirmed: false },
]

const SEED_MEMBERS = [
  { profile_code: '400TPTNT', analyte_code: '400GLU', display_order: 1 },
  { profile_code: '002HD', analyte_code: '002WBC', display_order: 1 },
  { profile_code: '002HD', analyte_code: '015RBC', display_order: 2 },
  { profile_code: '002HD', analyte_code: '016HGB', display_order: 3 },
  { profile_code: '783PROGES', analyte_code: '783PRO1', display_order: 1 },
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
  { action: string; actor_username: string; changed_at: Date; before_value: any; after_value: any } | undefined
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

  tenantA = await prepareTestTenant(admin.db, migrator, 'prof-a', testDbName('lis_t_prof_a'))
  tenantB = await prepareTestTenant(admin.db, migrator, 'prof-b', testDbName('lis_t_prof_b'))
  await createTestUser(admin.db, tenantA, 'ktv-a', 'mat-khau-test-a')
  await createTestUser(admin.db, tenantB, 'ktv-b', 'mat-khau-test-b')

  const dbA = await registry.forTenant(tenantA)
  await dbA
    .insertInto('analytes')
    .values(SEED_ANALYTES.map((a) => ({ ...a, source: 'legacy' as const, result_is_comment: false })))
    .execute()
  await dbA.insertInto('profiles').values(SEED_PROFILES).execute()
  await dbA.insertInto('profile_members').values(SEED_MEMBERS).execute()

  const dbB = await registry.forTenant(tenantB)
  await dbB
    .insertInto('analytes')
    .values({
      code: '999BENHVIENB',
      name: 'Chỉ có ở cơ sở B',
      source: 'legacy',
      result_is_comment: false,
      is_profile_head: true,
    })
    .execute()
  await dbB
    .insertInto('profiles')
    .values({ code: '999BENHVIENB', name: 'Nhóm riêng của B', source: 'legacy-declared', confirmed: true })
    .execute()

  tokenA = await login('prof-a', 'ktv-a', 'mat-khau-test-a')
  tokenB = await login('prof-b', 'ktv-b', 'mat-khau-test-b')
}, 120_000)

afterAll(async () => {
  await app.close()
})

describe('GET /api/catalogue/profiles', () => {
  it('liệt kê kèm tổng số, số thành viên và NHÃN CHỮ cho nguồn gốc', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/profiles')
      .set(authA())
      .expect(200)

    expect(res.body.total).toBe(SEED_PROFILES.length)
    const hd = res.body.items.find((p: { code: string }) => p.code === '002HD')
    expect(hd).toMatchObject({ source: 'legacy-inferred', confirmed: false, memberCount: 3 })
    // Nhãn chữ, không phải chỉ khác màu ở giao diện.
    expect(typeof hd.sourceLabel).toBe('string')
    expect(hd.sourceLabel.length).toBeGreaterThan(0)
    expect(hd.confirmedLabel).toContain('XÁC NHẬN')
  })

  it('phân biệt nhóm khai báo tường minh với nhóm suy diễn', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/profiles')
      .set(authA())
      .expect(200)
    const declared = res.body.items.find((p: { code: string }) => p.code === '400TPTNT')
    const inferred = res.body.items.find((p: { code: string }) => p.code === '002HD')
    expect(declared.source).toBe('legacy-declared')
    expect(declared.confirmed).toBe(true)
    expect(inferred.source).toBe('legacy-inferred')
    expect(inferred.confirmed).toBe(false)
    expect(declared.sourceLabel).not.toBe(inferred.sourceLabel)
    expect(declared.confirmedLabel).not.toBe(inferred.confirmedLabel)
  })

  it('lọc riêng được các nhóm CHỜ XÁC NHẬN', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/profiles?confirmed=false')
      .set(authA())
      .expect(200)
    expect(res.body.total).toBe(3)
    for (const item of res.body.items) expect(item.confirmed).toBe(false)
  })

  it('lọc được theo nguồn gốc', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/profiles?source=legacy-declared')
      .set(authA())
      .expect(200)
    expect(res.body.total).toBe(1)
    expect(res.body.items[0].code).toBe('400TPTNT')
  })

  it('có phân trang phía máy chủ', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/profiles?limit=2&offset=0')
      .set(authA())
      .expect(200)
    expect(res.body.items.length).toBe(2)
    expect(res.body.total).toBe(SEED_PROFILES.length)
  })

  it('tìm được theo mã và theo tên, không để ký tự đại diện lọt qua', async () => {
    const theoTen = await request(app.getHttpServer())
      .get(`/api/catalogue/profiles?q=${encodeURIComponent('nước tiểu')}`)
      .set(authA())
      .expect(200)
    expect(theoTen.body.total).toBe(1)
    expect(theoTen.body.items[0].code).toBe('400TPTNT')

    const theoMa = await request(app.getHttpServer())
      .get(`/api/catalogue/profiles?q=${encodeURIComponent('034NEU%')}`)
      .set(authA())
      .expect(200)
    expect(theoMa.body.total).toBe(1)
    expect(theoMa.body.items[0].code).toBe('034NEU%')
  })
})

describe('GET /api/catalogue/profiles/:code', () => {
  it('trả về thành viên theo đúng thứ tự, kèm TÊN chỉ số con', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/profiles/002HD')
      .set(authA())
      .expect(200)

    expect(res.body.members.map((m: { analyteCode: string }) => m.analyteCode)).toEqual([
      '002WBC',
      '015RBC',
      '016HGB',
    ])
    expect(res.body.members[0].analyteName).toBe('Số lượng bạch cầu (WBC)')
  })

  it('nhóm không tồn tại trả 404', async () => {
    await request(app.getHttpServer())
      .get('/api/catalogue/profiles/KHONG-CO')
      .set(authA())
      .expect(404)
  })
})

describe('GET /api/catalogue/profiles-orphan-heads', () => {
  it('chỉ ra các mã cha CHƯA CÓ CON NÀO', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/profiles-orphan-heads')
      .set(authA())
      .expect(200)

    const codes = res.body.items.map((i: { code: string }) => i.code)
    // 920IONDO: chưa có dòng nhóm nào. 034NEU%: có dòng nhóm nhưng rỗng.
    // 034NEUT: đầu nhóm nhưng chưa có nhóm nào.
    expect(codes).toContain('920IONDO')
    expect(codes).toContain('034NEU%')
    expect(codes).toContain('034NEUT')
    expect(codes).not.toContain('002HD')
    expect(res.body.total).toBe(3)
  })

  it('phân biệt "chưa có dòng nhóm" với "nhóm rỗng"', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/profiles-orphan-heads')
      .set(authA())
      .expect(200)
    const iondo = res.body.items.find((i: { code: string }) => i.code === '920IONDO')
    const neu = res.body.items.find((i: { code: string }) => i.code === '034NEU%')
    expect(iondo.hasProfileRow).toBe(false)
    expect(neu.hasProfileRow).toBe(true)
  })
})

describe('GET /api/catalogue/profiles-summary', () => {
  it('đếm riêng nhóm khai báo, nhóm suy diễn và nhóm chờ xác nhận', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/profiles-summary')
      .set(authA())
      .expect(200)
    expect(res.body).toMatchObject({
      profileCount: 4,
      declaredCount: 1,
      inferredCount: 3,
      pendingCount: 3,
      memberCount: SEED_MEMBERS.length,
      orphanHeadCount: 3,
    })
  })
})

describe('POST /api/catalogue/profiles/:code/confirm', () => {
  it('xác nhận một nhóm suy diễn mà KHÔNG xoá nguồn gốc', async () => {
    const res = await request(app.getHttpServer())
      .post('/api/catalogue/profiles/783PROGES/confirm')
      .set(authA())
      .expect(201)

    expect(res.body.confirmed).toBe(true)
    // Nguồn gốc là quá khứ, không sửa lại được: nhóm này vẫn là nhóm SUY DIỄN,
    // chỉ khác là đã có người đọc và chịu trách nhiệm cho nó.
    expect(res.body.source).toBe('legacy-inferred')

    const audit = await latestAudit(tenantA, 'profile', '783PROGES')
    expect(audit!.action).toBe('update')
    expect(audit!.before_value.confirmed).toBe(false)
    expect(audit!.after_value.confirmed).toBe(true)
  })

  it('xác nhận nhóm không tồn tại trả 404', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/profiles/KHONG-CO/confirm')
      .set(authA())
      .expect(404)
  })
})

describe('thành viên của nhóm', () => {
  it('thêm được thành viên, xếp vào cuối danh sách', async () => {
    const res = await request(app.getHttpServer())
      .post('/api/catalogue/profiles/400TPTNT/members')
      .set(authA())
      .send({ analyteCode: '016HGB' })
      .expect(201)

    expect(res.body.members.map((m: { analyteCode: string }) => m.analyteCode)).toEqual([
      '400GLU',
      '016HGB',
    ])
    const audit = await latestAudit(tenantA, 'profile_member', '400TPTNT:016HGB')
    expect(audit!.action).toBe('create')
  })

  it('thêm trùng thành viên trả 409, không phải 500', async () => {
    const res = await request(app.getHttpServer())
      .post('/api/catalogue/profiles/400TPTNT/members')
      .set(authA())
      .send({ analyteCode: '016HGB' })
      .expect(409)
    expect(res.body.message).toContain('016HGB')
  })

  it('thêm mã không có trong danh mục trả 400', async () => {
    const res = await request(app.getHttpServer())
      .post('/api/catalogue/profiles/400TPTNT/members')
      .set(authA())
      .send({ analyteCode: 'KHONG-TON-TAI' })
      .expect(400)
    expect(res.body.message).toContain('KHONG-TON-TAI')
  })

  it('bớt được thành viên và ghi vết', async () => {
    const res = await request(app.getHttpServer())
      .delete('/api/catalogue/profiles/400TPTNT/members/016HGB')
      .set(authA())
      .expect(200)
    expect(res.body.members.map((m: { analyteCode: string }) => m.analyteCode)).toEqual(['400GLU'])

    const audit = await latestAudit(tenantA, 'profile_member', '400TPTNT:016HGB')
    expect(audit!.action).toBe('delete')
    expect(audit!.after_value).toBeNull()
  })

  it('bớt thành viên không có trong nhóm trả 404', async () => {
    await request(app.getHttpServer())
      .delete('/api/catalogue/profiles/400TPTNT/members/015RBC')
      .set(authA())
      .expect(404)
  })

  it('đổi được thứ tự các chỉ số con', async () => {
    const res = await request(app.getHttpServer())
      .put('/api/catalogue/profiles/002HD/members-order')
      .set(authA())
      .send({ analyteCodes: ['016HGB', '002WBC', '015RBC'] })
      .expect(200)

    expect(res.body.members.map((m: { analyteCode: string }) => m.analyteCode)).toEqual([
      '016HGB',
      '002WBC',
      '015RBC',
    ])
    expect(res.body.members.map((m: { displayOrder: number }) => m.displayOrder)).toEqual([1, 2, 3])

    const audit = await latestAudit(tenantA, 'profile', '002HD')
    expect(audit!.action).toBe('update')
  })

  it('danh sách thứ tự thiếu hoặc thừa mã bị từ chối 400', async () => {
    const thieu = await request(app.getHttpServer())
      .put('/api/catalogue/profiles/002HD/members-order')
      .set(authA())
      .send({ analyteCodes: ['016HGB', '002WBC'] })
      .expect(400)
    expect(thieu.body.message).toContain('015RBC')

    await request(app.getHttpServer())
      .put('/api/catalogue/profiles/002HD/members-order')
      .set(authA())
      .send({ analyteCodes: ['016HGB', '002WBC', '015RBC', '400GLU'] })
      .expect(400)

    // Thứ tự cũ giữ nguyên sau khi bị từ chối.
    const sau = await request(app.getHttpServer())
      .get('/api/catalogue/profiles/002HD')
      .set(authA())
      .expect(200)
    expect(sau.body.members.map((m: { analyteCode: string }) => m.analyteCode)).toEqual([
      '016HGB',
      '002WBC',
      '015RBC',
    ])
  })
})

describe('POST /api/catalogue/profiles', () => {
  it('tạo được nhóm cho một mã cha chưa có con nào', async () => {
    const res = await request(app.getHttpServer())
      .post('/api/catalogue/profiles')
      .set(authA())
      .send({ code: '920IONDO' })
      .expect(201)

    // Nhóm do người tạo tay là nhóm đáng tin, không phải đề xuất chờ xác nhận.
    expect(res.body).toMatchObject({
      code: '920IONDO',
      name: 'Điện giải đồ (Na, K, Cl)',
      source: 'manual',
      confirmed: true,
      memberCount: 0,
    })
    expect((await latestAudit(tenantA, 'profile', '920IONDO'))!.action).toBe('create')
  })

  it('tạo trùng mã nhóm trả 409', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/profiles')
      .set(authA())
      .send({ code: '920IONDO' })
      .expect(409)
  })

  it('mã không có trong danh mục xét nghiệm trả 400', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/profiles')
      .set(authA())
      .send({ code: 'KHONG-TON-TAI' })
      .expect(400)
  })
})

describe('cách ly giữa hai bệnh viện', () => {
  it('token cơ sở A không thấy nhóm của cơ sở B', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/catalogue/profiles?q=999BENHVIENB')
      .set(authA())
      .expect(200)
    expect(res.body.total).toBe(0)

    await request(app.getHttpServer())
      .get('/api/catalogue/profiles/999BENHVIENB')
      .set(authA())
      .expect(404)

    const resB = await request(app.getHttpServer())
      .get('/api/catalogue/profiles/999BENHVIENB')
      .set(authB())
      .expect(200)
    expect(resB.body.code).toBe('999BENHVIENB')
  })

  it('token cơ sở A không xác nhận và không sửa được nhóm của cơ sở B', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/profiles/999BENHVIENB/confirm')
      .set(authA())
      .expect(404)
    await request(app.getHttpServer())
      .post('/api/catalogue/profiles/999BENHVIENB/members')
      .set(authA())
      .send({ analyteCode: '002WBC' })
      .expect(404)
  })

  it('nhật ký của mỗi bệnh viện nằm trong database của chính nó', async () => {
    await request(app.getHttpServer())
      .post('/api/catalogue/profiles/999BENHVIENB/members')
      .set(authB())
      .send({ analyteCode: '999BENHVIENB' })
      .expect(201)
    expect(
      (await latestAudit(tenantB, 'profile_member', '999BENHVIENB:999BENHVIENB'))!.actor_username,
    ).toBe('ktv-b')
    expect(await latestAudit(tenantA, 'profile_member', '999BENHVIENB:999BENHVIENB')).toBeUndefined()
  })
})
