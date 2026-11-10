import { Column, Entity, PrimaryColumn } from 'typeorm';

// Entity của CSDL phòng khám. Cấu trúc bảng do các tệp migration SQL quyết định (nguồn sự thật,
// dùng chung với agent/app .NET) — ở đây chỉ ánh xạ để đọc/ghi, `synchronize` luôn tắt.

/** numeric của Postgres ↔ number (khoảng tham chiếu, hệ số: không cần độ chính xác tuỳ ý). */
const numeric = {
  to: (value: number | null) => value,
  from: (value: string | null) => (value === null ? null : Number(value)),
};

@Entity({ name: 'app_user' })
export class AppUser {
  @PrimaryColumn('uuid') id: string;
  @Column('citext') username: string;
  @Column('text', { name: 'full_name' }) fullName: string;
  @Column('text', { nullable: true }) title: string | null;
  @Column('text', { name: 'password_hash', select: false }) passwordHash: string;
  @Column('text', { array: true }) roles: string[];
  @Column('bool') active: boolean;
  @Column('int', { name: 'failed_count' }) failedCount: number;
  @Column('timestamptz', { name: 'locked_until', nullable: true }) lockedUntil: Date | null;
  @Column('timestamptz', { name: 'last_login_at', nullable: true }) lastLoginAt: Date | null;
}

@Entity({ name: 'specimen_type' })
export class SpecimenType {
  @PrimaryColumn('uuid') id: string;
  @Column('text') code: string;
  @Column('text') name: string;
}

@Entity({ name: 'service' })
export class Service {
  @PrimaryColumn('uuid') id: string;
  @Column('text') code: string;
  @Column('text') name: string;
  @Column('text', { name: 'group_name', nullable: true }) groupName: string | null;
  @Column('uuid', { name: 'specimen_type_id', nullable: true }) specimenTypeId: string | null;
  @Column('text', { nullable: true }) unit: string | null;
  /** 'so' | 'dinh-tinh' | 'chu' */
  @Column('text', { name: 'value_type' }) valueType: string;
  @Column('int', { nullable: true }) decimals: number | null;
  @Column('int', { name: 'print_order', nullable: true }) printOrder: number | null;
  @Column('bool', { name: 'is_panel' }) isPanel: boolean;
  @Column('bool', { name: 'auto_approve' }) autoApprove: boolean;
  @Column('bool') active: boolean;
  @Column('text', { name: 'his_code', nullable: true }) hisCode: string | null;
}

@Entity({ name: 'service_child' })
export class ServiceChild {
  @PrimaryColumn('uuid', { name: 'parent_id' }) parentId: string;
  @PrimaryColumn('uuid', { name: 'child_id' }) childId: string;
  @Column('int', { name: 'sort_order' }) sortOrder: number;
}

@Entity({ name: 'reference_range' })
export class ReferenceRangeEntity {
  @PrimaryColumn('uuid') id: string;
  @Column('uuid', { name: 'service_id' }) serviceId: string;
  @Column('text', { nullable: true }) sex: 'M' | 'F' | null;
  @Column('int', { name: 'age_from_days', nullable: true }) ageFromDays: number | null;
  @Column('int', { name: 'age_to_days', nullable: true }) ageToDays: number | null;
  @Column('uuid', { name: 'instrument_id', nullable: true }) instrumentId: string | null;
  @Column('numeric', { nullable: true, transformer: numeric }) low: number | null;
  @Column('numeric', { nullable: true, transformer: numeric }) high: number | null;
  @Column('numeric', { name: 'critical_low', nullable: true, transformer: numeric }) criticalLow: number | null;
  @Column('numeric', { name: 'critical_high', nullable: true, transformer: numeric }) criticalHigh: number | null;
  @Column('text', { name: 'text_range', nullable: true }) textRange: string | null;
}

@Entity({ name: 'instrument' })
export class Instrument {
  @PrimaryColumn('uuid') id: string;
  @Column('text') name: string;
  @Column('text', { nullable: true }) model: string | null;
  @Column('text', { name: 'protocol_family', nullable: true }) protocolFamily: string | null;
  @Column('jsonb', { name: 'driver_config', nullable: true }) driverConfig: unknown;
  @Column('jsonb', { nullable: true }) connection: unknown;
  @Column('text', { name: 'workstation_code', nullable: true }) workstationCode: string | null;
  @Column('bool') active: boolean;
}

@Entity({ name: 'instrument_channel' })
export class InstrumentChannel {
  @PrimaryColumn('uuid') id: string;
  @Column('uuid', { name: 'instrument_id' }) instrumentId: string;
  @Column('text', { name: 'channel_code' }) channelCode: string;
  @Column('uuid', { name: 'service_id' }) serviceId: string;
  @Column('int', { nullable: true }) decimals: number | null;
  @Column('numeric', { nullable: true, transformer: numeric }) factor: number | null;
  @Column('bool', { name: 'keep_comparator' }) keepComparator: boolean;
  @Column('bool', { name: 'send_on_query' }) sendOnQuery: boolean;
}

export const TENANT_ENTITIES = [AppUser, SpecimenType, Service, ServiceChild, ReferenceRangeEntity, Instrument, InstrumentChannel];
