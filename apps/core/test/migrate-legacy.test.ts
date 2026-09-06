import 'reflect-metadata'
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import { sql } from 'kysely'
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
import { AppModule } from '../src/app.module.js'
import { buildDecisionTemplate } from '../src/migration/legacy-decisions.js'
import { loadLegacySource, type LegacySource } from '../src/migration/legacy-source.js'
import { validateLegacySource } from '../src/migration/legacy-validator.js'
import { writeLegacyCatalogue } from '../src/migration/legacy-writer.js'
import { formatReport } from '../src/migration/legacy-report.js'
import {
  parseMigrateLegacyArgs,
  runMigrateLegacy,
} from '../src/migration/migrate-legacy.command.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'
import { prepareTestTenant, resetAdminDatabase } from './helpers.js'

let app: INestApplication
let registry: TenantRegistryService
let tenantId: string
let dir: string

function write(name: string, rows: unknown[]): void {
  writeFileSync(join(dir, name), JSON.stringify(rows), 'utf8')
}

/**
 * Bộ tệp nguồn rỗng.
 *
 * `TestCodeNew.json` nằm trong danh sách vì danh mục hệ cũ trải trên HAI bảng
 * (`TestCode` 390 mã, `TestCodeNew` 2229 mã, giao nhau bằng 0); thiếu tệp này thì
 * `loadLegacySource` ném lỗi ngay.
 */
function blankSource(): void {
  for (const f of [
    'TestCode.json',
    'TestCodeNew.json',
    'Instrument.json',
    'InsTest.json',
    'InterfacePara.json',
    'ResultMapping.json',
    'Profile.json',
    'ProfileTest.json',
    'TestCode_HIS.json',
  ]) {
    write(f, [])
  }
}

async function freshTenant(code: string): Promise<string> {
  return prepareTestTenant(
    app.get(AdminDbService).db,
    app.get(TenantMigratorService),
    code,
    `lis_t_${code}`,
  )
}

async function runMigration(
  tenant: string,
): Promise<Awaited<ReturnType<typeof writeLegacyCatalogue>>> {
  const src = loadLegacySource(dir)
  const decisions = buildDecisionTemplate(validateLegacySource(src))
  const db = await registry.forTenant(tenant)
  // Không truyền agentId: bản ghi Agent giữ chỗ được tìm/tạo trong chính giao dịch,
  // sau khi đã cầm khoá tư vấn.
  return writeLegacyCatalogue(db, src, decisions)
}

beforeAll(async () => {
  await resetAdminDatabase()
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()
  registry = app.get(TenantRegistryService)
}, 90_000)

afterAll(async () => {
  app && (await app.close())
})

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'lis-mig-'))
  blankSource()
})

