import { describe, expect, it } from 'vitest'
import { PROTOCOL_VERSION, ProtocolError, parseAgentToCore } from './protocol.js'

const validRaw = {
  type: 'raw_message',
  messageId: '0193c8a0-0000-7000-8000-000000000001',
  sentAt: '2026-09-05T02:00:00.000Z',
  deviceId: 'dev-1',
  direction: 'inbound',
  payloadBase64: 'SGVsbG8=',
}

describe('parseAgentToCore', () => {
  it('accepts a well-formed raw_message', () => {
    const msg = parseAgentToCore(validRaw)
    expect(msg.type).toBe('raw_message')
    if (msg.type !== 'raw_message') throw new Error('narrowing failed')
    expect(msg.deviceId).toBe('dev-1')
  })

  it('accepts a hello message carrying the protocol version', () => {
    const msg = parseAgentToCore({
      type: 'hello',
      messageId: '0193c8a0-0000-7000-8000-000000000002',
      sentAt: '2026-09-05T02:00:00.000Z',
      protocolVersion: PROTOCOL_VERSION,
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
    })
    expect(msg.type).toBe('hello')
  })

  it('rejects an unknown message type', () => {
    expect(() => parseAgentToCore({ ...validRaw, type: 'nonsense' })).toThrow(ProtocolError)
  })

  it('rejects a message with no messageId', () => {
    const { messageId, ...withoutId } = validRaw
    expect(() => parseAgentToCore(withoutId)).toThrow(ProtocolError)
  })

  it('rejects payload that is not valid base64', () => {
    expect(() => parseAgentToCore({ ...validRaw, payloadBase64: 'not base64!!' })).toThrow(
      ProtocolError,
    )
  })

  it('rejects an empty base64 payload', () => {
    expect(() => parseAgentToCore({ ...validRaw, payloadBase64: '' })).toThrow(ProtocolError)
  })

  it('accepts a minimal valid base64 payload (single byte)', () => {
    const minimalPayload = Buffer.from([0x05]).toString('base64')
    const msg = parseAgentToCore({ ...validRaw, payloadBase64: minimalPayload })
    expect(msg.type).toBe('raw_message')
    if (msg.type !== 'raw_message') throw new Error('narrowing failed')
    expect(msg.payloadBase64).toBe(minimalPayload)
  })
})
