import {
  BadRequestException,
  ConflictException,
  Inject,
  Injectable,
  NotFoundException,
} from '@nestjs/common'
import { sql, type Kysely, type Transaction } from 'kysely'
import { escapeLikePattern } from '../catalogue/catalogue.service.js'
import { TenantRegistryService } from '../tenant/tenant-registry.service.js'
import type { PatientSex, TenantDatabase } from '../tenant/tenant-schema.js'
import {
  BARCODE_SERIAL_DIGITS,
  MIN_BARCODE_TAIL_DIGITS,
  TRUNCATED_TAIL_WINDOW_DAYS,
  formatBarcode,
  parseBarcodeTail,
} from './barcode.js'

/** Mã lỗi Postgres cần dịch sang lỗi HTTP đọc được thay vì để lộ 500. */
const PG_UNIQUE_VIOLATION = '23505'
const PG_CHECK_VIOLATION = '23514'
const PG_FOREIGN_KEY_VIOLATION = '23503'

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const SEXES: readonly PatientSex[] = ['nam', 'nu', 'khac']

export interface PatientView {
  id: string
  patientCode: string
  fullName: string
  birthDate: string | null
  birthYear: number | null
  ageYears: number | null
  ageMonths: number | null
  sex: string
  address: string | null
  phone: string | null
  hisPatientCode: string | null
  department: string | null
  patientType: string
  note: string | null
}

export interface PatientSearchResult {
  items: PatientView[]
  /** Tổng số dòng khớp bộ lọc, độc lập với limit/offset. */
  total: number
}

export interface OrderableAnalyteView {
  code: string
  name: string
  unit: string | null
  category: string | null
  /** true khi mã này là NHÓM: chỉ định nó kéo theo toàn bộ chỉ số con. */
  isProfile: boolean
  /** Số chỉ số con sẽ được thêm vào phiếu — hiện lên giao diện để khỏi bất ngờ. */
  memberCount: number
}

export interface OrderableAnalyteResult {
  items: OrderableAnalyteView[]
  total: number
}

export interface SpecimenView {
  id: string
  specimenType: string
  barcode: string
  /** bigint về từ `pg` dưới dạng chuỗi — giữ nguyên chuỗi, không ép sang number. */
  barcodeSerial: string
  status: string
  collectedAt: Date | null
}

export interface OrderItemView {
  id: string
  analyteCode: string
  analyteName: string | null
  /** Mã nhóm đã bung ra dòng này; null nếu được chỉ định lẻ. */
  parentAnalyteCode: string | null
  specimenId: string | null
  status: string
}

export interface OrderView {
  id: string
  orderNo: string
  patient: PatientView
  orderedAt: Date
  orderingDoctor: string | null
  diagnosis: string | null
  department: string | null
  status: string
  source: string
  hisOrderCode: string | null
  note: string | null
  specimens: SpecimenView[]
  items: OrderItemView[]
}

export interface BarcodeLookupResult extends OrderView {
  /** Ống đã khớp — một phiếu có nhiều ống, kết quả máy thuộc về đúng một ống. */
  matchedSpecimen: SpecimenView
  /**
   * 'exact': đuôi máy gửi trùng khít số thứ tự của ống.
   * 'truncated': máy cắt bớt chữ số; chỉ chấp nhận khi CHỈ CÓ MỘT ứng viên.
   */
  matchMode: 'exact' | 'truncated'
}

export interface PatientSearchQuery {
  q?: string | undefined
  limit: number
  offset: number
}

function pgErrorCode(err: unknown): string | undefined {
  const candidate = err as { code?: unknown; cause?: { code?: unknown } }
  if (typeof candidate?.code === 'string') return candidate.code
  if (typeof candidate?.cause?.code === 'string') return candidate.cause.code
  return undefined
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function requireNonEmptyString(value: unknown, field: string): string {
  if (typeof value !== 'string' || value.trim() === '') {
    throw new BadRequestException(`thiếu hoặc sai kiểu trường "${field}"`)
  }
  return value.trim()
}

function optionalText(value: unknown, field: string): string | null {
  if (value === null || value === undefined || value === '') return null
  if (typeof value !== 'string') throw new BadRequestException(`trường "${field}" phải là chuỗi`)
  return value.trim()
}

function optionalInteger(value: unknown, field: string): number | null {
  if (value === null || value === undefined) return null
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    throw new BadRequestException(`trường "${field}" phải là số nguyên`)
  }
  return value
}

