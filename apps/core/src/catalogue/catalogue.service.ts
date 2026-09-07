import {
  BadRequestException,
  ConflictException,
  Inject,
  Injectable,
  NotFoundException,
} from '@nestjs/common'
import type { Kysely, Transaction } from 'kysely'
import { TenantRegistryService } from '../tenant/tenant-registry.service.js'
import type { TenantDatabase } from '../tenant/tenant-schema.js'

/** Người thực hiện thay đổi, lấy từ JWT đã được JwtTenantGuard xác thực. */
export interface CatalogueActor {
  userId: string
  username: string
}

export interface AnalyteView {
  code: string
  name: string
  nameEn: string | null
  category: string | null
  unit: string | null
  decimals: number | null
  normalRangeText: string | null
  /** numeric của Postgres về dạng chuỗi qua `pg`; đã chuyển sang số ở đây. */
  lowerLimit: number | null
  higherLimit: number | null
  commentLower: string | null
  commentHigher: string | null
  commentGray: string | null
  criteria: string | null
  resultIsComment: boolean
  printOrder: number | null
  isProfileHead: boolean
  /** 'legacy' | 'legacy-new' | 'manual' — xem AnalyteSource. */
  source: string
  active: boolean
}

export interface AnalyteSearchResult {
  items: AnalyteView[]
  /** Tổng số dòng khớp điều kiện lọc, không phụ thuộc limit/offset. */
  total: number
}

export interface ChannelMapView {
  id: string
  deviceId: string
  channelCode: string
  analyteCode: string | null
  analyteName: string | null
  /**
   * BA trạng thái, không phải hai:
   * - 'assigned': đã gán xét nghiệm;
   * - 'unassigned': CHƯA GÁN — không ai khai xét nghiệm cho mã kênh này (quên);
   * - 'undecided': CHƯA QUYẾT — hệ cũ khai nhiều xét nghiệm cho cùng mã kênh,
   *   phần mềm không được chọn hộ và đang chờ người quyết.
   * Gộp hai trạng thái sau làm một là mất hẳn thông tin về 7 ca xung đột.
   */
  state: 'assigned' | 'unassigned' | 'undecided'
  /** Nhãn tiếng Việt dựng sẵn cho `state` — sản phẩm chỉ phục vụ tiếng Việt. */
  stateLabel: string
  /** Với 'undecided': các mã xét nghiệm ứng viên của hệ cũ, để người quyết chọn. */
  decisionCandidates: string[]
  round: number | null
  allowInsert: boolean
  omit: boolean
  keepHigherSign: boolean
  keepLowerSign: boolean
}

export interface ResultRuleView {
  id: string
  /** null nghĩa là luật áp dụng cho mọi máy. */
  deviceId: string | null
  /**
   * Phân biệt luật riêng của máy với luật chung. Có mặt để giao diện hiển thị
   * được bằng CHỮ chứ không bắt người dùng suy ra từ `deviceId === null`:
   * hiện có 6 luật HPV và 6 luật TPHA là luật chung, giấu đi thì kỹ thuật viên
   * không hiểu vì sao máy diễn giải theo một luật không thấy ở đâu.
   */
  scope: 'device' | 'common'
  /** Nhãn tiếng Việt dựng sẵn cho `scope` — sản phẩm chỉ phục vụ tiếng Việt. */
  scopeLabel: string
  analyteCode: string
  analyteName: string | null
  resultIn: string
  resultOut: string | null
  comment: string | null
  color: number | null
}

export interface AnalyteSearchQuery {
  q?: string | undefined
  category?: string | undefined
  limit: number
  offset: number
}

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** Mã lỗi Postgres cần dịch sang lỗi HTTP đọc được thay vì để lộ 500. */
const PG_UNIQUE_VIOLATION = '23505'
const PG_FOREIGN_KEY_VIOLATION = '23503'

const SCOPE_LABEL = {
  device: 'Luật riêng của máy này',
  common: 'Luật chung (áp dụng cho mọi máy)',
} as const

const CHANNEL_STATE_LABEL = {
  assigned: 'Đã gán xét nghiệm',
  unassigned: 'CHƯA GÁN XÉT NGHIỆM',
  undecided: 'CHƯA QUYẾT — hệ cũ khai nhiều xét nghiệm cho mã kênh này',
} as const

