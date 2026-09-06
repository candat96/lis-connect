import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { newMessageId } from '@lis/shared'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { LocalDb } from './local-db.js'
import { Outbox, QUEUE_WARNING_THRESHOLD } from './outbox.js'

let dir: string
let db: LocalDb
let outbox: Outbox

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

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'lis-outbox-'))
  db = LocalDb.open(join(dir, 'agent.sqlite'))
  outbox = new Outbox(db)
})

afterEach(() => {
  db.close()
  rmSync(dir, { recursive: true, force: true })
})

describe('Outbox', () => {
  it('hàng đợi rỗng lúc mới tạo', () => {
    expect(outbox.depth()).toBe(0)
    expect(outbox.peek(10)).toEqual([])
  })

  it('enqueue rồi peek trả lại đúng message', () => {
    const message = rawMessage('R|1|GLU|5.4')
    outbox.enqueue(message)

    const peeked = outbox.peek(10)
    expect(peeked).toHaveLength(1)
    expect(peeked[0]!.message).toEqual(message)
  })

  it('peek trả về theo đúng thứ tự đưa vào', () => {
    const first = rawMessage('một')
    const second = rawMessage('hai')
    outbox.enqueue(first)
    outbox.enqueue(second)

    const peeked = outbox.peek(10)
    expect(peeked.map((p) => p.message.messageId)).toEqual([first.messageId, second.messageId])
  })

  it('ack xoá message khỏi hàng đợi', () => {
    outbox.enqueue(rawMessage('x'))
    const [entry] = outbox.peek(1)
    outbox.ack(entry!.rowId)

    expect(outbox.depth()).toBe(0)
  })

  it('message chưa ack vẫn còn sau khi mở lại database', () => {
    outbox.enqueue(rawMessage('còn nguyên'))
    db.close()

    const reopened = LocalDb.open(join(dir, 'agent.sqlite'))
    const reopenedOutbox = new Outbox(reopened)
    expect(reopenedOutbox.depth()).toBe(1)
    reopened.close()
  })

  it('peek tôn trọng giới hạn số lượng', () => {
    for (let i = 0; i < 5; i += 1) outbox.enqueue(rawMessage(`m${i}`))
    expect(outbox.peek(2)).toHaveLength(2)
  })

  it('purgeOlderThan xoá message quá cũ và trả về số lượng đã xoá', () => {
    outbox.enqueue(rawMessage('cũ'))
    db.raw
      .prepare("UPDATE outbox SET created_at = datetime('now', '-10 days')")
      .run()
    outbox.enqueue(rawMessage('mới'))

    expect(outbox.purgeOlderThan(7)).toBe(1)
    expect(outbox.depth()).toBe(1)
  })

  it('purgeOlderThan không xoá gì khi mọi message còn trong hạn', () => {
    outbox.enqueue(rawMessage('mới'))
    expect(outbox.purgeOlderThan(7)).toBe(0)
  })

  it('chưa vượt trần thì isOverThreshold false, không cảnh báo', () => {
    outbox.enqueue(rawMessage('một'))
    expect(outbox.isOverThreshold()).toBe(false)
  })

  it('vượt trần số lượng thì báo cảnh báo chứ không xoá gì', () => {
    // Chèn thẳng qua DB cho nhanh, tương đương enqueue() nhiều lần.
    const insert = db.raw.prepare('INSERT INTO outbox (message_id, payload) VALUES (?, ?)')
    const insertMany = db.raw.transaction((n: number) => {
      for (let i = 0; i < n; i += 1) {
        insert.run(`msg-${i}`, JSON.stringify(rawMessage(`m${i}`)))
      }
    })
    insertMany(QUEUE_WARNING_THRESHOLD + 1)

    expect(outbox.depth()).toBe(QUEUE_WARNING_THRESHOLD + 1)
    expect(outbox.isOverThreshold()).toBe(true)

    // Vượt trần không xoá bất cứ gì — chỉ là một tín hiệu cảnh báo.
    expect(outbox.depth()).toBe(QUEUE_WARNING_THRESHOLD + 1)
  })
})
