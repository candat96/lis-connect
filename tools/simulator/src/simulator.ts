import { createServer, type Server, type Socket } from 'node:net'
import { ACK, ENQ, EOT, buildFrame } from './astm-frames.js'

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

  private waitForAck(): Promise<void> {
    return new Promise((resolve, reject) => {
      const socket = this.socket
      if (!socket) return reject(new Error('chưa có kết nối'))
      const timer = setTimeout(() => {
        socket.off('data', onData)
        reject(new Error('hết giờ chờ ACK'))
      }, ACK_TIMEOUT_MS)
      const onData = (chunk: Buffer): void => {
        if (!chunk.includes(ACK)) return
        clearTimeout(timer)
        socket.off('data', onData)
        resolve()
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
    await this.waitForAck()

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

      socket.write(frame)
      await this.waitForAck().catch(() => undefined)
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
