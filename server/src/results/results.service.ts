import { Inject, Injectable, NotFoundException } from '@nestjs/common';
import { DataSource } from 'typeorm';
import { formatRange, PatientFacts, ReferenceRange, selectRange } from '../common/reference-range';
import { APP_CONFIG, AppConfig } from '../config';

export type SampleStatus = 'cho-ket-qua' | 'cho-duyet' | 'da-duyet';

export interface SampleListItem {
  sampleId: string;
  barcode: string;
  patientName: string;
  patientCode: string;
  orderedAt: string;
  specimenName: string;
  total: number;
  withValue: number;
  status: SampleStatus;
  hasAbnormal: boolean;
  /** Máy gửi lại giá trị khác cho mẫu đã duyệt — có bản chờ thay thế cần xem. */
  hasPending: boolean;
}

export type ResultFlag = 'None' | 'Low' | 'High' | 'CriticalLow' | 'CriticalHigh' | 'Abnormal';

export interface ResultRow {
  orderItemId: string;
  serviceCode: string;
  serviceName: string;
  /** Dòng tiêu đề của gói (panel) — không có giá trị. */
  isPanelHeader: boolean;
  /** 0 = dòng gốc, 1 = chỉ số con của gói. */
  depth: number;
  value: string | null;
  unit: string | null;
  refText: string | null;
  flag: ResultFlag;
  status: string | null;
  pendingValue: string | null;
  source: string | null;
}

export interface SampleDetail {
  sampleId: string;
  barcode: string;
  patient: { name: string; code: string; sex: string; dob: string | null; birthYear: number | null };
  order: { doctor: string | null; dept: string | null; diagnosis: string | null; orderedAt: string };
  approvedAt: string | null;
  /** "Tự duyệt" khi mẫu được hệ thống tự duyệt. */
  approvedBy: string | null;
  rows: ResultRow[];
}

const AUTO_APPROVED_BY = 'Tự duyệt';

/** Phần đọc của màn Kết quả — chuyển từ Lis.Data (SampleResultsQuery). Mọi thao tác ghi làm ở đợt sau. */
@Injectable()
export class ResultsService {
  constructor(@Inject(APP_CONFIG) private readonly config: AppConfig) {}

  /**
   * Mẫu có chỉ định tạo trong khoảng ngày [from, to] theo giờ phòng khám, mới nhất trước.
   * `query` khớp barcode chính xác, mã người bệnh, hoặc một phần họ tên (không phân biệt dấu/hoa thường).
   */
  async list(db: DataSource, from: string, to: string, status?: string, query?: string): Promise<SampleListItem[]> {
    const offset = this.config.localUtcOffsetMinutes * 60_000;
    const fromUtc = new Date(Date.parse(`${from}T00:00:00Z`) - offset);
    const toUtcExclusive = new Date(Date.parse(`${to}T00:00:00Z`) - offset + 86_400_000);

    const conditions = ['o.created_at >= $1 AND o.created_at < $2'];
    const params: unknown[] = [fromUtc, toUtcExclusive];

    const trimmed = query?.trim();
    if (trimmed) {
      params.push(trimmed, `%${trimmed.replace(/[\\%_]/g, '\\$&')}%`);
      conditions.push(
        `(s.barcode = $3 OR lower(p.code) = lower($3) OR unaccent(lower(p.full_name)) LIKE unaccent(lower($4)) ESCAPE '\\')`);
    }
    if (status === 'da-duyet') conditions.push('s.approved_at IS NOT NULL');
    else if (status === 'cho-duyet') conditions.push('s.approved_at IS NULL AND l.total > 0 AND l.with_value = l.total');
    else if (status === 'cho-ket-qua') conditions.push('s.approved_at IS NULL AND coalesce(l.with_value, 0) < coalesce(l.total, 0)');

    const rows: Record<string, unknown>[] = await db.query(
      `WITH l AS (
         SELECT oi.sample_id,
                count(*)::int AS total,
                count(*) FILTER (WHERE r.value IS NOT NULL AND r.value <> '')::int AS with_value,
                bool_or(coalesce(r.flag, 'None') <> 'None') AS has_abnormal,
                bool_or(r.pending_value IS NOT NULL) AS has_pending
         FROM order_item oi
         JOIN service sv ON sv.id = oi.service_id AND sv.is_panel = false
         LEFT JOIN result r ON r.order_item_id = oi.id
         GROUP BY oi.sample_id
       )
       SELECT s.id AS sample_id, s.barcode, p.full_name AS patient_name, p.code AS patient_code, o.created_at AS ordered_at,
              st.name AS specimen_name,
              coalesce(l.total, 0) AS total, coalesce(l.with_value, 0) AS with_value,
              s.approved_at IS NOT NULL AS approved,
              coalesce(l.has_abnormal, false) AS has_abnormal,
              coalesce(l.has_pending, false) AS has_pending
       FROM sample s
       JOIN lab_order o ON o.id = s.order_id
       JOIN patient p ON p.id = o.patient_id
       JOIN specimen_type st ON st.id = s.specimen_type_id
       LEFT JOIN l ON l.sample_id = s.id
       WHERE ${conditions.join(' AND ')}
       ORDER BY o.created_at DESC, s.barcode DESC
       LIMIT 500`,
      params,
    );

    return rows.map((row) => {
      const total = row.total as number;
      const withValue = row.with_value as number;
      return {
        sampleId: row.sample_id as string,
        barcode: row.barcode as string,
        patientName: row.patient_name as string,
        patientCode: row.patient_code as string,
        orderedAt: (row.ordered_at as Date).toISOString(),
        specimenName: row.specimen_name as string,
        total,
        withValue,
        status: row.approved ? 'da-duyet' : total > 0 && withValue === total ? 'cho-duyet' : 'cho-ket-qua',
        hasAbnormal: row.has_abnormal as boolean,
        hasPending: row.has_pending as boolean,
      };
    });
  }

