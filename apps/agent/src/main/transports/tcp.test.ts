import { createServer, type Server, type Socket } from 'node:net'
import { afterEach, describe, expect, it } from 'vitest'
import { TcpTransport } from './tcp.js'

let server: Server | null = null
let transport: TcpTransport | null = null

async function startEchoServer(): Promise<{ port: number; sockets: Socket[] }> {
  const sockets: Socket[] = []
  server = createServer((socket) => {
    sockets.push(socket)
    socket.on('data', (chunk) => socket.write(chunk))
  })
  await new Promise<void>((resolve) => server!.listen(0, resolve))
  const address = server!.address()
  if (typeof address === 'string' || !address) throw new Error('không lấy được cổng')
  return { port: address.port, sockets }
}

afterEach(async () => {
  await transport?.close()
  transport = null
  await new Promise<void>((resolve) => {
    if (!server) return resolve()
    server.close(() => resolve())
  })
  server = null
})

describe('TcpTransport ở chế độ client', () => {
  it('mở kết nối và báo trạng thái connected', async () => {
    const { port } = await startEchoServer()
    transport = new TcpTransport({ kind: 'tcp', role: 'client', host: '127.0.0.1', port })

    const statuses: string[] = []
    transport.on('status', (s) => statuses.push(s.status))
    await transport.open()

    expect(transport.isOpen()).toBe(true)
    expect(statuses).toContain('connected')
  })

  it('nhận được dữ liệu máy gửi về', async () => {
    const { port } = await startEchoServer()
    transport = new TcpTransport({ kind: 'tcp', role: 'client', host: '127.0.0.1', port })

    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()
    await transport.write(Buffer.from('xin chào', 'utf8'))

    await new Promise((r) => setTimeout(r, 100))
    expect(Buffer.concat(received).toString('utf8')).toBe('xin chào')
  })

  it('báo trạng thái disconnected khi máy đóng kết nối', async () => {
    const { port, sockets } = await startEchoServer()
    transport = new TcpTransport({ kind: 'tcp', role: 'client', host: '127.0.0.1', port })

    const statuses: string[] = []
    transport.on('status', (s) => statuses.push(s.status))
    await transport.open()
    // Sự kiện 'connect' phía client có thể tới trước khi callback 'connection'
    // phía server kịp đẩy socket vào mảng `sockets` (đua giữa hai tiến trình sự
    // kiện trên cùng vòng lặp) — chờ tới khi server thực sự đã có socket rồi mới
    // đóng nó, nếu không destroy() sẽ chạy trên mảng rỗng.
    await new Promise<void>((resolve) => {
      const check = (): void => {
        if (sockets.length > 0) return resolve()
        setImmediate(check)
      }
      check()
    })
    sockets.forEach((s) => s.destroy())

    await new Promise((r) => setTimeout(r, 100))
    expect(statuses).toContain('disconnected')
    expect(transport.isOpen()).toBe(false)
  })

  it('báo lỗi khi không kết nối được', async () => {
    // Cổng 1 gần như chắc chắn không có gì lắng nghe.
    transport = new TcpTransport({ kind: 'tcp', role: 'client', host: '127.0.0.1', port: 1 })
    await expect(transport.open()).rejects.toThrow()
    expect(transport.isOpen()).toBe(false)
  })

  it('write khi chưa mở thì báo lỗi rõ ràng', async () => {
    transport = new TcpTransport({ kind: 'tcp', role: 'client', host: '127.0.0.1', port: 1 })
    await expect(transport.write(Buffer.from('x'))).rejects.toThrow(/chưa mở/)
  })
})

describe('TcpTransport ở chế độ server', () => {
  it('nhận kết nối từ máy và đọc được dữ liệu', async () => {
    transport = new TcpTransport({ kind: 'tcp', role: 'server', host: '127.0.0.1', port: 0 })
    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    const { connect } = await import('node:net')
    const client = connect({ host: '127.0.0.1', port: transport.listeningPort() })
    await new Promise((r) => client.once('connect', r))
    client.write('H|\\^&|')

    await new Promise((r) => setTimeout(r, 100))
    expect(Buffer.concat(received).toString('utf8')).toBe('H|\\^&|')
    client.destroy()
  })
})