describe('writeLegacyCatalogue', () => {
  it('ghi danh mục xét nghiệm giữ nguyên mã cũ', async () => {
    write('TestCode.json', [
      { TestCode: '123AST', TestName: 'AST (GOT)', Unit: 'U/L', GetComment: false },
      { TestCode: '034NEU%', TestName: 'Bạch cầu trung tính %', GetComment: false },
    ])
    tenantId = await freshTenant('mig1')
    const outcome = await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    const rows = await db.selectFrom('analytes').selectAll().orderBy('code').execute()
    expect(rows.map((r) => r.code)).toEqual(['034NEU%', '123AST'])
    expect(rows[1]!.source).toBe('legacy')
    expect(outcome.counts.analytes).toMatchObject({ read: 2, written: 2, skipped: 0 })
  })

  it('phân biệt nguồn legacy và legacy-new của hai bảng danh mục', async () => {
    write('TestCode.json', [{ TestCode: '123AST', TestName: 'AST', GetComment: false }])
    write('TestCodeNew.json', [
      { TestCode: '141ULeu', TestName: 'Bạch cầu niệu', GetComment: false },
      // Mã đã có ở TestCode: bản ở TestCodeNew bị loại ngay từ tầng đọc nguồn.
      { TestCode: '123AST', TestName: 'AST bản mới', GetComment: false },
    ])
    tenantId = await freshTenant('mig1b')
    const outcome = await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    const rows = await db.selectFrom('analytes').selectAll().orderBy('code').execute()
    expect(rows.map((r) => [r.code, r.source])).toEqual([
      ['123AST', 'legacy'],
      ['141ULeu', 'legacy-new'],
    ])
    expect(outcome.counts.analytes).toMatchObject({ read: 2, written: 2, skipped: 0 })
  })

  it('bỏ qua mã kênh xung đột chưa được quyết, và ghi rõ lý do', async () => {
    write('TestCode.json', [
      { TestCode: '128LDL', TestName: 'LDL', GetComment: false },
      { TestCode: '244ACo', TestName: 'Nồng độ cồn', GetComment: false },
    ])
    write('Instrument.json', [
      { InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', Active: true },
    ])
    write('InsTest.json', [
      { InsID: 1, TestCode: '128LDL', TestcodeIN: '19' },
      { InsID: 1, TestCode: '244ACo', TestcodeIN: '19' },
    ])
    tenantId = await freshTenant('mig2')
    const outcome = await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    const maps = await db.selectFrom('device_analyte_map').selectAll().execute()
    expect(maps).toHaveLength(0)
    expect(outcome.counts.device_analyte_map?.skipped).toBe(2)
    expect(outcome.skipped.some((s) => /chưa được quyết/.test(s.reason))).toBe(true)
  })

  it('ghi mã kênh khi đã có quyết định', async () => {
    write('TestCode.json', [
      { TestCode: '128LDL', TestName: 'LDL', GetComment: false },
      { TestCode: '244ACo', TestName: 'Nồng độ cồn', GetComment: false },
    ])
    write('Instrument.json', [
      { InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', Active: true },
    ])
    write('InsTest.json', [
      { InsID: 1, TestCode: '128LDL', TestcodeIN: '19' },
      { InsID: 1, TestCode: '244ACo', TestcodeIN: '19' },
    ])
    tenantId = await freshTenant('mig3')

    const src = loadLegacySource(dir)
    const decisions = buildDecisionTemplate(validateLegacySource(src))
    decisions.channels[0]!.chosen = '244ACo'

    const db = await registry.forTenant(tenantId)
    const outcome = await writeLegacyCatalogue(db, src, decisions)

    const maps = await db.selectFrom('device_analyte_map').selectAll().execute()
    expect(maps).toHaveLength(1)
    expect(maps[0]!.analyte_code).toBe('244ACo')
    // Ứng viên không được chọn vẫn phải nêu tên trong danh sách bỏ qua.
    expect(outcome.skipped.some((s) => /người vận hành chọn 244ACo/.test(s.reason))).toBe(true)
  })

  it('mã kênh KHÔNG xung đột vẫn được di trú dù tệp quyết định có mục khác', async () => {
    write('TestCode.json', [
      { TestCode: '128LDL', TestName: 'LDL', GetComment: false },
      { TestCode: '244ACo', TestName: 'Nồng độ cồn', GetComment: false },
      { TestCode: '120GLUS', TestName: 'Glucose', GetComment: false },
    ])
    write('Instrument.json', [
      { InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', Active: true },
    ])
    write('InsTest.json', [
      { InsID: 1, TestCode: '128LDL', TestcodeIN: '19' },
      { InsID: 1, TestCode: '244ACo', TestcodeIN: '19' },
      // Kênh 01 sạch: không có xung đột, phải đi qua nhánh 'no-conflict'.
      { InsID: 1, TestCode: '120GLUS', TestcodeIN: '01' },
    ])
    tenantId = await freshTenant('mig3b')
    const outcome = await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    const maps = await db.selectFrom('device_analyte_map').selectAll().execute()
    expect(maps.map((m) => m.channel_code)).toEqual(['01'])
    expect(outcome.counts.device_analyte_map).toMatchObject({ read: 3, written: 1, skipped: 2 })
  })

  it('tôn trọng quyết định bỏ hẳn một mã kênh', async () => {
    write('TestCode.json', [
      { TestCode: '128LDL', TestName: 'LDL', GetComment: false },
      { TestCode: '244ACo', TestName: 'Nồng độ cồn', GetComment: false },
    ])
    write('Instrument.json', [
      { InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', Active: true },
    ])
    write('InsTest.json', [
      { InsID: 1, TestCode: '128LDL', TestcodeIN: '19' },
      { InsID: 1, TestCode: '244ACo', TestcodeIN: '19' },
    ])
    tenantId = await freshTenant('mig3c')

    const src = loadLegacySource(dir)
    const decisions = buildDecisionTemplate(validateLegacySource(src))
    decisions.channels[0]!.chosen = '__BO_QUA__'

    const db = await registry.forTenant(tenantId)
    const outcome = await writeLegacyCatalogue(db, src, decisions)

    expect(await db.selectFrom('device_analyte_map').selectAll().execute()).toHaveLength(0)
    expect(outcome.counts.device_analyte_map).toMatchObject({ read: 2, written: 0, skipped: 2 })
    expect(outcome.skipped.filter((s) => /quyết bỏ mã kênh/.test(s.reason))).toHaveLength(2)
  })

  it('bỏ qua ánh xạ trỏ tới xét nghiệm không tồn tại', async () => {
    write('TestCode.json', [{ TestCode: '120GLUS', TestName: 'Glucose', GetComment: false }])
    write('Instrument.json', [
      { InsID: 9, InsName: 'EBA200', ProtocolID: 'ASTM', ConType: 'C', Active: true },
    ])
    write('InsTest.json', [{ InsID: 9, TestCode: '233GLU', TestcodeIN: 'GLU' }])
    tenantId = await freshTenant('mig4')
    const outcome = await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    expect(await db.selectFrom('device_analyte_map').selectAll().execute()).toHaveLength(0)
    expect(outcome.skipped.some((s) => /không có trong danh mục/.test(s.reason))).toBe(true)
  })

  it('gắn ba nhãn hoạt động độc lập cho máy', async () => {
    write('Instrument.json', [
      { InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', Active: true },
      { InsID: 3, InsName: 'BC3200', ProtocolID: 'ASTM', ConType: 'C', Active: true },
    ])
    write('InterfacePara.json', [
      { HostID: 'DK10.1', InstrumentID: 1, Settings: '9600,8,N,1', CommPort: 6 },
    ])
    tenantId = await freshTenant('mig5')
    await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    const rows = await db.selectFrom('devices').selectAll().orderBy('legacy_ins_id').execute()
    expect(rows[0]).toMatchObject({ legacy_ins_id: 1, has_port_config: true })
    expect(rows[1]).toMatchObject({ legacy_ins_id: 3, has_port_config: false })
  })

  it('loại mã cha ra khỏi danh sách thành viên của chính nó', async () => {
    write('TestCode.json', [
      { TestCode: '400TPTNT', TestName: 'Tổng phân tích nước tiểu', GetComment: false, Profile: true },
      { TestCode: '141ULeu', TestName: 'Bạch cầu niệu', GetComment: false },
    ])
    write('Profile.json', [{ ProfileID: 'TPTNT', ProfileName: 'Tổng phân tích nước tiểu' }])
    write('ProfileTest.json', [
      { ProfileID: 'TPTNT', TestCode: '400TPTNT' },
      { ProfileID: 'TPTNT', TestCode: '141ULeu' },
    ])
    tenantId = await freshTenant('mig6')
    const outcome = await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    const members = await db.selectFrom('profile_members').selectAll().execute()
    expect(members.map((m) => m.analyte_code)).toEqual(['141ULeu'])
    // Mã cha bị loại vẫn phải nêu lý do, không được biến mất không dấu vết.
    expect(outcome.skipped.some((s) => /mã cha không phải thành viên/.test(s.reason))).toBe(true)
  })

  it('không để lại gì khi giao dịch hỏng giữa chừng', async () => {
    write('TestCode.json', [{ TestCode: '123AST', TestName: 'AST', GetComment: false }])
    // Mã HIS dài hơn varchar(64) làm Postgres huỷ giao dịch ở bước ghi cuối cùng —
    // sau khi danh mục xét nghiệm đã được ghi. Nếu giao dịch không bao trọn mọi bảng,
    // bảng analytes sẽ còn lại dữ liệu.
    // (Brief dùng hai mã HIS trùng nhau để gây lỗi; sau khi chuyển sang
    //  ON CONFLICT DO NOTHING thì trùng khoá không còn ném lỗi nữa.)
    write('TestCode_HIS.json', [{ TestCodeHIS: 'A'.repeat(80), Testcode: '123AST' }])
    tenantId = await freshTenant('mig7')

    await expect(runMigration(tenantId)).rejects.toThrow()

    const db = await registry.forTenant(tenantId)
    expect(await db.selectFrom('analytes').selectAll().execute()).toHaveLength(0)
    // Bản ghi Agent giữ chỗ cũng nằm trong giao dịch, nên hỏng là không để lại rác.
    expect(await db.selectFrom('agents').selectAll().execute()).toHaveLength(0)
  })

  it('ghi đúng cột active: chỉ Active=false mới là "đã ngừng"', async () => {
    write('TestCode.json', [
      { TestCode: '123AST', TestName: 'AST', GetComment: false, Active: true },
      { TestCode: '124ALT', TestName: 'ALT (đã ngừng)', GetComment: false, Active: false },
      // Không khai cột Active: phải hiểu là đang dùng, không được thành "đã ngừng".
      { TestCode: '125GGT', TestName: 'GGT', GetComment: false },
    ])
    tenantId = await freshTenant('migd')
    await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    const rows = await db.selectFrom('analytes').select(['code', 'active']).orderBy('code').execute()
    expect(rows).toEqual([
      { code: '123AST', active: true },
      { code: '124ALT', active: false },
      { code: '125GGT', active: true },
    ])
  })

  it('lần chạy thứ hai nêu lý do cho cả bảng devices', async () => {
    write('Instrument.json', [
      { InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', Active: true },
    ])
    tenantId = await freshTenant('mige')
    await runMigration(tenantId)
    const second = await runMigration(tenantId)

    expect(second.counts.devices).toMatchObject({ read: 1, written: 0, skipped: 1 })
    const deviceReasons = second.skipped.filter((s) => s.table === 'devices')
    expect(deviceReasons).toHaveLength(1)
    expect(deviceReasons[0]!.reason).toMatch(/đã có sẵn trong database/)
    expect(deviceReasons[0]!.ref).toContain('AU640')
  })

  it('thứ tự hiển thị đếm riêng cho từng nhóm', async () => {
    write('TestCode.json', [
      { TestCode: 'A1', TestName: 'A1', GetComment: false },
      { TestCode: 'A2', TestName: 'A2', GetComment: false },
      { TestCode: 'B1', TestName: 'B1', GetComment: false },
    ])
    write('Profile.json', [
      { ProfileID: 'PA', ProfileName: 'Nhóm A' },
      { ProfileID: 'PB', ProfileName: 'Nhóm B' },
    ])
    write('ProfileTest.json', [
      { ProfileID: 'PA', TestCode: 'A1' },
      { ProfileID: 'PA', TestCode: 'A2' },
      { ProfileID: 'PB', TestCode: 'B1' },
    ])
    tenantId = await freshTenant('migf')
    await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    const members = await db
      .selectFrom('profile_members')
      .select(['profile_code', 'analyte_code', 'display_order'])
      .orderBy('profile_code')
      .orderBy('display_order')
      .execute()
    expect(members).toEqual([
      { profile_code: 'PA', analyte_code: 'A1', display_order: 1 },
      { profile_code: 'PA', analyte_code: 'A2', display_order: 2 },
      // Nhóm thứ hai bắt đầu lại từ 1, không nối tiếp bộ đếm chung.
      { profile_code: 'PB', analyte_code: 'B1', display_order: 1 },
    ])
  })

  it('chặn hai lần chạy song song thay vì nhân đôi danh mục', async () => {
    write('TestCode.json', [{ TestCode: '123AST', TestName: 'AST', GetComment: false }])
    write('Instrument.json', [
      { InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', Active: true },
    ])
    tenantId = await freshTenant('migg')
    const db = await registry.forTenant(tenantId)
    const src = loadLegacySource(dir)
    const decisions = buildDecisionTemplate(validateLegacySource(src))

    // Giả lập tiến trình thứ nhất đang giữ khoá: một kết nối riêng cầm khoá tư vấn
    // cùng số hiệu. Không giữ được xác định bằng thời gian, nên test không rung.
    await db.connection().execute(async (held) => {
      await sql`select pg_advisory_lock(2026, 2109)`.execute(held)
      try {
        await expect(writeLegacyCatalogue(db, src, decisions)).rejects.toThrow(
          /đang có một lần chạy di trú khác/,
        )
      } finally {
        await sql`select pg_advisory_unlock(2026, 2109)`.execute(held)
      }
    })

    // Bị chặn nghĩa là KHÔNG ghi gì — không có agent, máy hay xét nghiệm nhân đôi.
    expect(await db.selectFrom('agents').selectAll().execute()).toHaveLength(0)
    expect(await db.selectFrom('devices').selectAll().execute()).toHaveLength(0)
    expect(await db.selectFrom('analytes').selectAll().execute()).toHaveLength(0)

    // Khoá đã nhả: lần chạy tiếp theo thành công bình thường.
    const after = await writeLegacyCatalogue(db, src, decisions)
    expect(after.counts.devices).toMatchObject({ read: 1, written: 1, skipped: 0 })
    expect(await db.selectFrom('agents').selectAll().execute()).toHaveLength(1)
  })

  it('mã HIS trùng trong nguồn được ghi một lần và nêu rõ dòng bị bỏ', async () => {
    write('TestCode.json', [{ TestCode: '123AST', TestName: 'AST', GetComment: false }])
    write('TestCode_HIS.json', [
      { TestCodeHIS: 'A/01', Testcode: '123AST' },
      { TestCodeHIS: 'A/01', Testcode: '123AST' },
    ])
    tenantId = await freshTenant('mig7b')
    const outcome = await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    expect(await db.selectFrom('his_analyte_map').selectAll().execute()).toHaveLength(1)
    expect(outcome.counts.his_analyte_map).toMatchObject({ read: 2, written: 1, skipped: 1 })
    expect(outcome.skipped.some((s) => /trùng trong nguồn/.test(s.reason))).toBe(true)
  })

  it('chạy hai lần trên database trống cho kết quả giống hệt nhau', async () => {
    write('TestCode.json', [{ TestCode: '123AST', TestName: 'AST', GetComment: false }])

    const first = await freshTenant('mig8a')
    const outcomeA = await runMigration(first)
    const second = await freshTenant('mig8b')
    const outcomeB = await runMigration(second)

    expect(outcomeA.counts).toEqual(outcomeB.counts)
  })

  it('chạy lại trên cùng một database không sinh thêm dòng nào', async () => {
    write('TestCode.json', [
      { TestCode: '120GLUS', TestName: 'Glucose', GetComment: false },
      { TestCode: '034NEU%', TestName: 'Bạch cầu trung tính %', GetComment: false },
    ])
    write('Instrument.json', [
      { InsID: 9, InsName: 'EBA200', ProtocolID: 'ASTM', ConType: 'C', Active: true },
    ])
    write('InsTest.json', [{ InsID: 9, TestCode: '120GLUS', TestcodeIN: 'GLU' }])
    write('Profile.json', [{ ProfileID: 'TPTNT', ProfileName: 'Nước tiểu' }])
    write('ProfileTest.json', [{ ProfileID: 'TPTNT', TestCode: '034NEU%' }])
    write('ResultMapping.json', [
      { InstrumentID: 9, TestCode: '120GLUS', ResultIn: 'neg', Result: 'Âm tính', Color: 0 },
    ])
    write('TestCode_HIS.json', [{ TestCodeHIS: 'A/01', Testcode: '120GLUS' }])

    tenantId = await freshTenant('mig9')
    const first = await runMigration(tenantId)
    const db = await registry.forTenant(tenantId)

    const snapshot = async (): Promise<Record<string, unknown[]>> => ({
      agents: await db.selectFrom('agents').select(['hostname']).orderBy('hostname').execute(),
      analytes: await db.selectFrom('analytes').select(['code', 'source']).orderBy('code').execute(),
      devices: await db.selectFrom('devices').select(['device_key', 'name']).orderBy('device_key').execute(),
      maps: await db
        .selectFrom('device_analyte_map')
        .select(['channel_code', 'analyte_code'])
        .orderBy('channel_code')
        .execute(),
      profiles: await db.selectFrom('profiles').select(['code']).orderBy('code').execute(),
      members: await db
        .selectFrom('profile_members')
        .select(['profile_code', 'analyte_code', 'display_order'])
        .orderBy('analyte_code')
        .execute(),
      rules: await db
        .selectFrom('result_mappings')
        .select(['analyte_code', 'result_in', 'result_out'])
        .orderBy('result_in')
        .execute(),
      his: await db
        .selectFrom('his_analyte_map')
        .select(['his_service_code', 'analyte_code'])
        .orderBy('his_service_code')
        .execute(),
    })

    const before = await snapshot()
    expect(first.counts.analytes).toMatchObject({ read: 2, written: 2, skipped: 0 })

    const second = await runMigration(tenantId)
    expect(await snapshot()).toEqual(before)
    // Lần hai không ghi thêm gì, và mọi dòng đều có lý do đọc được.
    for (const c of Object.values(second.counts)) {
      expect(c.written).toBe(0)
      expect(c.read).toBe(c.skipped)
    }
    expect(second.skipped.every((s) => /đã có sẵn trong database/.test(s.reason))).toBe(true)
  })

  it('không dùng LIKE: mã chứa % chỉ khớp đúng chính nó', async () => {
    write('TestCode.json', [
      { TestCode: '034NEU%', TestName: 'Bạch cầu trung tính %', GetComment: false },
      { TestCode: '034NEUX', TestName: 'Mã trông giống nhưng khác', GetComment: false },
    ])
    write('TestCode_HIS.json', [{ TestCodeHIS: 'H/NEU', Testcode: '034NEU%' }])
    tenantId = await freshTenant('miga')
    await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    // Khớp bằng: đúng một dòng. Nếu ai đó thay bằng LIKE, '034NEU%' sẽ khớp cả hai.
    const exact = await db
      .selectFrom('analytes')
      .selectAll()
      .where('code', '=', '034NEU%')
      .execute()
    expect(exact.map((r) => r.code)).toEqual(['034NEU%'])
    const his = await db.selectFrom('his_analyte_map').selectAll().execute()
    expect(his.map((h) => h.analyte_code)).toEqual(['034NEU%'])
  })

  it('luật ghi đè trỏ tới máy không có trong danh sách trở thành luật chung và được ghi chú', async () => {
    write('TestCode.json', [{ TestCode: '335HPV', TestName: 'HPV', GetComment: false }])
    write('Instrument.json', [
      { InsID: 9, InsName: 'EBA200', ProtocolID: 'ASTM', ConType: 'C', Active: true },
    ])
    write('ResultMapping.json', [
      { InstrumentID: 30, TestCode: '335HPV', ResultIn: 'POS', Result: 'Dương tính', Color: 2 },
    ])
    tenantId = await freshTenant('migb')
    const outcome = await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    const rules = await db.selectFrom('result_mappings').selectAll().execute()
    expect(rules).toHaveLength(1)
    expect(rules[0]!.device_id).toBeNull()
    expect(outcome.counts.result_mappings).toMatchObject({ read: 1, written: 1, skipped: 0 })
    expect(outcome.notes.some((n) => /máy 30 không có trong danh sách/.test(n.reason))).toBe(true)
  })
})

describe('formatReport', () => {
  it('nêu rõ chưa ghi gì khi chạy không có --apply', () => {
    const text = formatReport(
      {
        channelConflicts: [{ insId: 1, channelCode: '19', candidates: ['128LDL', '244ACo'] }],
        orphans: [],
        profilesWithoutMembers: [],
        wildcardCodes: ['034NEU%'],
        duplicateAnalyteCodes: [],
        droppedSourceRows: [],
        shadowedAliases: [],
        ambiguousAliases: [],
      },
      null,
    )
    expect(text).toContain('CHƯA GHI VÀO DATABASE')
    expect(text).toContain('128LDL hoặc 244ACo')
  })

  it('in bảng đối chiếu và chi tiết dòng bị bỏ', () => {
    const text = formatReport(
      {
        channelConflicts: [],
        orphans: [],
        profilesWithoutMembers: [],
        wildcardCodes: [],
        duplicateAnalyteCodes: [],
        droppedSourceRows: [],
        shadowedAliases: [],
        ambiguousAliases: [],
      },
      {
        counts: { analytes: { read: 2, written: 1, skipped: 1 } },
        skipped: [{ table: 'analytes', ref: '123AST', reason: 'mã bị lặp trong nguồn' }],
        notes: [{ table: 'result_mappings', ref: 'máy 30', reason: 'thành luật chung' }],
      },
    )
    expect(text).toContain('ĐỐI CHIẾU SAU KHI GHI')
    expect(text).toMatch(/analytes\s+2\s+1\s+1/)
    expect(text).toContain('mã bị lặp trong nguồn')
    expect(text).toContain('thành luật chung')
  })

  it('in mọi dòng bị bộ đọc loại, kèm tệp nguồn và lý do', () => {
    const text = formatReport(
      {
        channelConflicts: [],
        orphans: [],
        profilesWithoutMembers: [],
        wildcardCodes: [],
        duplicateAnalyteCodes: [],
        droppedSourceRows: [
          { file: 'TestCode.json', key: 'AutoID=213', code: '780FSH1', reason: 'thiếu tên xét nghiệm' },
          { file: 'TestCode.json', key: 'AutoID=214', code: '780FSH2', reason: 'thiếu tên xét nghiệm' },
          { file: 'TestCode_HIS.json', key: 'dòng 12', code: null, reason: 'thiếu mã dịch vụ HIS' },
        ],
        shadowedAliases: [],
        ambiguousAliases: [],
      },
      null,
    )
    expect(text).toContain('Dòng bị loại ngay khi đọc tệp nguồn: 3')
    expect(text).toMatch(/2 × TestCode\.json — thiếu tên xét nghiệm/)
    expect(text).toContain('[TestCode_HIS.json] dòng 12 — thiếu mã dịch vụ HIS')
    expect(text).toContain('AutoID=213 (mã 780FSH1)')
  })

  it('in tên xét nghiệm cả hai bên cho bí danh trùng tên và bí danh nhập nhằng', () => {
    const text = formatReport(
      {
        channelConflicts: [],
        orphans: [],
        profilesWithoutMembers: [],
        wildcardCodes: [],
        duplicateAnalyteCodes: [],
        droppedSourceRows: [],
        shadowedAliases: [
          {
            alias: '000CD',
            primaryName: 'Cấy dịch tìm vi khuẩn/nấm',
            aliasTargetCode: '333CDom',
            aliasTargetName: 'Cấy đờm',
          },
        ],
        ambiguousAliases: [
          {
            alias: '000CDNT',
            candidates: [
              { code: '111CDN', name: 'Cấy dịch ngực (T)' },
              { code: '222CDNT', name: 'Cấy dịch não tủy' },
            ],
            autoResolvable: false,
          },
        ],
      },
      null,
    )
    // Kỹ thuật viên phải đọc được tên của cả bên được dùng lẫn bên bị bỏ.
    expect(text).toContain('Bí danh trùng tên với mã chính (mã chính được dùng): 1')
    expect(text).toContain('Cấy dịch tìm vi khuẩn/nấm')
    expect(text).toContain('Cấy đờm')
    expect(text).toContain('Bí danh trỏ tới nhiều mã chính (KHÔNG tự phân giải): 1')
    expect(text).toContain('Cấy dịch ngực (T)')
    expect(text).toContain('Cấy dịch não tủy')
  })
})

describe('phân giải bí danh mã xét nghiệm khi ghi', () => {
  /**
   * Bộ nguồn dựng tay: bảng bí danh do `legacy-source.ts` sinh ra từ cột đổi tên mã
   * của `TestCodeNew`, nên không dựng được bằng các tệp JSON rỗng ở trên.
   */
  function sourceWithAliases(): LegacySource {
    return {
      analytes: [
        { code: '000CD', name: 'Cấy dịch', nameEn: null, category: null, unit: null,
          normalRangeText: null, lowerLimit: null, higherLimit: null, commentLower: null,
          commentHigher: null, commentGray: null, criteria: null, resultIsComment: false,
          printOrder: null, isProfileHead: false, source: 'legacy' },
        { code: '333CDom', name: 'Cấy đờm', nameEn: null, category: null, unit: null,
          normalRangeText: null, lowerLimit: null, higherLimit: null, commentLower: null,
          commentHigher: null, commentGray: null, criteria: null, resultIsComment: false,
          printOrder: null, isProfileHead: false, source: 'legacy-new' },
        { code: '900XN04', name: 'Xét nghiệm 04', nameEn: null, category: null, unit: null,
          normalRangeText: null, lowerLimit: null, higherLimit: null, commentLower: null,
          commentHigher: null, commentGray: null, criteria: null, resultIsComment: false,
          printOrder: null, isProfileHead: false, source: 'legacy-new' },
        { code: '901XN05', name: 'Xét nghiệm 05', nameEn: null, category: null, unit: null,
          normalRangeText: null, lowerLimit: null, higherLimit: null, commentLower: null,
          commentHigher: null, commentGray: null, criteria: null, resultIsComment: false,
          printOrder: null, isProfileHead: false, source: 'legacy-new' },
      ],
      instruments: [
        { insId: 4, name: 'Cobas', protocol: 'ASTM', conType: 'C', location: null, active: true },
      ],
      insTests: [],
      interfaceParas: [],
      resultMappings: [
        // Mã chính: dùng thẳng, KHÔNG tra bí danh (bí danh 000CD trỏ tới cấy đờm).
        { insId: 4, analyteCode: '000CD', resultIn: 'pos', resultOut: 'Dương tính', comment: null, color: 2 },
        // Bí danh phân giải được về đúng một mã chính.
        { insId: 4, analyteCode: 'XNNT21_04', resultIn: 'neg', resultOut: 'Âm tính', comment: null, color: 0 },
        // Bí danh nhập nhằng: KHÔNG tự chọn.
        { insId: 4, analyteCode: '000CDNT', resultIn: 'pos', resultOut: 'Dương tính', comment: null, color: 2 },
        // Không phân giải được bằng đường nào.
        { insId: 4, analyteCode: 'khong-co-that', resultIn: 'neg', resultOut: 'Âm tính', comment: null, color: 0 },
      ],
      profiles: [],
      profileTests: [],
      hisMaps: [],
      analyteAliases: [
        { alias: '000CD', canonical: '333CDom' },
        { alias: 'XNNT21_04', canonical: '900XN04' },
        { alias: '000CDNT', canonical: '900XN04' },
        { alias: '000CDNT', canonical: '901XN05' },
      ],
    }
  }

  it('mã chính thắng bí danh, bí danh nhập nhằng bị bỏ kèm lý do', async () => {
    tenantId = await freshTenant('migc')
    const src = sourceWithAliases()
    const db = await registry.forTenant(tenantId)
    const outcome = await writeLegacyCatalogue(db, src, { channels: [] })

    const rules = await db
      .selectFrom('result_mappings')
      .select(['analyte_code', 'result_in'])
      .orderBy('analyte_code')
      .execute()
    expect(rules).toEqual([
      // 000CD giữ nguyên mã chính "cấy dịch", KHÔNG bị đổi sang 333CDom "cấy đờm".
      { analyte_code: '000CD', result_in: 'pos' },
      { analyte_code: '900XN04', result_in: 'neg' },
    ])
    expect(outcome.counts.result_mappings).toMatchObject({ read: 4, written: 2, skipped: 2 })
    expect(outcome.skipped.some((s) => /bí danh 000CDNT trỏ tới nhiều mã chính/.test(s.reason))).toBe(
      true,
    )
    expect(
      outcome.skipped.some((s) => /khong-co-that không có trong danh mục/.test(s.reason)),
    ).toBe(true)
    expect(outcome.notes.some((n) => /bí danh, đã quy về mã chính 900XN04/.test(n.reason))).toBe(true)
  })
})

describe('runMigrateLegacy — tệp quyết định', () => {
  let decisionsPath: string

  function options(tenantCode: string, apply: boolean): ReturnType<typeof parseMigrateLegacyArgs> {
    return parseMigrateLegacyArgs([
      '--tenant',
      tenantCode,
      '--source',
      dir,
      '--decisions',
      decisionsPath,
      ...(apply ? ['--apply'] : []),
    ])
  }

  async function run(tenantCode: string, apply: boolean): Promise<string> {
    const out: string[] = []
    await runMigrateLegacy(app.get(AdminDbService).db, registry, options(tenantCode, apply), (l) =>
      out.push(l),
    )
    return out.join('\n')
  }

  beforeEach(() => {
    decisionsPath = join(dir, 'quyet-dinh.json')
    write('TestCode.json', [
      { TestCode: '128LDL', TestName: 'LDL', GetComment: false },
      { TestCode: '244ACo', TestName: 'Nồng độ cồn', GetComment: false },
    ])
    write('Instrument.json', [
      { InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', Active: true },
    ])
    write('InsTest.json', [
      { InsID: 1, TestCode: '128LDL', TestcodeIN: '19' },
      { InsID: 1, TestCode: '244ACo', TestcodeIN: '19' },
    ])
  })

  it('chạy khô sinh tệp khi chưa có, và KHÔNG ghi đè khi đã có', async () => {
    await freshTenant('migh')
    expect(existsSync(decisionsPath)).toBe(false)
    await run('migh', false)
    expect(existsSync(decisionsPath)).toBe(true)

    // Kỹ thuật viên điền dở và thêm ghi chú.
    const edited = JSON.parse(readFileSync(decisionsPath, 'utf8')) as {
      channels: { chosen: string | null; note?: string }[]
    }
    edited.channels[0]!.note = 'đã hỏi bác sĩ Hoà, chờ trả lời'
    writeFileSync(decisionsPath, JSON.stringify(edited, null, 2), 'utf8')
    const untouched = readFileSync(decisionsPath, 'utf8')

    // Chạy khô lần nữa: tệp phải nguyên vẹn từng byte.
    await run('migh', false)
    expect(readFileSync(decisionsPath, 'utf8')).toBe(untouched)
  })

  it('ghi thật vẫn giữ ghi chú của kỹ thuật viên', async () => {
    await freshTenant('migi')
    await run('migi', false)
    const edited = JSON.parse(readFileSync(decisionsPath, 'utf8')) as {
      channels: { chosen: string | null; note?: string }[]
    }
    edited.channels[0]!.chosen = '244ACo'
    edited.channels[0]!.note = 'đã hỏi bác sĩ Hoà, chờ trả lời'
    writeFileSync(decisionsPath, JSON.stringify(edited, null, 2), 'utf8')

    await run('migi', true)

    const after = JSON.parse(readFileSync(decisionsPath, 'utf8')) as {
      channels: { chosen: string | null; note?: string }[]
    }
    expect(after.channels[0]!.chosen).toBe('244ACo')
    expect(after.channels[0]!.note).toBe('đã hỏi bác sĩ Hoà, chờ trả lời')
  })

  it('chạy khô không ghi gì vào database', async () => {
    const tenant = await freshTenant('migj')
    await run('migj', false)
    const db = await registry.forTenant(tenant)
    expect(await db.selectFrom('analytes').selectAll().execute()).toHaveLength(0)
    expect(await db.selectFrom('agents').selectAll().execute()).toHaveLength(0)
  })

  it('tệp quyết định hỏng báo lỗi tiếng Việt kèm tệp và dòng', async () => {
    await freshTenant('migk')
    writeFileSync(decisionsPath, '{\n  "channels": [\n    { "insId": 1, }\n  ]\n}', 'utf8')
    await expect(run('migk', false)).rejects.toThrow(/không đọc được.*dòng \d+, cột \d+/s)
  })

  it('tệp quyết định mặc định KHÔNG nằm trong thư mục dữ liệu nguồn', () => {
    const parsed = parseMigrateLegacyArgs(['--tenant', 'bvdemo', '--source', dir])
    expect(parsed.decisionsPath.startsWith(dir)).toBe(false)
    expect(parsed.decisionsPath).toContain('bvdemo')
  })
})
