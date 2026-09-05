import 'reflect-metadata'
import type { ExecutionContext } from '@nestjs/common'
import { UnauthorizedException } from '@nestjs/common'
import { afterEach, describe, expect, it } from 'vitest'
import { DevTenantGuard } from '../src/auth/dev-tenant.guard.js'

const ORIGINAL_NODE_ENV = process.env.NODE_ENV

afterEach(() => {
  if (ORIGINAL_NODE_ENV === undefined) {
    delete process.env.NODE_ENV
  } else {
    process.env.NODE_ENV = ORIGINAL_NODE_ENV
  }
})

interface FakeRequest {
  headers: Record<string, string>
  tenantId?: string
}

function makeContext(request: FakeRequest): ExecutionContext {
  return {
    switchToHttp: () => ({
      getRequest: () => request,
    }),
  } as unknown as ExecutionContext
}

describe('DevTenantGuard', () => {
  it('ném lỗi khi NODE_ENV=production', () => {
    process.env.NODE_ENV = 'production'
    const guard = new DevTenantGuard()
    const context = makeContext({ headers: { 'x-tenant-id': 'tenant-1' } })

    expect(() => guard.canActivate(context)).toThrow('DevTenantGuard không được dùng ở production')
  })

  it('trả về true và gán tenantId vào request khi có header hợp lệ', () => {
    process.env.NODE_ENV = 'test'
    const guard = new DevTenantGuard()
    const request: FakeRequest = { headers: { 'x-tenant-id': 'tenant-1' } }
    const context = makeContext(request)

    expect(guard.canActivate(context)).toBe(true)
    expect(request.tenantId).toBe('tenant-1')
  })

  it('ném UnauthorizedException khi thiếu header tenant', () => {
    process.env.NODE_ENV = 'test'
    const guard = new DevTenantGuard()
    const context = makeContext({ headers: {} })

    expect(() => guard.canActivate(context)).toThrow(UnauthorizedException)
  })
})
