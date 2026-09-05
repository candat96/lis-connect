export type AstmRecordType = 'H' | 'P' | 'O' | 'R' | 'L' | 'Q' | 'C'

export interface AstmRecord {
  type: AstmRecordType
  /** Các trường tách theo dấu '|', giữ nguyên thứ tự và ô rỗng. */
  fields: string[]
}

export interface ParsedMessage {
  records: AstmRecord[]
}

export type DriverEvent =
  | { kind: 'message'; raw: Buffer; parsed: ParsedMessage }
  | { kind: 'reply'; data: Buffer }
  | { kind: 'error'; reason: string; raw: Buffer }

/**
 * Bộ dịch giao thức của một model máy xét nghiệm.
 *
 * `feed` nhận từng mẩu byte đến bất kỳ lúc nào và tự gom thành frame hoàn chỉnh.
 * Nó KHÔNG BAO GIỜ ném lỗi: dữ liệu hỏng trả về sự kiện 'error' kèm nguyên văn
 * byte, để tầng trên vẫn lưu được vào raw_messages.
 */
export interface Driver {
  readonly name: string
  feed(chunk: Buffer): DriverEvent[]
  reset(): void
}
