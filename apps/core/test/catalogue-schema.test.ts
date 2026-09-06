import 'reflect-metadata'
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
import { AppModule } from '../src/app.module.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'
import { prepareTestTenant, resetAdminDatabase } from './helpers.js'

let app: INestApplication
let tenantId: string
let registry: TenantRegistryService

beforeAll(async () => {
  await resetAdminDatabase()
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()
  registry = app.get(TenantRegistryService)
  tenantId = await prepareTestTenant(
    app.get(AdminDbService).db,
    app.get(TenantMigratorService),
    'cat',
    'lis_t_cat',
  )
}, 90_000)

afterAll(async () => {
  await app.close()
})

describe('schema danh mục', () => {
  it('tạo bảng analytes với mã là khoá nghiệp vụ', async () => {
    const db = await registry.forTenant(tenantId)
    const row = await db
      .insertInto('analytes')
      .values({
        code: '123AST',
        name: 'AST (GOT)',
        unit: 'U/L',
        decimals: 1,
        result_is_comment: false,
        source: 'legacy',
      })
      .returningAll()
      .executeTakeFirstOrThrow()
    expect(row.code).toBe('123AST')
    expect(row.active).toBe(true)
  })

  it('không cho hai xét nghiệm cùng mã', async () => {
    const db = await registry.forTenant(tenantId)
    await expect(
      db
        .insertInto('analytes')
        .values({ code: '123AST', name: 'Trùng mã', result_is_comment: false, source: 'manual' })
        .execute(),
    ).rejects.toThrow()
  })

  it('giữ nguyên mã chứa ký tự phần trăm', async () => {
    const db = await registry.forTenant(tenantId)
    await db
      .insertInto('analytes')
      .values({ code: '034NEU%', name: 'Bạch cầu trung tính %', result_is_comment: false, source: 'legacy' })
      .execute()
    const found = await db
      .selectFrom('analytes')
      .selectAll()
      .where('code', '=', '034NEU%')
      .executeTakeFirstOrThrow()
    expect(found.name).toBe('Bạch cầu trung tính %')
  })

  it('chặn hai mã kênh trùng nhau trên cùng một máy', async () => {
    const db = await registry.forTenant(tenantId)
    const agent = await db
      .insertInto('agents')
      .values({ agent_key_id: crypto.randomUUID(), hostname: 'PC-CAT', agent_version: '0.1.0' })
      .returningAll()
      .executeTakeFirstOrThrow()
    const device = await db
      .insertInto('devices')
      .values({
        agent_id: agent.id,
        device_key: 'au640',
        name: 'AU640',
        transport: 'serial',
        driver: 'astm-e1394',
      })
      .returningAll()
      .executeTakeFirstOrThrow()

    await db
      .insertInto('device_analyte_map')
      .values({ device_id: device.id, channel_code: '19', analyte_code: '123AST' })
      .execute()

    await expect(
      db
        .insertInto('device_analyte_map')
        .values({ device_id: device.id, channel_code: '19', analyte_code: '034NEU%' })
        .execute(),
    ).rejects.toThrow()
  })

  it('lưu được luật ghi đè kết quả', async () => {
    const db = await registry.forTenant(tenantId)
    const device = await db.selectFrom('devices').selectAll().executeTakeFirstOrThrow()
    const rule = await db
      .insertInto('result_mappings')
      .values({
        device_id: device.id,
        analyte_code: '123AST',
        result_in: 'neg',
        result_out: 'Âm tính',
        color: 0,
      })
      .returningAll()
      .executeTakeFirstOrThrow()
    expect(rule.result_out).toBe('Âm tính')
  })

  it('lưu được ánh xạ mã HIS', async () => {
    const db = await registry.forTenant(tenantId)
    await db
      .insertInto('his_analyte_map')
      .values({ his_service_code: 'A/01', analyte_code: '123AST' })
      .execute()
    const row = await db
      .selectFrom('his_analyte_map')
      .selectAll()
      .where('his_service_code', '=', 'A/01')
      .executeTakeFirstOrThrow()
    expect(row.analyte_code).toBe('123AST')
  })

  it('bảng khoảng tham chiếu tồn tại và rỗng sau khi tạo', async () => {
    const db = await registry.forTenant(tenantId)
    const rows = await db.selectFrom('reference_ranges').selectAll().execute()
    expect(rows).toEqual([])
  })

  it('nhóm cha con lưu được thành viên kèm nguồn gốc', async () => {
    const db = await registry.forTenant(tenantId)
    await db
      .insertInto('profiles')
      .values({ code: 'TPTNT', name: 'Tổng phân tích nước tiểu', source: 'legacy-declared' })
      .execute()
    await db
      .insertInto('profile_members')
      .values({ profile_code: 'TPTNT', analyte_code: '123AST', display_order: 1 })
      .execute()
    const members = await db
      .selectFrom('profile_members')
      .selectAll()
      .where('profile_code', '=', 'TPTNT')
      .execute()
    expect(members).toHaveLength(1)
  })
})
