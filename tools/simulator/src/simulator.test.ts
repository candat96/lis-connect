import { connect, type Socket } from 'node:net'
import { afterEach, describe, expect, it } from 'vitest'
import { ACK, ENQ, EOT, ETX, NAK, STX, checksum, buildFrame } from './astm-frames.js'
import { AstmSimulator } from './simulator.js'

let sim: AstmSimulator | null = null
let client: Socket | null = null

afterEach(async () => {
  client?.destroy()
  client = null
  await sim?.stop()
  sim = null
})

function collect(socket: Socket): { chunks: Buffer[]; all: () => Buffer } {
  const chunks: Buffer[] = []
  socket.on('data', (chunk) => {
    chunks.push(chunk)
    // Tự động gửi ACK cho mỗi frame nhận được (STX...ETX checksum CR LF)
    // để test không bị stuck chờ ACK
    if (chunk.includes(STX) && chunk.includes(ETX)) {
      setImmediate(() => socket.write(Buffer.from([ACK])))
    }
  })
  return { chunks, all: () => Buffer.concat(chunks) }
}

async function waitFor(check: () => boolean, timeoutMs = 3000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (check()) return
    await new Promise((r) => setTimeout(r, 20))
  }
  throw new Error('hết giờ chờ điều kiện')
}

describe('checksum ASTM', () => {
  it('tính đúng theo ví dụ của chuẩn', () => {
    // Nội dung '1H|\^&' + ETX; tổng byte modulo 256.
    const body = Buffer.from([0x31, 0x48, 0x7c, 0x5c, 0x5e, 0x26, ETX])
    expect(checksum(body)).toBe('D8')
  })

  it('luôn trả về hai ký tự hex hoa', () => {
    expect(checksum(Buffer.from([0x01]))).toBe('01')
    expect(checksum(Buffer.from([0xff, 0x02]))).toBe('01')
  })
})

describe('buildFrame', () => {
  it('bọc nội dung bằng STX ... ETX checksum CR LF', () => {
    const frame = buildFrame(1, 'H|\\^&')
    expect(frame[0]).toBe(STX)
    expect(frame.at(-2)).toBe(0x0d)
    expect(frame.at(-1)).toBe(0x0a)
    expect(frame.includes(ETX)).toBe(true)
  })
})

