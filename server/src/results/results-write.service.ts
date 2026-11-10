import { BadRequestException, ConflictException, Inject, Injectable, NotFoundException } from '@nestjs/common';
import { DataSource, EntityManager } from 'typeorm';
import { PatientFacts, ReferenceRange, selectRange } from '../common/reference-range';
import { applyQualitative, computeFlag, normalizeNumber, parseValue, QualitativeRule, ResultFlag } from '../common/result-value';
import { APP_CONFIG, AppConfig } from '../config';

const STATUS_PENDING = 'cho-duyet';
const STATUS_APPROVED = 'da-duyet';
const SOURCE_MANUAL = 'tay';
const SOURCE_INSTRUMENT = 'may';

/** Giá trị đã qua đường xử lý chung, sẵn sàng ghi vào `result`. */
interface Computed {
  value: string | null;
  unit: string | null;
  refLow: number | null;
  refHigh: number | null;
  refText: string | null;
  flag: ResultFlag;
}

interface ServiceRow {
  id: string;
  unit: string | null;
  value_type: string;
  decimals: number | null;
  is_panel: boolean;
}

/** Dòng chỉ định + mẫu + người bệnh + kết quả hiện có — ngữ cảnh cho mọi lần ghi. */
interface ItemContext {
  order_item_id: string;
  sample_id: string;
  service_id: string;
  approved_at: Date | null;
  sex: string;
  dob: string | null;
  birth_year: number | null;
  order_created_at: Date;
  result_id: string | null;
  value: string | null;
  pending_value: string | null;
  pending_instrument_id: string | null;
  pending_instrument_flag: string | null;
  pending_flag: string | null;
}

export interface HistoryEntry {
  at: string;
  action: string;
  oldValue: string | null;
  newValue: string | null;
  reason: string | null;
  /** null = máy / hệ thống. */
  actorName: string | null;
}

const ITEM_CONTEXT_SELECT = (resultJoin: string) => `
  SELECT oi.id AS order_item_id, oi.sample_id, oi.service_id, s.approved_at,
         p.sex, to_char(p.dob, 'YYYY-MM-DD') AS dob, p.birth_year, o.created_at AS order_created_at,
         r.id AS result_id, r.value, r.pending_value, r.pending_instrument_id, r.pending_instrument_flag, r.pending_flag
  FROM order_item oi
  JOIN sample s ON s.id = oi.sample_id
  JOIN lab_order o ON o.id = oi.order_id
  JOIN patient p ON p.id = o.patient_id
  ${resultJoin} result r ON r.order_item_id = oi.id`;

const JOIN_RESULT_TO_SAMPLE = 'JOIN order_item oi ON oi.id = r.order_item_id WHERE oi.sample_id = $1';

/**
 * Nghiệp vụ ghi kết quả phía người dùng — chuyển từ Lis.Data (ResultService + ResultWriter): nhập tay,
 * duyệt / huỷ duyệt, chấp nhận / bỏ bản máy gửi lại. Mọi thao tác nằm trong một giao dịch; thứ tự khoá
 * dùng chung toàn hệ thống (kể cả đường máy gửi của server .NET): mẫu → chỉ định → kết quả.
 */
@Injectable()
export class ResultsWriteService {
  constructor(@Inject(APP_CONFIG) private readonly config: AppConfig) {}

