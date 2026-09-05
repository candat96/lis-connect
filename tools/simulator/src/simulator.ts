import { createServer, type Server, type Socket } from 'node:net'
import { ACK, ENQ, EOT, NAK, buildFrame } from './astm-frames.js'

export interface SimResult {
  sid: string
  testCode: string
  value: string
  unit: string
}

export type Misbehaviour = 'none' | 'bad-checksum' | 'truncate' | 'no-ack' | 'garbage-prefix'

export interface SimulatorOptions {
  misbehaviour: Misbehaviour
}

const ACK_TIMEOUT_MS = 3000

/**
 * Số lần tối đa gửi một frame (kể cả lần đầu) trước khi bỏ cuộc vì bị NAK
 * liên tục. Theo ASTM E1381/E1394, bên gửi thử lại tối đa 6 lần một frame bị
 * từ chối rồi mới huỷ phiên truyền — máy thật không đứng im chờ hết giờ như
 * khi mất kết nối, nó gửi lại ngay khi thấy NAK.
 */
const MAX_FRAME_ATTEMPTS = 6

type AckOutcome = 'ack' | 'nak' | 'timeout'

/**
 * Máy xét nghiệm giả lập nói ASTM E1381/E1394 qua TCP.
 *
 * Ngoài đường đi đúng, nó cố tình làm được các hành vi xấu mà máy thật hay mắc:
 * checksum sai, đứt giữa chừng, im lặng không ACK, rác ở đầu luồng. Không có
 * những hành vi này thì phần xử lý lỗi của Agent không bao giờ được kiểm chứng.
 */
export class AstmSimulator {
  private server: Server | null = null
  private socket: Socket | null = null
  private frameNumber = 1

  constructor(private readonly options: SimulatorOptions) {}

  /** Kiểm tra xem có client kết nối chưa. Dùng trong test để tránh race condition. */
  hasClient(): boolean {
    return this.socket !== null
  }

  async start(port: number): Promise<number> {
    return new Promise((resolve, reject) => {
      const server = createServer((socket) => {
        this.socket = socket
        socket.on('data', (chunk) => this.onData(chunk))
        socket.on('close', () => {
          this.socket = null
        })
        socket.on('error', (err) => {
          console.error('Socket error:', err)
          this.socket = null
        })
      })
      server.once('error', reject)
      server.listen(port, '127.0.0.1', () => {
        this.server = server
        const address = server.address()
        if (!address || typeof address === 'string') return reject(new Error('không lấy được cổng'))
        resolve(address.port)
      })
    })
  }

  private onData(chunk: Buffer): void {
    if (!chunk.includes(ENQ)) return
    if (this.options.misbehaviour === 'no-ack') return
    this.socket?.write(Buffer.from([ACK]))
  }

  /**
   * Chờ phản hồi cho một frame vừa gửi. Phân biệt ACK, NAK, và im lặng
   * (timeout): NAK nghĩa là bên kia còn đó và đang từ chối nội dung (checksum
   * sai chẳng hạn) — máy thật gửi lại ngay, không đứng chờ hết giờ. Timeout
   * là trường hợp bên kia thực sự im lặng (mất kết nối, misbehaviour
   * 'no-ack'), lúc đó chờ hết giờ mới là hành vi đúng.
   */
  private waitForAck(): Promise<AckOutcome> {
    return new Promise((resolve) => {
      const socket = this.socket
      if (!socket) return resolve('timeout')
      const timer = setTimeout(() => {
        socket.off('data', onData)
        resolve('timeout')
      }, ACK_TIMEOUT_MS)
      const onData = (chunk: Buffer): void => {
        if (chunk.includes(NAK)) {
          clearTimeout(timer)
          socket.off('data', onData)
          resolve('nak')
          return
        }
        if (!chunk.includes(ACK)) return
        clearTimeout(timer)
        socket.off('data', onData)
        resolve('ack')
      }
      socket.on('data', onData)
    })
  }

  /** Gửi một phiên kết quả lên host, theo đúng trình tự ENQ → frame → EOT. */
  async sendResults(results: SimResult[]): Promise<void> {
    const socket = this.socket
    if (!socket) throw new Error('chưa có host nào kết nối')

    if (this.options.misbehaviour === 'garbage-prefix') {
      socket.write(Buffer.from('rác đầu luồng', 'latin1'))
    }

    socket.write(Buffer.from([ENQ]))
    const handshake = await this.waitForAck()
    if (handshake !== 'ack') throw new Error('hết giờ chờ ACK')

    const lines = [
      'H|\\^&|||LIS-SIM^1.0|||||||P|1',
      'P|1',
      ...results.flatMap((r, index) => [
        `O|${index + 1}|${r.sid}||^^^${r.testCode}|R`,
        `R|1|^^^${r.testCode}|${r.value}|${r.unit}||N||F`,
      ]),
      'L|1|N',
    ]

    for (const [index, line] of lines.entries()) {
      let frame = buildFrame(this.frameNumber, line)
      this.frameNumber = (this.frameNumber % 7) + 1

      if (this.options.misbehaviour === 'bad-checksum') {
        // Đổi ký tự checksum cuối để nó chắc chắn sai.
        const copy = Buffer.from(frame)
        const checksumPos = copy.length - 3
        copy[checksumPos] = copy[checksumPos] === 0x41 ? 0x42 : 0x41
        frame = copy
      }

      if (this.options.misbehaviour === 'truncate' && index === 1) {
        socket.write(frame.subarray(0, Math.floor(frame.length / 2)))
        return
      }

      // Gửi lại đúng frame vừa bị từ chối (NAK), tối đa MAX_FRAME_ATTEMPTS
      // lần kể cả lần đầu. Timeout (im lặng thật) không được thử lại ở đây —
      // giữ nguyên hành vi cũ: bỏ qua và coi như đã "xong" frame này, để
      // không đứng chờ vô thời hạn khi bên kia mất kết nối giữa chừng.
      let outcome: AckOutcome = 'timeout'
      for (let attempt = 1; attempt <= MAX_FRAME_ATTEMPTS; attempt += 1) {
        socket.write(frame)
        outcome = await this.waitForAck()
        if (outcome !== 'nak') break
      }

      if (outcome === 'nak') {
        // Hết số lần cho phép mà vẫn bị từ chối: bỏ cuộc, kết thúc phiên
        // ngay bằng EOT thay vì tiếp tục gửi các dòng còn lại.
        socket.write(Buffer.from([EOT]))
        return
      }
    }

    socket.write(Buffer.from([EOT]))
  }

  async stop(): Promise<void> {
    this.socket?.destroy()
    this.socket = null
    if (this.server) {
      await new Promise<void>((resolve) => this.server!.close(() => resolve()))
      this.server = null
    }
  }
}
