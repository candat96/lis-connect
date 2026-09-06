import { describe, expect, it } from 'vitest'
import { ACK, ENQ, EOT, STX, ETX, CR, LF, checksum } from './astm-constants.js'
import { AstmE1394Driver } from './astm-e1394.js'

function frame(seq: number, content: string): Buffer {
  const body = Buffer.concat([Buffer.from(String(seq) + content, 'latin1'), Buffer.from([ETX])])
  return Buffer.concat([
    Buffer.from([STX]),
    body,
    Buffer.from(checksum(body), 'latin1'),
    Buffer.from([CR, LF]),
  ])
}

describe('AstmE1394Driver', () => {
  it('trả ACK khi nhận ENQ', () => {
    const driver = new AstmE1394Driver()
    const events = driver.feed(Buffer.from([ENQ]))
    expect(events).toHaveLength(1)
    expect(events[0]).toMatchObject({ kind: 'reply' })
    expect((events[0] as { data: Buffer }).data[0]).toBe(ACK)
  })

  it('trả ACK cho mỗi frame hợp lệ', () => {
    const driver = new AstmE1394Driver()
    driver.feed(Buffer.from([ENQ]))
    const events = driver.feed(frame(1, 'H|\\^&|||BS-240'))
    expect(events.some((e) => e.kind === 'reply')).toBe(true)
  })

  it('gom các frame thành một message hoàn chỉnh khi nhận EOT', () => {
    const driver = new AstmE1394Driver()
    driver.feed(Buffer.from([ENQ]))
    driver.feed(frame(1, 'H|\\^&|||BS-240'))
    driver.feed(frame(2, 'P|1||||NGUYEN VAN A||19900101|M'))
    driver.feed(frame(3, 'O|1|SID001||^^^GLU|R'))
    driver.feed(frame(4, 'R|1|^^^GLU|5.4|mmol/L||N||F'))
    driver.feed(frame(5, 'L|1|N'))
    const events = driver.feed(Buffer.from([EOT]))

    const message = events.find((e) => e.kind === 'message')
    expect(message).toBeDefined()
    if (message?.kind !== 'message') throw new Error('sai loại sự kiện')

    const types = message.parsed.records.map((r) => r.type)
    expect(types).toEqual(['H', 'P', 'O', 'R', 'L'])

    const result = message.parsed.records.find((r) => r.type === 'R')
    expect(result?.fields[3]).toBe('5.4')
    expect(result?.fields[4]).toBe('mmol/L')
  })

  it('lấy được SID từ bản ghi O', () => {
    const driver = new AstmE1394Driver()
    driver.feed(Buffer.from([ENQ]))
    driver.feed(frame(1, 'O|1|SID001||^^^GLU|R'))
    const events = driver.feed(Buffer.from([EOT]))
    const message = events.find((e) => e.kind === 'message')
    if (message?.kind !== 'message') throw new Error('sai loại sự kiện')
    expect(message.parsed.records[0]?.fields[2]).toBe('SID001')
  })

  it('ghép được frame bị chia làm nhiều mẩu TCP', () => {
    const driver = new AstmE1394Driver()
    driver.feed(Buffer.from([ENQ]))
    const full = frame(1, 'R|1|^^^GLU|5.4|mmol/L||N||F')
    driver.feed(full.subarray(0, 5))
    driver.feed(full.subarray(5, 12))
    driver.feed(full.subarray(12))
    const events = driver.feed(Buffer.from([EOT]))

    const message = events.find((e) => e.kind === 'message')
    if (message?.kind !== 'message') throw new Error('sai loại sự kiện')
    expect(message.parsed.records[0]?.fields[3]).toBe('5.4')
  })

  it('trả NAK và sự kiện error khi checksum sai, không ném ngoại lệ', () => {
    const driver = new AstmE1394Driver()
    driver.feed(Buffer.from([ENQ]))

    const broken = Buffer.from(frame(1, 'R|1|^^^GLU|5.4|mmol/L'))
    broken[broken.length - 3] = broken[broken.length - 3] === 0x41 ? 0x42 : 0x41

    const events = driver.feed(broken)
    expect(events.some((e) => e.kind === 'error')).toBe(true)
    const error = events.find((e) => e.kind === 'error')
    if (error?.kind !== 'error') throw new Error('sai loại sự kiện')
    expect(error.raw.length).toBeGreaterThan(0)
  })

  it('bỏ qua rác ở đầu luồng mà không hỏng phiên sau đó', () => {
    const driver = new AstmE1394Driver()
    driver.feed(Buffer.from('rác đầu luồng', 'latin1'))
    driver.feed(Buffer.from([ENQ]))
    driver.feed(frame(1, 'R|1|^^^GLU|5.4|mmol/L||N||F'))
    const events = driver.feed(Buffer.from([EOT]))
    expect(events.some((e) => e.kind === 'message')).toBe(true)
  })

  it('reset xoá trạng thái dở dang', () => {
    const driver = new AstmE1394Driver()
    driver.feed(Buffer.from([ENQ]))
    driver.feed(frame(1, 'R|1|^^^GLU|5.4'))
    driver.reset()
    const events = driver.feed(Buffer.from([EOT]))
    expect(events.some((e) => e.kind === 'message')).toBe(false)
  })

  it('phát sự kiện error kèm nguyên văn byte khi frame hợp lệ đến trước ENQ', () => {
    const driver = new AstmE1394Driver()
    const strayFrame = frame(1, 'R|1|^^^GLU|5.4|mmol/L||N||F')
    const events = driver.feed(strayFrame)

    expect(events.some((e) => e.kind === 'message')).toBe(false)
    const error = events.find((e) => e.kind === 'error')
    if (error?.kind !== 'error') throw new Error('sai loại sự kiện')
    expect(error.raw.equals(strayFrame)).toBe(true)

    // phiên thật sau đó vẫn hoạt động bình thường, không bị lẫn với frame lạc
    driver.feed(Buffer.from([ENQ]))
    driver.feed(frame(1, 'R|1|^^^URE|6.1|mmol/L||N||F'))
    const laterEvents = driver.feed(Buffer.from([EOT]))
    const message = laterEvents.find((e) => e.kind === 'message')
    if (message?.kind !== 'message') throw new Error('sai loại sự kiện')
    expect(message.parsed.records[0]?.fields[3]).toBe('6.1')
  })

  it('gom rác liên tiếp thành một sự kiện error duy nhất chứa toàn bộ byte rác', () => {
    const driver = new AstmE1394Driver()
    const junk = Buffer.from([0x41, 0x42, 0x99, 0x50, 0x7a])
    const events = driver.feed(junk)

    const errorEvents = events.filter((e) => e.kind === 'error')
    expect(errorEvents).toHaveLength(1)
    const error = errorEvents[0]
    if (error?.kind !== 'error') throw new Error('sai loại sự kiện')
    expect(error.raw.equals(junk)).toBe(true)
  })

  it('một phiên kết thúc rồi phiên mới bắt đầu được ngay', () => {
    const driver = new AstmE1394Driver()
    driver.feed(Buffer.from([ENQ]))
    driver.feed(frame(1, 'R|1|^^^GLU|5.4|mmol/L||N||F'))
    driver.feed(Buffer.from([EOT]))

    driver.feed(Buffer.from([ENQ]))
    driver.feed(frame(1, 'R|1|^^^URE|6.1|mmol/L||N||F'))
    const events = driver.feed(Buffer.from([EOT]))

    const message = events.find((e) => e.kind === 'message')
    if (message?.kind !== 'message') throw new Error('sai loại sự kiện')
    expect(message.parsed.records[0]?.fields[3]).toBe('6.1')
  })
})
