import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import request from 'supertest'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AppModule } from '../src/app.module.js'
import { resetAdminDatabase } from './helpers.js'

let app: INestApplication

beforeAll(async () => {
  await resetAdminDatabase()
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()
}, 90_000)

afterAll(async () => {
  await app.close()
})

describe('GET /health', () => {
  it('trả về ok khi kết nối được DB quản trị', async () => {
    const res = await request(app.getHttpServer()).get('/health').expect(200)
    expect(res.body).toMatchObject({ status: 'ok' })
  })

  it('không yêu cầu header tenant', async () => {
    await request(app.getHttpServer()).get('/health').expect(200)
  })

  it('báo phiên bản migration của DB quản trị', async () => {
    const res = await request(app.getHttpServer()).get('/health').expect(200)
    expect(res.body.adminMigration).toBeTruthy()
  })
})
