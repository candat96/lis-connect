import { v7 as uuidv7 } from 'uuid'

/** Sinh mã message dạng UUID v7: sắp xếp được theo thời gian, dùng làm khoá idempotent. */
export function newMessageId(): string {
  return uuidv7()
}
