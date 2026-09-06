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
 * Checksum ASTM E1381: tổng các byte từ ngay sau STX tới hết ETX (hoặc ETB),
 * lấy modulo 256, in dạng hex hoa hai chữ số.
 */
export function checksum(bodyWithTerminator: Buffer): string {
  let sum = 0
  for (const byte of bodyWithTerminator) sum = (sum + byte) % 256
  return sum.toString(16).toUpperCase().padStart(2, '0')
}

/**
 * Dựng một frame hoàn chỉnh.
 * frameNumber quay vòng 1..7 theo chuẩn.
 */
export function buildFrame(frameNumber: number, content: string): Buffer {
  const seq = String(frameNumber % 8)
  const body = Buffer.concat([Buffer.from(seq + content, 'latin1'), Buffer.from([ETX])])
  return Buffer.concat([
    Buffer.from([STX]),
    body,
    Buffer.from(checksum(body), 'latin1'),
    Buffer.from([CR, LF]),
  ])
}
