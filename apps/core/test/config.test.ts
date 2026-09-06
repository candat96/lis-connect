import 'reflect-metadata'
import { describe, expect, it } from 'vitest'
import { loadConfig } from '../src/config.js'

const BASE_ENV = {
  ADMIN_DATABASE_URL: 'postgres://lis:lis@localhost:55432/lis_admin_test',
  TENANT_DATABASE_URL_TEMPLATE: 'postgres://lis:lis@localhost:55432/{db}',
}

describe('loadConfig — JWT_SECRET', () => {
  it('từ chối khi thiếu JWT_SECRET', () => {
    expect(() => loadConfig({ ...BASE_ENV })).toThrow(/JWT_SECRET/)
  })

  it('thông báo riêng khi thiếu JWT_SECRET ở production', () => {
    expect(() => loadConfig({ ...BASE_ENV, NODE_ENV: 'production' })).toThrow(
      /JWT_SECRET.*production/,
    )
  })

  it('từ chối JWT_SECRET quá ngắn (dưới 32 ký tự) — chặn bí mật kiểu "lisconnect"', () => {
    expect(() => loadConfig({ ...BASE_ENV, JWT_SECRET: 'lisconnect' })).toThrow(
      /JWT_SECRET quá ngắn/,
    )
  })

  it('chấp nhận JWT_SECRET đủ dài (>=32 ký tự)', () => {
    const config = loadConfig({ ...BASE_ENV, JWT_SECRET: 'a'.repeat(32) })
    expect(config.jwtSecret).toBe('a'.repeat(32))
  })

  it('chấp nhận bí mật sinh bằng openssl rand -base64 48 (độ dài thực tế ~64 ký tự)', () => {
    const realistic = 'Zm9vYmFyYmF6cXV1eGNvcmdlZ3JhdWx0Z2FycGx5d2FsZG9mcmVkcGx1Z2g='
    const config = loadConfig({ ...BASE_ENV, JWT_SECRET: realistic })
    expect(config.jwtSecret).toBe(realistic)
  })
})