type AuditEntity = 'analyte' | 'device_analyte_map' | 'result_mapping'
type AuditAction = 'create' | 'update' | 'delete'

function pgErrorCode(err: unknown): string | undefined {
  // Kysely bọc lỗi driver; `pg` gắn `code` lên chính đối tượng lỗi.
  const candidate = err as { code?: unknown; cause?: { code?: unknown } }
  if (typeof candidate?.code === 'string') return candidate.code
  if (typeof candidate?.cause?.code === 'string') return candidate.cause.code
  return undefined
}

/**
 * Thoát ký tự đại diện của LIKE trong chuỗi người dùng nhập.
 *
 * Danh mục thật có 43 mã chứa `%` và 5 mã chứa `_` (họ XNNT21_xx). Cả hai đều
 * là ký tự đại diện: không thoát thì tìm `034NEU%` sẽ kéo về cả `034NEUT`.
 * Dấu `\` phải thoát trước, nếu không sẽ nhân đôi phần thoát của hai ký tự kia.
 */
export function escapeLikePattern(input: string): string {
  return input.replace(/\\/g, '\\\\').replace(/%/g, '\\%').replace(/_/g, '\\_')
}

/** numeric của Postgres về dạng chuỗi qua `pg` — Web UI phải nhận số. */
function toNumber(value: string | number | null): number | null {
  if (value === null || value === undefined) return null
  const n = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(n) ? n : null
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function requireNonEmptyString(value: unknown, field: string): string {
  if (typeof value !== 'string' || value.trim() === '') {
    throw new BadRequestException(`thiếu hoặc sai kiểu trường "${field}"`)
  }
  return value
}

function optionalText(value: unknown, field: string): string | null {
  if (value === null) return null
  if (typeof value !== 'string') throw new BadRequestException(`trường "${field}" phải là chuỗi`)
  return value
}

function optionalInteger(value: unknown, field: string): number | null {
  if (value === null) return null
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    throw new BadRequestException(`trường "${field}" phải là số nguyên`)
  }
  return value
}

function optionalDecimal(value: unknown, field: string): number | null {
  if (value === null) return null
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new BadRequestException(`trường "${field}" phải là số`)
  }
  return value
}

function optionalBoolean(value: unknown, field: string): boolean {
  if (typeof value !== 'boolean') {
    throw new BadRequestException(`trường "${field}" phải là true/false`)
  }
  return value
}

@Injectable()
export class CatalogueService {
  // `@Inject` tường minh vì bản build test dùng esbuild (qua vitest/tsx), và
  // esbuild không phát metadata `design:paramtypes` cho `emitDecoratorMetadata`
  // — nếu chỉ dựa vào kiểu tham số, Nest sẽ tiêm `undefined` vào `registry`
  // trong im lặng.
  constructor(@Inject(TenantRegistryService) private readonly registry: TenantRegistryService) {}

  // ---------------------------------------------------------------- danh mục

  async searchAnalytes(tenantId: string, query: AnalyteSearchQuery): Promise<AnalyteSearchResult> {
    const db = await this.registry.forTenant(tenantId)

    let base = db.selectFrom('analytes')
    const q = query.q?.trim()
    if (q) {
      // Chuỗi người dùng nhập được thoát MỘT lần rồi dùng cho cả ba mẫu.
      // Không thoát thì tìm '034NEU%' sẽ kéo về cả '034NEUT'.
      const escaped = escapeLikePattern(q)
      base = base.where((eb) =>
        eb.or([
          // Mã: khớp theo TIỀN TỐ, không phải khớp giữa chuỗi. Danh mục có
          // 2619 dòng nên bắt gõ đủ cả mã là không dùng được; nhưng khớp giữa
          // chuỗi lại làm mã ngắn kéo về quá nhiều dòng vô nghĩa.
          eb('code', 'ilike', `${escaped}%`),
          eb('name', 'ilike', `%${escaped}%`),
          eb('name_en', 'ilike', `%${escaped}%`),
        ]),
      )
    }
    if (query.category) {
      base = base.where('category', '=', query.category)
    }

    // Phân trang và lọc chạy phía máy chủ: danh mục thật có hơn 2600 dòng nên
    // không endpoint nào được trả toàn bộ.
    const [rows, count] = await Promise.all([
      base.selectAll().orderBy('code').limit(query.limit).offset(query.offset).execute(),
      base.select((eb) => eb.fn.countAll<string>().as('total')).executeTakeFirstOrThrow(),
    ])

    return { items: rows.map(toAnalyteView), total: Number(count.total) }
  }