/** `date` của Postgres về qua `pg` dưới dạng `Date`; giao diện cần `YYYY-MM-DD`. */
function toDateString(value: unknown): string | null {
  if (value === null || value === undefined) return null
  if (typeof value === 'string') return value.slice(0, 10)
  if (value instanceof Date) {
    const y = value.getFullYear()
    const m = String(value.getMonth() + 1).padStart(2, '0')
    const d = String(value.getDate()).padStart(2, '0')
    return `${y}-${m}-${d}`
  }
  return null
}

function requireUuid(value: unknown, field: string): string {
  if (typeof value !== 'string' || !UUID_RE.test(value)) {
    throw new BadRequestException(`trường "${field}" phải là uuid`)
  }
  return value
}

interface SpecimenRequest {
  specimenType: string
  analyteCodes: string[]
}

/**
 * Đọc phần `specimens` của yêu cầu tạo phiếu.
 *
 * Bắt buộc phải khai ống ngay lúc tạo phiếu, vì barcode dán theo ỐNG: một phiếu
 * chưa chia ống là một phiếu chưa in được mã nào.
 */
function parseSpecimenRequests(value: unknown): SpecimenRequest[] {
  if (!Array.isArray(value) || value.length === 0) {
    throw new BadRequestException(
      'phiếu phải có ít nhất một ống bệnh phẩm trong trường "specimens"',
    )
  }
  const seenTypes = new Set<string>()
  return value.map((entry, index) => {
    if (!isPlainObject(entry)) {
      throw new BadRequestException(`ống thứ ${index + 1} phải là một đối tượng JSON`)
    }
    const specimenType = requireNonEmptyString(entry.specimenType, `specimens[${index}].specimenType`)
    if (specimenType.length > 32) {
      throw new BadRequestException(`loại bệnh phẩm "${specimenType}" quá dài (tối đa 32 ký tự)`)
    }
    if (seenTypes.has(specimenType)) {
      throw new BadRequestException(
        `loại bệnh phẩm "${specimenType}" bị khai hai lần trong cùng một phiếu`,
      )
    }
    seenTypes.add(specimenType)

    const codes = entry.analyteCodes
    if (!Array.isArray(codes) || codes.length === 0) {
      throw new BadRequestException(`ống "${specimenType}" phải có ít nhất một dịch vụ xét nghiệm`)
    }
    return {
      specimenType,
      analyteCodes: codes.map((c, i) =>
        requireNonEmptyString(c, `specimens[${index}].analyteCodes[${i}]`),
      ),
    }
  })
}

@Injectable()
export class ReceptionService {
  // `@Inject` tường minh vì bản build test dùng esbuild (qua vitest/tsx), và
  // esbuild không phát metadata `design:paramtypes` cho `emitDecoratorMetadata`
  // — nếu chỉ dựa vào kiểu tham số, Nest sẽ tiêm `undefined` vào `registry`
  // trong im lặng và không test nào bắt được.
  constructor(@Inject(TenantRegistryService) private readonly registry: TenantRegistryService) {}

  // ------------------------------------------------------------- bệnh nhân

  async searchPatients(tenantId: string, query: PatientSearchQuery): Promise<PatientSearchResult> {
    const db = await this.registry.forTenant(tenantId)

    let base = db.selectFrom('patients')
    const q = query.q?.trim()
    if (q) {
      // Thoát ký tự đại diện MỘT lần rồi dùng cho cả ba mẫu: tên bệnh nhân và
      // mã bệnh nhân do người dùng gõ, hoàn toàn có thể chứa `%` hoặc `_`.
      const escaped = escapeLikePattern(q)
      base = base.where((eb) =>
        eb.or([
          eb('full_name', 'ilike', `%${escaped}%`),
          // Mã bệnh nhân và số điện thoại khớp theo TIỀN TỐ: người tiếp đón gõ
          // từ đầu mã hoặc đầu số, và khớp giữa chuỗi chỉ kéo về nhiễu.
          eb('patient_code', 'ilike', `${escaped}%`),
          eb('phone', 'ilike', `${escaped}%`),
          eb('his_patient_code', 'ilike', `${escaped}%`),
        ]),
      )
    }

    // Phân trang chạy phía máy chủ: danh sách bệnh nhân chỉ có lớn dần.
    const [rows, count] = await Promise.all([
      base
        .selectAll()
        .orderBy('created_at', 'desc')
        .orderBy('id')
        .limit(query.limit)
        .offset(query.offset)
        .execute(),
      base.select((eb) => eb.fn.countAll<string>().as('total')).executeTakeFirstOrThrow(),
    ])

    return { items: rows.map(toPatientView), total: Number(count.total) }
  }

