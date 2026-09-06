export const ENQ = 0x05
export const ACK = 0x06
export const NAK = 0x15
export const EOT = 0x04
export const STX = 0x02
export const ETX = 0x03
export const ETB = 0x17
export const CR = 0x0d
export const LF = 0x0a

/**
 * Tính checksum ASTM E1381: cộng dồn các byte từ ngay sau STX tới hết
 * ETX/ETB (bao gồm cả ETX/ETB), modulo 256, biểu diễn hex hoa hai chữ số.
 *
 * Sao chép có chủ ý từ `tools/simulator/src/astm-frames.ts` thay vì phụ
 * thuộc chung: công cụ giả lập không được đi kèm bản cài giao cho khách
 * hàng, và đây chỉ là mười dòng hằng số của một chuẩn công khai.
 */
export function checksum(bodyWithTerminator: Buffer): string {
  let sum = 0
  for (const byte of bodyWithTerminator) sum = (sum + byte) % 256
  return sum.toString(16).toUpperCase().padStart(2, '0')
}