  async listCategories(tenantId: string): Promise<string[]> {
    const db = await this.registry.forTenant(tenantId)
    const rows = await db
      .selectFrom('analytes')
      .select('category')
      .distinct()
      .where('category', 'is not', null)
      .orderBy('category')
      .execute()
    return rows.map((r) => r.category).filter((c): c is string => c !== null)
  }

  /**
   * Sửa một xét nghiệm. `code` là khoá nghiệp vụ nên không sửa được: trường
   * `code` trong body bị bỏ qua có chủ ý.
   */
  async updateAnalyte(
    tenantId: string,
    actor: CatalogueActor,
    code: string,
    body: unknown,
  ): Promise<AnalyteView> {
    if (!isPlainObject(body)) throw new BadRequestException('body phải là một đối tượng JSON')

    const patch: Record<string, unknown> = {}
    const put = (key: string, value: unknown): void => {
      patch[key] = value
    }
    if ('name' in body) put('name', requireNonEmptyString(body.name, 'name'))
    if ('nameEn' in body) put('name_en', optionalText(body.nameEn, 'nameEn'))
    if ('category' in body) put('category', optionalText(body.category, 'category'))
    if ('unit' in body) put('unit', optionalText(body.unit, 'unit'))
    if ('decimals' in body) put('decimals', optionalInteger(body.decimals, 'decimals'))
    if ('normalRangeText' in body)
      put('normal_range_text', optionalText(body.normalRangeText, 'normalRangeText'))
    if ('lowerLimit' in body) put('lower_limit', optionalDecimal(body.lowerLimit, 'lowerLimit'))
    if ('higherLimit' in body) put('higher_limit', optionalDecimal(body.higherLimit, 'higherLimit'))
    if ('commentLower' in body) put('comment_lower', optionalText(body.commentLower, 'commentLower'))
    if ('commentHigher' in body)
      put('comment_higher', optionalText(body.commentHigher, 'commentHigher'))
    if ('commentGray' in body) put('comment_gray', optionalText(body.commentGray, 'commentGray'))
    if ('criteria' in body) put('criteria', optionalText(body.criteria, 'criteria'))
    if ('resultIsComment' in body)
      put('result_is_comment', optionalBoolean(body.resultIsComment, 'resultIsComment'))
    if ('printOrder' in body) put('print_order', optionalInteger(body.printOrder, 'printOrder'))
    if ('isProfileHead' in body)
      put('is_profile_head', optionalBoolean(body.isProfileHead, 'isProfileHead'))
    if ('active' in body) put('active', optionalBoolean(body.active, 'active'))

    if (Object.keys(patch).length === 0) {
      throw new BadRequestException('không có trường nào hợp lệ để cập nhật')
    }

    // Giữ vết dòng nào đã bị sửa tay so với bản di trú: mọi bản ghi người dùng
    // đụng vào đều chuyển sang 'manual', kể cả khi nguồn là 'legacy-new'.
    patch.source = 'manual'

    const db = await this.registry.forTenant(tenantId)
    return db.transaction().execute(async (trx) => {
      const before = await trx
        .selectFrom('analytes')
        .selectAll()
        .where('code', '=', code)
        .executeTakeFirst()
      if (!before) throw new NotFoundException(`không tìm thấy xét nghiệm có mã "${code}"`)

      const after = await trx
        .updateTable('analytes')
        .set(patch as never)
        .where('code', '=', code)
        .returningAll()
        .executeTakeFirstOrThrow()

      await this.writeAudit(trx, actor, 'analyte', code, 'update', before, after)
      return toAnalyteView(after)
    })
  }

  // ------------------------------------------------------------- kênh của máy

  async listChannels(tenantId: string, deviceId: string): Promise<ChannelMapView[]> {
    const db = await this.registry.forTenant(tenantId)
    await this.assertDevice(db, deviceId)
    return this.channelsOf(db, deviceId)
  }

