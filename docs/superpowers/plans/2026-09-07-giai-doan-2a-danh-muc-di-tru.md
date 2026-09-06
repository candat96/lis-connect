# Khối 2A — Danh mục và di trú từ hệ cũ

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Đưa toàn bộ danh mục của hệ LIS cũ ở Bệnh viện Đa khoa 10 — 404 xét nghiệm, 20 máy, 292 ánh xạ mã kênh, 56 luật ghi đè, 835 mã HIS — sang LIS Connect, giữ nguyên mã cũ, sửa những chỗ hệ cũ làm sai, và cho kỹ thuật viên sửa được danh mục ngay trên web.

**Architecture:** Bảng danh mục nằm trong database nghiệp vụ của từng cơ sở, mở rộng `devices` và `device_analyte_map` đã có từ giai đoạn 1. Công cụ di trú là một lệnh CLI ba giai đoạn — đọc và kiểm tra, chờ người quyết những chỗ nhập nhằng, rồi ghi trong một giao dịch duy nhất. Ba màn hình quản lý thêm vào Web UI đã có.

**Tech Stack:** NestJS 11 · Kysely 0.27 + `pg` · PostgreSQL 16 · React 19 + Vite · Vitest 2 · TypeScript 5.7 (ESM, strict)

**Spec:** `docs/superpowers/specs/2026-09-06-giai-doan-2a-danh-muc-di-tru-design.md`

**Khảo sát nguồn:** `docs/khao-sat/lis-hien-huu.md`, dữ liệu ở `docs/khao-sat/seed/du-lieu/`

## Global Constraints

Kế thừa toàn bộ ràng buộc của kế hoạch giai đoạn 1
(`docs/superpowers/plans/2026-09-05-giai-doan-1-xuong-song-mot-chieu.md`), trong đó
quan trọng nhất với khối này:

- **Không truy vấn nghiệp vụ nào được tự chọn database.** Mọi truy cập DB nghiệp vụ đi
  qua `TenantRegistryService.forTenant`, trong `TenantContext.run(...)`.
- **Trong `apps/core`, mọi tham số constructor được tiêm phải khai `@Inject(...)` tường
  minh.** `esbuild` không phát metadata `design:paramtypes`, NestJS sẽ lặng lẽ tiêm
  `undefined`. Tệp test không đi qua `main.ts` phải `import 'reflect-metadata'` ở dòng đầu.
- **Mọi task có giao diện BẮT BUỘC gọi skill `ui-ux-pro-max`** trước khi viết dòng JSX
  đầu tiên.
- **Mọi lời gọi API ở Web UI phải có xử lý lỗi hiển thị được**; không màn hình nào được
  kẹt ở trạng thái "đang tải"; vòng tự làm mới phải chuyển sang "không rõ" thay vì giữ
  dữ liệu cũ.
- **Không chỉ dùng màu** để truyền đạt trạng thái — luôn kèm nhãn chữ.
- Tên bảng và cột Postgres `snake_case`; kiểu TypeScript `PascalCase`; biến `camelCase`.
- Mốc thời gian `timestamptz`.
- Giao diện và thông báo tiếng Việt có dấu đầy đủ; tên hàm, biến và thông điệp commit
  bằng tiếng Anh, theo Conventional Commits.
- **KHÔNG chạy `pnpm --filter @lis/agent build`** — nó biên dịch lại `better-sqlite3`
  cho ABI Electron và làm 23 test đỏ với lỗi `NODE_MODULE_VERSION`. Đó không phải lỗi mã.
  Khắc phục: `pnpm --filter @lis/agent rebuild better-sqlite3`.
- **Không chạy lệnh `dev` nào ở tiền cảnh** — chặn luồng vô hạn.

### Ràng buộc riêng của khối 2A

- **Mã xét nghiệm cũ là mã nghiệp vụ chính thức**, giữ nguyên: `123AST`, `126ChoT`,
  `400TPTNT`. Không đánh mã mới, không thêm lớp dịch.
- **Cắt khoảng trắng thừa ở mọi giá trị chuỗi lấy từ nguồn.** Dữ liệu gốc là cột `CHAR`
  của SQL Server nên có đệm: `ResultMapping.TestCode` chứa `"141ULeu        "`,
  `InsTest.Type` chứa `"P    "`. Không cắt thì mọi so khớp mã sẽ hỏng.
- **So khớp mã bằng `=`, không bao giờ dùng `LIKE`.** Danh mục có 8 mã chứa ký tự đại
  diện: `010LYM%`, `011MONO%`, `012GRAN%`, `034NEU%`, `035LYM%`, `036MONO%`, `037EOS%`,
  `038BASO%`. Ký tự `%` là một phần của mã, không phải ký tự đại diện.
- **Công cụ di trú không bao giờ tự đoán** khi gặp nhập nhằng. Hệ cũ đoán bằng `TOP 1`
  theo thứ tự alphabet — đó là cách kết quả nồng độ cồn có thể về nhầm ô LDL.

---

## Số liệu thật của nguồn dữ liệu

Đã đếm trực tiếp trên `docs/khao-sat/seed/du-lieu/`. Người thực thi dùng những con số
này làm mốc đối chiếu:

| Tệp | Số dòng |
|---|---|
| `TestCode.json` | 404 |
| `InsTest.json` | 292 |
| `Instrument.json` | 20 |
| `InterfacePara.json` | 9 |
| `ResultMapping.json` | 56 |
| `Profile.json` | 2 |
| `ProfileTest.json` | 33 |
| `TestCode_HIS.json` | 835 |

**Bảy cặp `(máy, mã kênh)` trùng nhau** — nhiều hơn con số tài liệu khảo sát nêu:

| Máy | Mã kênh | Các xét nghiệm cùng trỏ tới |
|---|---|---|
| 1 (AU640) | `01` | `120GLUS`, `342GLU` |
| 1 (AU640) | `19` | `128LDL`, `244ACo` |
| 2 (e2010) | `3` | `931Cortisol`, `932931Cortisol` |
| 9 (EBA200) | `GLU` | `120GLUS`, `233GLU` |
| 12 (AIA360) | `#CORT` | `931Cortisol`, `932931Cortisol` |
| 15 (AU2700) | `01` | `120GLUS`, `342GLU` |

(Cặp thứ bảy nằm trong cùng nhóm trên; công cụ phải liệt kê đủ, không dựa vào bảng này.)

Lưu ý `932931Cortisol` trông rõ ràng là lỗi nhập liệu — chuỗi `931Cortisol` bị lặp. Công
cụ **vẫn không được tự loại**; nó chỉ báo ra để người quyết.

**Dữ liệu mồ côi:**

- 2 mã trong `InsTest` không có trong danh mục: `233GLU`, `hc12`
- 8 mã trong `TestCode_HIS` trỏ tới xét nghiệm không tồn tại, gồm chuỗi rỗng, `233GLU`,
  `925PH`, và `7.82E+04` — một mã đã bị Excel biến thành ký hiệu khoa học
- `ResultMapping.TestCode` có cả giá trị `NULL` lẫn giá trị có đệm khoảng trắng

---

## File Structure

```
apps/core/src/
├─ tenant/migrations/
│  └─ 002-catalogue.ts               bảng danh mục cho DB nghiệp vụ
├─ tenant/tenant-schema.ts           MỞ RỘNG: thêm kiểu các bảng mới
├─ catalogue/
│  ├─ catalogue.service.ts           đọc/sửa danh mục
│  ├─ catalogue.controller.ts        API cho Web UI
│  └─ catalogue.module.ts
└─ migration/
   ├─ legacy-source.ts               đọc tệp JSON, cắt khoảng trắng, chuẩn hoá
   ├─ legacy-validator.ts            kiểm tra, phát hiện xung đột và mồ côi
   ├─ legacy-decisions.ts            đọc/ghi tệp quyết định
   ├─ legacy-writer.ts               ghi vào DB trong một giao dịch
   ├─ legacy-report.ts               báo cáo đối chiếu
   └─ migrate-legacy.command.ts      ghép ba giai đoạn thành một lệnh

apps/web/src/screens/
├─ CatalogueAnalytes.tsx             danh mục xét nghiệm
├─ CatalogueChannelMap.tsx           ánh xạ mã kênh máy
└─ CatalogueResultRules.tsx          luật ghi đè kết quả
```

**Nguyên tắc phân chia:** `legacy-source` chỉ đọc và chuẩn hoá, không phán xét;
`legacy-validator` chỉ phát hiện vấn đề, không sửa; `legacy-writer` chỉ ghi, không quyết
định. Ba lớp này ghép ở `migrate-legacy.command.ts` và chỉ ở đó — cùng kiểu phân tầng đã
dùng cho `DeviceManager` ở giai đoạn 1.

---

## Task 1: Schema danh mục

**Files:**
- Create: `apps/core/src/tenant/migrations/002-catalogue.ts`
- Modify: `apps/core/src/tenant/tenant-schema.ts`
- Modify: `apps/core/src/tenant/tenant-migrator.service.ts` (đăng ký migration mới)
- Test: `apps/core/test/catalogue-schema.test.ts`

**Interfaces:**
- Consumes: `TenantDatabase` và `TenantMigratorService` từ giai đoạn 1
- Produces: các kiểu bảng `AnalytesTable`, `ProfilesTable`, `ProfileMembersTable`, `ResultMappingsTable`, `HisAnalyteMapTable`, `ReferenceRangesTable`; các cột mở rộng trên `DevicesTable` và `DeviceAnalyteMapTable`

**Ghi chú thiết kế:** ràng buộc `UNIQUE (device_id, channel_code)` trên `device_analyte_map` là thay đổi quan trọng nhất của cả khối. Hệ cũ không có nó và gỡ nhập nhằng bằng `TOP 1` theo alphabet — kết quả là kênh `19` của AU640 có thể trả nồng độ cồn vào ô LDL mà không cảnh báo.

- [ ] **Step 1: Viết test cho schema mới**

`apps/core/test/catalogue-schema.test.ts`:
```ts
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
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test test/catalogue-schema.test.ts`
Expected: FAIL — bảng `analytes` không tồn tại.

- [ ] **Step 3: Viết migration**