  async getPatient(tenantId: string, id: string): Promise<PatientView> {
    const db = await this.registry.forTenant(tenantId)
    const row = await db
      .selectFrom('patients')
      .selectAll()
      .where('id', '=', id)
      .executeTakeFirst()
    // Không tìm thấy thì 404 — không tiết lộ rằng id đó tồn tại ở cơ sở khác.
    if (!row) throw new NotFoundException('không tìm thấy bệnh nhân')
    return toPatientView(row)
  }

  async createPatient(tenantId: string, body: unknown): Promise<PatientView> {
    if (!isPlainObject(body)) throw new BadRequestException('body phải là một đối tượng JSON')

    const values = this.readPatientFields(body, true)
    const db = await this.registry.forTenant(tenantId)

    return db.transaction().execute(async (trx) => {
      // Mã bệnh nhân sinh từ sequence, không kiểm-rồi-ghi: hai quầy tiếp đón
      // bấm lưu cùng lúc vẫn nhận hai mã khác nhau.
      const patientCode =
        (values.patient_code as string | null) ?? `BN${await nextSequenceValue(trx, 'patient_code_seq')}`

      try {
        const row = await trx
          .insertInto('patients')
          .values({ ...values, patient_code: patientCode } as never)
          .returningAll()
          .executeTakeFirstOrThrow()
        return toPatientView(row)
      } catch (err) {
        throw this.translatePatientError(err, patientCode)
      }
    })
  }

  async updatePatient(tenantId: string, id: string, body: unknown): Promise<PatientView> {
    if (!isPlainObject(body)) throw new BadRequestException('body phải là một đối tượng JSON')

    const patch = this.readPatientFields(body, false)
    if (Object.keys(patch).length === 0) {
      throw new BadRequestException('không có trường nào hợp lệ để cập nhật')
    }
    patch.updated_at = new Date()

    const db = await this.registry.forTenant(tenantId)
    try {
      const row = await db
        .updateTable('patients')
        .set(patch as never)
        .where('id', '=', id)
        .returningAll()
        .executeTakeFirst()
      if (!row) throw new NotFoundException('không tìm thấy bệnh nhân')
      return toPatientView(row)
    } catch (err) {
      if (err instanceof NotFoundException) throw err
      throw this.translatePatientError(err, String(patch.patient_code ?? ''))
    }
  }

  // ------------------------------------------- danh sách dịch vụ chọn được