  /**
   * Tạo hoặc sửa ánh xạ kênh.
   *
   * Gán một xét nghiệm KHÁC vào mã kênh đang có bị từ chối 409 thay vì ghi đè
   * trong im lặng: hệ cũ cho hai xét nghiệm dùng chung một kênh rồi chọn bừa
   * theo alphabet (7 cặp trong dữ liệu thật), và chính chỗ đó làm kết quả về
   * nhầm ô. Muốn đổi thì phải xoá ánh xạ cũ trước — một hành động có chủ ý.
   */
  async putChannel(
    tenantId: string,
    actor: CatalogueActor,
    deviceId: string,
    channelCode: string,
    body: unknown,
  ): Promise<ChannelMapView> {
    if (!isPlainObject(body)) throw new BadRequestException('body phải là một đối tượng JSON')
    const analyteCode = requireNonEmptyString(body.analyteCode, 'analyteCode')

    // Gán được xét nghiệm nghĩa là mã kênh này KHÔNG còn "chưa quyết" nữa: xoá
    // luôn cờ và danh sách ứng viên, nếu không màn hình sẽ vừa báo đã gán vừa
    // báo đang chờ quyết.
    const values: Record<string, unknown> = {
      analyte_code: analyteCode,
      pending_decision: false,
      decision_candidates: null,
    }
    if ('round' in body) values.round = optionalInteger(body.round, 'round')
    if ('allowInsert' in body) values.allow_insert = optionalBoolean(body.allowInsert, 'allowInsert')
    if ('omit' in body) values.omit = optionalBoolean(body.omit, 'omit')
    if ('keepHigherSign' in body)
      values.keep_higher_sign = optionalBoolean(body.keepHigherSign, 'keepHigherSign')
    if ('keepLowerSign' in body)
      values.keep_lower_sign = optionalBoolean(body.keepLowerSign, 'keepLowerSign')

    const db = await this.registry.forTenant(tenantId)
    // Thay đổi và vết của nó nằm trong CÙNG một giao dịch: nếu ghi vết hỏng thì
    // thay đổi cũng không được áp, và nếu thay đổi bị từ chối thì không có vết
    // nào nói dối rằng nó đã xảy ra.
    return db.transaction().execute(async (trx) => {
      await this.assertDevice(trx, deviceId)

      const existing = await trx
        .selectFrom('device_analyte_map')
        .selectAll()
        .where('device_id', '=', deviceId)
        .where('channel_code', '=', channelCode)
        .executeTakeFirst()

      // `analyte_code` cho phép NULL: đó là một mã kênh máy có khai báo nhưng
      // CHƯA gán xét nghiệm nào. Gán cho nó là thao tác bình thường — thậm chí
      // là lý do tồn tại của màn hình ánh xạ mã kênh — nên không được coi là
      // xung đột. Chỉ chặn khi kênh đang trỏ tới một xét nghiệm KHÁC.
      if (existing && existing.analyte_code !== null && existing.analyte_code !== analyteCode) {
        throw new ConflictException(
          `mã kênh "${channelCode}" của máy này đã gán cho xét nghiệm "${existing.analyte_code}". ` +
            `Hãy xoá ánh xạ cũ trước khi gán "${analyteCode}".`,
        )
      }

      let saved
      try {
        saved = existing
          ? await trx
              .updateTable('device_analyte_map')
              .set(values as never)
              .where('id', '=', existing.id)
              .returningAll()
              .executeTakeFirstOrThrow()
          : await trx
              .insertInto('device_analyte_map')
              .values({ ...values, device_id: deviceId, channel_code: channelCode } as never)
              .returningAll()
              .executeTakeFirstOrThrow()
      } catch (err) {
        const code = pgErrorCode(err)
        // Lưới an toàn cho tình huống hai request cùng tạo một kênh: kiểm tra
        // ở trên không nguyên tử, ràng buộc UNIQUE mới là chỗ chốt.
        if (code === PG_UNIQUE_VIOLATION) {
          throw new ConflictException(
            `mã kênh "${channelCode}" của máy này vừa được gán cho một xét nghiệm khác`,
          )
        }
        if (code === PG_FOREIGN_KEY_VIOLATION) {
          throw new BadRequestException(`mã xét nghiệm "${analyteCode}" không có trong danh mục`)
        }
        throw err
      }

      await this.writeAudit(
        trx,
        actor,
        'device_analyte_map',
        `${deviceId}:${channelCode}`,
        existing ? 'update' : 'create',
        existing ?? null,
        saved,
      )

      const analyte = await trx
        .selectFrom('analytes')
        .select('name')
        .where('code', '=', analyteCode)
        .executeTakeFirst()
      return toChannelView(saved, analyte?.name ?? null)
    })
  }