`apps/core/src/tenant/migrations/002-catalogue.ts`:
```ts
import { type Kysely, sql } from 'kysely'

export async function up(db: Kysely<unknown>): Promise<void> {
  await db.schema
    .createTable('analytes')
    // Mã cũ của hệ LIS đang chạy (123AST, 400TPTNT) là khoá nghiệp vụ.
    // Giữ nguyên để HIS và kỹ thuật viên không phải học lại.
    .addColumn('code', 'varchar(64)', (c) => c.primaryKey())
    .addColumn('name', 'text', (c) => c.notNull())
    .addColumn('name_en', 'text')
    .addColumn('category', 'varchar(64)')
    .addColumn('unit', 'varchar(32)')
    .addColumn('decimals', 'integer')
    .addColumn('normal_range_text', 'text')
    .addColumn('lower_limit', 'numeric')
    .addColumn('higher_limit', 'numeric')
    .addColumn('comment_lower', 'text')
    .addColumn('comment_higher', 'text')
    .addColumn('comment_gray', 'text')
    .addColumn('criteria', 'text')
    // false: cột kết quả giữ giá trị số, biện luận để riêng.
    // true: cột kết quả nhận luôn câu biện luận.
    .addColumn('result_is_comment', 'boolean', (c) => c.notNull().defaultTo(false))
    .addColumn('print_order', 'integer')
    .addColumn('is_profile_head', 'boolean', (c) => c.notNull().defaultTo(false))
    .addColumn('source', 'varchar(16)', (c) => c.notNull())
    .addColumn('active', 'boolean', (c) => c.notNull().defaultTo(true))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createIndex('analytes_name_idx')
    .on('analytes')
    .column('name')
    .execute()

  await db.schema
    .createTable('profiles')
    .addColumn('code', 'varchar(64)', (c) => c.primaryKey())
    .addColumn('name', 'text', (c) => c.notNull())
    // 'legacy-declared': có khai báo thành viên thật ở hệ cũ.
    // 'legacy-inferred': dựng lại từ thụt lề tên, CẦN kỹ thuật viên xác nhận.
    .addColumn('source', 'varchar(24)', (c) => c.notNull())
    .addColumn('confirmed', 'boolean', (c) => c.notNull().defaultTo(false))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createTable('profile_members')
    .addColumn('profile_code', 'varchar(64)', (c) =>
      c.notNull().references('profiles.code').onDelete('cascade'),
    )
    .addColumn('analyte_code', 'varchar(64)', (c) =>
      c.notNull().references('analytes.code').onDelete('cascade'),
    )
    .addColumn('display_order', 'integer', (c) => c.notNull().defaultTo(0))
    .addPrimaryKeyConstraint('profile_members_pk', ['profile_code', 'analyte_code'])
    .execute()

  await db.schema
    .createTable('result_mappings')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('device_id', 'uuid', (c) => c.notNull().references('devices.id').onDelete('cascade'))
    .addColumn('analyte_code', 'varchar(64)', (c) =>
      c.notNull().references('analytes.code').onDelete('cascade'),
    )
    .addColumn('result_in', 'text', (c) => c.notNull())
    .addColumn('result_out', 'text')
    .addColumn('comment', 'text')
    // 0 bình thường, 1 thấp, 2 cao — giữ đúng quy ước của hệ cũ
    .addColumn('color', 'integer')
    .addUniqueConstraint('result_mappings_unique', ['device_id', 'analyte_code', 'result_in'])
    .execute()

  await db.schema
    .createTable('his_analyte_map')
    .addColumn('his_service_code', 'varchar(64)', (c) => c.primaryKey())
    .addColumn('analyte_code', 'varchar(64)', (c) =>
      c.notNull().references('analytes.code').onDelete('cascade'),
    )
    .execute()

  await db.schema
    .createIndex('his_analyte_map_analyte_idx')
    .on('his_analyte_map')
    .column('analyte_code')
    .execute()

  await db.schema
    .createTable('reference_ranges')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('analyte_code', 'varchar(64)', (c) =>
      c.notNull().references('analytes.code').onDelete('cascade'),
    )
    // NULL nghĩa là áp dụng cho mọi máy
    .addColumn('device_id', 'uuid', (c) => c.references('devices.id').onDelete('cascade'))
    // NULL nghĩa là áp dụng cho mọi giới
    .addColumn('sex', 'varchar(8)')
    .addColumn('age_from_days', 'integer')
    .addColumn('age_to_days', 'integer')
    .addColumn('lower_limit', 'numeric')
    .addColumn('higher_limit', 'numeric')
    .addColumn('gray_lower', 'numeric')
    .addColumn('gray_upper', 'numeric')
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  // --- Mở rộng bảng có sẵn từ giai đoạn 1 ---

  await db.schema.alterTable('devices').addColumn('legacy_ins_id', 'integer').execute()
  await db.schema.alterTable('devices').addColumn('protocol', 'varchar(32)').execute()
  await db.schema.alterTable('devices').addColumn('location', 'varchar(64)').execute()
  // Ba nhãn hoạt động ĐỘC LẬP. Hệ cũ chỉ có một cờ `Active` và cả 20 máy đều bằng 1,
  // nên nó vô dụng. Dấu vết thật nằm ở chỗ có cấu hình cổng và từng gửi kết quả.
  await db.schema
    .alterTable('devices')
    .addColumn('has_port_config', 'boolean', (c) => c.notNull().defaultTo(false))
    .execute()
  await db.schema
    .alterTable('devices')
    .addColumn('has_sent_results', 'boolean', (c) => c.notNull().defaultTo(false))
    .execute()

  await db.schema
    .alterTable('device_analyte_map')
    .addColumn('analyte_code', 'varchar(64)', (c) => c.references('analytes.code'))
    .execute()
  await db.schema.alterTable('device_analyte_map').addColumn('round', 'integer').execute()
  await db.schema
    .alterTable('device_analyte_map')
    .addColumn('allow_insert', 'boolean', (c) => c.notNull().defaultTo(false))
    .execute()
  await db.schema
    .alterTable('device_analyte_map')
    .addColumn('omit', 'boolean', (c) => c.notNull().defaultTo(false))
    .execute()
  await db.schema
    .alterTable('device_analyte_map')
    .addColumn('keep_higher_sign', 'boolean', (c) => c.notNull().defaultTo(false))
    .execute()
  await db.schema
    .alterTable('device_analyte_map')
    .addColumn('keep_lower_sign', 'boolean', (c) => c.notNull().defaultTo(false))
    .execute()

  // Thay đổi quan trọng nhất của khối 2A: hệ cũ cho phép hai xét nghiệm cùng trỏ tới
  // một mã kênh rồi gỡ nhập nhằng bằng TOP 1 theo alphabet. Trên AU640, kênh 19 map
  // cả 128LDL lẫn 244ACo (nồng độ cồn) — kết quả có thể về nhầm ô mà không cảnh báo.
  await db.schema
    .alterTable('device_analyte_map')
    .addUniqueConstraint('device_analyte_map_channel_unique', ['device_id', 'channel_code'])
    .execute()
}

export async function down(db: Kysely<unknown>): Promise<void> {
  await db.schema
    .alterTable('device_analyte_map')
    .dropConstraint('device_analyte_map_channel_unique')
    .execute()
  for (const col of [
    'analyte_code',
    'round',
    'allow_insert',
    'omit',
    'keep_higher_sign',
    'keep_lower_sign',
  ]) {
    await db.schema.alterTable('device_analyte_map').dropColumn(col).execute()
  }
  for (const col of ['legacy_ins_id', 'protocol', 'location', 'has_port_config', 'has_sent_results']) {
    await db.schema.alterTable('devices').dropColumn(col).execute()
  }
  await db.schema.dropTable('reference_ranges').execute()
  await db.schema.dropTable('his_analyte_map').execute()
  await db.schema.dropTable('result_mappings').execute()
  await db.schema.dropTable('profile_members').execute()
  await db.schema.dropTable('profiles').execute()
  await db.schema.dropTable('analytes').execute()
}
```

- [ ] **Step 4: Đăng ký migration**

Trong `apps/core/src/tenant/tenant-migrator.service.ts`, thêm import và mục vào danh sách tĩnh:
```ts
import * as catalogue from './migrations/002-catalogue.js'

const migrations: Record<string, Migration> = {
  '001-initial': initial,
  '002-catalogue': catalogue,
}
```

- [ ] **Step 5: Khai kiểu bảng**

Trong `apps/core/src/tenant/tenant-schema.ts`, thêm các interface bảng mới và mở rộng
hai bảng cũ. Ví dụ cho `analytes`:
```ts
export type AnalyteSource = 'legacy' | 'manual'
export type ProfileSource = 'legacy-declared' | 'legacy-inferred' | 'manual'

export interface AnalytesTable {
  code: string
  name: string
  name_en: ColumnType<string | null, string | null | undefined, string | null>
  category: ColumnType<string | null, string | null | undefined, string | null>
  unit: ColumnType<string | null, string | null | undefined, string | null>
  decimals: ColumnType<number | null, number | null | undefined, number | null>
  normal_range_text: ColumnType<string | null, string | null | undefined, string | null>
  lower_limit: ColumnType<string | null, string | number | null | undefined, string | number | null>
  higher_limit: ColumnType<string | null, string | number | null | undefined, string | number | null>
  comment_lower: ColumnType<string | null, string | null | undefined, string | null>
  comment_higher: ColumnType<string | null, string | null | undefined, string | null>
  comment_gray: ColumnType<string | null, string | null | undefined, string | null>
  criteria: ColumnType<string | null, string | null | undefined, string | null>
  result_is_comment: Generated<boolean>
  print_order: ColumnType<number | null, number | null | undefined, number | null>
  is_profile_head: Generated<boolean>
  source: AnalyteSource
  active: Generated<boolean>
  created_at: Generated<Date>
}
```
Khai tương tự cho `ProfilesTable`, `ProfileMembersTable`, `ResultMappingsTable`,
`HisAnalyteMapTable`, `ReferenceRangesTable`, rồi thêm tất cả vào interface
`TenantDatabase`. Với `DevicesTable` và `DeviceAnalyteMapTable`, thêm các cột mới theo
đúng kiểu đã khai trong migration.

**Lưu ý về `numeric`:** trình điều khiển `pg` trả cột `numeric` dưới dạng **chuỗi** để
không mất độ chính xác. Khai kiểu đọc là `string | null` và chuyển đổi ở tầng service,
đừng khai `number`.

- [ ] **Step 6: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test test/catalogue-schema.test.ts`
Expected: 8 test PASS.

- [ ] **Step 7: Chạy toàn bộ test và kiểm kiểu**

Run: `pnpm test && pnpm typecheck`
Expected: toàn bộ PASS, không test cũ nào đỏ.

- [ ] **Step 8: Commit**

```bash
git add apps/core/src/tenant apps/core/test/catalogue-schema.test.ts
git commit -m "feat(core): catalogue schema with unique channel mapping constraint"
```

---

## Task 2: Đọc và chuẩn hoá dữ liệu nguồn

**Files:**
- Create: `apps/core/src/migration/legacy-source.ts`
- Test: `apps/core/src/migration/legacy-source.test.ts`

**Interfaces:**
- Consumes: không (đọc tệp JSON độc lập)
- Produces:
  - `loadLegacySource(dir: string): LegacySource`
  - type `LegacySource` = `{ analytes: LegacyAnalyte[]; instruments: LegacyInstrument[]; insTests: LegacyInsTest[]; interfaceParas: LegacyInterfacePara[]; resultMappings: LegacyResultMapping[]; profiles: LegacyProfile[]; profileTests: LegacyProfileTest[]; hisMaps: LegacyHisMap[] }`
  - `cleanString(value: unknown): string | null` — cắt khoảng trắng, trả `null` cho chuỗi rỗng

**Ghi chú thiết kế:** lớp này **chỉ đọc và chuẩn hoá**, không phán xét. Việc phát hiện xung đột là của Task 3. Tách như vậy để test được từng phần và để lỗi dữ liệu không lẫn với lỗi đọc tệp.

- [ ] **Step 1: Viết test**

`apps/core/src/migration/legacy-source.test.ts`:
```ts
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanString, loadLegacySource } from './legacy-source.js'

let dir: string