  /**
   * Danh sách dịch vụ được phép đưa vào phiếu chỉ định.
   *
   * CHỈ dịch vụ xét nghiệm. Danh mục hiện tại (2619 mã) toàn là xét nghiệm,
   * nhưng khối tiếp đón về sau nhận dữ liệu từ HIS — nơi có cả dịch vụ khám —
   * nên bộ lọc `service_kind` được đặt sẵn ở đây thay vì thêm sau, và được chốt
   * lại một lần nữa bằng khoá ngoại ghép ở tầng database.
   */
  async searchOrderableAnalytes(
    tenantId: string,
    query: PatientSearchQuery,
  ): Promise<OrderableAnalyteResult> {
    const db = await this.registry.forTenant(tenantId)

    let base = db
      .selectFrom('analytes')
      .where('service_kind', '=', 'xet_nghiem')
      .where('active', '=', true)

    const q = query.q?.trim()
    if (q) {
      // 48 mã trong danh mục thật chứa `%` hoặc `_`. Không thoát thì tìm
      // `034NEU%` sẽ kéo về cả `034NEUT`.
      const escaped = escapeLikePattern(q)
      base = base.where((eb) =>
        eb.or([
          eb('code', 'ilike', `${escaped}%`),
          eb('name', 'ilike', `%${escaped}%`),
          eb('name_en', 'ilike', `%${escaped}%`),
        ]),
      )
    }

    const [rows, count] = await Promise.all([
      base
        .select(['code', 'name', 'unit', 'category'])
        .select((eb) =>
          eb
            .selectFrom('profile_members')
            .whereRef('profile_members.profile_code', '=', 'analytes.code')
            .select((e) => e.fn.countAll<string>().as('c'))
            .as('member_count'),
        )
        .orderBy('code')
        .limit(query.limit)
        .offset(query.offset)
        .execute(),
      base.select((eb) => eb.fn.countAll<string>().as('total')).executeTakeFirstOrThrow(),
    ])

    return {
      items: rows.map((r) => {
        const memberCount = Number(r.member_count ?? 0)
        return {
          code: r.code,
          name: r.name,
          unit: r.unit,
          category: r.category,
          isProfile: memberCount > 0,
          memberCount,
        }
      }),
      total: Number(count.total),
    }
  }

  // ----------------------------------------------------------------- phiếu

  async createOrder(tenantId: string, body: unknown): Promise<OrderView> {
    if (!isPlainObject(body)) throw new BadRequestException('body phải là một đối tượng JSON')

    const patientId = requireUuid(body.patientId, 'patientId')
    const specimenRequests = parseSpecimenRequests(body.specimens)
    const orderingDoctor = optionalText(body.orderingDoctor, 'orderingDoctor')
    const diagnosis = optionalText(body.diagnosis, 'diagnosis')
    const department = optionalText(body.department, 'department')
    const note = optionalText(body.note, 'note')
    const source = body.source === 'his' ? 'his' : 'lis'
    const hisOrderCode = optionalText(body.hisOrderCode, 'hisOrderCode')

    // Một dịch vụ chỉ được xuất hiện MỘT lần trong phiếu, kể cả khi người dùng
    // rải nó sang hai ống khác nhau: không có cách nào chạy cùng một xét nghiệm
    // trên hai ống mà kết quả không giẫm lên nhau.
    const seen = new Set<string>()
    for (const spec of specimenRequests) {
      for (const code of spec.analyteCodes) {
        if (seen.has(code)) {
          throw new BadRequestException(
            `dịch vụ "${code}" bị chỉ định hai lần trong cùng một phiếu`,
          )
        }
        seen.add(code)
      }
    }

    const db = await this.registry.forTenant(tenantId)
    const orderId = await db.transaction().execute(async (trx) => {
      const patient = await trx
        .selectFrom('patients')
        .select('id')
        .where('id', '=', patientId)
        .executeTakeFirst()
      if (!patient) throw new NotFoundException('không tìm thấy bệnh nhân')

      await this.assertOrderableCodes(trx, [...seen])

      const now = new Date()
      const orderNo = `PH${await nextSequenceValue(trx, 'order_no_seq')}`
      const order = await trx
        .insertInto('orders')
        .values({
          order_no: orderNo,
          patient_id: patientId,
          ordering_doctor: orderingDoctor,
          diagnosis,
          department,
          source,
          his_order_code: hisOrderCode,
          note,
        } as never)
        .returningAll()
        .executeTakeFirstOrThrow()

      for (const spec of specimenRequests) {
        // Số thứ tự lấy từ sequence: không đặt lại theo ngày, không quay vòng.
        // Đây là điều làm cho đuôi barcode xác định duy nhất một ống — xem
        // ghi chú đầu `barcode.ts`.
        const serial = await nextSequenceValue(trx, 'specimen_barcode_seq')
        const specimen = await trx
          .insertInto('specimens')
          .values({
            order_id: order.id,
            specimen_type: spec.specimenType,
            barcode: formatBarcode(serial, now),
            barcode_serial: serial,
          } as never)
          .returningAll()
          .executeTakeFirstOrThrow()

        const rows = await this.expandForSpecimen(trx, spec.analyteCodes)
        for (const row of rows) {
          await trx
            .insertInto('order_items')
            .values({
              order_id: order.id,
              specimen_id: specimen.id,
              analyte_code: row.analyteCode,
              parent_analyte_code: row.parentAnalyteCode,
            } as never)
            // Chống trùng bằng ràng buộc database chứ không kiểm-rồi-ghi.
            // Một chỉ số vừa được chỉ định lẻ vừa nằm trong nhóm chỉ vào một lần.
            .onConflict((oc) => oc.columns(['order_id', 'analyte_code']).doNothing())
            .execute()
        }
      }

      return order.id
    })

    return this.getOrder(tenantId, orderId)
  }