  /** Nhập tay một giá trị. Ô đã có giá trị thì bắt buộc lý do. Mẫu đã duyệt phải huỷ duyệt trước. */
  async setManual(db: DataSource, orderItemId: string, value: string, actorId: string, reason: string | null): Promise<void> {
    await db.transaction(async (tx) => {
      const sampleId = await this.scalar<string>(tx, 'SELECT sample_id FROM order_item WHERE id = $1', [orderItemId]);
      if (!sampleId) throw new NotFoundException('Không tìm thấy dòng chỉ định');

      // Khoá mẫu TRƯỚC bằng một câu lệnh riêng (không gộp vào JOIN) để giữ đúng thứ tự khoá chung.
      if ((await this.lockSample(tx, sampleId)) !== null) throw new ConflictException('Mẫu đã duyệt — huỷ duyệt trước khi sửa');

      const [item]: ItemContext[] = await tx.query(`${ITEM_CONTEXT_SELECT('LEFT JOIN')} WHERE oi.id = $1 FOR UPDATE OF oi, s`, [orderItemId]);
      if (!item) throw new NotFoundException('Không tìm thấy dòng chỉ định');

      const service = await this.loadService(tx, item.service_id);
      if (service.is_panel) throw new BadRequestException('Dịch vụ cha (gói) không có giá trị kết quả');

      // Chỉ số kiểu số mà gõ chuỗi không đọc được thành số thì từ chối ngay — nếu lặng lẽ lưu rỗng,
      // người nhập tưởng đã lưu và mất luôn chuỗi đã gõ.
      const trimmed = value.trim();
      if (service.value_type === 'so' && trimmed.length > 0 && parseValue(trimmed).isEmpty) {
        throw new BadRequestException(`Giá trị không hợp lệ: '${trimmed}'`);
      }

      // Đọc lại kết quả hiện có SAU khi đã giữ khoá mẫu: bản LEFT JOIN ở trên có thể cũ khi người khác vừa ghi cùng ô.
      const [existing]: { id: string; value: string | null }[] = await tx.query('SELECT id, value FROM result WHERE order_item_id = $1 FOR UPDATE', [orderItemId]);
      const oldValue = existing?.value ?? null;
      const hasOldValue = oldValue !== null && oldValue !== '';
      if (hasOldValue && !reason?.trim()) throw new BadRequestException('Nhập lý do sửa kết quả');

      const computed = await this.compute(tx, service, patientOf(item), this.orderedLocalDate(item), null, value);
      const now = new Date();
      const [row]: { id: string }[] = await tx.query(
        `INSERT INTO result (order_item_id, value, unit, ref_low, ref_high, ref_text, flag, source, instrument_id, raw_ref, instrument_flag, status, updated_at, updated_by)
         VALUES ($1, $2, $3, $4, $5, $6, $7, $8, NULL, NULL, NULL, $9, $10, $11)
         ON CONFLICT (order_item_id) DO UPDATE SET
           value = EXCLUDED.value, unit = EXCLUDED.unit, ref_low = EXCLUDED.ref_low, ref_high = EXCLUDED.ref_high, ref_text = EXCLUDED.ref_text,
           flag = EXCLUDED.flag, source = EXCLUDED.source, instrument_id = EXCLUDED.instrument_id, raw_ref = EXCLUDED.raw_ref,
           instrument_flag = EXCLUDED.instrument_flag, status = EXCLUDED.status, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
         RETURNING id`,
        [orderItemId, computed.value, computed.unit, computed.refLow, computed.refHigh, computed.refText, computed.flag, SOURCE_MANUAL, STATUS_PENDING, now, actorId]);
      await this.history(tx, row.id, hasOldValue ? 'sua' : 'nhan', oldValue, computed.value, hasOldValue ? reason!.trim() : null, actorId, now);
    });
  }

  /** Duyệt mẫu: mọi kết quả có giá trị sang đã duyệt. Mẫu đã duyệt rồi thì không làm gì. */
  async approve(db: DataSource, sampleId: string, actorId: string): Promise<void> {
    await db.transaction(async (tx) => {
      if ((await this.lockSample(tx, sampleId)) !== null) return;

      const withValue = await this.scalar<number>(tx, `SELECT count(*)::int FROM result r ${JOIN_RESULT_TO_SAMPLE} AND r.value IS NOT NULL AND r.value <> ''`, [sampleId]);
      if (!withValue) throw new ConflictException('Chưa có kết quả để duyệt');

      const now = new Date();
      await tx.query(
        `INSERT INTO result_history (result_id, action, old_value, new_value, reason, actor_id, at)
         SELECT r.id, 'duyet', NULL, r.value, NULL, $2, $3 FROM result r ${JOIN_RESULT_TO_SAMPLE} AND r.value IS NOT NULL AND r.value <> ''`,
        [sampleId, actorId, now]);
      await tx.query(
        `UPDATE result r SET status = $2, updated_at = $3, updated_by = $4 FROM order_item oi
         WHERE oi.id = r.order_item_id AND oi.sample_id = $1 AND r.value IS NOT NULL AND r.value <> ''`,
        [sampleId, STATUS_APPROVED, now, actorId]);
      await tx.query('UPDATE sample SET approved_at = $2, approved_by = $3 WHERE id = $1', [sampleId, now, actorId]);
    });
  }