function write(name: string, rows: unknown[]): void {
  writeFileSync(join(dir, name), JSON.stringify(rows), 'utf8')
}

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'lis-legacy-'))
  write('TestCode.json', [])
  write('Instrument.json', [])
  write('InsTest.json', [])
  write('InterfacePara.json', [])
  write('ResultMapping.json', [])
  write('Profile.json', [])
  write('ProfileTest.json', [])
  write('TestCode_HIS.json', [])
})

afterEach(() => {
  rmSync(dir, { recursive: true, force: true })
})

describe('cleanString', () => {
  it('cắt khoảng trắng thừa của cột CHAR trong SQL Server', () => {
    expect(cleanString('141ULeu        ')).toBe('141ULeu')
    expect(cleanString('P    ')).toBe('P')
  })

  it('trả null cho chuỗi rỗng hoặc toàn khoảng trắng', () => {
    expect(cleanString('')).toBeNull()
    expect(cleanString('   ')).toBeNull()
    expect(cleanString(null)).toBeNull()
    expect(cleanString(undefined)).toBeNull()
  })

  it('giữ nguyên ký tự phần trăm trong mã', () => {
    expect(cleanString('034NEU%')).toBe('034NEU%')
  })
})

describe('loadLegacySource', () => {
  it('đọc được danh mục xét nghiệm và cắt khoảng trắng', () => {
    write('TestCode.json', [
      { TestCode: '123AST  ', TestName: 'AST (GOT)', Unit: 'U/L ', GetComment: false },
    ])
    const src = loadLegacySource(dir)
    expect(src.analytes).toHaveLength(1)
    expect(src.analytes[0]!.code).toBe('123AST')
    expect(src.analytes[0]!.unit).toBe('U/L')
  })

  it('đọc ánh xạ mã kênh và cắt khoảng trắng ở cả hai mã', () => {
    write('InsTest.json', [
      { InsID: 1, TestCode: '128LDL ', TestcodeIN: ' 19', Round: 2, Omit: false, AllowInsert: false },
    ])
    const src = loadLegacySource(dir)
    expect(src.insTests[0]).toMatchObject({ insId: 1, analyteCode: '128LDL', channelCode: '19', round: 2 })
  })

  it('đọc luật ghi đè kết quả, bỏ qua dòng có mã rỗng', () => {
    write('ResultMapping.json', [
      { InstrumentID: 19, TestCode: '141ULeu        ', ResultIn: '100', Result: '100', Comment: 'pos 2+', Color: 2 },
      { InstrumentID: 19, TestCode: null, ResultIn: 'x', Result: 'y', Comment: null, Color: 0 },
    ])
    const src = loadLegacySource(dir)
    expect(src.resultMappings).toHaveLength(1)
    expect(src.resultMappings[0]!.analyteCode).toBe('141ULeu')
  })

  it('đọc máy xét nghiệm kèm giao thức', () => {
    write('Instrument.json', [
      { InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', LocationID: 'DK10', Active: true },
    ])
    const src = loadLegacySource(dir)
    expect(src.instruments[0]).toMatchObject({ insId: 1, name: 'AU640', protocol: 'AU', conType: 'C' })
  })

  it('đọc cấu hình cổng', () => {
    write('InterfacePara.json', [
      { HostID: 'DK10.1', InstrumentID: 1, Settings: '9600,8,N,1', CommPort: 6, RemoteIP: null, RemotePort: null },
    ])
    const src = loadLegacySource(dir)
    expect(src.interfaceParas[0]).toMatchObject({ insId: 1, hostId: 'DK10.1', commPort: 6 })
  })

  it('đọc ánh xạ mã HIS', () => {
    write('TestCode_HIS.json', [{ TestCodeHIS: 'A/01', Testcode: '929AG' }])
    const src = loadLegacySource(dir)
    expect(src.hisMaps[0]).toMatchObject({ hisCode: 'A/01', analyteCode: '929AG' })
  })

  it('ném lỗi rõ ràng khi thiếu tệp', () => {
    rmSync(join(dir, 'TestCode.json'))
    expect(() => loadLegacySource(dir)).toThrow(/TestCode\.json/)
  })

  it('ném lỗi rõ ràng khi tệp không phải JSON hợp lệ', () => {
    writeFileSync(join(dir, 'Profile.json'), '{ hỏng', 'utf8')
    expect(() => loadLegacySource(dir)).toThrow(/Profile\.json/)
  })
})
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test src/migration/legacy-source.test.ts`
Expected: FAIL — không tìm thấy `./legacy-source.js`.

- [ ] **Step 3: Viết `legacy-source.ts`**

```ts
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

/**
 * Cắt khoảng trắng thừa và quy chuỗi rỗng về null.
 *
 * Dữ liệu nguồn đến từ cột CHAR của SQL Server nên có đệm khoảng trắng:
 * `ResultMapping.TestCode` chứa "141ULeu        ", `InsTest.Type` chứa "P    ".
 * Không cắt thì mọi so khớp mã sẽ hỏng.
 */
export function cleanString(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const trimmed = value.trim()
  return trimmed.length > 0 ? trimmed : null
}

function cleanNumber(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string') {
    const parsed = Number(value.trim())
    return Number.isFinite(parsed) ? parsed : null
  }
  return null
}

function cleanBool(value: unknown): boolean {
  return value === true
}

export interface LegacyAnalyte {
  code: string
  name: string
  nameEn: string | null
  category: string | null
  unit: string | null
  normalRangeText: string | null
  lowerLimit: number | null
  higherLimit: number | null
  commentLower: string | null
  commentHigher: string | null
  commentGray: string | null
  criteria: string | null
  resultIsComment: boolean
  printOrder: number | null
  isProfileHead: boolean
}

export interface LegacyInstrument {
  insId: number
  name: string
  protocol: string | null
  conType: string | null
  location: string | null
  active: boolean
}

export interface LegacyInsTest {
  insId: number
  analyteCode: string
  channelCode: string
  round: number | null
  allowInsert: boolean
  omit: boolean
  keepHigherSign: boolean
  keepLowerSign: boolean
}

export interface LegacyInterfacePara {
  hostId: string | null
  insId: number
  settings: string | null
  commPort: number | null
  remoteIp: string | null
  remotePort: number | null
}

export interface LegacyResultMapping {
  insId: number
  analyteCode: string
  resultIn: string
  resultOut: string | null
  comment: string | null
  color: number | null
}

export interface LegacyProfile {
  code: string
  name: string
}

export interface LegacyProfileTest {
  profileCode: string
  analyteCode: string
}

export interface LegacyHisMap {
  hisCode: string
  analyteCode: string
}

export interface LegacySource {
  analytes: LegacyAnalyte[]
  instruments: LegacyInstrument[]
  insTests: LegacyInsTest[]
  interfaceParas: LegacyInterfacePara[]
  resultMappings: LegacyResultMapping[]
  profiles: LegacyProfile[]
  profileTests: LegacyProfileTest[]
  hisMaps: LegacyHisMap[]
}

function readRows(dir: string, file: string): Record<string, unknown>[] {
  let text: string
  try {
    text = readFileSync(join(dir, file), 'utf8')
  } catch (error) {
    throw new Error(`không đọc được tệp nguồn ${file}: ${String(error)}`)
  }
  try {
    const parsed: unknown = JSON.parse(text)
    if (Array.isArray(parsed)) return parsed as Record<string, unknown>[]
    if (parsed && typeof parsed === 'object') {
      const first = Object.values(parsed).find((v) => Array.isArray(v))
      if (Array.isArray(first)) return first as Record<string, unknown>[]
    }
    throw new Error('không tìm thấy mảng dữ liệu')
  } catch (error) {
    throw new Error(`tệp nguồn ${file} không phải JSON hợp lệ: ${String(error)}`)
  }
}

export function loadLegacySource(dir: string): LegacySource {
  const analytes: LegacyAnalyte[] = []
  for (const r of readRows(dir, 'TestCode.json')) {
    const code = cleanString(r.TestCode)
    const name = cleanString(r.TestName)
    // Dòng không có mã hoặc không có tên thì không dùng được; bỏ ở đây và để
    // báo cáo đối chiếu ở Task 5 nêu số lượng.
    if (!code || !name) continue
    analytes.push({
      code,
      name,
      nameEn: cleanString(r.TestNameE),
      category: cleanString(r.Category),
      unit: cleanString(r.Unit),
      normalRangeText: cleanString(r.NormalRange),
      lowerLimit: cleanNumber(r.LowerLimit),
      higherLimit: cleanNumber(r.HigherLimit),
      commentLower: cleanString(r.BLLower),
      commentHigher: cleanString(r.BLHigher),
      commentGray: cleanString(r.BLGray),
      criteria: cleanString(r.Criteria),
      resultIsComment: cleanBool(r.GetComment),
      printOrder: cleanNumber(r.PrintOrder),
      isProfileHead: cleanBool(r.Profile) || cleanBool(r.TestHead),
    })
  }

  const instruments: LegacyInstrument[] = []
  for (const r of readRows(dir, 'Instrument.json')) {
    const insId = cleanNumber(r.InsID)
    const name = cleanString(r.InsName)
    if (insId === null || !name) continue
    instruments.push({
      insId,
      name,
      protocol: cleanString(r.ProtocolID),
      conType: cleanString(r.ConType),
      location: cleanString(r.LocationID),
      active: cleanBool(r.Active),
    })
  }

  const insTests: LegacyInsTest[] = []
  for (const r of readRows(dir, 'InsTest.json')) {
    const insId = cleanNumber(r.InsID)
    const analyteCode = cleanString(r.TestCode)
    const channelCode = cleanString(r.TestcodeIN)
    if (insId === null || !analyteCode || !channelCode) continue
    insTests.push({
      insId,
      analyteCode,
      channelCode,
      round: cleanNumber(r.Round),
      allowInsert: cleanBool(r.AllowInsert),
      omit: cleanBool(r.Omit),
      keepHigherSign: cleanBool(r.VALhigher),
      keepLowerSign: cleanBool(r.VALlower),
    })
  }

  const interfaceParas: LegacyInterfacePara[] = []
  for (const r of readRows(dir, 'InterfacePara.json')) {
    const insId = cleanNumber(r.InstrumentID)
    if (insId === null) continue
    interfaceParas.push({
      hostId: cleanString(r.HostID),
      insId,
      settings: cleanString(r.Settings),
      commPort: cleanNumber(r.CommPort),
      remoteIp: cleanString(r.RemoteIP),
      remotePort: cleanNumber(r.RemotePort),
    })
  }

  const resultMappings: LegacyResultMapping[] = []
  for (const r of readRows(dir, 'ResultMapping.json')) {
    const insId = cleanNumber(r.InstrumentID)
    const analyteCode = cleanString(r.TestCode)
    const resultIn = cleanString(r.ResultIn)
    if (insId === null || !analyteCode || !resultIn) continue
    resultMappings.push({
      insId,
      analyteCode,
      resultIn,
      resultOut: cleanString(r.Result),
      comment: cleanString(r.Comment),
      color: cleanNumber(r.Color),
    })
  }

  const profiles: LegacyProfile[] = []
  for (const r of readRows(dir, 'Profile.json')) {
    const code = cleanString(r.ProfileID)
    const name = cleanString(r.ProfileName)
    if (!code || !name) continue
    profiles.push({ code, name })
  }

  const profileTests: LegacyProfileTest[] = []
  for (const r of readRows(dir, 'ProfileTest.json')) {
    const profileCode = cleanString(r.ProfileID)
    const analyteCode = cleanString(r.TestCode)
    if (!profileCode || !analyteCode) continue
    profileTests.push({ profileCode, analyteCode })
  }

  const hisMaps: LegacyHisMap[] = []
  for (const r of readRows(dir, 'TestCode_HIS.json')) {
    const hisCode = cleanString(r.TestCodeHIS)
    const analyteCode = cleanString(r.Testcode)
    if (!hisCode || !analyteCode) continue
    hisMaps.push({ hisCode, analyteCode })
  }

  return {
    analytes,
    instruments,
    insTests,
    interfaceParas,
    resultMappings,
    profiles,
    profileTests,
    hisMaps,
  }
}
```

- [ ] **Step 4: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test src/migration/legacy-source.test.ts`
Expected: 11 test PASS.

