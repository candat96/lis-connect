import { Column, Entity, PrimaryColumn } from 'typeorm';

/** Một phòng khám trên server: mã (dùng khi đăng nhập), tên, và tên CSDL riêng của nó. */
@Entity({ name: 'clinic' })
export class Clinic {
  @PrimaryColumn('uuid') id: string;
  @Column('text', { unique: true }) code: string;
  @Column('text') name: string;
  @Column('text', { name: 'db_name', unique: true }) dbName: string;
  @Column('timestamptz', { name: 'created_at', default: () => 'now()' }) createdAt: Date;
}

/** Trạng thái kết nối một máy xét nghiệm do agent báo về. */
export interface InstrumentStatusReport {
  instrumentId: string;
  state: 'NotRunning' | 'Connecting' | 'Connected' | 'Error';
  text: string;
  lastResultAt: string | null;
  samplesReceived: number;
  pendingToRetry: number;
}

/** Một agent (máy tính cạnh máy xét nghiệm). `workstationCode` khớp `instrument.workstation_code`. */
@Entity({ name: 'agent' })
export class Agent {
  @PrimaryColumn('uuid') id: string;
  @Column('uuid', { name: 'clinic_id' }) clinicId: string;
  @Column('text') name: string;
  @Column('text', { name: 'workstation_code' }) workstationCode: string;
  @Column('text', { name: 'key_hash', select: false }) keyHash: string;
  @Column('timestamptz', { name: 'last_seen_at', nullable: true }) lastSeenAt: Date | null;
  @Column('jsonb', { name: 'last_status', nullable: true }) lastStatus: InstrumentStatusReport[] | null;
  @Column('timestamptz', { name: 'created_at', default: () => 'now()' }) createdAt: Date;
}
