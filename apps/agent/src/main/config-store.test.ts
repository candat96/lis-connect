import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { ConfigStore, type DeviceConfig } from './config-store.js'

let dir: string
let store: ConfigStore

const serialDevice: DeviceConfig = {
  deviceKey: 'may-sinh-hoa',
  name: 'Máy sinh hóa',
  driver: 'astm-e1394',
  enabled: true,
  transport: {
    kind: 'serial',
    path: 'COM3',
    baudRate: 9600,
    dataBits: 8,
    parity: 'none',
    stopBits: 1,
  },
}

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'lis-agent-'))
  store = new ConfigStore(join(dir, 'config.json'))
})

afterEach(() => {
  rmSync(dir, { recursive: true, force: true })
})

describe('ConfigStore', () => {
  it('trả về cấu hình mặc định khi chưa có file', () => {
    const config = store.load()
    expect(config.coreUrl).toBe('')
    expect(config.agentKey).toBe('')
    expect(config.hisPort).toBe(8080)
    expect(config.devices).toEqual([])
  })

  it('lưu rồi đọc lại giữ nguyên nội dung', () => {
    store.save({
      coreUrl: 'wss://core.example.vn/agent',
      agentKey: 'lis_agent_abc',
      proxyUrl: null,
      hisPort: 8080,
      devices: [serialDevice],
    })

    const reloaded = new ConfigStore(join(dir, 'config.json')).load()
    expect(reloaded.coreUrl).toBe('wss://core.example.vn/agent')
    expect(reloaded.devices[0]?.transport).toMatchObject({ kind: 'serial', path: 'COM3' })
  })

  it('ghi file dạng JSON đọc được bằng trình soạn thảo thường', () => {
    store.save({ coreUrl: 'wss://x', agentKey: 'k', proxyUrl: null, hisPort: 8080, devices: [] })
    const text = readFileSync(join(dir, 'config.json'), 'utf8')
    expect(text).toContain('\n')
    expect(JSON.parse(text).coreUrl).toBe('wss://x')
  })

  it('upsertDevice thêm máy mới', () => {
    store.upsertDevice(serialDevice)
    expect(store.load().devices).toHaveLength(1)
  })

  it('upsertDevice thay thế máy trùng deviceKey thay vì thêm bản sao', () => {
    store.upsertDevice(serialDevice)
    store.upsertDevice({ ...serialDevice, name: 'Máy sinh hóa (đổi tên)' })

    const devices = store.load().devices
    expect(devices).toHaveLength(1)
    expect(devices[0]?.name).toBe('Máy sinh hóa (đổi tên)')
  })

  it('removeDevice xoá đúng máy', () => {
    store.upsertDevice(serialDevice)
    store.upsertDevice({ ...serialDevice, deviceKey: 'may-huyet-hoc', name: 'Máy huyết học' })
    store.removeDevice('may-sinh-hoa')

    const devices = store.load().devices
    expect(devices.map((d) => d.deviceKey)).toEqual(['may-huyet-hoc'])
  })

  it('không sập khi file cấu hình hỏng, mà trả về mặc định', () => {
    const path = join(dir, 'config.json')
    writeFileSync(path, '{ hỏng', 'utf8')
    const config = new ConfigStore(path).load()
    expect(config.devices).toEqual([])
  })

  it('devices sai kiểu (chuỗi thay vì mảng) không làm load() hay upsertDevice() sập', () => {
    const path = join(dir, 'config.json')
    writeFileSync(path, JSON.stringify({ devices: 'not-an-array' }), 'utf8')
    const configStore = new ConfigStore(path)

    const config = configStore.load()
    expect(config.devices).toEqual([])
    expect(() => configStore.upsertDevice(serialDevice)).not.toThrow()
  })

  it('hisPort sai kiểu (chuỗi thay vì số) dùng lại giá trị mặc định', () => {
    const path = join(dir, 'config.json')
    writeFileSync(path, JSON.stringify({ hisPort: 'tám nghìn' }), 'utf8')
    const configStore = new ConfigStore(path)

    const config = configStore.load()
    expect(config.hisPort).toBe(8080)
    expect(() => configStore.upsertDevice(serialDevice)).not.toThrow()
  })

  it('coreUrl sai kiểu (số thay vì chuỗi) dùng lại giá trị mặc định', () => {
    const path = join(dir, 'config.json')
    writeFileSync(path, JSON.stringify({ coreUrl: 12345 }), 'utf8')
    const configStore = new ConfigStore(path)

    const config = configStore.load()
    expect(config.coreUrl).toBe('')
    expect(() => configStore.upsertDevice(serialDevice)).not.toThrow()
  })

  it('toàn bộ tệp là một mảng JSON thay vì object thì trả về cấu hình mặc định', () => {
    const path = join(dir, 'config.json')
    writeFileSync(path, JSON.stringify([1, 2, 3]), 'utf8')
    const configStore = new ConfigStore(path)

    const config = configStore.load()
    expect(config).toEqual({
      coreUrl: '',
      agentKey: '',
      proxyUrl: null,
      hisPort: 8080,
      devices: [],
    })
    expect(() => configStore.upsertDevice(serialDevice)).not.toThrow()
  })
})