- [ ] **Step 5: Chạy thử trên dữ liệu thật**

Viết một script tạm (xoá sau khi chạy) nạp `docs/khao-sat/seed/du-lieu/` và in số dòng
đọc được mỗi loại. Đối chiếu với bảng số liệu ở đầu kế hoạch: 404 xét nghiệm, 20 máy,
292 ánh xạ, 9 cấu hình cổng, 56 luật, 2 nhóm, 33 thành viên nhóm, 835 mã HIS.

Ghi số thật quan sát được vào report — nếu lệch, đó là thông tin quan trọng cho Task 3.

- [ ] **Step 6: Commit**

```bash
git add apps/core/src/migration
git commit -m "feat(core): read and normalise legacy LIS catalogue files"
```

---

## Task 3: Phát hiện xung đột và dữ liệu mồ côi

**Files:**
- Create: `apps/core/src/migration/legacy-validator.ts`
- Test: `apps/core/src/migration/legacy-validator.test.ts`

**Interfaces:**
- Consumes: Task 2 (`LegacySource` và các kiểu `Legacy*`)
- Produces:
  - `validateLegacySource(src: LegacySource): ValidationReport`
  - type `ChannelConflict` = `{ insId: number; channelCode: string; candidates: string[] }`
  - type `OrphanRef` = `{ kind: 'ins-test' | 'his-map' | 'result-mapping' | 'profile-member'; ref: string; analyteCode: string }`
  - type `ValidationReport` = `{ channelConflicts: ChannelConflict[]; orphans: OrphanRef[]; profilesWithoutMembers: string[]; wildcardCodes: string[]; duplicateAnalyteCodes: string[] }`

**Ghi chú thiết kế:** lớp này **chỉ phát hiện, không sửa và không quyết**. Nó không được phép bỏ dòng nào — việc quyết định là của người, ở Task 4. Đây là điểm khác biệt cốt lõi với hệ cũ: hệ cũ gỡ nhập nhằng bằng `TOP 1` theo alphabet và không báo cho ai.

- [ ] **Step 1: Viết test**

`apps/core/src/migration/legacy-validator.test.ts`:
```ts
import { describe, expect, it } from 'vitest'
import type { LegacySource } from './legacy-source.js'
import { validateLegacySource } from './legacy-validator.js'

function emptySource(): LegacySource {
  return {
    analytes: [],
    instruments: [],
    insTests: [],
    interfaceParas: [],
    resultMappings: [],
    profiles: [],
    profileTests: [],
    hisMaps: [],
  }
}

function analyte(code: string) {
  return {
    code,
    name: code,
    nameEn: null,
    category: null,
    unit: null,
    normalRangeText: null,
    lowerLimit: null,
    higherLimit: null,
    commentLower: null,
    commentHigher: null,
    commentGray: null,
    criteria: null,
    resultIsComment: false,
    printOrder: null,
    isProfileHead: false,
  }
}

function insTest(insId: number, analyteCode: string, channelCode: string) {
  return {
    insId,
    analyteCode,
    channelCode,
    round: null,
    allowInsert: false,
    omit: false,
    keepHigherSign: false,
    keepLowerSign: false,
  }
}

describe('validateLegacySource', () => {
  it('phát hiện hai xét nghiệm cùng trỏ tới một mã kênh trên cùng máy', () => {
    const src = emptySource()
    src.analytes = [analyte('128LDL'), analyte('244ACo')]
    src.insTests = [insTest(1, '128LDL', '19'), insTest(1, '244ACo', '19')]

    const report = validateLegacySource(src)
    expect(report.channelConflicts).toHaveLength(1)
    expect(report.channelConflicts[0]).toMatchObject({ insId: 1, channelCode: '19' })
    expect(report.channelConflicts[0]!.candidates.sort()).toEqual(['128LDL', '244ACo'])
  })

  it('không coi là xung đột khi cùng mã kênh nhưng khác máy', () => {
    const src = emptySource()
    src.analytes = [analyte('120GLUS')]
    src.insTests = [insTest(1, '120GLUS', '01'), insTest(15, '120GLUS', '01')]
    expect(validateLegacySource(src).channelConflicts).toEqual([])
  })

  it('phát hiện ánh xạ trỏ tới xét nghiệm không có trong danh mục', () => {
    const src = emptySource()
    src.analytes = [analyte('128LDL')]
    src.insTests = [insTest(9, '233GLU', 'GLU')]

    const report = validateLegacySource(src)
    expect(report.orphans).toHaveLength(1)
    expect(report.orphans[0]).toMatchObject({ kind: 'ins-test', analyteCode: '233GLU' })
  })

  it('phát hiện mã HIS trỏ tới xét nghiệm không tồn tại', () => {
    const src = emptySource()
    src.analytes = [analyte('929AG')]
    src.hisMaps = [
      { hisCode: 'A/01', analyteCode: '929AG' },
      { hisCode: 'B/02', analyteCode: '7.82E+04' },
    ]

    const report = validateLegacySource(src)
    expect(report.orphans.filter((o) => o.kind === 'his-map')).toHaveLength(1)
    expect(report.orphans[0]!.analyteCode).toBe('7.82E+04')
  })

  it('phát hiện luật ghi đè trỏ tới xét nghiệm không tồn tại', () => {
    const src = emptySource()
    src.analytes = [analyte('141ULeu')]
    src.resultMappings = [
      { insId: 19, analyteCode: '141ULeu', resultIn: '100', resultOut: '100', comment: null, color: 2 },
      { insId: 19, analyteCode: 'khong-co', resultIn: 'x', resultOut: 'y', comment: null, color: 0 },
    ]
    const report = validateLegacySource(src)
    expect(report.orphans.filter((o) => o.kind === 'result-mapping')).toHaveLength(1)
  })

  it('phát hiện nhóm không có thành viên nào', () => {
    const src = emptySource()
    src.analytes = [analyte('027TB')]
    src.profiles = [
      { code: 'TPTMau22', name: 'Tổng phân tích máu' },
      { code: 'TRONG', name: 'Nhóm rỗng' },
    ]
    src.profileTests = [{ profileCode: 'TPTMau22', analyteCode: '027TB' }]

    expect(validateLegacySource(src).profilesWithoutMembers).toEqual(['TRONG'])
  })

  it('liệt kê các mã chứa ký tự đại diện để cảnh báo', () => {
    const src = emptySource()
    src.analytes = [analyte('034NEU%'), analyte('123AST')]
    expect(validateLegacySource(src).wildcardCodes).toEqual(['034NEU%'])
  })

  it('phát hiện mã xét nghiệm bị lặp trong danh mục', () => {
    const src = emptySource()
    src.analytes = [analyte('123AST'), analyte('123AST')]
    expect(validateLegacySource(src).duplicateAnalyteCodes).toEqual(['123AST'])
  })

  it('nguồn sạch thì mọi danh sách đều rỗng', () => {
    const src = emptySource()
    src.analytes = [analyte('123AST')]
    src.insTests = [insTest(1, '123AST', '01')]
    const report = validateLegacySource(src)
    expect(report.channelConflicts).toEqual([])
    expect(report.orphans).toEqual([])
    expect(report.profilesWithoutMembers).toEqual([])
    expect(report.duplicateAnalyteCodes).toEqual([])
  })
})
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test src/migration/legacy-validator.test.ts`
Expected: FAIL — không tìm thấy `./legacy-validator.js`.

- [ ] **Step 3: Viết `legacy-validator.ts`**

```ts
import type { LegacySource } from './legacy-source.js'

export interface ChannelConflict {
  insId: number
  channelCode: string
  candidates: string[]
}

export interface OrphanRef {
  kind: 'ins-test' | 'his-map' | 'result-mapping' | 'profile-member'
  /** Mô tả chỗ tham chiếu, để người đọc báo cáo lần ra được dòng nguồn. */
  ref: string
  analyteCode: string
}

export interface ValidationReport {
  channelConflicts: ChannelConflict[]
  orphans: OrphanRef[]
  profilesWithoutMembers: string[]
  wildcardCodes: string[]
  duplicateAnalyteCodes: string[]
}

/**
 * Phát hiện mọi chỗ dữ liệu nguồn không nhất quán.
 *
 * Lớp này CHỈ phát hiện — không sửa, không bỏ dòng, không quyết. Hệ cũ gỡ nhập nhằng
 * mã kênh bằng `TOP 1` theo thứ tự alphabet và không báo cho ai; đó là cách kết quả
 * nồng độ cồn có thể về nhầm ô LDL. Ở đây mọi xung đột đều nổi lên để người quyết.
 */
export function validateLegacySource(src: LegacySource): ValidationReport {
  const analyteCodes = new Set<string>()
  const duplicateAnalyteCodes: string[] = []
  for (const a of src.analytes) {
    if (analyteCodes.has(a.code)) duplicateAnalyteCodes.push(a.code)
    else analyteCodes.add(a.code)
  }

  const byChannel = new Map<string, Set<string>>()
  for (const t of src.insTests) {
    const key = `${t.insId}${t.channelCode}`
    const set = byChannel.get(key) ?? new Set<string>()
    set.add(t.analyteCode)
    byChannel.set(key, set)
  }

  const channelConflicts: ChannelConflict[] = []
  for (const [key, candidates] of byChannel) {
    if (candidates.size < 2) continue
    const [insIdText, channelCode] = key.split('')
    channelConflicts.push({
      insId: Number(insIdText),
      channelCode: channelCode ?? '',
      candidates: [...candidates].sort(),
    })
  }

  const orphans: OrphanRef[] = []
  for (const t of src.insTests) {
    if (!analyteCodes.has(t.analyteCode)) {
      orphans.push({
        kind: 'ins-test',
        ref: `máy ${t.insId} kênh ${t.channelCode}`,
        analyteCode: t.analyteCode,
      })
    }
  }
  for (const h of src.hisMaps) {
    if (!analyteCodes.has(h.analyteCode)) {
      orphans.push({ kind: 'his-map', ref: `mã HIS ${h.hisCode}`, analyteCode: h.analyteCode })
    }
  }
  for (const m of src.resultMappings) {
    if (!analyteCodes.has(m.analyteCode)) {
      orphans.push({
        kind: 'result-mapping',
        ref: `máy ${m.insId} giá trị ${m.resultIn}`,
        analyteCode: m.analyteCode,
      })
    }
  }
  for (const p of src.profileTests) {
    if (!analyteCodes.has(p.analyteCode)) {
      orphans.push({
        kind: 'profile-member',
        ref: `nhóm ${p.profileCode}`,
        analyteCode: p.analyteCode,
      })
    }
  }

  const profilesWithMembers = new Set(src.profileTests.map((p) => p.profileCode))
  const profilesWithoutMembers = src.profiles
    .map((p) => p.code)
    .filter((code) => !profilesWithMembers.has(code))

  // Ký tự `%` là một phần của mã, không phải ký tự đại diện. Liệt kê để cảnh báo
  // người viết truy vấn sau này không dùng LIKE.
  const wildcardCodes = src.analytes
    .map((a) => a.code)
    .filter((code) => code.includes('%') || code.includes('_'))

  return {
    channelConflicts,
    orphans,
    profilesWithoutMembers,
    wildcardCodes,
    duplicateAnalyteCodes,
  }
}
```