  async deleteChannel(
    tenantId: string,
    actor: CatalogueActor,
    deviceId: string,
    channelCode: string,
  ): Promise<void> {
    const db = await this.registry.forTenant(tenantId)
    await db.transaction().execute(async (trx) => {
      await this.assertDevice(trx, deviceId)

      const deleted = await trx
        .deleteFrom('device_analyte_map')
        .where('device_id', '=', deviceId)
        .where('channel_code', '=', channelCode)
        .returningAll()
        .executeTakeFirst()

      if (!deleted) {
        throw new NotFoundException(`máy này không có ánh xạ cho mã kênh "${channelCode}"`)
      }

      await this.writeAudit(
        trx,
        actor,
        'device_analyte_map',
        `${deviceId}:${channelCode}`,
        'delete',
        deleted,
        null,
      )
    })
  }

  // ------------------------------------------------------- luật ghi đè kết quả

  /**
   * Luật của một máy, kèm các luật chung (device_id NULL) vì chúng CÓ áp dụng
   * cho máy này — kỹ thuật viên cần thấy đủ những gì sẽ tác động lên kết quả
   * của máy đang xem. Trường `scope`/`scopeLabel` phân biệt hai loại.
   */
  async listResultRules(tenantId: string, deviceId: string): Promise<ResultRuleView[]> {
    const db = await this.registry.forTenant(tenantId)
    await this.assertDevice(db, deviceId)

    const rows = await db
      .selectFrom('result_mappings as r')
      .leftJoin('analytes as a', 'a.code', 'r.analyte_code')
      .select([
        'r.id',
        'r.device_id',
        'r.analyte_code',
        'r.result_in',
        'r.result_out',
        'r.comment',
        'r.color',
        'a.name as analyte_name',
      ])
      .where((eb) => eb.or([eb('r.device_id', '=', deviceId), eb('r.device_id', 'is', null)]))
      .orderBy('r.analyte_code')
      .orderBy('r.result_in')
      .execute()

    return rows.map((r) => toRuleView(r, r.analyte_name ?? null))
  }

  async createResultRule(
    tenantId: string,
    actor: CatalogueActor,
    body: unknown,
  ): Promise<ResultRuleView> {
    if (!isPlainObject(body)) throw new BadRequestException('body phải là một đối tượng JSON')

    const analyteCode = requireNonEmptyString(body.analyteCode, 'analyteCode')
    const resultIn = requireNonEmptyString(body.resultIn, 'resultIn')
    const deviceId = this.parseOptionalDeviceId(body.deviceId)
    const values = {
      device_id: deviceId,
      analyte_code: analyteCode,
      result_in: resultIn,
      result_out: 'resultOut' in body ? optionalText(body.resultOut, 'resultOut') : null,
      comment: 'comment' in body ? optionalText(body.comment, 'comment') : null,
      color: 'color' in body ? optionalInteger(body.color, 'color') : null,
    }

    const db = await this.registry.forTenant(tenantId)
    return db.transaction().execute(async (trx) => {
      if (deviceId) await this.assertDevice(trx, deviceId)

      const row = await this.runRuleWrite(analyteCode, resultIn, deviceId, async () =>
        trx.insertInto('result_mappings').values(values).returningAll().executeTakeFirstOrThrow(),
      )
      await this.writeAudit(trx, actor, 'result_mapping', row.id, 'create', null, row)
      return this.ruleViewWithName(trx, row)
    })
  }

