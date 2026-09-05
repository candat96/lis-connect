import { Injectable, type OnModuleDestroy } from '@nestjs/common'
import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import type { AdminDatabase } from '../admin-db/schema.js'
import { assertValidDatabaseName } from './database-name.js'
import type { TenantDatabase } from './tenant-schema.js'

interface PoolHandle {
  db: Kysely<TenantDatabase>
  /**
   * Tham chiếu trực tiếp tới `pg.Pool` đã truyền cho `PostgresDialect`.
   *
   * `Kysely.destroy()` là no-op nếu driver của nó chưa từng khởi tạo (driver
   * chỉ khởi tạo khi có truy vấn đầu tiên chạy qua) — một pool được tạo và
   * cache nhưng chưa từng query thì `db.destroy()` không đóng gì cả, và
   * `pg.Pool` bên dưới vẫn sống, giữ nguyên kết nối tới Postgres. Vì vậy việc
   * đóng pool không được phụ thuộc vào vòng đời driver của Kysely — phải tự
   * gọi `pool.end()` trực tiếp.
   */
  pool: pg.Pool
}

interface Entry {
  handlePromise: Promise<PoolHandle>
  lastUsedAt: number
}

/**
 * Cửa duy nhất để lấy connection tới DB nghiệp vụ của một tenant.
 *
 * Pool được tạo lười và đóng lại khi nhàn rỗi: với hàng chục tenant, mỗi tenant
 * một pool đầy đủ sẽ ăn hết số kết nối Postgres cho phép.
 *
 * Trạng thái tenant được kiểm ở MỌI lời gọi (không chỉ lần đầu): nếu chỉ kiểm
 * lúc tạo pool rồi cache mãi, một bệnh viện bị tạm ngưng vẫn đọc được dữ liệu
 * qua pool đã cache cho tới khi `evictIdle` chạy hoặc tiến trình khởi động lại.
 */
@Injectable()
export class TenantRegistryService implements OnModuleDestroy {
  private readonly entries = new Map<string, Entry>()

  constructor(
    private readonly admin: Kysely<AdminDatabase>,
    private readonly urlTemplate: string,
  ) {}

  async forTenant(tenantId: string): Promise<Kysely<TenantDatabase>> {
    const tenant = await this.lookupActiveTenant(tenantId)

    // Cache được kiểm lại NGAY sau khi truy vấn tenant vừa resolve, và việc tạo
    // + đăng ký pool mới diễn ra đồng bộ (không có `await` xen giữa) trước khi
    // await promise kết nối thật sự. Nhờ vậy, nếu nhiều lời gọi forTenant cho
    // cùng một tenant chưa cache chạy đồng thời, lời gọi có truy vấn resolve
    // trước sẽ đăng ký entry trước, và các lời gọi còn lại — dù truy vấn của
    // chúng resolve sau đó bao lâu — đều thấy entry đã đăng ký ở bước này và
    // dùng lại đúng một pool, không tạo pool mồ côi nào.
    const cached = this.entries.get(tenantId)
    if (cached) {
      cached.lastUsedAt = Date.now()
      return (await cached.handlePromise).db
    }

    assertValidDatabaseName(tenant.database_name)
    const entry: Entry = {
      handlePromise: this.createPool(tenant.database_name),
      lastUsedAt: Date.now(),
    }
    this.entries.set(tenantId, entry)

    try {
      return (await entry.handlePromise).db
    } catch (err) {
      // Tạo pool thất bại: gỡ entry hỏng để lần gọi sau được thử lại từ đầu.
      if (this.entries.get(tenantId) === entry) this.entries.delete(tenantId)
      throw err
    }
  }

  /** Tra tenant trong DB quản trị; từ chối nếu không tồn tại hoặc không active. */
  private async lookupActiveTenant(tenantId: string): Promise<{ database_name: string }> {
    const tenant = await this.admin
      .selectFrom('tenants')
      .select(['database_name', 'status'])
      .where('id', '=', tenantId)
      .executeTakeFirst()

    if (!tenant) {
      await this.evictEntry(tenantId)
      throw new Error(`không tìm thấy tenant ${tenantId}`)
    }
    if (tenant.status !== 'active') {
      await this.evictEntry(tenantId)
      throw new Error(`tenant ${tenantId} đang tạm ngưng`)
    }
    return tenant
  }

  private async createPool(databaseName: string): Promise<PoolHandle> {
    // `{db}` được nội suy bằng hàm thay vì chuỗi để `String.replace` không
    // diễn giải các mẫu đặc biệt ($&, $`, $', $<n>...) có thể xuất hiện trong
    // databaseName.
    const url = this.urlTemplate.replace('{db}', () => databaseName)
    const pool = new pg.Pool({ connectionString: url, max: 4, idleTimeoutMillis: 10_000 })
    const db = new Kysely<TenantDatabase>({ dialect: new PostgresDialect({ pool }) })
    return { db, pool }
  }

  /** Gỡ và đóng hẳn pool đang cache của một tenant, nếu có. */
  private async evictEntry(tenantId: string): Promise<void> {
    const entry = this.entries.get(tenantId)
    if (!entry) return
    this.entries.delete(tenantId)
    await this.destroyEntry(entry)
  }

  private async destroyEntry(entry: Entry): Promise<void> {
    let handle: PoolHandle
    try {
      handle = await entry.handlePromise
    } catch {
      // Tạo pool thất bại: không có gì để đóng.
      return
    }

    try {
      // Dọn phần Kysely quản lý (không làm gì nếu driver chưa từng khởi tạo).
      await handle.db.destroy()
    } catch {
      // Không để lỗi dọn dẹp phía Kysely chặn việc đóng pool thật bên dưới.
    }

    try {
      // Đóng thẳng pg.Pool: đây mới là bước thực sự trả kết nối cho Postgres,
      // bất kể driver của Kysely đã khởi tạo hay chưa.
      await handle.pool.end()
    } catch {
      // `pg` ném lỗi nếu end() được gọi khi pool đã đóng rồi (ví dụ
      // db.destroy() ở trên đã tự đóng vì driver có khởi tạo) — bỏ qua.
    }
  }

  /** Đóng các pool không được dùng trong `maxIdleMs` mili giây. */
  async evictIdle(maxIdleMs: number): Promise<number> {
    const cutoff = Date.now() - maxIdleMs
    const stale = [...this.entries.entries()].filter(([, entry]) => entry.lastUsedAt <= cutoff)
    for (const [tenantId] of stale) {
      this.entries.delete(tenantId)
    }
    await Promise.all(stale.map(([, entry]) => this.destroyEntry(entry)))
    return stale.length
  }

  async closeAll(): Promise<void> {
    const all = [...this.entries.values()]
    this.entries.clear()
    await Promise.all(all.map((entry) => this.destroyEntry(entry)))
  }

  async onModuleDestroy(): Promise<void> {
    await this.closeAll()
  }
}
