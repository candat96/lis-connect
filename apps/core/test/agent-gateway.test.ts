import 'reflect-metadata'
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import { PROTOCOL_VERSION, newMessageId } from '@lis/shared'
import WebSocket from 'ws'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AppModule } from '../src/app.module.js'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
import { AgentGateway } from '../src/agent-gateway/agent.gateway.js'
import { AgentKeyService } from '../src/tenant/agent-key.service.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'
import { prepareTestTenant, resetAdminDatabase } from './helpers.js'

let app: INestApplication
let port: number
let agentKey: string
let tenantId: string

/** Chờ đúng một message từ socket, hoặc hết giờ sau 5 giây. */
function nextMessage(ws: WebSocket): Promise<Record<string, unknown>> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('hết giờ chờ message')), 5000)
    ws.once('message', (data) => {
      clearTimeout(timer)
      resolve(JSON.parse(data.toString()) as Record<string, unknown>)
    })
  })
}

function closedWith(ws: WebSocket): Promise<number> {
  return new Promise((resolve) => ws.once('close', (code) => resolve(code)))
}

beforeAll(async () => {
  await resetAdminDatabase()
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()
  await app.listen(0)
  app.get(AgentGateway).attach(app.getHttpServer())
  port = app.getHttpServer().address().port

  const admin = app.get(AdminDbService)
  const migrator = app.get(TenantMigratorService)
  const keys = app.get(AgentKeyService)
  tenantId = await prepareTestTenant(admin.db, migrator, 'gw-a', 'lis_t_gw_a')
  agentKey = (await keys.issue(tenantId, 'PC test')).plainKey
}, 90_000)

afterAll(async () => {
  await app.close()
})

describe('AgentGateway', () => {
  it('từ chối kết nối không có khoá', async () => {
    const ws = new WebSocket(`ws://localhost:${port}/agent`)
    expect(await closedWith(ws)).toBe(4401)
  })

  it('từ chối khoá sai', async () => {
    const ws = new WebSocket(`ws://localhost:${port}/agent?key=lis_agent_sai`)
    expect(await closedWith(ws)).toBe(4401)
  })

  it('chấp nhận khoá đúng và ack message hello', async () => {
    const ws = new WebSocket(`ws://localhost:${port}/agent?key=${agentKey}`)
    await new Promise((r) => ws.once('open', r))

    const messageId = newMessageId()
    ws.send(
      JSON.stringify({
        type: 'hello',
        messageId,
        sentAt: new Date().toISOString(),
        protocolVersion: PROTOCOL_VERSION,
        agentVersion: '0.1.0',
        hostname: 'LAB-PC-01',
      }),
    )

    const reply = await nextMessage(ws)
    expect(reply).toMatchObject({ type: 'ack', messageId })
    ws.close()
  })

  it('ghi bản ghi agent vào đúng database của tenant', async () => {
    const registry = app.get(TenantRegistryService)
    const db = await registry.forTenant(tenantId)
    const agents = await db.selectFrom('agents').selectAll().execute()
    expect(agents.length).toBe(1)
    expect(agents[0]!.hostname).toBe('LAB-PC-01')
  })

  it('đóng kết nối khi phiên bản giao thức không khớp', async () => {
    const ws = new WebSocket(`ws://localhost:${port}/agent?key=${agentKey}`)
    await new Promise((r) => ws.once('open', r))
    ws.send(
      JSON.stringify({
        type: 'hello',
        messageId: newMessageId(),
        sentAt: new Date().toISOString(),
        protocolVersion: PROTOCOL_VERSION + 99,
        agentVersion: '0.1.0',
        hostname: 'LAB-PC-02',
      }),
    )
    expect(await closedWith(ws)).toBe(4400)
  })

  it('trả nack cho message sai định dạng thay vì đóng kết nối', async () => {
    const ws = new WebSocket(`ws://localhost:${port}/agent?key=${agentKey}`)
    await new Promise((r) => ws.once('open', r))
    ws.send(
      JSON.stringify({
        type: 'hello',
        messageId: newMessageId(),
        sentAt: new Date().toISOString(),
        protocolVersion: PROTOCOL_VERSION,
        agentVersion: '0.1.0',
        hostname: 'LAB-PC-03',
      }),
    )
    await nextMessage(ws)

    ws.send(JSON.stringify({ type: 'raw_message', messageId: 'không-phải-uuid' }))
    const reply = await nextMessage(ws)
    expect(reply.type).toBe('nack')
    expect(reply.retryable).toBe(false)
    expect(ws.readyState).toBe(WebSocket.OPEN)
    ws.close()
  })
})