- [ ] **Step 4: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test src/migration/legacy-validator.test.ts`
Expected: 9 test PASS.

- [ ] **Step 5: Chạy trên dữ liệu thật và đối chiếu**

Viết script tạm nạp `docs/khao-sat/seed/du-lieu/` rồi in báo cáo kiểm tra. Kết quả **phải khớp** những con số đã đếm trước:

- 7 cặp `(máy, mã kênh)` xung đột, trong đó có máy 1 kênh `19` với `128LDL` và `244ACo`
- 2 mã mồ côi từ `InsTest`: `233GLU`, `hc12`
- 8 mã mồ côi từ `TestCode_HIS`, gồm `7.82E+04`
- 8 mã chứa ký tự đại diện

Nếu lệch, **dừng lại và báo** — hoặc bộ đọc ở Task 2 sai, hoặc dữ liệu nguồn đã đổi. Đừng chỉnh test cho khớp. Xoá script sau khi chạy, ghi kết quả vào report.

- [ ] **Step 6: Commit**

```bash
git add apps/core/src/migration
git commit -m "feat(core): detect conflicts and orphans in legacy catalogue data"
```

---

## Task 4: Tệp quyết định

**Files:**
- Create: `apps/core/src/migration/legacy-decisions.ts`
- Test: `apps/core/src/migration/legacy-decisions.test.ts`

**Interfaces:**
- Consumes: Task 3 (`ValidationReport`, `ChannelConflict`)
- Produces:
  - `buildDecisionTemplate(report: ValidationReport): DecisionFile`
  - `loadDecisions(path: string): DecisionFile`
  - `saveDecisions(path: string, file: DecisionFile): void`
  - `resolveChannel(decisions: DecisionFile, insId: number, channelCode: string): Resolution`
  - type `Resolution` = `{ kind: 'keep'; analyteCode: string } | { kind: 'skip' } | { kind: 'undecided' }`

**Ghi chú thiết kế:** khi một xung đột chưa được quyết, kết quả là `undecided` và dòng đó **không được di trú** — chứ không phải chọn bừa một cái. Đây là toàn bộ lý do task này tồn tại.

- [ ] **Step 1: Viết test**

`apps/core/src/migration/legacy-decisions.test.ts`:
```ts
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import {
  buildDecisionTemplate,
  loadDecisions,
  resolveChannel,
  saveDecisions,
} from './legacy-decisions.js'
import type { ValidationReport } from './legacy-validator.js'

let dir: string

function report(): ValidationReport {
  return {
    channelConflicts: [
      { insId: 1, channelCode: '19', candidates: ['128LDL', '244ACo'] },
      { insId: 2, channelCode: '3', candidates: ['931Cortisol', '932931Cortisol'] },
    ],
    orphans: [],
    profilesWithoutMembers: [],
    wildcardCodes: [],
    duplicateAnalyteCodes: [],
  }
}

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'lis-decision-'))
})

afterEach(() => {
  rmSync(dir, { recursive: true, force: true })
})

describe('buildDecisionTemplate', () => {
  it('tạo một mục cho mỗi xung đột, chưa ai quyết', () => {
    const file = buildDecisionTemplate(report())
    expect(file.channels).toHaveLength(2)
    expect(file.channels[0]).toMatchObject({ insId: 1, channelCode: '19', chosen: null })
    expect(file.channels[0]!.candidates).toEqual(['128LDL', '244ACo'])
  })

  it('giữ nguyên lựa chọn đã quyết khi tạo lại từ báo cáo mới', () => {
    const existing = buildDecisionTemplate(report())
    existing.channels[0]!.chosen = '128LDL'
    const merged = buildDecisionTemplate(report(), existing)
    expect(merged.channels[0]!.chosen).toBe('128LDL')
    expect(merged.channels[1]!.chosen).toBeNull()
  })
})

describe('lưu và đọc lại', () => {
  it('giữ nguyên nội dung', () => {
    const path = join(dir, 'quyet-dinh.json')
    const file = buildDecisionTemplate(report())
    file.channels[0]!.chosen = '244ACo'
    saveDecisions(path, file)
    expect(loadDecisions(path).channels[0]!.chosen).toBe('244ACo')
  })

  it('ghi ra JSON đọc được bằng trình soạn thảo thường', () => {
    const path = join(dir, 'quyet-dinh.json')
    saveDecisions(path, buildDecisionTemplate(report()))
    const text = require('node:fs').readFileSync(path, 'utf8')
    expect(text).toContain('\n')
    expect(text).toContain('channelCode')
  })
})

describe('resolveChannel', () => {
  it('trả về xét nghiệm đã chọn', () => {
    const file = buildDecisionTemplate(report())
    file.channels[0]!.chosen = '128LDL'
    expect(resolveChannel(file, 1, '19')).toEqual({ kind: 'keep', analyteCode: '128LDL' })
  })

  it('cho phép quyết định bỏ hẳn mã kênh đó', () => {
    const file = buildDecisionTemplate(report())
    file.channels[0]!.chosen = '__BO_QUA__'
    expect(resolveChannel(file, 1, '19')).toEqual({ kind: 'skip' })
  })

  it('chưa quyết thì trả về undecided, KHÔNG tự chọn', () => {
    const file = buildDecisionTemplate(report())
    expect(resolveChannel(file, 1, '19')).toEqual({ kind: 'undecided' })
  })

  it('từ chối lựa chọn không nằm trong danh sách ứng viên', () => {
    const file = buildDecisionTemplate(report())
    file.channels[0]!.chosen = 'khong-co-trong-danh-sach'
    expect(() => resolveChannel(file, 1, '19')).toThrow(/không nằm trong danh sách/)
  })

  it('mã kênh không có xung đột thì coi như đã quyết ngầm', () => {
    const file = buildDecisionTemplate(report())
    expect(resolveChannel(file, 99, 'XX')).toEqual({ kind: 'undecided' })
  })
})
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test src/migration/legacy-decisions.test.ts`
Expected: FAIL — không tìm thấy `./legacy-decisions.js`.

- [ ] **Step 3: Viết `legacy-decisions.ts`**

```ts
import { readFileSync, writeFileSync } from 'node:fs'
import type { ValidationReport } from './legacy-validator.js'

/** Giá trị đặc biệt để người vận hành nói "bỏ hẳn mã kênh này". */
export const SKIP_MARKER = '__BO_QUA__'

export interface ChannelDecision {
  insId: number
  channelCode: string
  candidates: string[]
  /** null nghĩa là chưa ai quyết. */
  chosen: string | null
  note?: string
}

export interface DecisionFile {
  channels: ChannelDecision[]
}

export type Resolution =
  | { kind: 'keep'; analyteCode: string }
  | { kind: 'skip' }
  | { kind: 'undecided' }

/**
 * Sinh khung tệp quyết định từ báo cáo kiểm tra.
 *
 * Nếu truyền vào tệp cũ, mọi lựa chọn đã quyết được giữ nguyên — để chạy lại công cụ
 * sau khi dữ liệu nguồn cập nhật mà không mất công quyết lại từ đầu.
 */
export function buildDecisionTemplate(
  report: ValidationReport,
  existing?: DecisionFile,
): DecisionFile {
  const previous = new Map<string, string | null>()
  for (const c of existing?.channels ?? []) {
    previous.set(`${c.insId}${c.channelCode}`, c.chosen)
  }

  return {
    channels: report.channelConflicts.map((conflict) => ({
      insId: conflict.insId,
      channelCode: conflict.channelCode,
      candidates: conflict.candidates,
      chosen: previous.get(`${conflict.insId}${conflict.channelCode}`) ?? null,
    })),
  }
}

export function saveDecisions(path: string, file: DecisionFile): void {
  writeFileSync(path, JSON.stringify(file, null, 2), 'utf8')
}

export function loadDecisions(path: string): DecisionFile {
  const parsed: unknown = JSON.parse(readFileSync(path, 'utf8'))
  if (!parsed || typeof parsed !== 'object' || !Array.isArray((parsed as DecisionFile).channels)) {
    throw new Error(`tệp quyết định không hợp lệ: ${path}`)
  }
  return parsed as DecisionFile
}

/**
 * Tra xem một mã kênh đang xung đột đã được quyết chưa.
 *
 * Chưa quyết thì trả về `undecided` và dòng đó KHÔNG được di trú. Tuyệt đối không tự
 * chọn một ứng viên — đó chính là hành vi của hệ cũ mà khối này sinh ra để loại bỏ.
 */
export function resolveChannel(
  decisions: DecisionFile,
  insId: number,
  channelCode: string,
): Resolution {
  const entry = decisions.channels.find(
    (c) => c.insId === insId && c.channelCode === channelCode,
  )
  if (!entry || entry.chosen === null) return { kind: 'undecided' }
  if (entry.chosen === SKIP_MARKER) return { kind: 'skip' }
  if (!entry.candidates.includes(entry.chosen)) {
    throw new Error(
      `lựa chọn "${entry.chosen}" cho máy ${insId} kênh ${channelCode} không nằm trong danh sách ứng viên: ${entry.candidates.join(', ')}`,
    )
  }
  return { kind: 'keep', analyteCode: entry.chosen }
}
```

- [ ] **Step 4: Sửa lời gọi `require` trong test**

Test ở Step 1 dùng `require('node:fs')` trong một tệp ESM — nó sẽ ném `require is not defined`. Đổi thành `import { readFileSync } from 'node:fs'` ở đầu tệp và dùng trực tiếp.

- [ ] **Step 5: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test src/migration/legacy-decisions.test.ts`
Expected: 8 test PASS.

- [ ] **Step 6: Commit**

```bash
git add apps/core/src/migration
git commit -m "feat(core): decision file for ambiguous legacy channel mappings"
```

---

## Task 5: Ghi vào database và lệnh di trú

**Files:**
- Create: `apps/core/src/migration/legacy-writer.ts`
- Create: `apps/core/src/migration/legacy-report.ts`
- Create: `apps/core/src/migration/migrate-legacy.command.ts`
- Modify: `apps/core/src/admin-cli.ts` (thêm lệnh `migrate-legacy`)
- Test: `apps/core/test/migrate-legacy.test.ts`

