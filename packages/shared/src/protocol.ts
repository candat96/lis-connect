import { z } from 'zod'

export const PROTOCOL_VERSION = 1

export class ProtocolError extends Error {
  constructor(message: string, readonly detail?: unknown) {
    super(message)
    this.name = 'ProtocolError'
  }
}

const base64 = z.string().regex(/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/, {
  message: 'không phải base64 hợp lệ',
})

const envelope = z.object({
  messageId: z.string().uuid(),
  sentAt: z.string().datetime(),
})

const helloSchema = envelope.extend({
  type: z.literal('hello'),
  protocolVersion: z.number().int(),
  agentVersion: z.string(),
  hostname: z.string(),
})

const rawMessageSchema = envelope.extend({
  type: z.literal('raw_message'),
  deviceId: z.string().min(1),
  direction: z.enum(['inbound', 'outbound']),
  payloadBase64: base64,
})

const connectionEventSchema = envelope.extend({
  type: z.literal('connection_event'),
  deviceId: z.string().min(1),
  status: z.enum(['connected', 'disconnected', 'error']),
  detail: z.string().optional(),
})

const heartbeatSchema = envelope.extend({
  type: z.literal('heartbeat'),
  queueDepth: z.number().int().nonnegative(),
})

const agentToCoreSchema = z.discriminatedUnion('type', [
  helloSchema,
  rawMessageSchema,
  connectionEventSchema,
  heartbeatSchema,
])

export type HelloMessage = z.infer<typeof helloSchema>
export type RawMessageUp = z.infer<typeof rawMessageSchema>
export type ConnectionEventUp = z.infer<typeof connectionEventSchema>
export type HeartbeatUp = z.infer<typeof heartbeatSchema>
export type AgentToCore = z.infer<typeof agentToCoreSchema>

export interface AckMessage {
  type: 'ack'
  messageId: string
}

export interface NackMessage {
  type: 'nack'
  messageId: string
  reason: string
  /** true nếu Agent nên thử lại; false nếu message hỏng vĩnh viễn. */
  retryable: boolean
}

export interface ConfigPush {
  type: 'config'
  devices: Array<{ deviceId: string; driver: string; enabled: boolean }>
}

export type CoreToAgent = AckMessage | NackMessage | ConfigPush

export function parseAgentToCore(json: unknown): AgentToCore {
  const result = agentToCoreSchema.safeParse(json)
  if (!result.success) {
    throw new ProtocolError('message từ Agent không hợp lệ', result.error.flatten())
  }
  return result.data
}