  /** Huỷ duyệt (bắt buộc lý do): mọi kết quả về chờ duyệt. Mẫu chưa duyệt thì không làm gì. */
  async unapprove(db: DataSource, sampleId: string, actorId: string, reason: string): Promise<void> {
    if (!reason.trim()) throw new BadRequestException('Nhập lý do huỷ duyệt');
    await db.transaction(async (tx) => {
      if ((await this.lockSample(tx, sampleId)) === null) return;
      await this.unapproveLocked(tx, sampleId, actorId, reason.trim(), new Date());
    });
  }

  /** Dùng giá trị máy gửi lại làm giá trị chính thức, và đưa mẫu về chưa duyệt để duyệt lại. */
  async acceptPending(db: DataSource, resultId: string, actorId: string): Promise<void> {
    await db.transaction(async (tx) => {
      const item = await this.lockResult(tx, resultId);
      if (item.pending_value === null) throw new ConflictException('Không có giá trị máy gửi lại để chấp nhận');

      const service = await this.loadService(tx, item.service_id);
      // pending_value đã là giá trị tính xong lúc máy gửi — không làm tròn / quy đổi định tính lại:
      // kiểu số chỉ tính lại khoảng tham chiếu + cờ; kiểu định tính dùng thẳng cờ đã lưu (pending_flag).
      const computed = await this.computeFromFinal(
        tx, service, patientOf(item), this.orderedLocalDate(item), item.pending_instrument_id, item.pending_value, (item.pending_flag as ResultFlag | null) ?? 'None');
      const now = new Date();

      // Cờ máy gửi kèm (pending_instrument_flag) thành cờ chính thức — giá trị có cờ máy không được "sạch cờ" vì đi vòng qua đây.
      await tx.query(
        `UPDATE result SET value = $2, unit = $3, ref_low = $4, ref_high = $5, ref_text = $6, flag = $7, source = $8,
           instrument_id = pending_instrument_id, raw_ref = pending_raw_ref, instrument_flag = $9, status = $10,
           pending_value = NULL, pending_instrument_id = NULL, pending_raw_ref = NULL, pending_instrument_flag = NULL, pending_flag = NULL,
           updated_at = $11, updated_by = $12
         WHERE id = $1`,
        [resultId, computed.value, computed.unit, computed.refLow, computed.refHigh, computed.refText, computed.flag, SOURCE_INSTRUMENT,
          item.pending_instrument_flag, STATUS_PENDING, now, actorId]);
      await this.history(tx, resultId, 'thay-the', item.value, item.pending_value, null, actorId, now);

      if (item.approved_at !== null) await this.unapproveLocked(tx, item.sample_id, actorId, 'Chấp nhận bản thay thế của máy', now);
    });
  }

  /** Bỏ giá trị máy gửi lại, giữ nguyên giá trị đã duyệt. */
  async rejectPending(db: DataSource, resultId: string, actorId: string): Promise<void> {
    await db.transaction(async (tx) => {
      const item = await this.lockResult(tx, resultId);
      if (item.pending_value === null) throw new ConflictException('Không có giá trị máy gửi lại để bỏ');
      await tx.query(
        `UPDATE result SET pending_value = NULL, pending_instrument_id = NULL, pending_raw_ref = NULL, pending_instrument_flag = NULL, pending_flag = NULL WHERE id = $1`,
        [resultId]);
      await this.history(tx, resultId, 'bo-thay-the', item.pending_value, item.value, null, actorId, new Date());
    });
  }

  /** Lịch sử thao tác trên một chỉ số của mẫu (nhận, sửa, duyệt, huỷ duyệt, thay thế), mới nhất trước. */
  async itemHistory(db: DataSource, orderItemId: string): Promise<HistoryEntry[]> {
    const rows: Record<string, unknown>[] = await db.query(
      `SELECT h.at, h.action, h.old_value, h.new_value, h.reason, u.full_name AS actor_name
       FROM result_history h JOIN result r ON r.id = h.result_id LEFT JOIN app_user u ON u.id = h.actor_id
       WHERE r.order_item_id = $1 ORDER BY h.at DESC, h.created_at DESC`,
      [orderItemId]);
    return rows.map((r) => ({
      at: (r.at as Date).toISOString(), action: r.action as string, oldValue: r.old_value as string | null,
      newValue: r.new_value as string | null, reason: r.reason as string | null, actorName: r.actor_name as string | null,
    }));
  }

