import type { AgentToCore } from '@lis/shared'
import type { LocalDb } from './local-db.js'

export interface OutboxEntry {
  rowId: number
  message: AgentToCore
}

/**
 * Hàng đợi bền của các message chờ gửi lên Core.
 *
 * Message chỉ bị xoá sau khi Core trả ack. Mất điện giữa chừng thì cùng lắm gửi
 * lại một message đã tới nơi, và Core đã idempotent nên không sao; ngược lại,
 * xoá trước khi ack là mất kết quả xét nghiệm vĩnh viễn.
 */
export class Outbox {
  constructor(private readonly db: LocalDb) {}

  enqueue(message: AgentToCore): void {
    this.db.raw
      .prepare('INSERT OR IGNORE INTO outbox (message_id, payload) VALUES (?, ?)')
      .run(message.messageId, JSON.stringify(message))
  }

  peek(limit: number): OutboxEntry[] {
    const rows = this.db.raw
      .prepare('SELECT row_id, payload FROM outbox ORDER BY row_id ASC LIMIT ?')
      .all(limit) as Array<{ row_id: number; payload: string }>
    return rows.map((row) => ({
      rowId: row.row_id,
      message: JSON.parse(row.payload) as AgentToCore,
    }))
  }

  ack(rowId: number): void {
    this.db.raw.prepare('DELETE FROM outbox WHERE row_id = ?').run(rowId)
  }

  depth(): number {
    const row = this.db.raw.prepare('SELECT COUNT(*) AS n FROM outbox').get() as { n: number }
    return row.n
  }

  /** Xoá message cũ hơn số ngày cho trước. Trả về số bản ghi đã xoá. */
  purgeOlderThan(days: number): number {
    const result = this.db.raw
      .prepare(`DELETE FROM outbox WHERE created_at < datetime('now', ?)`)
      .run(`-${days} days`)
    return result.changes
  }
}