  async getOrder(tenantId: string, id: string): Promise<OrderView> {
    const db = await this.registry.forTenant(tenantId)
    const order = await db.selectFrom('orders').selectAll().where('id', '=', id).executeTakeFirst()
    if (!order) throw new NotFoundException('không tìm thấy phiếu chỉ định')
    return this.buildOrderView(db, order)
  }

  // ------------------------------------------------- khớp ngược theo barcode

  /**
   * Tra phiếu từ thứ máy xét nghiệm gửi về: barcode đầy đủ hoặc chỉ phần đuôi.
   *
   * Đây là chỗ hệ cũ gây ra lỗi an toàn bệnh nhân. Hệ cũ tra mờ
   * `SID LIKE '%-' + <đuôi>` trong 10 ngày rồi LẤY BẢN GHI MỚI NHẤT, nên hai
   * bệnh nhân trùng đuôi trong cửa sổ đó bị khớp nhầm mà không ai biết.
   *
   * Ở đây:
   * - Đuôi là số thứ tự KHÔNG QUAY VÒNG và UNIQUE trên toàn bảng, nên phép so
   *   khớp chính xác không thể ra hai ống. Không có cửa sổ thời gian nào.
   * - Trường hợp máy CẮT BỚT chữ số vẫn được dò, nhưng nếu có nhiều hơn một
   *   ứng viên thì TỪ CHỐI (409) và nói rõ có mấy ống — không bao giờ đoán.
   */
  async findOrderByBarcode(tenantId: string, raw: string): Promise<BarcodeLookupResult> {
    const parsed = parseBarcodeTail(raw)
    if (!parsed) {
      throw new BadRequestException(
        `không đọc được barcode "${raw}": phải là dãy chữ số, hoặc dạng "ddmmyy-<số>"`,
      )
    }
    if (parsed.digits.length < MIN_BARCODE_TAIL_DIGITS) {
      throw new BadRequestException(
        `đuôi barcode "${parsed.digits}" quá ngắn (cần ít nhất ${MIN_BARCODE_TAIL_DIGITS} chữ số) — ` +
          'từ chối tra cứu thay vì đoán một ống bất kỳ',
      )
    }

    const db = await this.registry.forTenant(tenantId)

    // Hai nhánh trong MỘT truy vấn:
    //  - `barcode_serial = <đuôi>`: so khớp chính xác trên số nguyên có index,
    //    không giới hạn thời gian. Ràng buộc UNIQUE bảo đảm tối đa một dòng.
    //  - `lpad(...) like '%<đuôi>'`: nhánh suy giảm cho máy cắt bớt chữ số, giới
    //    hạn trong cửa sổ mẫu còn có thể chạy lại. `<đuôi>` đã được
    //    `parseBarcodeTail` bảo đảm chỉ gồm chữ số nên không mang ký tự đại diện.
    const candidates = await sql<{ id: string; barcode: string; barcode_serial: string }>`
      select id, barcode, barcode_serial
        from specimens
       where barcode_serial = ${parsed.digits}::bigint
          or (lpad(barcode_serial::text, ${sql.lit(BARCODE_SERIAL_DIGITS)}, '0')
                like ${`%${parsed.digits}`}
              and created_at >= now() - make_interval(days => ${sql.lit(TRUNCATED_TAIL_WINDOW_DAYS)}))
       order by barcode_serial
       limit 5
    `.execute(db)

    const rows = candidates.rows
    if (rows.length === 0) {
      throw new NotFoundException(
        `không có ống bệnh phẩm nào mang barcode "${parsed.digits}" — kết quả này chưa gắn được vào phiếu nào`,
      )
    }
    if (rows.length > 1) {
      const list = rows.map((r) => r.barcode).join(', ')
      throw new ConflictException(
        `đuôi barcode "${parsed.digits}" nhập nhằng: khớp ${rows.length} ống (${list}). ` +
          'Từ chối khớp để kết quả không gắn nhầm bệnh nhân — hãy nhập đủ barcode.',
      )
    }

    const hit = rows[0]!
    const specimen = await db
      .selectFrom('specimens')
      .selectAll()
      .where('id', '=', hit.id)
      .executeTakeFirstOrThrow()
    const order = await db
      .selectFrom('orders')
      .selectAll()
      .where('id', '=', specimen.order_id)
      .executeTakeFirstOrThrow()

    const view = await this.buildOrderView(db, order)
    return {
      ...view,
      matchedSpecimen: toSpecimenView(specimen),
      matchMode: BigInt(hit.barcode_serial) === parsed.serial ? 'exact' : 'truncated',
    }
  }

