import type { WebSocket } from 'ws'

export interface AgentSession {
  tenantId: string
  agentKeyId: string
  /** agents.id trong DB nghiệp vụ; chỉ có sau khi nhận hello. */
  agentId: string | null
  hostname: string | null
  socket: WebSocket
}
