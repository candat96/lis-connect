import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { FileTransport } from './file.js'

let dir: string
let transport: FileTransport | null = null

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'lis-file-'))
})

afterEach(async () => {
  await transport?.close()
  transport = null
  rmSync(dir, { recursive: true, force: true })
})

async function waitFor(check: () => boolean, timeoutMs = 3000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (check()) return
    await new Promise((r) => setTimeout(r, 50))
  }
  throw new Error('hết giờ chờ điều kiện')
}

describe('FileTransport', () => {
  it('đọc file đã có sẵn trong thư mục khi mở', async () => {
    writeFileSync(join(dir, 'kq1.txt'), 'R|1|Glu|5.4', 'utf8')
    // FileTransport bỏ qua file có mtime mới hơn STABLE_AFTER_MS (500ms) để tránh
    // đọc file mà máy còn đang ghi dở — chờ để file được coi là đã ổn định.
    await new Promise((r) => setTimeout(r, 600))
    transport = new FileTransport({
      kind: 'file',
      directory: dir,
      pattern: '*.txt',
      deleteAfterRead: false,
    })

    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    await waitFor(() => received.length === 1)
    expect(received[0]!.toString('utf8')).toBe('R|1|Glu|5.4')
  })

  it('đọc file mới xuất hiện sau khi đã mở', async () => {
    transport = new FileTransport({
      kind: 'file',
      directory: dir,
      pattern: '*.txt',
      deleteAfterRead: false,
    })
    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    writeFileSync(join(dir, 'kq2.txt'), 'R|2|Ure|6.1', 'utf8')
    // Chờ file được coi là đã ổn định trước khi transport có thể đọc nó.
    await new Promise((r) => setTimeout(r, 600))
    await waitFor(() => received.length === 1)
    expect(received[0]!.toString('utf8')).toBe('R|2|Ure|6.1')
  })

  it('bỏ qua file không khớp mẫu', async () => {
    writeFileSync(join(dir, 'ghichu.log'), 'không phải kết quả', 'utf8')
    transport = new FileTransport({
      kind: 'file',
      directory: dir,
      pattern: '*.txt',
      deleteAfterRead: false,
    })
    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    await new Promise((r) => setTimeout(r, 300))
    expect(received).toHaveLength(0)
  })

  it('xoá file sau khi đọc nếu được cấu hình', async () => {
    const path = join(dir, 'kq3.txt')
    writeFileSync(path, 'R|3|Cre|88', 'utf8')
    transport = new FileTransport({
      kind: 'file',
      directory: dir,
      pattern: '*.txt',
      deleteAfterRead: true,
    })
    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    await waitFor(() => !existsSync(path))
    expect(received).toHaveLength(1)
  })

  it('không đọc lại cùng một file hai lần khi không xoá', async () => {
    writeFileSync(join(dir, 'kq4.txt'), 'R|4|Na|140', 'utf8')
    transport = new FileTransport({
      kind: 'file',
      directory: dir,
      pattern: '*.txt',
      deleteAfterRead: false,
    })
    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    await waitFor(() => received.length === 1)
    await new Promise((r) => setTimeout(r, 500))
    expect(received).toHaveLength(1)
  })

  it('không đọc cụt và mất phần còn lại khi máy tạm dừng ghi giữa hai đợt', async () => {
    // Kịch bản hỏng: máy ghi phần đầu rồi tạm dừng hơn STABLE_AFTER_MS và trọn
    // một tick poll (mã cũ chỉ nhìn mtime một lần nên sẽ coi file "đã ổn định"
    // ở nhịp quét đó, đọc phần cụt rồi khoá vĩnh viễn qua `seen`), sau đó máy
    // ghi tiếp phần còn lại. Transport phải chỉ phát ra đúng một lần, với nội
    // dung đầy đủ — không phải bản cụt.
    const path = join(dir, 'kq5.txt')
    writeFileSync(path, 'R|5|Na|', 'utf8')
    transport = new FileTransport({
      kind: 'file',
      directory: dir,
      pattern: '*.txt',
      deleteAfterRead: false,
    })
    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    // Vượt STABLE_AFTER_MS (500ms) và đi qua ít nhất một tick poll (1000ms)
    // trước khi ghi tiếp phần còn lại.
    await new Promise((r) => setTimeout(r, 1100))
    writeFileSync(path, 'R|5|Na|140', 'utf8')

    await waitFor(() => received.length === 1, 5000)
    // Chờ thêm một nhịp poll nữa để chắc chắn không có lần phát thứ hai (ví dụ
    // phần cụt trước rồi phần đầy đủ sau).
    await new Promise((r) => setTimeout(r, 1200))
    expect(received).toHaveLength(1)
    expect(received[0]!.toString('utf8')).toBe('R|5|Na|140')
  })

  it('báo lỗi khi thư mục không tồn tại', async () => {
    transport = new FileTransport({
      kind: 'file',
      directory: join(dir, 'khong-co'),
      pattern: '*.txt',
      deleteAfterRead: false,
    })
    await expect(transport.open()).rejects.toThrow(/không tồn tại/)
  })
})