  async updateResultRule(
    tenantId: string,
    actor: CatalogueActor,
    id: string,
    body: unknown,
  ): Promise<ResultRuleView> {
    if (!isPlainObject(body)) throw new BadRequestException('body phải là một đối tượng JSON')

    const patch: Record<string, unknown> = {}
    if ('deviceId' in body) patch.device_id = this.parseOptionalDeviceId(body.deviceId)
    if ('analyteCode' in body)
      patch.analyte_code = requireNonEmptyString(body.analyteCode, 'analyteCode')
    if ('resultIn' in body) patch.result_in = requireNonEmptyString(body.resultIn, 'resultIn')
    if ('resultOut' in body) patch.result_out = optionalText(body.resultOut, 'resultOut')
    if ('comment' in body) patch.comment = optionalText(body.comment, 'comment')
    if ('color' in body) patch.color = optionalInteger(body.color, 'color')

    if (Object.keys(patch).length === 0) {
      throw new BadRequestException('không có trường nào hợp lệ để cập nhật')
    }

    const db = await this.registry.forTenant(tenantId)
    return db.transaction().execute(async (trx) => {
      const current = await trx
        .selectFrom('result_mappings')
        .selectAll()
        .where('id', '=', id)
        .executeTakeFirst()
      if (!current) throw new NotFoundException('không tìm thấy luật ghi đè kết quả')

      const nextDeviceId = ('device_id' in patch ? patch.device_id : current.device_id) as
        | string
        | null
      if (nextDeviceId) await this.assertDevice(trx, nextDeviceId)
      const analyteCode = (patch.analyte_code as string) ?? current.analyte_code
      const resultIn = (patch.result_in as string) ?? current.result_in

      const row = await this.runRuleWrite(analyteCode, resultIn, nextDeviceId, async () =>
        trx
          .updateTable('result_mappings')
          .set(patch as never)
          .where('id', '=', id)
          .returningAll()
          .executeTakeFirstOrThrow(),
      )
      await this.writeAudit(trx, actor, 'result_mapping', id, 'update', current, row)
      return this.ruleViewWithName(trx, row)
    })
  }

  async deleteResultRule(tenantId: string, actor: CatalogueActor, id: string): Promise<void> {
    const db = await this.registry.forTenant(tenantId)
    await db.transaction().execute(async (trx) => {
      const deleted = await trx
        .deleteFrom('result_mappings')
        .where('id', '=', id)
        .returningAll()
        .executeTakeFirst()
      if (!deleted) throw new NotFoundException('không tìm thấy luật ghi đè kết quả')
      await this.writeAudit(trx, actor, 'result_mapping', id, 'delete', deleted, null)
    })
  }

  // ------------------------------------------------------------------ nội bộ

  /**
   * Ghi một dòng nhật ký. Luôn được gọi bên trong giao dịch của chính thay đổi
   * mà nó ghi vết — vết và thay đổi sống chết cùng nhau.
   */
  private async writeAudit(
    trx: Transaction<TenantDatabase>,
    actor: CatalogueActor,
    entity: AuditEntity,
    entityKey: string,
    action: AuditAction,
    before: unknown,
    after: unknown,
  ): Promise<void> {
    await trx
      .insertInto('catalogue_audit')
      .values({
        actor_user_id: actor.userId,
        actor_username: actor.username,
        entity,
        entity_key: entityKey,
        action,
        before_value: before === null || before === undefined ? null : JSON.stringify(before),
        after_value: after === null || after === undefined ? null : JSON.stringify(after),
      })
      .execute()
  }

  private async channelsOf(
    db: Kysely<TenantDatabase>,
    deviceId: string,
  ): Promise<ChannelMapView[]> {
    const rows = await db
      .selectFrom('device_analyte_map as m')
      .leftJoin('analytes as a', 'a.code', 'm.analyte_code')
      .select([
        'm.id',
        'm.device_id',
        'm.channel_code',
        'm.analyte_code',
        'm.pending_decision',
        'm.decision_candidates',
        'm.round',
        'm.allow_insert',
        'm.omit',
        'm.keep_higher_sign',
        'm.keep_lower_sign',
        'a.name as analyte_name',
      ])
      .where('m.device_id', '=', deviceId)
      .orderBy('m.channel_code')
      .execute()

    return rows.map((r) => toChannelView(r, r.analyte_name ?? null))
  }

  /**
   * Máy phải thuộc đúng database của bệnh viện đang đăng nhập. Không tìm thấy
   * thì 404 — không tiết lộ rằng id đó tồn tại ở cơ sở khác.
   */
  private async assertDevice(db: Kysely<TenantDatabase>, deviceId: string): Promise<void> {
    const found = await db
      .selectFrom('devices')
      .select('id')
      .where('id', '=', deviceId)
      .executeTakeFirst()
    if (!found) throw new NotFoundException('không tìm thấy máy xét nghiệm')
  }

  private parseOptionalDeviceId(value: unknown): string | null {
    if (value === undefined || value === null || value === '') return null
    if (typeof value !== 'string' || !UUID_RE.test(value)) {
      throw new BadRequestException('trường "deviceId" phải là uuid hoặc để trống')
    }
    return value
  }

