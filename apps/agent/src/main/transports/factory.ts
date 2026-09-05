import type { TransportConfig } from '../config-store.js'
import { FileTransport } from './file.js'
import { SerialTransport } from './serial.js'
import { TcpTransport } from './tcp.js'
import type { Transport } from './types.js'

export function createTransport(config: TransportConfig): Transport {
  switch (config.kind) {
    case 'serial':
      return new SerialTransport(config)
    case 'tcp':
      return new TcpTransport(config)
    case 'file':
      return new FileTransport(config)
  }
}
