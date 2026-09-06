import { EventEmitter } from 'node:events'
import { createConnection, createServer, type Server, type Socket } from 'node:net'
import type { TcpConfig } from '../config-store.js'
import type { Transport, TransportEvents } from './types.js'

export class TcpTransport implements Transport {
  private readonly emitter = new EventEmitter()
  private socket: Socket | null = null
  private server: Server | null = null

  constructor(readonly config: TcpConfig) {}

  isOpen(): boolean {
    return this.socket !== null && !this.socket.destroyed
  }

  /** Cổng đang lắng nghe ở chế độ server. Ném lỗi nếu chưa mở. */
  listeningPort(): number {
    const address = this.server?.address()
    if (!address || typeof address === 'string') throw new Error('server chưa lắng nghe')
    return address.port
  }

  async open(): Promise<void> {
    if (this.config.role === 'server') return this.openServer()
    return this.openClient()
  }

  private attachSocket(socket: Socket): void {
    this.socket = socket
    socket.on('data', (chunk: Buffer) => this.emitter.emit('data', chunk))
    socket.on('close', () => {
      this.socket = null
      this.emitter.emit('status', { status: 'disconnected' })
    })
    socket.on('error', (error) => {
      this.emitter.emit('status', { status: 'error', detail: error.message })
    })
    this.emitter.emit('status', { status: 'connected' })
  }

  private openClient(): Promise<void> {
    return new Promise((resolve, reject) => {
      const socket = createConnection({ host: this.config.host, port: this.config.port })
      const onError = (error: Error): void => {
        socket.destroy()
        this.emitter.emit('status', { status: 'error', detail: error.message })
        reject(error)
      }
      socket.once('error', onError)
      socket.once('connect', () => {
        socket.off('error', onError)
        this.attachSocket(socket)
        resolve()
      })
    })
  }

  private openServer(): Promise<void> {
    return new Promise((resolve, reject) => {
      const server = createServer((socket) => this.attachSocket(socket))
      server.once('error', reject)
      server.listen(this.config.port, this.config.host, () => {
        this.server = server
        resolve()
      })
    })
  }

  async write(data: Buffer): Promise<void> {
    const socket = this.socket
    if (!socket || socket.destroyed) throw new Error('kết nối TCP chưa mở')
    await new Promise<void>((resolve, reject) => {
      socket.write(data, (error) => (error ? reject(error) : resolve()))
    })
  }

  async close(): Promise<void> {
    this.socket?.destroy()
    this.socket = null
    if (this.server) {
      await new Promise<void>((resolve) => this.server!.close(() => resolve()))
      this.server = null
    }
  }

  on<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.on(event, handler as (...args: unknown[]) => void)
  }

  off<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.off(event, handler as (...args: unknown[]) => void)
  }
}