**Interfaces:**
- Consumes: Task 1 (`TenantDatabase`), Task 2 (`LegacySource`), Task 3 (`ValidationReport`), Task 4 (`DecisionFile`, `resolveChannel`), và `TenantRegistryService` từ giai đoạn 1
- Produces:
  - `writeLegacyCatalogue(db: Kysely<TenantDatabase>, src: LegacySource, decisions: DecisionFile, agentId: string): Promise<MigrationOutcome>`
  - type `MigrationOutcome` = `{ counts: Record<string, { read: number; written: number; skipped: number }>; skipped: SkippedRow[] }`
  - type `SkippedRow` = `{ table: string; ref: string; reason: string }`
  - `formatReport(validation: ValidationReport, outcome: MigrationOutcome | null): string`

**Ghi chú thiết kế:** toàn bộ việc ghi nằm trong **một giao dịch duy nhất**. Hỏng giữa chừng thì không để lại trạng thái nửa vời — một danh mục di trú được một nửa còn tệ hơn không di trú, vì người vận hành sẽ tưởng là xong.

**Về `agentId`:** bảng `devices` từ giai đoạn 1 yêu cầu `agent_id`. Lúc di trú chưa có Agent nào chạy, nên lệnh tạo một bản ghi `agents` giữ chỗ với `hostname = 'di-tru-he-cu'`. Khi Agent thật kết nối lần đầu, nó tạo bản ghi riêng của mình; máy sẽ được gán lại ở khối 2B khi kỹ thuật viên khai báo cổng. Ghi rõ điều này trong báo cáo để người vận hành không bối rối.

- [ ] **Step 1: Viết test**

`apps/core/test/migrate-legacy.test.ts`:
```ts
import 'reflect-metadata'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
import { AppModule } from '../src/app.module.js'
import { buildDecisionTemplate } from '../src/migration/legacy-decisions.js'
import { loadLegacySource } from '../src/migration/legacy-source.js'
import { validateLegacySource } from '../src/migration/legacy-validator.js'
import { writeLegacyCatalogue } from '../src/migration/legacy-writer.js'
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

function blankSource(): void {
  for (const f of [
    'TestCode.json',
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

async function runMigration(tenant: string): Promise<Awaited<ReturnType<typeof writeLegacyCatalogue>>> {
  const src = loadLegacySource(dir)
  const decisions = buildDecisionTemplate(validateLegacySource(src))
  const db = await registry.forTenant(tenant)
  const agent = await db
    .insertInto('agents')
    .values({
      agent_key_id: crypto.randomUUID(),
      hostname: 'di-tru-he-cu',
      agent_version: 'migration',
    })
    .returningAll()
    .executeTakeFirstOrThrow()
  return writeLegacyCatalogue(db, src, decisions, agent.id)
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

  it('bỏ qua mã kênh xung đột chưa được quyết, và ghi rõ lý do', async () => {
    write('TestCode.json', [
      { TestCode: '128LDL', TestName: 'LDL', GetComment: false },
      { TestCode: '244ACo', TestName: 'Nồng độ cồn', GetComment: false },
    ])
    write('Instrument.json', [{ InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', Active: true }])
    write('InsTest.json', [
      { InsID: 1, TestCode: '128LDL', TestcodeIN: '19' },
      { InsID: 1, TestCode: '244ACo', TestcodeIN: '19' },
    ])
    tenantId = await freshTenant('mig2')
    const outcome = await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    const maps = await db.selectFrom('device_analyte_map').selectAll().execute()
    expect(maps).toHaveLength(0)
    expect(outcome.counts.device_analyte_map.skipped).toBe(2)
    expect(outcome.skipped.some((s) => /chưa được quyết/.test(s.reason))).toBe(true)
  })

  it('ghi mã kênh khi đã có quyết định', async () => {
    write('TestCode.json', [
      { TestCode: '128LDL', TestName: 'LDL', GetComment: false },
      { TestCode: '244ACo', TestName: 'Nồng độ cồn', GetComment: false },
    ])
    write('Instrument.json', [{ InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', Active: true }])
    write('InsTest.json', [
      { InsID: 1, TestCode: '128LDL', TestcodeIN: '19' },
      { InsID: 1, TestCode: '244ACo', TestcodeIN: '19' },
    ])
    tenantId = await freshTenant('mig3')

    const src = loadLegacySource(dir)
    const decisions = buildDecisionTemplate(validateLegacySource(src))
    decisions.channels[0]!.chosen = '244ACo'

    const db = await registry.forTenant(tenantId)
    const agent = await db
      .insertInto('agents')
      .values({ agent_key_id: crypto.randomUUID(), hostname: 'di-tru-he-cu', agent_version: 'migration' })
      .returningAll()
      .executeTakeFirstOrThrow()
    await writeLegacyCatalogue(db, src, decisions, agent.id)

    const maps = await db.selectFrom('device_analyte_map').selectAll().execute()
    expect(maps).toHaveLength(1)
    expect(maps[0]!.analyte_code).toBe('244ACo')
  })

  it('bỏ qua ánh xạ trỏ tới xét nghiệm không tồn tại', async () => {
    write('TestCode.json', [{ TestCode: '120GLUS', TestName: 'Glucose', GetComment: false }])
    write('Instrument.json', [{ InsID: 9, InsName: 'EBA200', ProtocolID: 'ASTM', ConType: 'C', Active: true }])
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
    write('InterfacePara.json', [{ HostID: 'DK10.1', InstrumentID: 1, Settings: '9600,8,N,1', CommPort: 6 }])
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
    await runMigration(tenantId)

    const db = await registry.forTenant(tenantId)
    const members = await db.selectFrom('profile_members').selectAll().execute()
    expect(members.map((m) => m.analyte_code)).toEqual(['141ULeu'])
  })

  it('không để lại gì khi giao dịch hỏng giữa chừng', async () => {
    write('TestCode.json', [{ TestCode: '123AST', TestName: 'AST', GetComment: false }])
    // Mã HIS trùng nhau sẽ vi phạm khoá chính his_analyte_map và làm hỏng giao dịch.
    write('TestCode_HIS.json', [
      { TestCodeHIS: 'A/01', Testcode: '123AST' },
      { TestCodeHIS: 'A/01', Testcode: '123AST' },
    ])
    tenantId = await freshTenant('mig7')

    await expect(runMigration(tenantId)).rejects.toThrow()

    const db = await registry.forTenant(tenantId)
    expect(await db.selectFrom('analytes').selectAll().execute()).toHaveLength(0)
  })

  it('chạy hai lần trên database trống cho kết quả giống hệt nhau', async () => {
    write('TestCode.json', [{ TestCode: '123AST', TestName: 'AST', GetComment: false }])

    const first = await freshTenant('mig8a')
    const outcomeA = await runMigration(first)
    const second = await freshTenant('mig8b')
    const outcomeB = await runMigration(second)

    expect(outcomeA.counts).toEqual(outcomeB.counts)
  })
})
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test test/migrate-legacy.test.ts`
Expected: FAIL — không tìm thấy `legacy-writer.js`.

- [ ] **Step 3: Viết `legacy-writer.ts`**

