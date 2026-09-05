import { EventEmitter } from 'node:events'
import { SerialPort } from 'serialport'
import type { SerialConfig } from '../config-store.js'
import type { Transport, TransportEvents } from './types.js'

export class SerialTransport implements Transport {
  private readonly emitter = new EventEmitter()
  private port: SerialPort | null = null

  constructor(readonly config: SerialConfig) {}

  isOpen(): boolean {
    return this.port?.isOpen ?? false
  }

  async open(): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      const port = new SerialPort(
        {
          path: this.config.path,
          baudRate: this.config.baudRate,
          dataBits: this.config.dataBits,
          parity: this.config.parity,
          stopBits: this.config.stopBits,
          autoOpen: false,
        },
        undefined,
      )
      port.open((error) => {
        if (error) {
          this.emitter.emit('status', { status: 'error', detail: error.message })
          return reject(error)
        }
        this.port = port
        port.on('data', (chunk: Buffer) => this.emitter.emit('data', chunk))
        port.on('close', () => {
          this.port = null
          this.emitter.emit('status', { status: 'disconnected' })
        })
        port.on('error', (err) =>
          this.emitter.emit('status', { status: 'error', detail: err.message }),
        )
        this.emitter.emit('status', { status: 'connected' })
        resolve()
      })
    })
  }

  async write(data: Buffer): Promise<void> {
    const port = this.port
    if (!port?.isOpen) throw new Error('cổng serial chưa mở')
    await new Promise<void>((resolve, reject) => {
      port.write(data, (error) => (error ? reject(error) : resolve()))
    })
  }

  async close(): Promise<void> {
    const port = this.port
    if (!port) return
    await new Promise<void>((resolve) => port.close(() => resolve()))
    this.port = null
  }

  on<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.on(event, handler as (...args: unknown[]) => void)
  }

  off<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.off(event, handler as (...args: unknown[]) => void)
  }
}

/** Liệt kê cổng COM đang có trên máy, dùng cho dropdown trong tab Máy xét nghiệm. */
export async function listSerialPorts(): Promise<
  Array<{ path: string; manufacturer: string | null; friendlyName: string | null }>
> {
  const ports = await SerialPort.list()
  return ports.map((p) => ({
    path: p.path,
    manufacturer: p.manufacturer ?? null,
    friendlyName: (p as { friendlyName?: string }).friendlyName ?? null,
  }))
}