describe('AstmSimulator', () => {
  it('gửi ENQ rồi các frame kết quả rồi EOT khi được yêu cầu', async () => {
    sim = new AstmSimulator({ misbehaviour: 'none' })
    const port = await sim.start(0)

    client = connect({ host: '127.0.0.1', port })
    const received = collect(client)
    await new Promise((r) => client!.once('connect', r))
    await waitFor(() => sim!.hasClient())

    const sendPromise = sim.sendResults([{ sid: 'SID001', testCode: 'GLU', value: '5.4', unit: 'mmol/L' }])

    await waitFor(() => received.all().includes(ENQ))
    client.write(Buffer.from([ACK]))

    await waitFor(() => received.all().includes(EOT), 5000)

    try {
      await sendPromise
    } catch (e) {
      // error expected in some tests
    }
    const text = received.all().toString('latin1')
    expect(text).toContain('SID001')
    expect(text).toContain('GLU')
    expect(text).toContain('5.4')
  })

  it('với misbehaviour bad-checksum thì checksum sai lệch', async () => {
    sim = new AstmSimulator({ misbehaviour: 'bad-checksum' })
    const port = await sim.start(0)
    client = connect({ host: '127.0.0.1', port })
    const received = collect(client)
    await new Promise((r) => client!.once('connect', r))
    await waitFor(() => sim!.hasClient())

    const sendPromise = sim.sendResults([{ sid: 'SID002', testCode: 'URE', value: '6.1', unit: 'mmol/L' }])
    await waitFor(() => received.all().includes(ENQ))
    client.write(Buffer.from([ACK]))
    await waitFor(() => received.all().toString('latin1').includes('SID002'), 5000)
    await sendPromise.catch(() => undefined)

    const frames = received.all().toString('latin1')
    const match = /\x02(.*?)\x03([0-9A-F]{2})/s.exec(frames)
    expect(match).not.toBeNull()
    const body = Buffer.from(`${match![1]}\x03`, 'latin1')
    expect(match![2]).not.toBe(checksum(body))
  })

  it('với misbehaviour truncate thì frame bị cắt giữa chừng', async () => {
    sim = new AstmSimulator({ misbehaviour: 'truncate' })
    const port = await sim.start(0)
    client = connect({ host: '127.0.0.1', port })
    const received = collect(client)
    await new Promise((r) => client!.once('connect', r))
    await waitFor(() => sim!.hasClient())

    void sim.sendResults([{ sid: 'SID003', testCode: 'CRE', value: '88', unit: 'umol/L' }])
    await waitFor(() => received.all().includes(ENQ))
    client.write(Buffer.from([ACK]))
    await new Promise((r) => setTimeout(r, 500))

    expect(received.all().includes(EOT)).toBe(false)
  })

  it('với misbehaviour no-ack thì không trả ACK cho ENQ của host', async () => {
    sim = new AstmSimulator({ misbehaviour: 'no-ack' })
    const port = await sim.start(0)
    client = connect({ host: '127.0.0.1', port })
    const received = collect(client)
    await new Promise((r) => client!.once('connect', r))

    client.write(Buffer.from([ENQ]))
    await new Promise((r) => setTimeout(r, 500))
    expect(received.all().includes(ACK)).toBe(false)
  })

  it('với misbehaviour none thì trả ACK cho ENQ của host', async () => {
    sim = new AstmSimulator({ misbehaviour: 'none' })
    const port = await sim.start(0)
    client = connect({ host: '127.0.0.1', port })
    const received = collect(client)
    await new Promise((r) => client!.once('connect', r))

    client.write(Buffer.from([ENQ]))
    await waitFor(() => received.all().includes(ACK))
    expect(received.all().includes(ACK)).toBe(true)
  })

  it('với misbehaviour garbage-prefix thì rác xuất hiện trước ENQ', async () => {
    sim = new AstmSimulator({ misbehaviour: 'garbage-prefix' })
    const port = await sim.start(0)
    client = connect({ host: '127.0.0.1', port })
    const received = collect(client)
    await new Promise((r) => client!.once('connect', r))
    await waitFor(() => sim!.hasClient())

    const sendPromise = sim.sendResults([{ sid: 'SID004', testCode: 'URE', value: '6.1', unit: 'mmol/L' }])
    await waitFor(() => received.all().includes(ENQ))
    client.write(Buffer.from([ACK]))
    await waitFor(() => received.all().toString('latin1').includes('SID004'), 5000)
    await sendPromise.catch(() => undefined)

    const buffer = received.all()
    const enqIndex = buffer.indexOf(ENQ)
    // ENQ không nằm ở byte thứ nhất, vì có rác trước nó
    expect(enqIndex).toBeGreaterThan(0)
  })

  it('khi bị NAK liên tục thì gửi lại đúng frame rồi bỏ cuộc sau số lần giới hạn', async () => {
    sim = new AstmSimulator({ misbehaviour: 'none' })
    const port = await sim.start(0)
    client = connect({ host: '127.0.0.1', port })

    const frameBodies: string[] = []
    let eotSeen = false
    client.on('data', (chunk: Buffer) => {
      if (chunk.includes(ENQ)) {
        setImmediate(() => client!.write(Buffer.from([ACK])))
        return
      }
      if (chunk.includes(STX)) {
        frameBodies.push(chunk.toString('latin1'))
        // Luôn từ chối: mô phỏng máy host báo checksum/nội dung sai mãi mãi.
        setImmediate(() => client!.write(Buffer.from([NAK])))
        return
      }
      if (chunk.includes(EOT)) eotSeen = true
    })
    await new Promise((r) => client!.once('connect', r))
    await waitFor(() => sim!.hasClient())

    await sim.sendResults([{ sid: 'SID005', testCode: 'GLU', value: '5.4', unit: 'mmol/L' }])
    // sendResults() chỉ đợi write() được xếp hàng, không đợi client thực sự
    // nhận được EOT — chờ riêng để tránh race với việc gói tin tới muộn hơn.
    await waitFor(() => eotSeen)

    // Dòng đầu tiên (H) bị NAK liên tục: đúng 6 lần thử (kể cả lần đầu) rồi
    // simulator phải bỏ cuộc — không lặp vô hạn, không chuyển sang dòng kế.
    expect(frameBodies.length).toBe(6)
    // Cả 6 lần gửi đều là chính xác cùng một frame (cùng nội dung/checksum),
    // không phải một frame mới mỗi lần.
    expect(new Set(frameBodies).size).toBe(1)
    expect(eotSeen).toBe(true)
  })
})
