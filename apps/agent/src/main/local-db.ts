import { mkdirSync } from 'node:fs'
import { dirname } from 'node:path'
import Database from 'better-sqlite3'

/**
 * Kho dữ liệu cục bộ của Agent: hàng đợi gửi lên Core và nhật ký.
 *
 * Dùng SQLite (không phải file JSON như cấu hình) vì đây là nơi ghi liên tục và
 * phải chịu được mất điện giữa chừng.
 */
export class LocalDb {
  private constructor(readonly raw: Database.Database) {}

  static open(path: string): LocalDb {
    mkdirSync(dirname(path), { recursive: true })
    const raw = new Database(path)
    raw.pragma('journal_mode = WAL')
    raw.exec(`
      CREATE TABLE IF NOT EXISTS outbox (
        row_id     INTEGER PRIMARY KEY AUTOINCREMENT,
        message_id TEXT NOT NULL UNIQUE,
        payload    TEXT NOT NULL,
        created_at TEXT NOT NULL DEFAULT (datetime('now'))
      );
      CREATE INDEX IF NOT EXISTS outbox_created_idx ON outbox (created_at);
    `)
    return new LocalDb(raw)
  }

  close(): void {
    this.raw.close()
  }
}