  // ---------------------------------------------------------------- nội bộ

  /**
   * Bung một danh sách mã đã chọn thành các dòng sẽ ghi vào phiếu.
   *
   * Chỉ định một NHÓM (ví dụ `002HD` — tổng phân tích tế bào máu, 20 chỉ số con)
   * nghĩa là chỉ định cả các con: máy trả về kết quả của từng chỉ số con, nên
   * nếu con không có dòng trong phiếu thì kết quả sẽ rơi vào trạng thái "có bệnh
   * nhân nhưng chưa có chỉ định xét nghiệm đó" — đúng mã lỗi E01 của hệ cũ.
   *
   * Con được ghi CÙNG ỐNG với nhóm cha: chúng đo trên cùng một ống bệnh phẩm.
   */
  private async expandForSpecimen(
    trx: Transaction<TenantDatabase>,
    codes: string[],
  ): Promise<Array<{ analyteCode: string; parentAnalyteCode: string | null }>> {
    const out: Array<{ analyteCode: string; parentAnalyteCode: string | null }> = []
    const explicit = new Set(codes)
    for (const code of codes) out.push({ analyteCode: code, parentAnalyteCode: null })

    const members = await trx
      .selectFrom('profile_members as m')
      .innerJoin('analytes as a', 'a.code', 'm.analyte_code')
      .select(['m.profile_code', 'm.analyte_code'])
      // Chỉ số con cũng phải là dịch vụ xét nghiệm — nếu không khoá ngoại ghép
      // sẽ chặn, và chặn ở đây cho ra thông báo dễ hiểu hơn.
      .where('a.service_kind', '=', 'xet_nghiem')
      .where('m.profile_code', 'in', codes)
      .orderBy('m.profile_code')
      .orderBy('m.display_order')
      .execute()

    for (const member of members) {
      // Chỉ số đã được chọn LẺ thì giữ nguyên dòng lẻ của nó, không nhân bản:
      // ràng buộc UNIQUE (order_id, analyte_code) là chỗ chốt cuối cùng.
      if (explicit.has(member.analyte_code)) continue
      out.push({ analyteCode: member.analyte_code, parentAnalyteCode: member.profile_code })
    }
    return out
  }

  /**
   * Chốt ranh giới "chỉ dịch vụ xét nghiệm" ngay ở tầng service, để người dùng
   * nhận được thông báo dễ hiểu thay vì một lỗi khoá ngoại của Postgres.
   */
  private async assertOrderableCodes(
    trx: Transaction<TenantDatabase>,
    codes: string[],
  ): Promise<void> {
    if (codes.length === 0) return
    const rows = await trx
      .selectFrom('analytes')
      // So khớp CHÍNH XÁC bằng `in`, không dùng LIKE: 48 mã trong danh mục thật
      // chứa ký tự đại diện.
      .select(['code', 'service_kind', 'active'])
      .where('code', 'in', codes)
      .execute()

    const found = new Map(rows.map((r) => [r.code, r]))
    const missing = codes.filter((c) => !found.has(c))
    if (missing.length > 0) {
      throw new BadRequestException(
        `không có trong danh mục xét nghiệm: ${missing.join(', ')}`,
      )
    }
    const notLab = rows.filter((r) => r.service_kind !== 'xet_nghiem').map((r) => r.code)
    if (notLab.length > 0) {
      throw new BadRequestException(
        `chỉ được chỉ định dịch vụ xét nghiệm; các mã sau không phải xét nghiệm: ${notLab.join(', ')}`,
      )
    }
    const inactive = rows.filter((r) => !r.active).map((r) => r.code)
    if (inactive.length > 0) {
      throw new BadRequestException(
        `các xét nghiệm sau đã ngừng sử dụng, không chỉ định được: ${inactive.join(', ')}`,
      )
    }
  }