  /** Dịch lỗi ràng buộc của Postgres sang lỗi HTTP đọc được bằng tiếng Việt. */
  private async runRuleWrite<T>(
    analyteCode: string,
    resultIn: string,
    deviceId: string | null,
    write: () => Promise<T>,
  ): Promise<T> {
    try {
      return await write()
    } catch (err) {
      const code = pgErrorCode(err)
      if (code === PG_UNIQUE_VIOLATION) {
        const scope = deviceId ? 'máy này' : 'mọi máy (luật chung)'
        throw new ConflictException(
          `đã có luật ghi đè cho xét nghiệm "${analyteCode}" với giá trị vào "${resultIn}" trên ${scope}`,
        )
      }
      if (code === PG_FOREIGN_KEY_VIOLATION) {
        throw new BadRequestException(`mã xét nghiệm "${analyteCode}" không có trong danh mục`)
      }
      throw err
    }
  }

  private async ruleViewWithName(
    db: Kysely<TenantDatabase>,
    row: ResultRuleRow,
  ): Promise<ResultRuleView> {
    const analyte = await db
      .selectFrom('analytes')
      .select('name')
      .where('code', '=', row.analyte_code)
      .executeTakeFirst()
    return toRuleView(row, analyte?.name ?? null)
  }
}

interface ResultRuleRow {
  id: string
  device_id: string | null
  analyte_code: string
  result_in: string
  result_out: string | null
  comment: string | null
  color: number | null
}

interface ChannelRow {
  id: string
  device_id: string
  channel_code: string
  analyte_code: string | null
  pending_decision: boolean
  decision_candidates: unknown
  round: number | null
  allow_insert: boolean
  omit: boolean
  keep_higher_sign: boolean
  keep_lower_sign: boolean
}

/** jsonb về từ `pg` đã là mảng; mọi hình dạng khác đều coi như không có ứng viên. */
function toCandidateList(value: unknown): string[] {
  if (!Array.isArray(value)) return []
  return value.filter((v): v is string => typeof v === 'string')
}

function toChannelView(row: ChannelRow, analyteName: string | null): ChannelMapView {
  const state =
    row.analyte_code !== null ? 'assigned' : row.pending_decision ? 'undecided' : 'unassigned'
  return {
    id: row.id,
    deviceId: row.device_id,
    channelCode: row.channel_code,
    analyteCode: row.analyte_code,
    analyteName,
    state,
    stateLabel: CHANNEL_STATE_LABEL[state],
    decisionCandidates: toCandidateList(row.decision_candidates),
    round: row.round,
    allowInsert: row.allow_insert,
    omit: row.omit,
    keepHigherSign: row.keep_higher_sign,
    keepLowerSign: row.keep_lower_sign,
  }
}

function toRuleView(row: ResultRuleRow, analyteName: string | null): ResultRuleView {
  const scope = row.device_id === null ? 'common' : 'device'
  return {
    id: row.id,
    deviceId: row.device_id,
    scope,
    scopeLabel: SCOPE_LABEL[scope],
    analyteCode: row.analyte_code,
    analyteName,
    resultIn: row.result_in,
    resultOut: row.result_out,
    comment: row.comment,
    color: row.color,
  }
}

function toAnalyteView(row: {
  code: string
  name: string
  name_en: string | null
  category: string | null
  unit: string | null
  decimals: number | null
  normal_range_text: string | null
  lower_limit: string | number | null
  higher_limit: string | number | null
  comment_lower: string | null
  comment_higher: string | null
  comment_gray: string | null
  criteria: string | null
  result_is_comment: boolean
  print_order: number | null
  is_profile_head: boolean
  source: string
  active: boolean
}): AnalyteView {
  return {
    code: row.code,
    name: row.name,
    nameEn: row.name_en,
    category: row.category,
    unit: row.unit,
    decimals: row.decimals,
    normalRangeText: row.normal_range_text,
    lowerLimit: toNumber(row.lower_limit),
    higherLimit: toNumber(row.higher_limit),
    commentLower: row.comment_lower,
    commentHigher: row.comment_higher,
    commentGray: row.comment_gray,
    criteria: row.criteria,
    resultIsComment: row.result_is_comment,
    printOrder: row.print_order,
    isProfileHead: row.is_profile_head,
    source: row.source,
    active: row.active,
  }
}
