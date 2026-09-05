import { createServer, type Server } from 'node:http'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { newMessageId } from '@lis/shared'
import { WebSocketServer, type WebSocket } from 'ws'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { CoreClient } from './core-client.js'
import { LocalDb } from './local-db.js'
import { Outbox } from './outbox.js'

let dir: string
let db: LocalDb
let outbox: Outbox
let http: Server
let wss: WebSocketServer
let port: number
let client: CoreClient | null = null

/** Message đã tới server, gom lại để kiểm tra. */
let received: Array<Record<string, unknown>> = []
/** Đặt true để server không ack, phục vụ test gửi lại. */
let withholdAck = false

function rawMessage(payload: string) {
  return {
    type: 'raw_message' as const,
    messageId: newMessageId(),
    sentAt: new Date().toISOString(),
    deviceId: 'may-1',
    direction: 'inbound' as const,
    payloadBase64: Buffer.from(payload, 'utf8').toString('base64'),
  }
}

async function waitFor(check: () => boolean, timeoutMs = 5000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (check()) return
    await new Promise((r) => setTimeout(r, 20))
  }
  throw new Error('hết giờ chờ điều kiện')
}

beforeEach(async () => {
  dir = mkdtempSync(join(tmpdir(), 'lis-client-'))
  db = LocalDb.open(join(dir, 'agent.sqlite'))
  outbox = new Outbox(db)
  received = []
  withholdAck = false

  http = createServer()
  wss = new WebSocketServer({ server: http, path: '/agent' })
  wss.on('connection', (socket: WebSocket, request) => {
    const url = new URL(request.url ?? '/', 'http://localhost')
    if (url.searchParams.get('key') !== 'lis_agent_dung') {
      socket.close(4401, 'khoá sai')
      return
    }
    socket.on('message', (data) => {
      const message = JSON.parse(data.toString()) as Record<string, unknown>
      received.push(message)
      if (withholdAck && message.type === 'raw_message') return
      socket.send(JSON.stringify({ type: 'ack', messageId: message.messageId }))
    })
  })
  await new Promise<void>((resolve) => http.listen(0, resolve))
  port = (http.address() as { port: number }).port
})

afterEach(async () => {
  await client?.stop()
  client = null
  wss.close()
  await new Promise<void>((resolve) => http.close(() => resolve()))
  db.close()
  rmSync(dir, { recursive: true, force: true })
})

describe('CoreClient', () => {
  it('kết nối và gửi hello ngay khi khởi động', async () => {
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_dung',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
    })
    client.start()

    await waitFor(() => received.some((m) => m.type === 'hello'))
    expect(client.isConnected()).toBe(true)
  })

  it('gửi các message đang chờ trong hàng đợi sau khi kết nối', async () => {
    outbox.enqueue(rawMessage('kết quả tồn từ lúc mất mạng'))
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_dung',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
    })
    client.start()

    await waitFor(() => outbox.depth() === 0)
    expect(received.some((m) => m.type === 'raw_message')).toBe(true)
  })

  it('không xoá message khỏi hàng đợi khi Core chưa ack', async () => {
    withholdAck = true
    outbox.enqueue(rawMessage('chưa được ack'))
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_dung',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
    })
    client.start()

    await waitFor(() => received.some((m) => m.type === 'raw_message'))
    await new Promise((r) => setTimeout(r, 300))
    expect(outbox.depth()).toBe(1)
  })

  it('báo trạng thái unauthorized và ngừng thử lại khi khoá sai', async () => {
    const statuses: string[] = []
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_sai',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
    })
    client.on('status', (s) => statuses.push(s))
    client.start()

    await waitFor(() => statuses.includes('unauthorized'))
    const countAfterFirst = statuses.filter((s) => s === 'unauthorized').length
    await new Promise((r) => setTimeout(r, 800))
    expect(statuses.filter((s) => s === 'unauthorized').length).toBe(countAfterFirst)
  })

  it('tự kết nối lại sau khi server đóng kết nối', async () => {
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_dung',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
      initialBackoffMs: 50,
    })
    client.start()
    await waitFor(() => client!.isConnected())

    for (const socket of wss.clients) socket.terminate()
    await waitFor(() => !client!.isConnected())
    await waitFor(() => client!.isConnected(), 8000)

    expect(received.filter((m) => m.type === 'hello').length).toBeGreaterThanOrEqual(2)
  })

  it('message đưa vào hàng đợi lúc đang mất kết nối sẽ được gửi khi nối lại', async () => {
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_dung',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
      initialBackoffMs: 50,
    })
    client.start()
    await waitFor(() => client!.isConnected())

    for (const socket of wss.clients) socket.terminate()
    await waitFor(() => !client!.isConnected())
    outbox.enqueue(rawMessage('gửi lúc mất mạng'))

    await waitFor(() => outbox.depth() === 0, 10_000)
  })

  it('backoff giãn dần rồi chững ở trần khi server bắt tay xong nhưng luôn từ chối phiên', async () => {
    // Server khác: hoàn tất bắt tay WebSocket (sự kiện 'open' phía client sẽ bắn),
    // rồi đóng ngay với mã KHÁC 4401 — mô phỏng Core còn sống nhưng từ chối phiên
    // (quá tải, đang triển khai bản mới, giới hạn tải...).
    const rejectHttp = createServer()
    const rejectWss = new WebSocketServer({ server: rejectHttp, path: '/agent' })
    const connectTimes: number[] = []
    rejectWss.on('connection', (socket: WebSocket) => {
      connectTimes.push(Date.now())
      socket.close(1011, 'Core đang quá tải')
    })
    await new Promise<void>((resolve) => rejectHttp.listen(0, resolve))
    const rejectPort = (rejectHttp.address() as { port: number }).port

    try {
      client = new CoreClient({
        url: `ws://localhost:${rejectPort}/agent`,
        agentKey: 'lis_agent_dung',
        agentVersion: '0.1.0',
        hostname: 'LAB-PC-01',
        outbox,
        initialBackoffMs: 50,
        maxBackoffMs: 400,
      })
      client.start()

      await waitFor(() => connectTimes.length >= 6, 10_000)

      const gaps: number[] = []
      for (let i = 1; i < connectTimes.length; i += 1) {
        gaps.push(connectTimes[i]! - connectTimes[i - 1]!)
      }

      // Giãn dần: mỗi khoảng cách (trừ khi đã chạm trần) phải lớn hơn khoảng trước,
      // với biên độ dung sai cho jitter của lịch trình hệ điều hành.
      for (let i = 1; i < gaps.length; i += 1) {
        const prev = gaps[i - 1]!
        const curr = gaps[i]!
        const prevAtCeiling = prev >= 400 * 0.8
        expect(curr >= prev * 0.7 || prevAtCeiling).toBe(true)
      }

      // Chững ở trần: khoảng cách cuối cùng không vượt xa maxBackoffMs.
      const lastGap = gaps[gaps.length - 1]!
      expect(lastGap).toBeLessThan(400 * 1.8)
      // Và không đứng yên ở initialBackoffMs suốt: phải có ít nhất một khoảng
      // giãn ra rõ rệt so với initialBackoffMs.
      expect(Math.max(...gaps)).toBeGreaterThan(50 * 1.8)
    } finally {
      rejectWss.close()
      await new Promise<void>((resolve) => rejectHttp.close(() => resolve()))
    }
  })
})
