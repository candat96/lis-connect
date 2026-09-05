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

export class FileTransport implements Transport {
  private readonly emitter = new EventEmitter()
  private readonly seen = new Set<string>()
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
      if (Date.now() - stat.mtimeMs < STABLE_AFTER_MS) continue

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
