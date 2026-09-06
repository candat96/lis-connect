import type { AgentToCore } from '@lis/shared'
import type { LocalDb } from './local-db.js'

export interface OutboxEntry {
  rowId: number
  message: AgentToCore
}

/**
 * Trần số lượng message trong hàng đợi trước khi cảnh báo "hàng đợi quá đầy".
 *
 * KHÔNG phải hạn xoá — vượt trần chỉ ghi log cảnh báo và phơi ra qua
 * `agent:status` để giao diện hiển thị, không bao giờ tự xoá message (spec
 * 6.2: đầy thì cảnh báo lớn, không âm thầm xóa). Con số 50.000 được chọn vì:
 * mỗi message chỉ vài trăm byte tới vài KB (kết quả HL7/ASTM thô), nên 50.000
 * bản ghi tốn cỡ vài chục MB — nhỏ so với ổ đĩa bất kỳ máy phòng xét nghiệm
 * nào — trong khi một Core mất liên lạc 7 ngày với vài máy chạy hết công suất
 * hiếm khi vượt qua con số này; nếu vượt, đó là dấu hiệu Core đã mất liên lạc
 * bất thường lâu và cần người can thiệp, không phải lý do để xoá dữ liệu bệnh
 * nhân.
 */
export const QUEUE_WARNING_THRESHOLD = 50_000

/**
 * Hàng đợi bền của các message chờ gửi lên Core.
 *
 * Message chỉ bị xoá sau khi Core trả ack. Mất điện giữa chừng thì cùng lắm gửi
 * lại một message đã tới nơi, và Core đã idempotent nên không sao; ngược lại,
 * xoá trước khi ack là mất kết quả xét nghiệm vĩnh viễn.
 *
 * Hàng đợi KHÔNG bao giờ tự xoá theo tuổi: `purgeOlderThan` tồn tại như một
 * công cụ vận hành thủ công (một kỹ sư chạy tay khi thật sự cần dọn), không
 * bao giờ được gọi tự động từ luồng khởi động — xem lịch sử ở đầu file này:
 * bản cũ gọi `purgeOlderThan(7)` mỗi lần khởi động, hiểu "giữ tối thiểu 7
 * ngày" (spec 6.2) thành "hạn xoá sau 7 ngày", và dùng đồng hồ hệ thống của
 * chính PC phòng xét nghiệm — một PC hết pin CMOS nhảy về năm 2000 rồi được
 * chỉnh lại sẽ xoá sạch hàng đợi ngay lần khởi động kế tiếp.
 */
export class Outbox {
  constructor(private readonly db: LocalDb) {}

  /** true nếu hàng đợi đã vượt trần cảnh báo — không xoá gì, chỉ để báo động. */
  isOverThreshold(): boolean {
    return this.depth() > QUEUE_WARNING_THRESHOLD
  }

  enqueue(message: AgentToCore): void {
    this.db.raw
      .prepare('INSERT OR IGNORE INTO outbox (message_id, payload) VALUES (?, ?)')
      .run(message.messageId, JSON.stringify(message))
  }

  /** Chỉ trả message CHƯA bị cách ly — message cách ly không bao giờ được tự gửi lại. */
  peek(limit: number): OutboxEntry[] {
    const rows = this.db.raw
      .prepare('SELECT row_id, payload FROM outbox WHERE quarantined = 0 ORDER BY row_id ASC LIMIT ?')
      .all(limit) as Array<{ row_id: number; payload: string }>
    return rows.map((row) => ({
      rowId: row.row_id,
      message: JSON.parse(row.payload) as AgentToCore,
    }))
  }

  ack(rowId: number): void {
    this.db.raw.prepare('DELETE FROM outbox WHERE row_id = ?').run(rowId)
  }

  /** Số message đang chờ gửi (không tính message đã bị cách ly). */
  depth(): number {
    const row = this.db.raw
      .prepare('SELECT COUNT(*) AS n FROM outbox WHERE quarantined = 0')
      .get() as { n: number }
    return row.n
  }

  /** Số message đang ở trạng thái cách ly — không mất, chỉ chờ người kiểm tra. */
  quarantinedCount(): number {
    const row = this.db.raw
      .prepare('SELECT COUNT(*) AS n FROM outbox WHERE quarantined = 1')
      .get() as { n: number }
    return row.n
  }

  /**
   * Ghi nhận một lần thử gửi message này KHÔNG có phản hồi khớp từ Core (ví
   * dụ: Core trả nack với messageId không xác định được, nên không có cách
   * nào chắc chắn ánh xạ ngược lại đúng dòng đã gửi).
   *
   * Không bao giờ xoá: sau `maxAttempts` lần, chuyển sang "cách ly"
   * (quarantined) để nó không còn được peek() lấy ra thử lại nữa — nếu
   * không, cùng vài message hỏng ở đầu hàng đợi sẽ chặn mọi kết quả phía sau
   * vĩnh viễn trong khi vẫn báo queueDepth tăng dần mà không ai biết vì sao.
   *
   * Trả về true nếu dòng này VỪA bị chuyển sang cách ly ở lần gọi này (để nơi
   * gọi ghi log cảnh báo); false nếu chưa tới ngưỡng, hoặc dòng đã bị ack/xoá
   * trước khi kịp gọi hàm này (không còn gì để đánh dấu).
   */
  recordFailedAttempt(rowId: number, maxAttempts: number): boolean {
    const row = this.db.raw
      .prepare('SELECT attempts FROM outbox WHERE row_id = ?')
      .get(rowId) as { attempts: number } | undefined
    if (!row) return false

    const attempts = row.attempts + 1
    const quarantine = attempts >= maxAttempts
    this.db.raw
      .prepare('UPDATE outbox SET attempts = ?, quarantined = ? WHERE row_id = ?')
      .run(attempts, quarantine ? 1 : 0, rowId)
    return quarantine
  }

  /**
   * Xoá message cũ hơn số ngày cho trước. Trả về số bản ghi đã xoá.
   *
   * CÔNG CỤ VẬN HÀNH THỦ CÔNG — không được gọi tự động ở bất kỳ luồng khởi
   * động hay định kỳ nào. Chỉ dùng khi một người thật sự quyết định dọn hàng
   * đợi (ví dụ qua một lệnh vận hành có chủ đích), sau khi đã xác nhận các
   * message đó không còn cần thiết. Xoá tự động là đúng thứ lỗi C2 đã sửa.
   */
  purgeOlderThan(days: number): number {
    const result = this.db.raw
      .prepare(`DELETE FROM outbox WHERE created_at < datetime('now', ?)`)
      .run(`-${days} days`)
    return result.changes
  }
}