```ts
import type { Kysely } from 'kysely'
import type { DecisionFile } from './legacy-decisions.js'
import { resolveChannel } from './legacy-decisions.js'
import type { LegacySource } from './legacy-source.js'
import type { TenantDatabase } from '../tenant/tenant-schema.js'

export interface SkippedRow {
  table: string
  ref: string
  reason: string
}

export interface MigrationOutcome {
  counts: Record<string, { read: number; written: number; skipped: number }>
  skipped: SkippedRow[]
}

/**
 * Ghi toàn bộ danh mục hệ cũ vào database nghiệp vụ của một cơ sở.
 *
 * Mọi thao tác nằm trong MỘT giao dịch. Hỏng giữa chừng thì không để lại gì — một danh
 * mục di trú được một nửa còn tệ hơn không di trú, vì người vận hành sẽ tưởng là xong.
 */
export async function writeLegacyCatalogue(
  db: Kysely<TenantDatabase>,
  src: LegacySource,
  decisions: DecisionFile,
  agentId: string,
): Promise<MigrationOutcome> {
  const skipped: SkippedRow[] = []
  const counts: MigrationOutcome['counts'] = {}
  const bump = (table: string, field: 'read' | 'written' | 'skipped', by = 1): void => {
    counts[table] ??= { read: 0, written: 0, skipped: 0 }
    counts[table][field] += by
  }

  return db.transaction().execute(async (trx) => {
    // --- Danh mục xét nghiệm ---
    const analyteCodes = new Set<string>()
    bump('analytes', 'read', src.analytes.length)
    for (const a of src.analytes) {
      if (analyteCodes.has(a.code)) {
        skipped.push({ table: 'analytes', ref: a.code, reason: 'mã bị lặp trong nguồn' })
        bump('analytes', 'skipped')
        continue
      }
      analyteCodes.add(a.code)
      await trx
        .insertInto('analytes')
        .values({
          code: a.code,
          name: a.name,
          name_en: a.nameEn,
          category: a.category,
          unit: a.unit,
          normal_range_text: a.normalRangeText,
          lower_limit: a.lowerLimit,
          higher_limit: a.higherLimit,
          comment_lower: a.commentLower,
          comment_higher: a.commentHigher,
          comment_gray: a.commentGray,
          criteria: a.criteria,
          result_is_comment: a.resultIsComment,
          print_order: a.printOrder,
          is_profile_head: a.isProfileHead,
          source: 'legacy',
        })
        .execute()
      bump('analytes', 'written')
    }

    // --- Máy xét nghiệm ---
    const portConfigured = new Set(src.interfaceParas.map((p) => p.insId))
    const deviceIdByInsId = new Map<number, string>()
    bump('devices', 'read', src.instruments.length)
    for (const ins of src.instruments) {
      const row = await trx
        .insertInto('devices')
        .values({
          agent_id: agentId,
          device_key: `legacy-${ins.insId}`,
          name: ins.name,
          transport: ins.conType === 'S' ? 'tcp' : 'serial',
          driver: 'unknown',
          legacy_ins_id: ins.insId,
          protocol: ins.protocol,
          location: ins.location,
          has_port_config: portConfigured.has(ins.insId),
          // Dấu vết "từng gửi kết quả" nằm ở bảng Result_OUT của hệ cũ, không có trong
          // bộ tệp trích. Để mặc định false; kỹ thuật viên bật lại ở màn hình quản lý.
          has_sent_results: false,
        })
        .returning('id')
        .executeTakeFirstOrThrow()
      deviceIdByInsId.set(ins.insId, row.id)
      bump('devices', 'written')
    }

    // --- Ánh xạ mã kênh ---
    const conflicted = new Set(
      decisions.channels.map((c) => `${c.insId} ${c.channelCode}`),
    )
    bump('device_analyte_map', 'read', src.insTests.length)
    for (const t of src.insTests) {
      const ref = `máy ${t.insId} kênh ${t.channelCode} → ${t.analyteCode}`
      const deviceId = deviceIdByInsId.get(t.insId)
      if (!deviceId) {
        skipped.push({ table: 'device_analyte_map', ref, reason: 'máy không có trong danh sách' })
        bump('device_analyte_map', 'skipped')
        continue
      }
      if (!analyteCodes.has(t.analyteCode)) {
        skipped.push({
          table: 'device_analyte_map',
          ref,
          reason: `xét nghiệm ${t.analyteCode} không có trong danh mục`,
        })
        bump('device_analyte_map', 'skipped')
        continue
      }
      if (conflicted.has(`${t.insId} ${t.channelCode}`)) {
        const decision = resolveChannel(decisions, t.insId, t.channelCode)
        if (decision.kind === 'undecided') {
          skipped.push({
            table: 'device_analyte_map',
            ref,
            reason: 'mã kênh có nhiều xét nghiệm, chưa được quyết',
          })
          bump('device_analyte_map', 'skipped')
          continue
        }
        if (decision.kind === 'skip' || decision.analyteCode !== t.analyteCode) {
          skipped.push({
            table: 'device_analyte_map',
            ref,
            reason:
              decision.kind === 'skip'
                ? 'người vận hành quyết bỏ mã kênh này'
                : `người vận hành chọn ${decision.analyteCode}`,
          })
          bump('device_analyte_map', 'skipped')
          continue
        }
      }
      await trx
        .insertInto('device_analyte_map')
        .values({
          device_id: deviceId,
          channel_code: t.channelCode,
          analyte_code: t.analyteCode,
          round: t.round,
          allow_insert: t.allowInsert,
          omit: t.omit,
          keep_higher_sign: t.keepHigherSign,
          keep_lower_sign: t.keepLowerSign,
        })
        .execute()
      bump('device_analyte_map', 'written')
    }

    // --- Nhóm cha con ---
    bump('profiles', 'read', src.profiles.length)
    for (const p of src.profiles) {
      await trx
        .insertInto('profiles')
        .values({ code: p.code, name: p.name, source: 'legacy-declared' })
        .execute()
      bump('profiles', 'written')
    }

    const headCodes = new Set(src.analytes.filter((a) => a.isProfileHead).map((a) => a.code))
    const profileCodes = new Set(src.profiles.map((p) => p.code))
    bump('profile_members', 'read', src.profileTests.length)
    let order = 0
    for (const m of src.profileTests) {
      const ref = `nhóm ${m.profileCode} → ${m.analyteCode}`
      if (!profileCodes.has(m.profileCode)) {
        skipped.push({ table: 'profile_members', ref, reason: 'nhóm không tồn tại' })
        bump('profile_members', 'skipped')
        continue
      }
      if (!analyteCodes.has(m.analyteCode)) {
        skipped.push({ table: 'profile_members', ref, reason: 'xét nghiệm không có trong danh mục' })
        bump('profile_members', 'skipped')
        continue
      }
      // Hệ cũ để mã cha nằm trong chính danh sách thành viên của nó và phải lọc bằng cờ
      // mỗi lần truy vấn. Loại ngay tại đây để hệ mới không mang theo quy ước ngầm đó.
      if (headCodes.has(m.analyteCode)) {
        skipped.push({ table: 'profile_members', ref, reason: 'mã cha không phải thành viên của chính nó' })
        bump('profile_members', 'skipped')
        continue
      }
      order += 1
      await trx
        .insertInto('profile_members')
        .values({ profile_code: m.profileCode, analyte_code: m.analyteCode, display_order: order })
        .execute()
      bump('profile_members', 'written')
    }

    // --- Luật ghi đè kết quả ---
    bump('result_mappings', 'read', src.resultMappings.length)
    const seenRules = new Set<string>()
    for (const r of src.resultMappings) {
      const ref = `máy ${r.insId} ${r.analyteCode} ${r.resultIn}`
      const deviceId = deviceIdByInsId.get(r.insId)
      if (!deviceId || !analyteCodes.has(r.analyteCode)) {
        skipped.push({
          table: 'result_mappings',
          ref,
          reason: !deviceId ? 'máy không tồn tại' : 'xét nghiệm không có trong danh mục',
        })
        bump('result_mappings', 'skipped')
        continue
      }
      const key = `${deviceId} ${r.analyteCode} ${r.resultIn}`
      if (seenRules.has(key)) {
        skipped.push({ table: 'result_mappings', ref, reason: 'luật trùng trong nguồn' })
        bump('result_mappings', 'skipped')
        continue
      }
      seenRules.add(key)
      await trx
        .insertInto('result_mappings')
        .values({
          device_id: deviceId,
          analyte_code: r.analyteCode,
          result_in: r.resultIn,
          result_out: r.resultOut,
          comment: r.comment,
          color: r.color,
        })
        .execute()
      bump('result_mappings', 'written')
    }

    // --- Ánh xạ mã HIS ---
    bump('his_analyte_map', 'read', src.hisMaps.length)
    for (const h of src.hisMaps) {
      if (!analyteCodes.has(h.analyteCode)) {
        skipped.push({
          table: 'his_analyte_map',
          ref: h.hisCode,
          reason: `xét nghiệm ${h.analyteCode} không có trong danh mục`,
        })
        bump('his_analyte_map', 'skipped')
        continue
      }
      await trx
        .insertInto('his_analyte_map')
        .values({ his_service_code: h.hisCode, analyte_code: h.analyteCode })
        .execute()
      bump('his_analyte_map', 'written')
    }

    return { counts, skipped }
  })
}
```

- [ ] **Step 4: Viết `legacy-report.ts`**

```ts
import type { MigrationOutcome } from './legacy-writer.js'
import type { ValidationReport } from './legacy-validator.js'

/** Dựng báo cáo tiếng Việt để người vận hành cầm đi đối chiếu với hệ cũ. */
export function formatReport(
  validation: ValidationReport,
  outcome: MigrationOutcome | null,
): string {
  const lines: string[] = []

  lines.push('=== KIỂM TRA DỮ LIỆU NGUỒN ===', '')
  lines.push(`Mã kênh có nhiều xét nghiệm: ${validation.channelConflicts.length}`)
  for (const c of validation.channelConflicts) {
    lines.push(`  máy ${c.insId} kênh ${c.channelCode}: ${c.candidates.join(' hoặc ')}`)
  }
  lines.push('', `Tham chiếu tới xét nghiệm không tồn tại: ${validation.orphans.length}`)
  for (const o of validation.orphans) {
    lines.push(`  [${o.kind}] ${o.ref} → ${o.analyteCode}`)
  }
  lines.push('', `Nhóm không có thành viên: ${validation.profilesWithoutMembers.join(', ') || 'không có'}`)
  lines.push(`Mã chứa ký tự đại diện: ${validation.wildcardCodes.join(', ') || 'không có'}`)
  lines.push(`Mã bị lặp trong danh mục: ${validation.duplicateAnalyteCodes.join(', ') || 'không có'}`)

  if (!outcome) {
    lines.push(
      '',
      '=== CHƯA GHI VÀO DATABASE ===',
      'Chạy lại với --apply sau khi đã điền tệp quyết định.',
    )
    return lines.join('\n')
  }

  lines.push('', '=== ĐỐI CHIẾU SAU KHI GHI ===', '')
  lines.push('Bảng                      Đọc    Ghi    Bỏ qua')
  for (const [table, c] of Object.entries(outcome.counts)) {
    lines.push(
      `${table.padEnd(24)} ${String(c.read).padStart(5)}  ${String(c.written).padStart(5)}  ${String(c.skipped).padStart(6)}`,
    )
  }

  if (outcome.skipped.length > 0) {
    lines.push('', 'Chi tiết các dòng bị bỏ qua:')
    for (const s of outcome.skipped) {
      lines.push(`  [${s.table}] ${s.ref} — ${s.reason}`)
    }
  }

  return lines.join('\n')
}
```

- [ ] **Step 5: Viết lệnh và nối vào CLI**

`apps/core/src/migration/migrate-legacy.command.ts` ghép ba giai đoạn: nạp nguồn, kiểm tra, sinh hoặc đọc tệp quyết định, in báo cáo, và chỉ ghi khi có `--apply`. Thêm nhánh `migrate-legacy` vào `apps/core/src/admin-cli.ts` theo đúng kiểu các lệnh đã có, kèm cập nhật dòng trợ giúp.

Cú pháp:
```
admin migrate-legacy --tenant <mã cơ sở> --source <thư mục> [--decisions <tệp>] [--apply]
```

Không có `--apply` thì chỉ chạy kiểm tra, sinh tệp quyết định nếu chưa có, in báo cáo, và **không ghi gì**.

- [ ] **Step 6: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test test/migrate-legacy.test.ts`
Expected: 8 test PASS.

- [ ] **Step 7: Chạy thật trên dữ liệu BVĐK 10**

```bash
cd apps/core
node --import tsx/esm --env-file=.env.demo src/admin-cli.ts migrate-legacy \
  --tenant bvdemo --source ../../docs/khao-sat/seed/du-lieu
```

Xác nhận báo cáo nêu đúng **7 xung đột mã kênh** và các mã mồ côi đã biết (`233GLU`, `hc12`, `7.82E+04`). Điền tệp quyết định cho vài xung đột, chạy lại với `--apply`, rồi đối chiếu bảng số lượng với: 404 xét nghiệm, 20 máy, 292 ánh xạ, 56 luật, 835 mã HIS.

Dán toàn bộ báo cáo vào report. Nếu số lệch, **nêu rõ lệch ở đâu** thay vì chỉnh cho khớp.

- [ ] **Step 8: Chạy toàn bộ test và kiểm kiểu**

Run: `pnpm test && pnpm typecheck`
Expected: toàn bộ PASS.

- [ ] **Step 9: Commit**

```bash
git add apps/core/src/migration apps/core/src/admin-cli.ts apps/core/test/migrate-legacy.test.ts
git commit -m "feat(core): legacy catalogue migration command with decision gate"
```

---

## Task 6: API danh mục cho Web UI

**Files:**
- Create: `apps/core/src/catalogue/catalogue.service.ts`, `catalogue.controller.ts`, `catalogue.module.ts`
- Modify: `apps/core/src/app.module.ts`
- Test: `apps/core/test/catalogue-api.test.ts`

**Interfaces:**
- Consumes: Task 1 (bảng danh mục), `JwtTenantGuard` và `withTenant` từ khối xác thực, `TenantRegistryService`
- Produces:
  - `GET /api/catalogue/analytes?q=&category=&limit=&offset=` → `{ items: AnalyteView[]; total: number }`
  - `PATCH /api/catalogue/analytes/:code` → `AnalyteView`
  - `GET /api/catalogue/categories` → `string[]`
  - `GET /api/catalogue/devices/:deviceId/channels` → `ChannelMapView[]`
  - `PUT /api/catalogue/devices/:deviceId/channels/:channelCode` → `ChannelMapView`
  - `DELETE /api/catalogue/devices/:deviceId/channels/:channelCode` → `204`
  - `GET /api/catalogue/devices/:deviceId/result-rules` → `ResultRuleView[]`
  - `PUT /api/catalogue/result-rules/:id` và `POST /api/catalogue/result-rules` → `ResultRuleView`
  - `DELETE /api/catalogue/result-rules/:id` → `204`

**Ghi chú thiết kế:** danh mục có 404 dòng nên endpoint tìm kiếm phải phân trang và tìm được theo cả mã lẫn tên. Cột `numeric` của Postgres về dưới dạng **chuỗi** qua trình điều khiển `pg`; chuyển sang số ở tầng service để Web UI nhận đúng kiểu.

- [ ] **Step 1: Viết test**

`apps/core/test/catalogue-api.test.ts` — dựng ứng dụng thật, đăng nhập lấy token qua `POST /api/auth/login` (đúng cách các test tích hợp hiện có làm), rồi kiểm:

```ts
describe('GET /api/catalogue/analytes', () => {
  it('trả về danh sách kèm tổng số', async () => { /* seed 3 xét nghiệm, khẳng định total = 3 */ })
  it('tìm được theo mã', async () => { /* q=123AST */ })
  it('tìm được theo tên không phân biệt hoa thường và có dấu', async () => { /* q=glucose */ })
  it('tìm mã chứa ký tự phần trăm không khớp nhầm mã khác', async () => {
    // seed '034NEU%' và '034NEUT'; tìm '034NEU%' phải trả đúng 1 dòng.
    // Đây là ca bảo vệ ràng buộc "không dùng LIKE trên mã".
  })
  it('phân trang đúng', async () => { /* limit=2&offset=2 */ })
  it('lọc theo nhóm', async () => { /* category=CTM */ })
  it('từ chối khi không có token', async () => { /* 401 */ })
  it('không trả dữ liệu của cơ sở khác', async () => { /* token cơ sở A, dữ liệu cơ sở B */ })
})

