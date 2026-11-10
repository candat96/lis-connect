import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

/** Cấu hình server đọc từ biến môi trường (và tệp `.env` cạnh package.json khi chạy dev). */
export interface AppConfig {
  port: number;
  masterDatabaseUrl: string;
  /** Chuỗi kết nối CSDL phòng khám, `{db}` thay bằng tên CSDL. */
  tenantDatabaseUrlTemplate: string;
  jwtSecret: string;
  /** Số phút lệch UTC của giờ phòng khám — dùng khi lọc "theo ngày". */
  localUtcOffsetMinutes: number;
}

/** Nạp `.env` rất đơn giản (KEY=VALUE mỗi dòng) — không ghi đè biến đã có sẵn trong môi trường. */
function loadDotEnv(): void {
  const file = join(process.cwd(), '.env');
  if (!existsSync(file)) return;
  for (const line of readFileSync(file, 'utf8').split(/\r?\n/)) {
    const match = /^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/.exec(line);
    if (match && process.env[match[1]] === undefined) process.env[match[1]] = match[2];
  }
}

function required(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`Thiếu biến môi trường ${name} (xem .env.example)`);
  return value;
}

export function loadConfig(): AppConfig {
  loadDotEnv();
  const jwtSecret = required('JWT_SECRET');
  if (jwtSecret === 'CHANGE_ME' || jwtSecret.length < 24) {
    throw new Error('JWT_SECRET phải là chuỗi ngẫu nhiên dài ít nhất 24 ký tự, không dùng giá trị mẫu');
  }
  return {
    port: Number(process.env.PORT ?? 8084),
    masterDatabaseUrl: required('MASTER_DATABASE_URL'),
    tenantDatabaseUrlTemplate: required('TENANT_DATABASE_URL_TEMPLATE'),
    jwtSecret,
    localUtcOffsetMinutes: Number(process.env.LOCAL_UTC_OFFSET_MINUTES ?? 420),
  };
}

export const APP_CONFIG = Symbol('APP_CONFIG');
