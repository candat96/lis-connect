import {
  BadRequestException,
  ConflictException,
  Inject,
  Injectable,
  NotFoundException,
} from '@nestjs/common'
import type { Kysely } from 'kysely'
import { TenantRegistryService } from '../tenant/tenant-registry.service.js'
import type { TenantDatabase } from '../tenant/tenant-schema.js'

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
  if (typeof value !== 'boolean') throw new BadRequestException(`trường "${field}" phải là true/false`)
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
      const pattern = `%${escapeLikePattern(q)}%`
      // Mã so khớp BẰNG, không LIKE: mã là khoá nghiệp vụ và chứa ký tự đại
      // diện. Tên thì ILIKE, nhưng trên chuỗi đã được thoát.
      base = base.where((eb) =>
        eb.or([
          eb('code', '=', q),
          eb('name', 'ilike', pattern),
          eb('name_en', 'ilike', pattern),
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
      base
        .select((eb) => eb.fn.countAll<string>().as('total'))
        .executeTakeFirstOrThrow(),
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
    const updated = await db
      .updateTable('analytes')
      .set(patch as never)
      .where('code', '=', code)
      .returningAll()
      .executeTakeFirst()

    if (!updated) throw new NotFoundException(`không tìm thấy xét nghiệm có mã "${code}"`)
    return toAnalyteView(updated)
  }

  // ------------------------------------------------------------- kênh của máy

  async listChannels(tenantId: string, deviceId: string): Promise<ChannelMapView[]> {
    const db = await this.registry.forTenant(tenantId)
    await this.assertDevice(db, deviceId)

    const rows = await db
      .selectFrom('device_analyte_map as m')
      .leftJoin('analytes as a', 'a.code', 'm.analyte_code')
      .select([
        'm.id',
        'm.device_id',
        'm.channel_code',
        'm.analyte_code',
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

    return rows.map((r) => ({
      id: r.id,
      deviceId: r.device_id,
      channelCode: r.channel_code,
      analyteCode: r.analyte_code,
      analyteName: r.analyte_name ?? null,
      round: r.round,
      allowInsert: r.allow_insert,
      omit: r.omit,
      keepHigherSign: r.keep_higher_sign,
      keepLowerSign: r.keep_lower_sign,
    }))
  }

  /**
   * Tạo hoặc sửa ánh xạ kênh.
   *
   * Gán một xét nghiệm KHÁC vào mã kênh đang có bị từ chối 409 thay vì ghi đè
   * trong im lặng: hệ cũ cho hai xét nghiệm dùng chung một kênh rồi chọn bừa
   * theo alphabet, và chính chỗ đó làm kết quả về nhầm ô. Muốn đổi thì phải
   * xoá ánh xạ cũ trước — một hành động có chủ ý.
   */
  async putChannel(
    tenantId: string,
    deviceId: string,
    channelCode: string,
    body: unknown,
  ): Promise<ChannelMapView> {
    if (!isPlainObject(body)) throw new BadRequestException('body phải là một đối tượng JSON')
    const analyteCode = requireNonEmptyString(body.analyteCode, 'analyteCode')

    const db = await this.registry.forTenant(tenantId)
    await this.assertDevice(db, deviceId)

    const existing = await db
      .selectFrom('device_analyte_map')
      .selectAll()
      .where('device_id', '=', deviceId)
      .where('channel_code', '=', channelCode)
      .executeTakeFirst()

    if (existing && existing.analyte_code !== analyteCode) {
      throw new ConflictException(
        `mã kênh "${channelCode}" của máy này đã gán cho xét nghiệm "${existing.analyte_code}". ` +
          `Hãy xoá ánh xạ cũ trước khi gán "${analyteCode}".`,
      )
    }

    const values: Record<string, unknown> = { analyte_code: analyteCode }
    if ('round' in body) values.round = optionalInteger(body.round, 'round')
    if ('allowInsert' in body) values.allow_insert = optionalBoolean(body.allowInsert, 'allowInsert')
    if ('omit' in body) values.omit = optionalBoolean(body.omit, 'omit')
    if ('keepHigherSign' in body)
      values.keep_higher_sign = optionalBoolean(body.keepHigherSign, 'keepHigherSign')
    if ('keepLowerSign' in body)
      values.keep_lower_sign = optionalBoolean(body.keepLowerSign, 'keepLowerSign')

    try {
      if (existing) {
        await db
          .updateTable('device_analyte_map')
          .set(values as never)
          .where('id', '=', existing.id)
          .execute()
      } else {
        await db
          .insertInto('device_analyte_map')
          .values({ ...values, device_id: deviceId, channel_code: channelCode } as never)
          .execute()
      }
    } catch (err) {
      // Lưới an toàn cho tình huống hai request cùng tạo một kênh: kiểm tra ở
      // trên không nguyên tử, ràng buộc UNIQUE mới là chỗ chốt.
      if (pgErrorCode(err) === PG_UNIQUE_VIOLATION) {
        throw new ConflictException(
          `mã kênh "${channelCode}" của máy này vừa được gán cho một xét nghiệm khác`,
        )
      }
      if (pgErrorCode(err) === PG_FOREIGN_KEY_VIOLATION) {
        throw new BadRequestException(`mã xét nghiệm "${analyteCode}" không có trong danh mục`)
      }
      throw err
    }

    const rows = await this.listChannels(tenantId, deviceId)
    const saved = rows.find((r) => r.channelCode === channelCode)
    if (!saved) throw new NotFoundException(`không tìm thấy mã kênh "${channelCode}"`)
    return saved
  }

  async deleteChannel(tenantId: string, deviceId: string, channelCode: string): Promise<void> {
    const db = await this.registry.forTenant(tenantId)
    await this.assertDevice(db, deviceId)

    const deleted = await db
      .deleteFrom('device_analyte_map')
      .where('device_id', '=', deviceId)
      .where('channel_code', '=', channelCode)
      .returning('id')
      .executeTakeFirst()

    if (!deleted) {
      throw new NotFoundException(`máy này không có ánh xạ cho mã kênh "${channelCode}"`)
    }
  }

  // ------------------------------------------------------- luật ghi đè kết quả

  /**
   * Luật của một máy, kèm các luật chung (device_id NULL) vì chúng cũng áp
   * dụng cho máy này — kỹ thuật viên cần thấy đủ những gì sẽ tác động lên kết
   * quả của máy đang xem. Trường `deviceId` phân biệt hai loại.
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

    return rows.map((r) => ({
      id: r.id,
      deviceId: r.device_id,
      analyteCode: r.analyte_code,
      analyteName: r.analyte_name ?? null,
      resultIn: r.result_in,
      resultOut: r.result_out,
      comment: r.comment,
      color: r.color,
    }))
  }

  async createResultRule(tenantId: string, body: unknown): Promise<ResultRuleView> {
    if (!isPlainObject(body)) throw new BadRequestException('body phải là một đối tượng JSON')

    const analyteCode = requireNonEmptyString(body.analyteCode, 'analyteCode')
    const resultIn = requireNonEmptyString(body.resultIn, 'resultIn')
    const deviceId = this.parseOptionalDeviceId(body.deviceId)

    const db = await this.registry.forTenant(tenantId)
    if (deviceId) await this.assertDevice(db, deviceId)

    const values = {
      device_id: deviceId,
      analyte_code: analyteCode,
      result_in: resultIn,
      result_out: 'resultOut' in body ? optionalText(body.resultOut, 'resultOut') : null,
      comment: 'comment' in body ? optionalText(body.comment, 'comment') : null,
      color: 'color' in body ? optionalInteger(body.color, 'color') : null,
    }

    const row = await this.runRuleWrite(analyteCode, resultIn, deviceId, async () =>
      db.insertInto('result_mappings').values(values).returningAll().executeTakeFirstOrThrow(),
    )
    return this.toRuleView(db, row)
  }

  async updateResultRule(tenantId: string, id: string, body: unknown): Promise<ResultRuleView> {
    if (!isPlainObject(body)) throw new BadRequestException('body phải là một đối tượng JSON')

    const db = await this.registry.forTenant(tenantId)
    const current = await db
      .selectFrom('result_mappings')
      .selectAll()
      .where('id', '=', id)
      .executeTakeFirst()
    if (!current) throw new NotFoundException('không tìm thấy luật ghi đè kết quả')

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

    const nextDeviceId = ('device_id' in patch ? patch.device_id : current.device_id) as
      | string
      | null
    if (nextDeviceId) await this.assertDevice(db, nextDeviceId)
    const analyteCode = (patch.analyte_code as string) ?? current.analyte_code
    const resultIn = (patch.result_in as string) ?? current.result_in

    const row = await this.runRuleWrite(analyteCode, resultIn, nextDeviceId, async () =>
      db
        .updateTable('result_mappings')
        .set(patch as never)
        .where('id', '=', id)
        .returningAll()
        .executeTakeFirstOrThrow(),
    )
    return this.toRuleView(db, row)
  }

  async deleteResultRule(tenantId: string, id: string): Promise<void> {
    const db = await this.registry.forTenant(tenantId)
    const deleted = await db
      .deleteFrom('result_mappings')
      .where('id', '=', id)
      .returning('id')
      .executeTakeFirst()
    if (!deleted) throw new NotFoundException('không tìm thấy luật ghi đè kết quả')
  }

  // ------------------------------------------------------------------ nội bộ

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

  private async toRuleView(
    db: Kysely<TenantDatabase>,
    row: {
      id: string
      device_id: string | null
      analyte_code: string
      result_in: string
      result_out: string | null
      comment: string | null
      color: number | null
    },
  ): Promise<ResultRuleView> {
    const analyte = await db
      .selectFrom('analytes')
      .select('name')
      .where('code', '=', row.analyte_code)
      .executeTakeFirst()
    return {
      id: row.id,
      deviceId: row.device_id,
      analyteCode: row.analyte_code,
      analyteName: analyte?.name ?? null,
      resultIn: row.result_in,
      resultOut: row.result_out,
      comment: row.comment,
      color: row.color,
    }
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