describe('PATCH /api/catalogue/analytes/:code', () => {
  it('sửa được đơn vị, số lẻ và ngưỡng', async () => { /* ... */ })
  it('không cho đổi mã', async () => { /* gửi code mới → bị bỏ qua hoặc 400 */ })
  it('trả 404 với mã không tồn tại', async () => { /* ... */ })
  it('đánh dấu nguồn thành manual sau khi người dùng sửa', async () => {
    // Giữ vết dòng nào đã bị sửa tay so với bản di trú.
  })
})

describe('kênh máy', () => {
  it('liệt kê các kênh của một máy kèm tên xét nghiệm', async () => { /* ... */ })
  it('từ chối gán hai xét nghiệm vào cùng một mã kênh', async () => {
    // Ràng buộc UNIQUE phải nổi lên thành lỗi 409 đọc được, không phải 500.
  })
  it('xoá được một ánh xạ kênh', async () => { /* ... */ })
})

describe('luật ghi đè kết quả', () => {
  it('tạo, sửa, xoá được luật', async () => { /* ... */ })
  it('từ chối luật trùng (máy, xét nghiệm, giá trị vào)', async () => { /* 409 */ })
})
```

Viết đầy đủ phần thân từng ca theo mẫu các test tích hợp đã có trong `apps/core/test/`.

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test test/catalogue-api.test.ts`
Expected: FAIL — mọi route trả 404.

- [ ] **Step 3: Viết service**

`catalogue.service.ts` với các phương thức tương ứng. Ba điểm bắt buộc:

- Tìm kiếm dùng `ilike` cho **tên**, nhưng so khớp **mã** bằng `=` — hoặc nếu cần tìm mã theo tiền tố thì phải **escape** `%` và `_` trước khi ghép vào mẫu. Danh mục có 8 mã chứa `%`.
- Chuyển `lower_limit` / `higher_limit` từ chuỗi sang số khi trả ra.
- Khi người dùng sửa một dòng có `source = 'legacy'`, đổi thành `'manual'` để giữ vết.

Bắt lỗi vi phạm ràng buộc duy nhất của Postgres (`23505`) và ném `ConflictException` với thông báo tiếng Việt nói rõ mã kênh nào đang trùng — không để lộ lỗi thô 500.

- [ ] **Step 4: Viết controller và module**

Theo đúng kiểu `DevicesController` đã có: `@UseGuards(JwtTenantGuard)`, mọi handler bọc trong `withTenant(req.tenantId, ...)`, `@Inject(...)` tường minh trên constructor. Thêm `CatalogueModule` vào `imports` của `AppModule`.

- [ ] **Step 5: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test test/catalogue-api.test.ts`
Expected: toàn bộ PASS.

- [ ] **Step 6: Commit**

```bash
git add apps/core/src/catalogue apps/core/src/app.module.ts apps/core/test/catalogue-api.test.ts
git commit -m "feat(core): catalogue read and edit api"
```

---

## Task 7: Màn hình danh mục xét nghiệm

**Files:**
- Create: `apps/web/src/screens/CatalogueAnalytes.tsx`
- Modify: `apps/web/src/api.ts` (thêm lời gọi danh mục), `apps/web/src/App.tsx` (điều hướng)

**Interfaces:**
- Consumes: Task 6 (`GET`/`PATCH /api/catalogue/analytes`, `GET /api/catalogue/categories`)
- Produces: màn hình danh mục xét nghiệm trong Web UI

**BẮT BUỘC trước khi viết JSX:** gọi công cụ Skill với `skill: "ui-ux-pro-max"`. Bối cảnh cho skill: bảng dữ liệu 404 dòng, người dùng là kỹ thuật viên xét nghiệm, dùng trên máy trạm phòng xét nghiệm màn hình nhỏ, cần tìm nhanh một xét nghiệm và sửa vài trường, tiếng Việt có dấu đầy đủ.

- [ ] **Step 1: Gọi skill và ghi lại hướng dẫn**

Ghi vào report hướng dẫn skill trả về và những gì bạn áp dụng.

- [ ] **Step 2: Thêm lời gọi API vào `api.ts`**

Theo đúng kiểu các hàm đã có: gắn `Authorization: Bearer`, xử lý 401 bằng cách đưa về đăng nhập, ném lỗi đọc được cho các mã khác.

- [ ] **Step 3: Viết màn hình**

Yêu cầu chức năng:
- Ô tìm kiếm theo mã hoặc tên, có hoãn gọi (debounce) để không bắn request mỗi phím
- Lọc theo nhóm
- Phân trang, hiện tổng số
- Sửa tại chỗ: đơn vị, số chữ số thập phân, ngưỡng dưới/trên, ba câu biện luận, thứ tự in
- **Nhãn phân biệt dòng đến từ di trú và dòng đã bị sửa tay**
- Trạng thái rỗng nói rõ "không tìm thấy xét nghiệm nào khớp *X*", không để bảng trắng
- Mọi lời gọi API có xử lý lỗi hiển thị được; lưu thất bại phải báo rõ là **chưa lưu**

- [ ] **Step 4: Kiểm chứng bằng dữ liệu thật**

Chạy `pnpm typecheck` (phải sạch). Nếu muốn xem trực tiếp, **chạy nền có thời hạn rồi dọn**, đừng chạy `dev` ở tiền cảnh:
```
(pnpm --filter @lis/web dev > /tmp/w.log 2>&1 &) ; sleep 15 ; curl -sS -o /dev/null -w "%{http_code}\n" http://localhost:5173/ ; pkill -f vite
```

- [ ] **Step 5: Commit**

```bash
git add apps/web/src
git commit -m "feat(web): analyte catalogue screen"
```

---

## Task 8: Màn hình ánh xạ mã kênh và luật ghi đè

**Files:**
- Create: `apps/web/src/screens/CatalogueChannelMap.tsx`, `apps/web/src/screens/CatalogueResultRules.tsx`
- Modify: `apps/web/src/api.ts`, `apps/web/src/App.tsx`

**Interfaces:**
- Consumes: Task 6 (các endpoint kênh máy và luật ghi đè)
- Produces: hai màn hình quản lý còn lại

**BẮT BUỘC trước khi viết JSX:** gọi công cụ Skill với `skill: "ui-ux-pro-max"`. Bối cảnh: hai màn hình cấu hình kỹ thuật; màn ánh xạ mã kênh là nơi kỹ thuật viên vào khi "máy trả kết quả về mà không hiện lên", nên nó phải làm rõ **kênh nào chưa được gán xét nghiệm nào**.

- [ ] **Step 1: Gọi skill và ghi lại hướng dẫn**

- [ ] **Step 2: Màn hình ánh xạ mã kênh**

Chọn máy → bảng các kênh, mỗi dòng: mã kênh, xét nghiệm được gán (mã và tên), số chữ số thập phân, các cờ (`cho phép tự thêm chỉ định`, `bỏ qua`, `giữ dấu lớn hơn`, `giữ dấu nhỏ hơn`).

Ba điểm quan trọng:
- **Gán một xét nghiệm vào mã kênh đã có xét nghiệm khác phải bị chặn**, kèm thông báo nói rõ kênh đó đang trỏ tới đâu. Đây là ràng buộc trung tâm của cả khối 2A; giao diện phải giải thích được vì sao bị chặn, không chỉ báo lỗi.
- Hiện rõ máy nào **chưa có kênh nào** được ánh xạ.
- Nhãn ba trạng thái hoạt động của máy (*có cấu hình cổng*, *từng gửi kết quả*, *đang bật*) — kèm chữ, không chỉ màu.

- [ ] **Step 3: Màn hình luật ghi đè kết quả**

Chọn máy → danh sách luật `(xét nghiệm, giá trị máy trả về) → kết quả hiển thị, biện luận, màu`. Thêm, sửa, xoá tại chỗ. Ví dụ thực tế để hiện trong trạng thái rỗng: `neg` → `Âm tính`.

Màu là số 0/1/2 (bình thường/thấp/cao) theo quy ước hệ cũ — hiển thị bằng **nhãn chữ** kèm dấu hiệu, không bắt người dùng nhớ số.

- [ ] **Step 4: Kiểm chứng**

`pnpm typecheck` sạch. Xem trực tiếp bằng cách chạy nền có thời hạn như Task 7.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src
git commit -m "feat(web): channel mapping and result rule screens"
```

---

## Định nghĩa hoàn thành khối 2A

- [ ] `pnpm test` xanh toàn bộ, `pnpm test:e2e` xanh, `pnpm typecheck` sạch
- [ ] Chạy lệnh di trú trên dữ liệu thật của BVĐK 10, báo cáo nêu đúng **7 xung đột mã kênh** và các mã mồ côi đã biết
- [ ] Sau khi điền tệp quyết định và chạy `--apply`, bảng đối chiếu khớp: **404** xét nghiệm, **20** máy, **56** luật ghi đè, **835** mã HIS; số ánh xạ mã kênh bằng **292 trừ đi** số dòng bị bỏ có lý do ghi rõ
- [ ] Ràng buộc `(device_id, channel_code)` duy nhất thật sự chặn được — có test chứng minh
- [ ] Mã chứa ký tự `%` không gây so khớp nhầm — có test chứng minh
- [ ] Công cụ di trú chạy hai lần trên database trống cho kết quả giống hệt nhau
- [ ] Ba màn hình quản lý dùng được, đã qua skill `ui-ux-pro-max`, mọi lời gọi API có xử lý lỗi hiển thị được
- [ ] Danh sách 11 nhóm cha dựng lại từ thụt lề đã được xuất ra để kỹ thuật viên xác nhận (khối 2A chỉ xuất danh sách; việc xác nhận là công việc chuyên môn, không phải việc của phần mềm)