  // ---- đường tính giá trị dùng chung ----

  /**
   * Giá trị trống → lưu NULL (không phải "0"), cờ None. Kiểu `so`: làm tròn theo số lẻ, chọn khoảng tham chiếu,
   * tính cờ trên giá trị ĐÃ làm tròn. Kiểu `dinh-tinh`: quy đổi theo luật, cờ là cờ ghi đè của luật. Kiểu `chu`: giữ nguyên.
   */
  private async compute(tx: EntityManager, service: ServiceRow, patient: PatientFacts, at: string, instrumentId: string | null, raw: string): Promise<Computed> {
    const trimmed = raw.trim();
    const none: Computed = { value: null, unit: service.unit, refLow: null, refHigh: null, refText: null, flag: 'None' };
    if (trimmed.length === 0) return none;

    if (service.value_type === 'so') {
      const parsed = parseValue(trimmed);
      if (parsed.isEmpty) return none;
      const normalized = normalizeNumber(trimmed, parsed, service.decimals);
      const range = selectRange(await this.ranges(tx, service.id), patient, at, instrumentId);
      return { value: normalized, unit: service.unit, refLow: range?.low ?? null, refHigh: range?.high ?? null, refText: range?.textRange ?? null, flag: computeFlag(parseValue(normalized), range) };
    }

    if (service.value_type === 'dinh-tinh') {
      const { display, flag } = applyQualitative(trimmed, instrumentId, await this.qualitativeRules(tx, service.id));
      const range = selectRange(await this.ranges(tx, service.id), patient, at, instrumentId);
      return { value: display, unit: service.unit, refLow: range?.low ?? null, refHigh: range?.high ?? null, refText: range?.textRange ?? null, flag: flag ?? 'None' };
    }

    return { ...none, value: trimmed };
  }

  /** Giá trị đã tính xong từ trước (bản máy gửi lại): không làm tròn / quy đổi lại, chỉ gắn khoảng tham chiếu và cờ. */
  private async computeFromFinal(
    tx: EntityManager, service: ServiceRow, patient: PatientFacts, at: string, instrumentId: string | null, finalValue: string, pendingFlag: ResultFlag,
  ): Promise<Computed> {
    const none: Computed = { value: null, unit: service.unit, refLow: null, refHigh: null, refText: null, flag: 'None' };
    if (finalValue === '') return none;

    if (service.value_type === 'so') {
      const parsed = parseValue(finalValue);
      if (parsed.isEmpty) return none;
      const range = selectRange(await this.ranges(tx, service.id), patient, at, instrumentId);
      return { value: finalValue, unit: service.unit, refLow: range?.low ?? null, refHigh: range?.high ?? null, refText: range?.textRange ?? null, flag: computeFlag(parsed, range) };
    }
    if (service.value_type === 'dinh-tinh') {
      const range = selectRange(await this.ranges(tx, service.id), patient, at, instrumentId);
      return { value: finalValue, unit: service.unit, refLow: range?.low ?? null, refHigh: range?.high ?? null, refText: range?.textRange ?? null, flag: pendingFlag };
    }
    return { ...none, value: finalValue };
  }

  private async ranges(tx: EntityManager, serviceId: string): Promise<ReferenceRange[]> {
    const rows: Record<string, unknown>[] = await tx.query(
      `SELECT sex, age_from_days, age_to_days, instrument_id, low::float8 AS low, high::float8 AS high,
              critical_low::float8 AS critical_low, critical_high::float8 AS critical_high, text_range
       FROM reference_range WHERE service_id = $1`, [serviceId]);
    return rows.map((r) => ({
      sex: r.sex as 'M' | 'F' | null, ageFromDays: r.age_from_days as number | null, ageToDays: r.age_to_days as number | null,
      instrumentId: r.instrument_id as string | null, low: r.low as number | null, high: r.high as number | null,
      criticalLow: r.critical_low as number | null, criticalHigh: r.critical_high as number | null, textRange: r.text_range as string | null,
    }));
  }

