import { Inject, Injectable, OnModuleDestroy, OnModuleInit } from '@nestjs/common';
import { DataSource } from 'typeorm';
import { APP_CONFIG, AppConfig } from '../config';
import { TENANT_ENTITIES } from '../entities';
import { Agent, Clinic } from './master.entities';

/**
 * Nhiều phòng khám trên một server: CSDL master giữ danh sách phòng khám + agent; mỗi phòng khám
 * có CSDL riêng (cách ly hẳn, sao lưu/chuyển độc lập). Service này giữ một DataSource TypeORM
 * cho master và một DataSource cho mỗi phòng khám đã được dùng tới.
 */
@Injectable()
export class TenancyService implements OnModuleInit, OnModuleDestroy {
  readonly master: DataSource;
  private readonly tenants = new Map<string, Promise<DataSource>>();

  constructor(@Inject(APP_CONFIG) private readonly config: AppConfig) {
    this.master = new DataSource({ type: 'postgres', url: config.masterDatabaseUrl, entities: [Clinic, Agent], synchronize: false });
  }

  async onModuleInit(): Promise<void> {
    await this.master.initialize();
  }

  async onModuleDestroy(): Promise<void> {
    for (const pending of this.tenants.values()) {
      const dataSource = await pending.catch(() => null);
      if (dataSource?.isInitialized) await dataSource.destroy();
    }
    if (this.master.isInitialized) await this.master.destroy();
  }

  findClinicByCode(code: string): Promise<Clinic | null> {
    return this.master.getRepository(Clinic).findOneBy({ code: code.trim().toLowerCase() });
  }

  findClinicById(id: string): Promise<Clinic | null> {
    return this.master.getRepository(Clinic).findOneBy({ id });
  }

  agentsOf(clinicId: string): Promise<Agent[]> {
    return this.master.getRepository(Agent).find({ where: { clinicId }, order: { name: 'ASC' } });
  }

  /** DataSource của CSDL phòng khám (mở lần đầu, dùng lại về sau). */
  dataSourceFor(clinic: Clinic): Promise<DataSource> {
    let pending = this.tenants.get(clinic.dbName);
    if (!pending) {
      const dataSource = new DataSource({
        type: 'postgres',
        url: this.config.tenantDatabaseUrlTemplate.replace('{db}', encodeURIComponent(clinic.dbName)),
        entities: TENANT_ENTITIES,
        synchronize: false,
      });
      pending = dataSource.initialize();
      this.tenants.set(clinic.dbName, pending);
      // Mở lỗi (CSDL chưa lên…) thì bỏ khỏi bộ nhớ để lần gọi sau thử lại.
      pending.catch(() => this.tenants.delete(clinic.dbName));
    }
    return pending;
  }
}
