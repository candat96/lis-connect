import { AstmE1394Driver } from './astm-e1394.js'
import type { Driver } from './types.js'

type DriverFactory = () => Driver

const factories = new Map<string, DriverFactory>([['astm-e1394', () => new AstmE1394Driver()]])

export const driverRegistry = {
  /** Tạo một instance driver mới. Mỗi máy có instance riêng vì driver giữ trạng thái phiên. */
  get(name: string): Driver {
    const factory = factories.get(name)
    if (!factory) throw new Error(`chưa có driver tên "${name}"`)
    return factory()
  },

  list(): string[] {
    return [...factories.keys()]
  },

  register(name: string, factory: DriverFactory): void {
    factories.set(name, factory)
  },
}