  /**
   * Bảng chỉ số của một mẫu: dòng gói rồi các chỉ số con thụt vào, theo thứ tự in. Chỉ số chưa
   * có kết quả (hoặc kết quả ghi khi danh mục chưa có khoảng tham chiếu) lấy khoảng hiện tại của
   * danh mục theo giới/tuổi người bệnh và đơn vị của chỉ số để hiển thị.
   */
  async detail(db: DataSource, sampleId: string): Promise<SampleDetail> {
    const [sample]: Record<string, unknown>[] = await db.query(
      `SELECT s.id, s.barcode, s.approved_at, u.full_name AS approver_name,
              p.full_name AS patient_name, p.code AS patient_code, p.sex, to_char(p.dob, 'YYYY-MM-DD') AS dob, p.birth_year,
              o.ordering_doctor, o.ordering_dept, o.diagnosis, o.created_at AS ordered_at
       FROM sample s
       JOIN lab_order o ON o.id = s.order_id
       JOIN patient p ON p.id = o.patient_id
       LEFT JOIN app_user u ON u.id = s.approved_by
       WHERE s.id = $1`,
      [sampleId],
    );
    if (!sample) throw new NotFoundException('Không tìm thấy mẫu');

    const items: ItemRow[] = await db.query(
      `SELECT oi.id AS order_item_id, oi.parent_item_id,
              sv.id AS service_id, sv.code AS service_code, sv.name AS service_name, sv.is_panel, sv.decimals, sv.unit AS catalog_unit,
              CASE WHEN oi.parent_item_id IS NULL THEN coalesce(sv.print_order, 0) ELSE coalesce(psv.print_order, 0) END AS top_print_order,
              coalesce(psv.code, sv.code) AS top_code,
              coalesce(sc.sort_order, 0) AS child_sort_order,
              r.value, r.unit, r.ref_low::float8 AS ref_low, r.ref_high::float8 AS ref_high, r.ref_text, r.flag, r.status,
              r.pending_value, r.source, r.instrument_id
       FROM order_item oi
       JOIN service sv ON sv.id = oi.service_id
       LEFT JOIN order_item poi ON poi.id = oi.parent_item_id
       LEFT JOIN service psv ON psv.id = poi.service_id
       LEFT JOIN service_child sc ON sc.parent_id = poi.service_id AND sc.child_id = oi.service_id
       LEFT JOIN result r ON r.order_item_id = oi.id
       WHERE oi.sample_id = $1`,
      [sampleId],
    );

    const patient: PatientFacts = { sex: sample.sex as string, dob: sample.dob as string | null, birthYear: sample.birth_year as number | null };

    const missing = [...new Set(items.filter(needsCatalogRange).map((i) => i.service_id))];
    const rangesByService = new Map<string, (ReferenceRange & { serviceId: string })[]>();
    if (missing.length > 0) {
      const ranges: Record<string, unknown>[] = await db.query(
        `SELECT service_id, sex, age_from_days, age_to_days, instrument_id,
                low::float8 AS low, high::float8 AS high, critical_low::float8 AS critical_low, critical_high::float8 AS critical_high, text_range
         FROM reference_range WHERE service_id = ANY($1)`,
        [missing],
      );
      for (const r of ranges) {
        const range = {
          serviceId: r.service_id as string, sex: r.sex as 'M' | 'F' | null,
          ageFromDays: r.age_from_days as number | null, ageToDays: r.age_to_days as number | null,
          instrumentId: r.instrument_id as string | null, low: r.low as number | null, high: r.high as number | null,
          criticalLow: r.critical_low as number | null, criticalHigh: r.critical_high as number | null, textRange: r.text_range as string | null,
        };
        rangesByService.set(range.serviceId, [...(rangesByService.get(range.serviceId) ?? []), range]);
      }
    }
    const today = new Date(Date.now() + this.config.localUtcOffsetMinutes * 60_000).toISOString().slice(0, 10);

    const rows = [...items]
      .sort((a, b) =>
        a.top_print_order - b.top_print_order
        || compareOrdinal(a.top_code, b.top_code)
        || (a.parent_item_id === null ? 0 : 1) - (b.parent_item_id === null ? 0 : 1)
        || a.child_sort_order - b.child_sort_order
        || compareOrdinal(a.service_code, b.service_code))
      .map((item): ResultRow => {
        let { ref_low: low, ref_high: high, ref_text: text } = item;
        if (needsCatalogRange(item)) {
          const range = selectRange(rangesByService.get(item.service_id) ?? [], patient, today, item.instrument_id);
          if (range) ({ low, high, textRange: text } = range);
        }
        return {
          orderItemId: item.order_item_id,
          serviceCode: item.service_code,
          serviceName: item.service_name,
          isPanelHeader: item.is_panel,
          depth: item.parent_item_id === null ? 0 : 1,
          value: item.is_panel ? null : item.value,
          unit: item.is_panel ? null : item.unit?.trim() ? item.unit : item.catalog_unit?.trim() ? item.catalog_unit : null,
          refText: item.is_panel ? null : formatRange(text, low, high, item.decimals),
          flag: (item.flag as ResultFlag | null) ?? 'None',
          status: item.status,
          pendingValue: item.pending_value,
          source: item.source,
        };
      });

    const approvedAt = sample.approved_at as Date | null;
    return {
      sampleId: sample.id as string,
      barcode: sample.barcode as string,
      patient: {
        name: sample.patient_name as string, code: sample.patient_code as string, sex: patient.sex, dob: patient.dob, birthYear: patient.birthYear,
      },
      order: {
        doctor: sample.ordering_doctor as string | null, dept: sample.ordering_dept as string | null,
        diagnosis: sample.diagnosis as string | null, orderedAt: (sample.ordered_at as Date).toISOString(),
      },
      approvedAt: approvedAt?.toISOString() ?? null,
      approvedBy: approvedAt === null ? null : (sample.approver_name as string | null) ?? AUTO_APPROVED_BY,
      rows,
    };
  }
}

interface ItemRow {
  order_item_id: string;
  parent_item_id: string | null;
  service_id: string;
  service_code: string;
  service_name: string;
  is_panel: boolean;
  decimals: number | null;
  catalog_unit: string | null;
  top_print_order: number;
  top_code: string;
  child_sort_order: number;
  value: string | null;
  unit: string | null;
  ref_low: number | null;
  ref_high: number | null;
  ref_text: string | null;
  flag: string | null;
  status: string | null;
  pending_value: string | null;
  source: string | null;
  instrument_id: string | null;
}

const needsCatalogRange = (item: ItemRow) => !item.is_panel && item.ref_text === null && item.ref_low === null && item.ref_high === null;

/** So chuỗi theo mã ký tự (như StringComparer.Ordinal bên .NET) — không theo locale. */
const compareOrdinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
