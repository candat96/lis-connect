import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { runAdminMigrations } from '../src/admin-db/migrator.js'
import type { AdminDatabase } from '../src/admin-db/schema.js'
import { AgentKeyService } from '../src/tenant/agent-key.service.js'
import { TenantContext } from '../src/tenant/tenant-context.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'

const ROOT = 'postgres://lis:lis@localhost:55432/postgres'
const ADMIN_DB = 'lis_admin_iso_test'
const TEMPLATE = 'postgres://lis:lis@localhost:55432/{db}'

let admin: Kysely<AdminDatabase>
let migrator: TenantMigratorService
let registry: TenantRegistryService
let keys: AgentKeyService
let tenantA: string
let tenantB: string

async function resetDatabase(name: string): Promise<void> {
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.query(`CREATE DATABASE ${name}`)
  await root.end()
}

async function dropIfExists(name: string): Promise<void> {
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.end()
}

async function seedAgentAndDevice(tenantId: string, deviceKey: string): Promise<string> {
  const db = await registry.forTenant(tenantId)
  const agent = await db
    .insertInto('agents')
    .values({
      agent_key_id: crypto.randomUUID(),
      hostname: `host-${deviceKey}`,
      agent_version: '0.1.0',
    })
    .returningAll()
    .executeTakeFirstOrThrow()
  const device = await db
    .insertInto('devices')
    .values({
      agent_id: agent.id,
      device_key: deviceKey,
      name: `Máy ${deviceKey}`,
      transport: 'tcp',
      driver: 'astm-e1394',
    })
    .returningAll()
    .executeTakeFirstOrThrow()
  return device.id
}

beforeAll(async () => {
  await resetDatabase(ADMIN_DB)
  await dropIfExists('lis_t_iso_a')
  await dropIfExists('lis_t_iso_b')

  admin = new Kysely<AdminDatabase>({
    dialect: new PostgresDialect({
      pool: new pg.Pool({ connectionString: `postgres://lis:lis@localhost:55432/${ADMIN_DB}` }),
    }),
  })
  await runAdminMigrations(admin)

  const a = await admin
    .insertInto('tenants')
    .values({ code: 'iso-a', name: 'Bệnh viện A', database_name: 'lis_t_iso_a', status: 'active' })
    .returningAll()
    .executeTakeFirstOrThrow()
  const b = await admin
    .insertInto('tenants')
    .values({ code: 'iso-b', name: 'Bệnh viện B', database_name: 'lis_t_iso_b', status: 'active' })
    .returningAll()
    .executeTakeFirstOrThrow()
  tenantA = a.id
  tenantB = b.id

  migrator = new TenantMigratorService(admin, TEMPLATE)
  await migrator.provisionDatabase(tenantA)
  await migrator.provisionDatabase(tenantB)

  registry = new TenantRegistryService(admin, TEMPLATE)
  keys = new AgentKeyService(admin)
}, 90_000)

afterAll(async () => {
  await registry.closeAll()
  await migrator.closeAll()
  await admin.destroy()
})

