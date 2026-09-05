import { EventEmitter } from 'node:events'
import { existsSync, readFileSync, readdirSync, statSync, unlinkSync } from 'node:fs'
import { join } from 'node:path'
import type { FileConfig } from '../config-store.js'
import type { Transport, TransportEvents } from './types.js'

const POLL_INTERVAL_MS = 1000
/** Chờ file ổn định trước khi đọc: máy có thể còn đang ghi dở. */
const STABLE_AFTER_MS = 500

function globToRegExp(pattern: string): RegExp {
  const escaped = pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*')
  return new RegExp(`^${escaped}$`, 'i')
}

interface FileSnapshot {
  size: number
  mtimeMs: number
}

export class FileTransport implements Transport {
  private readonly emitter = new EventEmitter()
  private readonly seen = new Set<string>()
  /**
   * File khớp mẫu, đã qua STABLE_AFTER_MS nhưng chưa được xác nhận ổn định:
   * ghi lại {size, mtimeMs} thấy ở lần quét trước để so sánh với lần quét
   * tiếp theo. Chỉ khi cả size lẫn mtime giống hệt nhau ở hai lần quét liên
   * tiếp mới coi là máy đã ghi xong — tránh đọc cụt khi máy tạm dừng ghi giữa
   * hai đợt lâu hơn STABLE_AFTER_MS (thường gặp trên ổ mạng).
   */
  private readonly pending = new Map<string, FileSnapshot>()
  private timer: NodeJS.Timeout | null = null
  private readonly matcher: RegExp

  constructor(readonly config: FileConfig) {
    this.matcher = globToRegExp(config.pattern)
  }

  isOpen(): boolean {
    return this.timer !== null
  }

  async open(): Promise<void> {
    if (!existsSync(this.config.directory)) {
      throw new Error(`thư mục không tồn tại: ${this.config.directory}`)
    }
    this.timer = setInterval(() => this.scan(), POLL_INTERVAL_MS)
    this.emitter.emit('status', { status: 'connected' })
    this.scan()
  }

  /**
   * Quét thư mục bằng polling thay vì fs.watch: thư mục kết quả thường nằm trên
   * ổ mạng, nơi fs.watch không phát sự kiện đáng tin trên Windows.
   */
  private scan(): void {
    let entries: string[]
    try {
      entries = readdirSync(this.config.directory)
    } catch (error) {
      this.emitter.emit('status', {
        status: 'error',
        detail: error instanceof Error ? error.message : String(error),
      })
      return
    }

    const currentPaths = new Set<string>()

    for (const name of entries) {
      if (!this.matcher.test(name)) continue
      const path = join(this.config.directory, name)
      if (this.seen.has(path)) continue

      let stat
      try {
        stat = statSync(path)
      } catch {
        continue
      }
      if (!stat.isFile()) continue
      currentPaths.add(path)
      if (Date.now() - stat.mtimeMs < STABLE_AFTER_MS) continue

      const snapshot: FileSnapshot = { size: stat.size, mtimeMs: stat.mtimeMs }
      const previous = this.pending.get(path)
      if (!previous || previous.size !== snapshot.size || previous.mtimeMs !== snapshot.mtimeMs) {
        // Chưa thấy lần nào, hoặc kích thước/mtime vừa đổi so với lần quét
        // trước: file có thể vẫn đang được ghi tiếp — ghi lại và chờ nhịp
        // quét sau xác nhận.
        this.pending.set(path, snapshot)
        continue
      }

      // size và mtime giống hệt lần quét trước: file đã ổn định, an toàn để đọc.
      this.pending.delete(path)
      try {
        const content = readFileSync(path)
        this.seen.add(path)
        this.emitter.emit('data', content)
        if (this.config.deleteAfterRead) {
          unlinkSync(path)
          this.seen.delete(path)
        }
      } catch (error) {
        this.emitter.emit('status', {
          status: 'error',
          detail: `đọc file ${name} thất bại: ${String(error)}`,
        })
      }
    }

    // Dọn pending cho file đã biến mất khỏi thư mục (bị xoá bên ngoài, hoặc
    // không còn khớp mẫu), để map không phình vô hạn.
    for (const path of this.pending.keys()) {
      if (!currentPaths.has(path)) this.pending.delete(path)
    }
  }

  async write(): Promise<void> {
    throw new Error('transport dạng file chỉ đọc, không gửi được xuống máy')
  }

  async close(): Promise<void> {
    if (this.timer) clearInterval(this.timer)
    this.timer = null
    this.emitter.emit('status', { status: 'disconnected' })
  }

  on<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.on(event, handler as (...args: unknown[]) => void)
  }

  off<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.off(event, handler as (...args: unknown[]) => void)
  }
}
