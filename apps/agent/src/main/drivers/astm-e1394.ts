import { ACK, CR, ENQ, EOT, ETB, ETX, LF, NAK, STX, checksum } from './astm-constants.js'
import type { AstmRecord, AstmRecordType, Driver, DriverEvent } from './types.js'

const RECORD_TYPES: AstmRecordType[] = ['H', 'P', 'O', 'R', 'L', 'Q', 'C']

export class AstmE1394Driver implements Driver {
  readonly name = 'astm-e1394'

  private buffer = Buffer.alloc(0)
  private sessionFrames: string[] = []
  private sessionRaw: Buffer[] = []
  private inSession = false

  reset(): void {
    this.buffer = Buffer.alloc(0)
    this.sessionFrames = []
    this.sessionRaw = []
    this.inSession = false
  }

  feed(chunk: Buffer): DriverEvent[] {
    this.buffer = Buffer.concat([this.buffer, chunk])
    const events: DriverEvent[] = []

    for (;;) {
      if (this.buffer.length === 0) break

      const first = this.buffer[0]!

      if (first === ENQ) {
        this.buffer = this.buffer.subarray(1)
        this.inSession = true
        this.sessionFrames = []
        this.sessionRaw = []
        events.push({ kind: 'reply', data: Buffer.from([ACK]) })
        continue
      }

      if (first === EOT) {
        this.buffer = this.buffer.subarray(1)
        if (this.inSession && this.sessionFrames.length > 0) {
          const records = this.sessionFrames
            .map((line) => parseRecord(line))
            .filter((record): record is AstmRecord => record !== null)
          events.push({
            kind: 'message',
            raw: Buffer.concat(this.sessionRaw),
            parsed: { records },
          })
        }
        this.inSession = false
        this.sessionFrames = []
        this.sessionRaw = []
        continue
      }

      if (first === STX) {
        const end = findFrameEnd(this.buffer)
        if (end === -1) break // frame chưa về đủ, chờ mẩu tiếp theo

        const rawFrame = this.buffer.subarray(0, end)
        this.buffer = this.buffer.subarray(end)

        const verified = verifyFrame(rawFrame)
        if (!verified.ok) {
          events.push({ kind: 'error', reason: verified.reason, raw: Buffer.from(rawFrame) })
          events.push({ kind: 'reply', data: Buffer.from([NAK]) })
          continue
        }

        if (!this.inSession) {
          // Frame hợp lệ nhưng đến khi chưa có phiên (không ENQ trước đó): nhiễu
          // dây, gói truyền lại lạc, hay máy khởi động sai trình tự. Không được
          // âm thầm giữ tạm rồi vứt khi ENQ thật sự tới — phải báo error kèm
          // nguyên văn byte để tầng trên còn lưu được vào raw_messages.
          events.push({
            kind: 'error',
            reason: 'frame hợp lệ nhưng đến khi chưa có phiên (thiếu ENQ mở phiên)',
            raw: Buffer.from(rawFrame),
          })
          continue
        }

        this.sessionFrames.push(verified.content)
        this.sessionRaw.push(Buffer.from(rawFrame))
        events.push({ kind: 'reply', data: Buffer.from([ACK]) })
        continue
      }

      // Byte không thuộc khung nào: gom các byte rác liên tiếp thành MỘT sự
      // kiện error duy nhất (chốt khi gặp byte có ý nghĩa tiếp theo hoặc khi
      // hết buffer hiện tại), thay vì bỏ lặng lẽ từng byte hoặc phát một sự
      // kiện cho mỗi byte — một dây nhiễu dài không được tạo hàng nghìn sự
      // kiện vô nghĩa, nhưng byte rác vẫn phải được báo lại nguyên văn.
      {
        let end = 1
        while (
          end < this.buffer.length &&
          this.buffer[end] !== ENQ &&
          this.buffer[end] !== EOT &&
          this.buffer[end] !== STX
        ) {
          end += 1
        }
        const junk = this.buffer.subarray(0, end)
        this.buffer = this.buffer.subarray(end)
        events.push({ kind: 'error', reason: 'byte rác không thuộc khung nào', raw: Buffer.from(junk) })
      }
    }

    return events
  }
}

/** Trả về vị trí ngay sau LF cuối frame, hoặc -1 nếu frame chưa về đủ. */
function findFrameEnd(buffer: Buffer): number {
  for (let i = 1; i < buffer.length; i += 1) {
    if (buffer[i] !== ETX && buffer[i] !== ETB) continue
    // sau ETX/ETB là 2 ký tự checksum, rồi CR, rồi LF
    const endIndex = i + 4
    if (endIndex >= buffer.length) return -1
    if (buffer[i + 3] === CR && buffer[i + 4] === LF) return endIndex + 1
  }
  return -1
}

type Verified = { ok: true; content: string } | { ok: false; reason: string }

function verifyFrame(frame: Buffer): Verified {
  const terminatorIndex = frame.findIndex((b) => b === ETX || b === ETB)
  if (terminatorIndex === -1) return { ok: false, reason: 'frame không có ETX/ETB' }

  const body = frame.subarray(1, terminatorIndex + 1)
  const expected = checksum(body)
  const actual = frame.subarray(terminatorIndex + 1, terminatorIndex + 3).toString('latin1')
  if (expected !== actual) {
    return { ok: false, reason: `checksum sai: nhận ${actual}, tính ra ${expected}` }
  }

  // Bỏ ký tự số thứ tự frame ở đầu body.
  return { ok: true, content: body.subarray(1, body.length - 1).toString('latin1') }
}

function parseRecord(line: string): AstmRecord | null {
  const fields = line.split('|')
  const type = fields[0]
  if (type === undefined || !isRecordType(type)) return null
  return { type, fields }
}

function isRecordType(value: string): value is AstmRecordType {
  return (RECORD_TYPES as string[]).includes(value)
}