describe('cô lập giữa các tenant', () => {
  it('dữ liệu ghi cho tenant A không xuất hiện ở tenant B', async () => {
    await seedAgentAndDevice(tenantA, 'may-a')

    const dbB = await registry.forTenant(tenantB)
    const devicesB = await dbB.selectFrom('devices').selectAll().execute()
    expect(devicesB).toEqual([])
  })

  it('và ngược lại', async () => {
    await seedAgentAndDevice(tenantB, 'may-b')

    const dbA = await registry.forTenant(tenantA)
    const namesA = (await dbA.selectFrom('devices').select('device_key').execute()).map(
      (r) => r.device_key,
    )
    expect(namesA).toEqual(['may-a'])
  })

  it('forTenant trả về cùng một pool khi gọi lại', async () => {
    const first = await registry.forTenant(tenantA)
    const second = await registry.forTenant(tenantA)
    expect(first).toBe(second)
  })

  it('evictIdle đóng pool không dùng tới', async () => {
    const before = await registry.forTenant(tenantA)
    const closed = await registry.evictIdle(0)
    expect(closed).toBeGreaterThan(0)

    // Pool cũ phải thực sự bị đóng (destroy), không chỉ gỡ khỏi cache — một
    // bản cài đặt chỉ xoá Map mà quên gọi destroy() vẫn để lộ kết nối treo.
    await expect(before.selectFrom('devices').selectAll().execute()).rejects.toThrow()

    // Sau khi đóng, gọi lại vẫn phải hoạt động (tạo pool mới).
    const db = await registry.forTenant(tenantA)
    expect(db).not.toBe(before)
    const rows = await db.selectFrom('devices').selectAll().execute()
    expect(rows.length).toBe(1)
  })

  it('forTenant từ chối tenant không tồn tại', async () => {
    await expect(registry.forTenant(crypto.randomUUID())).rejects.toThrow(/không tìm thấy tenant/)
  })

  it('forTenant từ chối tenant đã bị tạm ngưng', async () => {
    const suspended = await admin
      .insertInto('tenants')
      .values({
        code: 'iso-c',
        name: 'Bệnh viện C',
        database_name: 'lis_t_iso_c',
        status: 'suspended',
      })
      .returningAll()
      .executeTakeFirstOrThrow()

    await expect(registry.forTenant(suspended.id)).rejects.toThrow(/tạm ngưng/)
  })

  it('forTenant từ chối tenant đã cache nhưng vừa bị tạm ngưng', async () => {
    await dropIfExists('lis_t_iso_d')
    const d = await admin
      .insertInto('tenants')
      .values({ code: 'iso-d', name: 'Bệnh viện D', database_name: 'lis_t_iso_d', status: 'active' })
      .returningAll()
      .executeTakeFirstOrThrow()
    await migrator.provisionDatabase(d.id)

    // Cache pool trong khi tenant còn active.
    await registry.forTenant(d.id)

    await admin.updateTable('tenants').set({ status: 'suspended' }).where('id', '=', d.id).execute()

    // Lần gọi sau, dù pool đã cache, vẫn phải bị từ chối ngay — không được
    // đợi tới evictIdle hay khởi động lại tiến trình mới có hiệu lực.
    await expect(registry.forTenant(d.id)).rejects.toThrow(/tạm ngưng/)
  }, 20_000)

  it('nhiều lời gọi đồng thời cho tenant chưa cache trả về cùng một instance', async () => {
    await dropIfExists('lis_t_iso_e')
    const e = await admin
      .insertInto('tenants')
      .values({ code: 'iso-e', name: 'Bệnh viện E', database_name: 'lis_t_iso_e', status: 'active' })
      .returningAll()
      .executeTakeFirstOrThrow()
    await migrator.provisionDatabase(e.id)

    const [r1, r2, r3] = await Promise.all([
      registry.forTenant(e.id),
      registry.forTenant(e.id),
      registry.forTenant(e.id),
    ])

    expect(r1).toBe(r2)
    expect(r2).toBe(r3)
  }, 20_000)
})

describe('TenantContext', () => {
  it('trả về tenant hiện tại bên trong run', async () => {
    const seen = await TenantContext.run(tenantA, async () => TenantContext.currentTenantId())
    expect(seen).toBe(tenantA)
  })

  it('ném lỗi khi gọi ngoài phạm vi run', () => {
    expect(() => TenantContext.currentTenantId()).toThrow(/không có ngữ cảnh tenant/)
  })

  it('giữ đúng tenant khi hai luồng chạy xen kẽ', async () => {
    const [x, y] = await Promise.all([
      TenantContext.run(tenantA, async () => {
        await new Promise((r) => setTimeout(r, 20))
        return TenantContext.currentTenantId()
      }),
      TenantContext.run(tenantB, async () => TenantContext.currentTenantId()),
    ])
    expect(x).toBe(tenantA)
    expect(y).toBe(tenantB)
  })
})

describe('AgentKeyService', () => {
  it('cấp khoá và xác thực được đúng tenant', async () => {
    const issued = await keys.issue(tenantA, 'PC sinh hóa')
    const verified = await keys.verify(issued.plainKey)
    expect(verified?.tenantId).toBe(tenantA)
  })

  it('không lưu khoá gốc trong database', async () => {
    const issued = await keys.issue(tenantB, 'PC huyết học')
    const row = await admin
      .selectFrom('agent_keys')
      .selectAll()
      .where('id', '=', issued.keyId)
      .executeTakeFirstOrThrow()
    // Kiểm mọi cột, không chỉ key_hash — khoá gốc không được lưu ở bất kỳ đâu.
    for (const value of Object.values(row)) {
      expect(String(value)).not.toContain(issued.plainKey)
    }
  })

  it('từ chối khoá sai', async () => {
    expect(await keys.verify('lis_agent_khong_ton_tai')).toBeNull()
  })

  it('từ chối khoá của tenant đã tạm ngưng', async () => {
    await dropIfExists('lis_t_iso_f')
    const f = await admin
      .insertInto('tenants')
      .values({ code: 'iso-f', name: 'Bệnh viện F', database_name: 'lis_t_iso_f', status: 'active' })
      .returningAll()
      .executeTakeFirstOrThrow()

    const issued = await keys.issue(f.id, 'PC sắp bị tạm ngưng')
    await admin.updateTable('tenants').set({ status: 'suspended' }).where('id', '=', f.id).execute()

    expect(await keys.verify(issued.plainKey)).toBeNull()
  })

  it('từ chối khoá đã thu hồi', async () => {
    const issued = await keys.issue(tenantA, 'PC sắp thu hồi')
    await admin
      .updateTable('agent_keys')
      .set({ status: 'revoked' })
      .where('id', '=', issued.keyId)
      .execute()
    expect(await keys.verify(issued.plainKey)).toBeNull()
  })
})