  private async buildOrderView(
    db: Kysely<TenantDatabase>,
    order: {
      id: string
      order_no: string
      patient_id: string
      ordered_at: Date
      ordering_doctor: string | null
      diagnosis: string | null
      department: string | null
      status: string
      source: string
      his_order_code: string | null
      note: string | null
    },
  ): Promise<OrderView> {
    const [patient, specimens, items] = await Promise.all([
      db
        .selectFrom('patients')
        .selectAll()
        .where('id', '=', order.patient_id)
        .executeTakeFirstOrThrow(),
      db
        .selectFrom('specimens')
        .selectAll()
        .where('order_id', '=', order.id)
        .orderBy('barcode_serial')
        .execute(),
      db
        .selectFrom('order_items as i')
        .leftJoin('analytes as a', 'a.code', 'i.analyte_code')
        .select([
          'i.id',
          'i.analyte_code',
          'i.parent_analyte_code',
          'i.specimen_id',
          'i.status',
          'a.name as analyte_name',
        ])
        .where('i.order_id', '=', order.id)
        .orderBy('i.analyte_code')
        .execute(),
    ])

    return {
      id: order.id,
      orderNo: order.order_no,
      patient: toPatientView(patient),
      orderedAt: order.ordered_at,
      orderingDoctor: order.ordering_doctor,
      diagnosis: order.diagnosis,
      department: order.department,
      status: order.status,
      source: order.source,
      hisOrderCode: order.his_order_code,
      note: order.note,
      specimens: specimens.map(toSpecimenView),
      items: items.map((i) => ({
        id: i.id,
        analyteCode: i.analyte_code,
        analyteName: i.analyte_name ?? null,
        parentAnalyteCode: i.parent_analyte_code,
        specimenId: i.specimen_id,
        status: i.status,
      })),
    }
  }

  /**
   * Đọc các trường của bệnh nhân từ body.
   *
   * `forCreate = true` đòi đủ trường bắt buộc; `false` chỉ nhận những trường có
   * mặt, để PATCH không xoá dữ liệu người dùng không đụng tới.
   */
  private readPatientFields(
    body: Record<string, unknown>,
    forCreate: boolean,
  ): Record<string, unknown> {
    const out: Record<string, unknown> = {}
    const has = (key: string): boolean => key in body

    if (forCreate || has('fullName')) out.full_name = requireNonEmptyString(body.fullName, 'fullName')
    if (forCreate || has('sex')) {
      const sex = requireNonEmptyString(body.sex, 'sex')
      if (!SEXES.includes(sex as PatientSex)) {
        throw new BadRequestException('trường "sex" phải là "nam", "nu" hoặc "khac"')
      }
      out.sex = sex
    }
    if (has('patientCode')) out.patient_code = optionalText(body.patientCode, 'patientCode')
    if (forCreate && !has('patientCode')) out.patient_code = null
    if (has('birthDate')) out.birth_date = this.parseBirthDate(body.birthDate)
    if (has('birthYear')) out.birth_year = optionalInteger(body.birthYear, 'birthYear')
    if (has('ageYears')) out.age_years = optionalInteger(body.ageYears, 'ageYears')
    if (has('ageMonths')) out.age_months = optionalInteger(body.ageMonths, 'ageMonths')
    if (has('address')) out.address = optionalText(body.address, 'address')
    if (has('phone')) out.phone = optionalText(body.phone, 'phone')
    if (has('hisPatientCode')) out.his_patient_code = optionalText(body.hisPatientCode, 'hisPatientCode')
    if (has('department')) out.department = optionalText(body.department, 'department')
    if (has('patientType')) {
      const value = optionalText(body.patientType, 'patientType')
      if (value !== null) out.patient_type = value
    }
    if (has('note')) out.note = optionalText(body.note, 'note')

    if (forCreate) {
      // Thực tế Việt Nam rất hay chỉ có tuổi, không có ngày sinh — chấp nhận cả
      // ba cách khai, nhưng KHÔNG chấp nhận bỏ trống hết: khoảng tham chiếu
      // theo tuổi đọc chính những cột này, thiếu là kết quả trẻ sơ sinh bị so
      // với ngưỡng người lớn.
      const hasAge =
        out.birth_date != null ||
        out.birth_year != null ||
        out.age_years != null ||
        out.age_months != null
      if (!hasAge) {
        throw new BadRequestException(
          'phải có ít nhất một trong: "birthDate", "birthYear", "ageYears", "ageMonths" — ' +
            'khoảng tham chiếu của kết quả xét nghiệm phụ thuộc vào tuổi bệnh nhân',
        )
      }
    }
    return out
  }