  private async qualitativeRules(tx: EntityManager, serviceId: string): Promise<QualitativeRule[]> {
    const rows: Record<string, unknown>[] = await tx.query('SELECT instrument_id, raw_value, display_value, flag FROM qualitative_rule WHERE service_id = $1', [serviceId]);
    return rows.map((r) => ({
      instrumentId: r.instrument_id as string | null, rawValue: r.raw_value as string, displayValue: r.display_value as string, flag: r.flag as ResultFlag | null,
    }));
  }

  // ---- khoá và ghi ----

  /** Khoá dòng mẫu trong giao dịch, trả về `approved_at` hiện tại. */
  private async lockSample(tx: EntityManager, sampleId: string): Promise<Date | null> {
    const [row]: { approved_at: Date | null }[] = await tx.query('SELECT approved_at FROM sample WHERE id = $1 FOR UPDATE', [sampleId]);
    if (!row) throw new NotFoundException('Không tìm thấy mẫu');
    return row.approved_at;
  }

  /** Khoá kết quả kèm mẫu của nó (mẫu trước, kết quả sau). */
  private async lockResult(tx: EntityManager, resultId: string): Promise<ItemContext> {
    const sampleId = await this.scalar<string>(tx, 'SELECT oi.sample_id FROM result r JOIN order_item oi ON oi.id = r.order_item_id WHERE r.id = $1', [resultId]);
    if (!sampleId) throw new NotFoundException('Không tìm thấy kết quả');
    await this.lockSample(tx, sampleId);
    const [item]: ItemContext[] = await tx.query(`${ITEM_CONTEXT_SELECT('JOIN')} WHERE r.id = $1 FOR UPDATE OF oi, s, r`, [resultId]);
    if (!item) throw new NotFoundException('Không tìm thấy kết quả');
    return item;
  }

  private async unapproveLocked(tx: EntityManager, sampleId: string, actorId: string, reason: string, now: Date): Promise<void> {
    await tx.query(
      `INSERT INTO result_history (result_id, action, old_value, new_value, reason, actor_id, at)
       SELECT r.id, 'huy-duyet', NULL, r.value, $2, $3, $4 FROM result r ${JOIN_RESULT_TO_SAMPLE} AND r.status = $5`,
      [sampleId, reason, actorId, now, STATUS_APPROVED]);
    await tx.query(
      `UPDATE result r SET status = $2, updated_at = $3, updated_by = $4 FROM order_item oi WHERE oi.id = r.order_item_id AND oi.sample_id = $1`,
      [sampleId, STATUS_PENDING, now, actorId]);
    await tx.query('UPDATE sample SET approved_at = NULL, approved_by = NULL WHERE id = $1', [sampleId]);
  }

  private async loadService(tx: EntityManager, serviceId: string): Promise<ServiceRow> {
    const [service]: ServiceRow[] = await tx.query('SELECT id, unit, value_type, decimals, is_panel FROM service WHERE id = $1', [serviceId]);
    if (!service) throw new NotFoundException('Không tìm thấy dịch vụ');
    return service;
  }

  private async history(tx: EntityManager, resultId: string, action: string, oldValue: string | null, newValue: string | null, reason: string | null, actorId: string, now: Date): Promise<void> {
    await tx.query('INSERT INTO result_history (result_id, action, old_value, new_value, reason, actor_id, at) VALUES ($1, $2, $3, $4, $5, $6, $7)',
      [resultId, action, oldValue, newValue, reason, actorId, now]);
  }

  private async scalar<T>(tx: EntityManager, sql: string, params: unknown[]): Promise<T | null> {
    const [row]: Record<string, T>[] = await tx.query(sql, params);
    return row ? Object.values(row)[0] : null;
  }

  /** Ngày chỉ định theo giờ phòng khám — mốc tính tuổi khi chọn khoảng tham chiếu. */
  private orderedLocalDate(item: ItemContext): string {
    return new Date(item.order_created_at.getTime() + this.config.localUtcOffsetMinutes * 60_000).toISOString().slice(0, 10);
  }
}

const patientOf = (item: ItemContext): PatientFacts => ({ sex: item.sex, dob: item.dob, birthYear: item.birth_year });