  private parseBirthDate(value: unknown): string | null {
    if (value === null || value === undefined || value === '') return null
    if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
      throw new BadRequestException('trường "birthDate" phải có dạng YYYY-MM-DD')
    }
    return value
  }

  /** Dịch lỗi ràng buộc của Postgres sang lỗi HTTP đọc được bằng tiếng Việt. */
  private translatePatientError(err: unknown, patientCode: string): unknown {
    const code = pgErrorCode(err)
    if (code === PG_UNIQUE_VIOLATION) {
      return new ConflictException(`mã bệnh nhân "${patientCode}" đã tồn tại`)
    }
    if (code === PG_CHECK_VIOLATION) {
      return new BadRequestException(
        'dữ liệu bệnh nhân không hợp lệ: giới tính phải là nam/nu/khac và phải có ít nhất một thông tin về tuổi',
      )
    }
    if (code === PG_FOREIGN_KEY_VIOLATION) {
      return new BadRequestException('dữ liệu bệnh nhân tham chiếu tới một bản ghi không tồn tại')
    }
    return err
  }
}

/** Lấy số kế tiếp của một sequence. Không kiểm-rồi-ghi, không thể trùng. */
async function nextSequenceValue(
  trx: Transaction<TenantDatabase>,
  sequence: 'patient_code_seq' | 'order_no_seq' | 'specimen_barcode_seq',
): Promise<string> {
  // Tên sequence là hằng trong mã nguồn (kiểu union ở trên), không đến từ input.
  const result = await sql<{ nextval: string }>`select nextval(${sql.lit(sequence)}) as nextval`.execute(
    trx,
  )
  const row = result.rows[0]
  if (!row) throw new Error(`không lấy được giá trị kế tiếp của sequence ${sequence}`)
  return String(row.nextval)
}

function toSpecimenView(row: {
  id: string
  specimen_type: string
  barcode: string
  barcode_serial: string
  status: string
  collected_at: Date | null
}): SpecimenView {
  return {
    id: row.id,
    specimenType: row.specimen_type,
    barcode: row.barcode,
    barcodeSerial: String(row.barcode_serial),
    status: row.status,
    collectedAt: row.collected_at,
  }
}

function toPatientView(row: {
  id: string
  patient_code: string
  full_name: string
  birth_date: unknown
  birth_year: number | null
  age_years: number | null
  age_months: number | null
  sex: string
  address: string | null
  phone: string | null
  his_patient_code: string | null
  department: string | null
  patient_type: string
  note: string | null
}): PatientView {
  return {
    id: row.id,
    patientCode: row.patient_code,
    fullName: row.full_name,
    birthDate: toDateString(row.birth_date),
    birthYear: row.birth_year,
    ageYears: row.age_years,
    ageMonths: row.age_months,
    sex: row.sex,
    address: row.address,
    phone: row.phone,
    hisPatientCode: row.his_patient_code,
    department: row.department,
    patientType: row.patient_type,
    note: row.note,
  }
}
