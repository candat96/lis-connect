# LIS Connect — Giai đoạn 1: Xương sống một chiều

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dựng đường đi hoàn chỉnh của một kết quả xét nghiệm từ máy giả lập, qua Agent trên Windows, lên Core multi-tenant trên cloud, hiện lên Web UI — kèm công cụ cấu hình và dò lỗi tại chỗ.

**Architecture:** Agent là app Electron chạy tại phòng xét nghiệm: `main` mở cổng serial/TCP/thư mục, parse bằng driver nạp động, xếp message vào hàng đợi SQLite bền, đẩy lên Core qua WebSocket có `messageId` để Core xử lý idempotent. Core là NestJS định tuyến mỗi request/kết nối vào đúng database của tenant, với một DB quản trị chung và một DB nghiệp vụ cho mỗi khách. Web UI hiển thị trạng thái kết nối máy và kết quả thô.

**Tech Stack:** pnpm workspaces · TypeScript 5.7 · Node 22 · NestJS 11 · Kysely 0.27 + `pg` · PostgreSQL 16 · React 19 + Vite · Electron 33 + electron-vite · `serialport` 12 · `better-sqlite3` 11 · Vitest 2 · Docker Compose

**Spec:** `docs/superpowers/specs/2026-09-05-lis-connect-design.md`

## Global Constraints

- Node 22 LTS. TypeScript `strict: true` ở mọi package. ESM (`"type": "module"`) trừ `apps/agent` phần main của Electron dùng CJS theo mặc định của electron-vite.
- **Không bao giờ vứt dữ liệu từ máy xét nghiệm.** Mọi byte nhận được phải được lưu vào `raw_messages` trước khi parse. Parse lỗi thì đánh dấu, không xoá.
- **Mọi message Agent → Core mang `messageId` (UUID v7 do Agent sinh) và Core xử lý idempotent.** Nhận lại `messageId` đã có thì trả ACK và bỏ qua, không tạo bản ghi mới.
- **Không có truy vấn nghiệp vụ nào được tự chọn database.** Mọi truy cập DB nghiệp vụ đi qua `TenantContext` do middleware/guard gán. Vi phạm quy tắc này là rò dữ liệu bệnh nhân giữa hai bệnh viện.
- Tên bảng và cột trong Postgres viết `snake_case`. Kiểu TypeScript viết `PascalCase`, biến `camelCase`.
- Ngôn ngữ giao diện: tiếng Việt có dấu đầy đủ. Mã nguồn, tên hàm, tên biến, thông điệp commit: tiếng Anh.
- Mọi thời điểm lưu dạng `timestamptz`, sinh bằng `now()` của Postgres hoặc `new Date()` UTC. Không lưu giờ địa phương.
- Commit sau mỗi task. Thông điệp theo Conventional Commits.

---

## File Structure

```
lis-connect/
├─ package.json                      pnpm workspace root, script chung
├─ pnpm-workspace.yaml
├─ tsconfig.base.json                cấu hình TS dùng chung
├─ docker-compose.yml                Postgres cho phát triển và test
├─ packages/shared/
│  ├─ src/protocol.ts                kiểu message Agent ↔ Core, hằng số
│  ├─ src/protocol.test.ts
│  ├─ src/ids.ts                     sinh UUID v7
│  └─ src/index.ts
├─ apps/core/
│  ├─ src/main.ts                    bootstrap NestJS
│  ├─ src/app.module.ts
│  ├─ src/admin-db/schema.ts         kiểu Kysely của DB quản trị
│  ├─ src/admin-db/admin-db.service.ts
│  ├─ src/admin-db/migrations/       migration DB quản trị
│  ├─ src/tenant/tenant-schema.ts    kiểu Kysely của DB nghiệp vụ
│  ├─ src/tenant/tenant-registry.service.ts   pool theo tenant
│  ├─ src/tenant/tenant-migrator.service.ts   chạy migration trên N DB
│  ├─ src/tenant/migrations/         migration DB nghiệp vụ
│  ├─ src/tenant/tenant-context.ts   AsyncLocalStorage giữ tenant hiện tại
│  ├─ src/tenant/agent-key.guard.ts
│  ├─ src/agent-gateway/agent.gateway.ts      WebSocket cho Agent
│  ├─ src/ingest/ingest.service.ts   ghi raw_message + result, idempotent
│  ├─ src/devices/devices.controller.ts
│  └─ test/                          test tích hợp có Postgres thật
├─ apps/agent/
│  ├─ electron.vite.config.ts
│  ├─ src/main/index.ts              điểm vào Electron main
│  ├─ src/main/config-store.ts       cấu hình cục bộ
│  ├─ src/main/local-db.ts           SQLite: hàng đợi + log
│  ├─ src/main/outbox.ts             hàng đợi bền gửi lên Core
│  ├─ src/main/core-client.ts        WebSocket client có backoff
│  ├─ src/main/transports/serial.ts
│  ├─ src/main/transports/tcp.ts
│  ├─ src/main/transports/file.ts
│  ├─ src/main/transports/types.ts
│  ├─ src/main/drivers/registry.ts
│  ├─ src/main/drivers/astm-e1394.ts
│  ├─ src/main/device-manager.ts     ghép transport + driver + outbox
│  ├─ src/preload/index.ts
│  └─ src/renderer/                  React: 3 tab của giai đoạn 1
├─ apps/web/
│  └─ src/                           React: đăng nhập, theo dõi máy, kết quả thô
└─ tools/simulator/
   └─ src/index.ts                   máy xét nghiệm giả lập nói ASTM
```

**Nguyên tắc phân chia:** transport không biết gì về giao thức; driver không biết gì về mạng; outbox không biết gì về nội dung message. Ba lớp này được ghép lại ở `device-manager.ts` và chỉ ở đó.

---

## Task 1: Dựng monorepo và hạ tầng kiểm thử

**Files:**
- Create: `package.json`, `pnpm-workspace.yaml`, `tsconfig.base.json`, `docker-compose.yml`, `.gitignore`, `vitest.workspace.ts`
- Create: `packages/shared/package.json`, `packages/shared/tsconfig.json`, `packages/shared/src/index.ts`
- Test: `packages/shared/src/smoke.test.ts`

**Interfaces:**
- Consumes: không
- Produces: workspace `@lis/shared`; lệnh `pnpm test` chạy Vitest toàn repo; Postgres ở `postgres://lis:lis@localhost:55432/postgres`

- [ ] **Step 1: Tạo file cấu hình workspace**

`package.json`:
```json
{
  "name": "lis-connect",
  "private": true,
  "type": "module",
  "packageManager": "pnpm@9.15.0",
  "engines": { "node": ">=22" },
  "scripts": {
    "test": "vitest run",
    "test:watch": "vitest",
    "typecheck": "tsc -b --pretty",
    "db:up": "docker compose up -d && node scripts/wait-for-db.mjs",
    "db:down": "docker compose down -v"
  },
  "devDependencies": {
    "typescript": "5.7.2",
    "vitest": "2.1.8",
    "@types/node": "22.10.2"
  }
}
```

`pnpm-workspace.yaml`:
```yaml
packages:
  - 'packages/*'
  - 'apps/*'
  - 'tools/*'
```

`tsconfig.base.json`:
```json
{
  "compilerOptions": {
    "target": "ES2023",
    "lib": ["ES2023"],
    "module": "ESNext",
    "moduleResolution": "Bundler",
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "exactOptionalPropertyTypes": true,
    "skipLibCheck": true,
    "declaration": true,
    "sourceMap": true,
    "esModuleInterop": true,
    "resolveJsonModule": true
  }
}
```

`docker-compose.yml`:
```yaml
services:
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_USER: lis
      POSTGRES_PASSWORD: lis
      POSTGRES_DB: postgres
    ports:
      - "55432:5432"
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U lis"]
      interval: 2s
      timeout: 3s
      retries: 30
```

`vitest.workspace.ts`:
```ts
export default ['packages/*', 'apps/*', 'tools/*']
```

`.gitignore`:
```
node_modules/
dist/
out/
*.tsbuildinfo
.env
.env.local
```

- [ ] **Step 2: Tạo package shared và một test khói**

`packages/shared/package.json`:
```json
{
  "name": "@lis/shared",
  "version": "0.0.0",
  "type": "module",
  "main": "./src/index.ts",
  "types": "./src/index.ts",
  "scripts": { "test": "vitest run" }
}
```

`packages/shared/tsconfig.json`:
```json
{
  "extends": "../../tsconfig.base.json",
  "compilerOptions": { "outDir": "dist", "rootDir": "src" },
  "include": ["src"]
}
```

`packages/shared/src/index.ts`:
```ts
export const PACKAGE_NAME = '@lis/shared'
```

`packages/shared/src/smoke.test.ts`:
```ts
import { describe, expect, it } from 'vitest'
import { PACKAGE_NAME } from './index.js'

describe('shared package', () => {
  it('is wired into the workspace', () => {
    expect(PACKAGE_NAME).toBe('@lis/shared')
  })
})
```

- [ ] **Step 3: Cài phụ thuộc và chạy test**

Run:
```bash
pnpm install
pnpm test
```
Expected: 1 test PASS.

- [ ] **Step 4: Viết script chờ Postgres sẵn sàng**

`scripts/wait-for-db.mjs`:
```js
import { execSync } from 'node:child_process'

const deadline = Date.now() + 60_000
while (Date.now() < deadline) {
  try {
    execSync('docker compose exec -T postgres pg_isready -U lis', { stdio: 'ignore' })
    console.log('postgres ready')
    process.exit(0)
  } catch {
    await new Promise((r) => setTimeout(r, 1000))
  }
}
console.error('postgres did not become ready in 60s')
process.exit(1)
```

- [ ] **Step 5: Khởi động Postgres và xác nhận**

Run:
```bash
pnpm db:up
```
Expected: in ra `postgres ready`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: bootstrap pnpm monorepo with vitest and postgres"
```

---

## Task 2: Định nghĩa giao thức Agent ↔ Core

Đây là hợp đồng giữa hai chương trình do hai nhóm task khác nhau xây. Định nghĩa nó trước, ở một chỗ, để Agent và Core không bao giờ lệch nhau.

**Files:**
- Create: `packages/shared/src/ids.ts`, `packages/shared/src/protocol.ts`
- Modify: `packages/shared/src/index.ts`
- Test: `packages/shared/src/protocol.test.ts`, `packages/shared/src/ids.test.ts`

**Interfaces:**
- Consumes: Task 1 (workspace `@lis/shared`)
- Produces:
  - `newMessageId(): string` — UUID v7
  - type `AgentToCore` = `HelloMessage | RawMessageUp | ConnectionEventUp | HeartbeatUp`
  - type `CoreToAgent` = `AckMessage | NackMessage | ConfigPush`
  - `parseAgentToCore(json: unknown): AgentToCore` — ném `ProtocolError` nếu sai
  - hằng `PROTOCOL_VERSION = 1`

- [ ] **Step 1: Viết test cho sinh UUID v7**

`packages/shared/src/ids.test.ts`:
```ts
import { describe, expect, it } from 'vitest'
import { newMessageId } from './ids.js'

describe('newMessageId', () => {
  it('returns a v7 uuid', () => {
    const id = newMessageId()
    expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  })

  it('sorts chronologically as a string', async () => {
    const first = newMessageId()
    await new Promise((r) => setTimeout(r, 5))
    const second = newMessageId()
    expect(first < second).toBe(true)
  })
})
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm vitest run packages/shared/src/ids.test.ts`
Expected: FAIL — không tìm thấy module `./ids.js`.

- [ ] **Step 3: Cài phụ thuộc và viết `ids.ts`**

Run: `pnpm --filter @lis/shared add uuid@11.0.3 zod@3.24.1`

`packages/shared/src/ids.ts`:
```ts
import { v7 as uuidv7 } from 'uuid'

/** Sinh mã message dạng UUID v7: sắp xếp được theo thời gian, dùng làm khoá idempotent. */
export function newMessageId(): string {
  return uuidv7()
}
```

- [ ] **Step 4: Chạy test, xác nhận pass**

Run: `pnpm vitest run packages/shared/src/ids.test.ts`
Expected: 2 test PASS.

- [ ] **Step 5: Viết test cho parse giao thức**

`packages/shared/src/protocol.test.ts`:
```ts
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
})
```

- [ ] **Step 6: Chạy test, xác nhận fail**

Run: `pnpm vitest run packages/shared/src/protocol.test.ts`
Expected: FAIL — không tìm thấy module `./protocol.js`.

- [ ] **Step 7: Viết `protocol.ts`**

`packages/shared/src/protocol.ts`:
```ts
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
```

- [ ] **Step 8: Xuất lại từ index và chạy toàn bộ test**

`packages/shared/src/index.ts`:
```ts
export * from './ids.js'
export * from './protocol.js'

export const PACKAGE_NAME = '@lis/shared'
```

Run: `pnpm test`
Expected: toàn bộ test PASS (1 smoke + 2 ids + 5 protocol).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(shared): define agent-core protocol with zod validation"
```

---

## Task 3: Core — khung NestJS và DB quản trị

**Files:**
- Create: `apps/core/package.json`, `apps/core/tsconfig.json`, `apps/core/vitest.config.ts`, `apps/core/.env.example`
- Create: `apps/core/src/main.ts`, `apps/core/src/app.module.ts`, `apps/core/src/config.ts`
- Create: `apps/core/src/admin-db/schema.ts`, `apps/core/src/admin-db/admin-db.service.ts`, `apps/core/src/admin-db/admin-db.module.ts`
- Create: `apps/core/src/admin-db/migrations/001-initial.ts`, `apps/core/src/admin-db/migrator.ts`
- Test: `apps/core/test/admin-db.test.ts`

**Interfaces:**
- Consumes: Task 1 (Postgres ở cổng 55432)
- Produces:
  - `AdminDatabase` — kiểu Kysely của DB quản trị, có bảng `tenants`, `agent_keys`, `tenant_migrations`
  - `AdminDbService.db: Kysely<AdminDatabase>`
  - `runAdminMigrations(db: Kysely<AdminDatabase>): Promise<void>`
  - `loadConfig(): CoreConfig` với `{ adminDatabaseUrl: string; tenantDatabaseUrlTemplate: string; port: number }`

**Ghi chú thiết kế:** `tenantDatabaseUrlTemplate` chứa `{db}` và được thay bằng `tenants.database_name`. Nhờ vậy một biến môi trường duy nhất mô tả được cả cụm, và việc tách một tenant nặng sang Postgres khác chỉ là ghi đè `tenants.database_url`.

- [ ] **Step 1: Tạo package và cài phụ thuộc**

`apps/core/package.json`:
```json
{
  "name": "@lis/core",
  "version": "0.0.0",
  "type": "module",
  "scripts": {
    "start:dev": "node --import tsx/esm --watch src/main.ts",
    "test": "vitest run"
  }
}
```

Run:
```bash
pnpm --filter @lis/core add @nestjs/common@11 @nestjs/core@11 @nestjs/platform-express@11 @nestjs/platform-ws@11 @nestjs/websockets@11 reflect-metadata@0.2.2 rxjs@7.8.1 kysely@0.27.5 pg@8.13.1 ws@8.18.0 @lis/shared@workspace:*
pnpm --filter @lis/core add -D tsx@4.19.2 @types/pg@8.11.10 @types/ws@8.5.13
```

`apps/core/tsconfig.json`:
```json
{
  "extends": "../../tsconfig.base.json",
  "compilerOptions": {
    "outDir": "dist",
    "rootDir": "src",
    "experimentalDecorators": true,
    "emitDecoratorMetadata": true,
    "moduleResolution": "Bundler"
  },
  "include": ["src", "test"]
}
```

`apps/core/vitest.config.ts`:
```ts
import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    include: ['src/**/*.test.ts', 'test/**/*.test.ts'],
    hookTimeout: 30_000,
    testTimeout: 30_000,
    // Test tích hợp dùng Postgres thật; chạy tuần tự để tránh giẫm chân nhau.
    fileParallelism: false,
  },
})
```

`apps/core/.env.example`:
```
CORE_PORT=3000
ADMIN_DATABASE_URL=postgres://lis:lis@localhost:55432/lis_admin
TENANT_DATABASE_URL_TEMPLATE=postgres://lis:lis@localhost:55432/{db}
```

- [ ] **Step 2: Viết test cho migration DB quản trị**

`apps/core/test/admin-db.test.ts`:
```ts
import { Kysely, PostgresDialect, sql } from 'kysely'
import pg from 'pg'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import type { AdminDatabase } from '../src/admin-db/schema.js'
import { runAdminMigrations } from '../src/admin-db/migrator.js'

const ROOT_URL = 'postgres://lis:lis@localhost:55432/postgres'
const DB_NAME = 'lis_admin_test'

let db: Kysely<AdminDatabase>

async function recreateDatabase(): Promise<void> {
  const root = new pg.Client({ connectionString: ROOT_URL })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${DB_NAME} WITH (FORCE)`)
  await root.query(`CREATE DATABASE ${DB_NAME}`)
  await root.end()
}

beforeAll(async () => {
  await recreateDatabase()
  db = new Kysely<AdminDatabase>({
    dialect: new PostgresDialect({
      pool: new pg.Pool({ connectionString: `postgres://lis:lis@localhost:55432/${DB_NAME}` }),
    }),
  })
  await runAdminMigrations(db)
})

afterAll(async () => {
  await db.destroy()
})

describe('admin database', () => {
  it('creates the tenants table', async () => {
    const rows = await db.selectFrom('tenants').selectAll().execute()
    expect(rows).toEqual([])
  })

  it('stores a tenant and reads it back', async () => {
    const tenant = await db
      .insertInto('tenants')
      .values({ code: 'bvA', name: 'Bệnh viện A', database_name: 'lis_t_bva', status: 'active' })
      .returningAll()
      .executeTakeFirstOrThrow()

    expect(tenant.code).toBe('bvA')
    expect(tenant.database_url).toBeNull()
  })

  it('rejects two tenants with the same code', async () => {
    await db
      .insertInto('tenants')
      .values({ code: 'bvB', name: 'Bệnh viện B', database_name: 'lis_t_bvb', status: 'active' })
      .execute()

    await expect(
      db
        .insertInto('tenants')
        .values({ code: 'bvB', name: 'Trùng mã', database_name: 'lis_t_other', status: 'active' })
        .execute(),
    ).rejects.toThrow()
  })

  it('stores an agent key bound to a tenant', async () => {
    const tenant = await db
      .insertInto('tenants')
      .values({ code: 'bvC', name: 'Bệnh viện C', database_name: 'lis_t_bvc', status: 'active' })
      .returningAll()
      .executeTakeFirstOrThrow()

    const key = await db
      .insertInto('agent_keys')
      .values({
        tenant_id: tenant.id,
        key_hash: 'sha256:abc',
        agent_label: 'PC phòng sinh hóa',
        status: 'active',
      })
      .returningAll()
      .executeTakeFirstOrThrow()

    expect(key.tenant_id).toBe(tenant.id)
  })

  it('is idempotent when migrations run twice', async () => {
    await expect(runAdminMigrations(db)).resolves.not.toThrow()
    const count = await sql<{ n: string }>`select count(*)::text as n from tenants`.execute(db)
    expect(Number(count.rows[0]!.n)).toBeGreaterThan(0)
  })
})
```

- [ ] **Step 3: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test`
Expected: FAIL — không tìm thấy `../src/admin-db/schema.js`.

- [ ] **Step 4: Viết schema và migration của DB quản trị**

`apps/core/src/admin-db/schema.ts`:
```ts
import type { ColumnType, Generated } from 'kysely'

export type TenantStatus = 'active' | 'suspended'
export type AgentKeyStatus = 'active' | 'revoked'

export interface TenantsTable {
  id: Generated<string>
  code: string
  name: string
  /** Tên database nghiệp vụ của tenant, ghép vào TENANT_DATABASE_URL_TEMPLATE. */
  database_name: string
  /** Ghi đè toàn bộ URL khi tenant được tách sang cụm Postgres khác. */
  database_url: ColumnType<string | null, string | null | undefined, string | null>
  status: TenantStatus
  created_at: Generated<Date>
}

export interface AgentKeysTable {
  id: Generated<string>
  tenant_id: string
  /** Chỉ lưu hash; khoá gốc chỉ hiện một lần lúc cấp phát. */
  key_hash: string
  agent_label: string
  status: AgentKeyStatus
  last_seen_at: ColumnType<Date | null, Date | null | undefined, Date | null>
  created_at: Generated<Date>
}

export interface TenantMigrationsTable {
  tenant_id: string
  migration_name: string
  applied_at: Generated<Date>
}

export interface AdminDatabase {
  tenants: TenantsTable
  agent_keys: AgentKeysTable
  tenant_migrations: TenantMigrationsTable
}
```

`apps/core/src/admin-db/migrations/001-initial.ts`:
```ts
import { type Kysely, sql } from 'kysely'

export async function up(db: Kysely<unknown>): Promise<void> {
  await sql`create extension if not exists "pgcrypto"`.execute(db)

  await db.schema
    .createTable('tenants')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('code', 'varchar(64)', (c) => c.notNull().unique())
    .addColumn('name', 'text', (c) => c.notNull())
    .addColumn('database_name', 'varchar(63)', (c) => c.notNull().unique())
    .addColumn('database_url', 'text')
    .addColumn('status', 'varchar(16)', (c) => c.notNull().defaultTo('active'))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createTable('agent_keys')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('tenant_id', 'uuid', (c) => c.notNull().references('tenants.id').onDelete('cascade'))
    .addColumn('key_hash', 'text', (c) => c.notNull().unique())
    .addColumn('agent_label', 'text', (c) => c.notNull())
    .addColumn('status', 'varchar(16)', (c) => c.notNull().defaultTo('active'))
    .addColumn('last_seen_at', 'timestamptz')
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createTable('tenant_migrations')
    .addColumn('tenant_id', 'uuid', (c) => c.notNull().references('tenants.id').onDelete('cascade'))
    .addColumn('migration_name', 'text', (c) => c.notNull())
    .addColumn('applied_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .addPrimaryKeyConstraint('tenant_migrations_pk', ['tenant_id', 'migration_name'])
    .execute()
}

export async function down(db: Kysely<unknown>): Promise<void> {
  await db.schema.dropTable('tenant_migrations').execute()
  await db.schema.dropTable('agent_keys').execute()
  await db.schema.dropTable('tenants').execute()
}
```

`apps/core/src/admin-db/migrator.ts`:
```ts
import { Kysely, Migrator, type Migration, type MigrationProvider } from 'kysely'
import type { AdminDatabase } from './schema.js'
import * as initial from './migrations/001-initial.js'

const migrations: Record<string, Migration> = {
  '001-initial': initial,
}

class StaticProvider implements MigrationProvider {
  async getMigrations(): Promise<Record<string, Migration>> {
    return migrations
  }
}

export async function runAdminMigrations(db: Kysely<AdminDatabase>): Promise<void> {
  const migrator = new Migrator({ db, provider: new StaticProvider() })
  const { error, results } = await migrator.migrateToLatest()
  if (error) {
    const failed = results?.find((r) => r.status === 'Error')?.migrationName ?? 'không rõ'
    throw new Error(`migration DB quản trị thất bại ở "${failed}": ${String(error)}`)
  }
}
```

**Vì sao dùng danh sách migration tĩnh thay vì `FileMigrationProvider`:** Core sẽ được đóng gói và chạy trên cloud, nơi đường dẫn thư mục sau khi build không giống lúc phát triển. Nhập tĩnh khiến migration đi cùng bundle và không bao giờ "biến mất" lúc chạy thật.

- [ ] **Step 5: Chạy test, xác nhận pass**

Run: `pnpm db:up && pnpm --filter @lis/core test`
Expected: 5 test PASS.

- [ ] **Step 6: Viết config, service và module**

`apps/core/src/config.ts`:
```ts
export interface CoreConfig {
  port: number
  adminDatabaseUrl: string
  tenantDatabaseUrlTemplate: string
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): CoreConfig {
  const adminDatabaseUrl = env.ADMIN_DATABASE_URL
  const tenantDatabaseUrlTemplate = env.TENANT_DATABASE_URL_TEMPLATE
  if (!adminDatabaseUrl) throw new Error('thiếu biến môi trường ADMIN_DATABASE_URL')
  if (!tenantDatabaseUrlTemplate) throw new Error('thiếu biến môi trường TENANT_DATABASE_URL_TEMPLATE')
  if (!tenantDatabaseUrlTemplate.includes('{db}')) {
    throw new Error('TENANT_DATABASE_URL_TEMPLATE phải chứa {db}')
  }
  return {
    port: Number(env.CORE_PORT ?? 3000),
    adminDatabaseUrl,
    tenantDatabaseUrlTemplate,
  }
}
```

`apps/core/src/admin-db/admin-db.service.ts`:
```ts
import { Injectable, type OnModuleDestroy, type OnModuleInit } from '@nestjs/common'
import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import { loadConfig } from '../config.js'
import { runAdminMigrations } from './migrator.js'
import type { AdminDatabase } from './schema.js'

@Injectable()
export class AdminDbService implements OnModuleInit, OnModuleDestroy {
  readonly db: Kysely<AdminDatabase>

  constructor() {
    const config = loadConfig()
    this.db = new Kysely<AdminDatabase>({
      dialect: new PostgresDialect({
        pool: new pg.Pool({ connectionString: config.adminDatabaseUrl, max: 10 }),
      }),
    })
  }

  async onModuleInit(): Promise<void> {
    await runAdminMigrations(this.db)
  }

  async onModuleDestroy(): Promise<void> {
    await this.db.destroy()
  }
}
```

`apps/core/src/admin-db/admin-db.module.ts`:
```ts
import { Global, Module } from '@nestjs/common'
import { AdminDbService } from './admin-db.service.js'

@Global()
@Module({ providers: [AdminDbService], exports: [AdminDbService] })
export class AdminDbModule {}
```

`apps/core/src/app.module.ts`:
```ts
import { Module } from '@nestjs/common'
import { AdminDbModule } from './admin-db/admin-db.module.js'

@Module({ imports: [AdminDbModule] })
export class AppModule {}
```

`apps/core/src/main.ts`:
```ts
import 'reflect-metadata'
import { NestFactory } from '@nestjs/core'
import { AppModule } from './app.module.js'
import { loadConfig } from './config.js'

async function bootstrap(): Promise<void> {
  const config = loadConfig()
  const app = await NestFactory.create(AppModule)
  await app.listen(config.port)
  console.log(`LIS Connect Core đang chạy ở cổng ${config.port}`)
}

void bootstrap()
```

- [ ] **Step 7: Chạy thử Core**

Run:
```bash
cd apps/core && cp .env.example .env
psql postgres://lis:lis@localhost:55432/postgres -c "CREATE DATABASE lis_admin"
node --import tsx/esm --env-file=.env src/main.ts
```
Expected: in ra `LIS Connect Core đang chạy ở cổng 3000`, không có lỗi migration. Dừng bằng Ctrl-C.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(core): bootstrap nestjs with admin database and migrations"
```

---

## Task 4: Core — schema nghiệp vụ và bộ chạy migration trên N database

**Files:**
- Create: `apps/core/src/tenant/tenant-schema.ts`, `apps/core/src/tenant/migrations/001-initial.ts`, `apps/core/src/tenant/tenant-migrator.service.ts`
- Test: `apps/core/test/tenant-migrator.test.ts`

**Interfaces:**
- Consumes: Task 3 (`AdminDatabase`, `AdminDbService`, `loadConfig`)
- Produces:
  - `TenantDatabase` — kiểu Kysely của DB nghiệp vụ với bảng `agents`, `devices`, `raw_messages`, `connection_events`, `device_status`
  - `TenantMigratorService.migrateTenant(tenantId: string): Promise<void>`
  - `TenantMigratorService.migrateAll(): Promise<{ ok: string[]; failed: Array<{ tenantId: string; error: string }> }>`
  - `TenantMigratorService.provisionDatabase(tenantId: string): Promise<void>` — tạo database nếu chưa có, rồi chạy migration

**Ghi chú thiết kế:** `migrateAll` **không dừng ở tenant lỗi đầu tiên**. Một tenant hỏng không được chặn 49 tenant còn lại lên phiên bản mới; nó trả về danh sách thất bại để người vận hành xử lý riêng.

- [ ] **Step 1: Viết test**

`apps/core/test/tenant-migrator.test.ts`:
```ts
import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { runAdminMigrations } from '../src/admin-db/migrator.js'
import type { AdminDatabase } from '../src/admin-db/schema.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import type { TenantDatabase } from '../src/tenant/tenant-schema.js'

const ROOT = 'postgres://lis:lis@localhost:55432/postgres'
const ADMIN_DB = 'lis_admin_mig_test'
const TEMPLATE = 'postgres://lis:lis@localhost:55432/{db}'

let admin: Kysely<AdminDatabase>
let migrator: TenantMigratorService
let tenantAId: string

async function dropAndCreate(name: string): Promise<void> {
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.query(`CREATE DATABASE ${name}`)
  await root.end()
}

async function dropIfExists(name: string): Promise<void> {
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.end()
}

beforeAll(async () => {
  await dropAndCreate(ADMIN_DB)
  await dropIfExists('lis_t_mig_a')
  await dropIfExists('lis_t_mig_b')

  admin = new Kysely<AdminDatabase>({
    dialect: new PostgresDialect({
      pool: new pg.Pool({ connectionString: `postgres://lis:lis@localhost:55432/${ADMIN_DB}` }),
    }),
  })
  await runAdminMigrations(admin)

  const a = await admin
    .insertInto('tenants')
    .values({ code: 'a', name: 'A', database_name: 'lis_t_mig_a', status: 'active' })
    .returningAll()
    .executeTakeFirstOrThrow()
  tenantAId = a.id

  await admin
    .insertInto('tenants')
    .values({ code: 'b', name: 'B', database_name: 'lis_t_mig_b', status: 'active' })
    .execute()

  migrator = new TenantMigratorService(admin, TEMPLATE)
}, 60_000)

afterAll(async () => {
  await migrator.closeAll()
  await admin.destroy()
})

describe('TenantMigratorService', () => {
  it('provisions a database and creates the business tables', async () => {
    await migrator.provisionDatabase(tenantAId)

    const db = new Kysely<TenantDatabase>({
      dialect: new PostgresDialect({
        pool: new pg.Pool({ connectionString: 'postgres://lis:lis@localhost:55432/lis_t_mig_a' }),
      }),
    })
    const devices = await db.selectFrom('devices').selectAll().execute()
    expect(devices).toEqual([])
    await db.destroy()
  })

  it('records applied migrations in the admin database', async () => {
    const rows = await admin
      .selectFrom('tenant_migrations')
      .selectAll()
      .where('tenant_id', '=', tenantAId)
      .execute()
    expect(rows.map((r) => r.migration_name)).toContain('001-initial')
  })

  it('is idempotent when run twice on the same tenant', async () => {
    await expect(migrator.migrateTenant(tenantAId)).resolves.not.toThrow()
  })

  it('migrateAll keeps going when one tenant fails', async () => {
    // Tenant B chưa có database và ta cố tình chỉ migrate, không provision.
    const result = await migrator.migrateAll()
    expect(result.ok).toContain(tenantAId)
    expect(result.failed.length).toBe(1)
    expect(result.failed[0]!.error).toBeTruthy()
  })
})
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test test/tenant-migrator.test.ts`
Expected: FAIL — không tìm thấy `tenant-schema.js`.

- [ ] **Step 3: Viết schema nghiệp vụ**

`apps/core/src/tenant/tenant-schema.ts`:
```ts
import type { ColumnType, Generated } from 'kysely'

export type DeviceTransport = 'serial' | 'tcp' | 'file'
export type DeviceConnStatus = 'connected' | 'disconnected' | 'error'
export type RawDirection = 'inbound' | 'outbound'
export type RawParseStatus = 'pending' | 'parsed' | 'parse_error'

export interface AgentsTable {
  id: Generated<string>
  /** agent_keys.id ở DB quản trị. Không có khoá ngoại vì khác database. */
  agent_key_id: string
  hostname: string
  agent_version: string
  last_seen_at: ColumnType<Date | null, Date | null | undefined, Date | null>
  created_at: Generated<Date>
}

export interface DevicesTable {
  id: Generated<string>
  agent_id: string
  /** Mã do Agent đặt, duy nhất trong phạm vi một Agent. */
  device_key: string
  name: string
  transport: DeviceTransport
  driver: string
  enabled: Generated<boolean>
  created_at: Generated<Date>
}

export interface RawMessagesTable {
  id: Generated<string>
  /** messageId của Agent — khoá idempotent. */
  message_id: string
  device_id: string
  direction: RawDirection
  payload: Buffer
  parse_status: Generated<RawParseStatus>
  parse_error: ColumnType<string | null, string | null | undefined, string | null>
  received_at: Generated<Date>
  sent_at: Date
}

export interface ConnectionEventsTable {
  id: Generated<string>
  message_id: string
  device_id: string
  status: DeviceConnStatus
  detail: ColumnType<string | null, string | null | undefined, string | null>
  occurred_at: Date
  created_at: Generated<Date>
}

export interface DeviceStatusTable {
  device_id: string
  status: DeviceConnStatus
  detail: ColumnType<string | null, string | null | undefined, string | null>
  last_message_at: ColumnType<Date | null, Date | null | undefined, Date | null>
  updated_at: Date
}

export interface TenantDatabase {
  agents: AgentsTable
  devices: DevicesTable
  raw_messages: RawMessagesTable
  connection_events: ConnectionEventsTable
  device_status: DeviceStatusTable
}
```

- [ ] **Step 4: Viết migration nghiệp vụ**

`apps/core/src/tenant/migrations/001-initial.ts`:
```ts
import { type Kysely, sql } from 'kysely'

export async function up(db: Kysely<unknown>): Promise<void> {
  await sql`create extension if not exists "pgcrypto"`.execute(db)

  await db.schema
    .createTable('agents')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('agent_key_id', 'uuid', (c) => c.notNull().unique())
    .addColumn('hostname', 'text', (c) => c.notNull())
    .addColumn('agent_version', 'text', (c) => c.notNull())
    .addColumn('last_seen_at', 'timestamptz')
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createTable('devices')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('agent_id', 'uuid', (c) => c.notNull().references('agents.id').onDelete('cascade'))
    .addColumn('device_key', 'varchar(64)', (c) => c.notNull())
    .addColumn('name', 'text', (c) => c.notNull())
    .addColumn('transport', 'varchar(16)', (c) => c.notNull())
    .addColumn('driver', 'varchar(64)', (c) => c.notNull())
    .addColumn('enabled', 'boolean', (c) => c.notNull().defaultTo(true))
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .addUniqueConstraint('devices_agent_key_unique', ['agent_id', 'device_key'])
    .execute()

  await db.schema
    .createTable('raw_messages')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('message_id', 'uuid', (c) => c.notNull().unique())
    .addColumn('device_id', 'uuid', (c) => c.notNull().references('devices.id').onDelete('cascade'))
    .addColumn('direction', 'varchar(16)', (c) => c.notNull())
    .addColumn('payload', 'bytea', (c) => c.notNull())
    .addColumn('parse_status', 'varchar(16)', (c) => c.notNull().defaultTo('pending'))
    .addColumn('parse_error', 'text')
    .addColumn('received_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .addColumn('sent_at', 'timestamptz', (c) => c.notNull())
    .execute()

  await db.schema
    .createIndex('raw_messages_device_received_idx')
    .on('raw_messages')
    .columns(['device_id', 'received_at'])
    .execute()

  await db.schema
    .createTable('connection_events')
    .addColumn('id', 'uuid', (c) => c.primaryKey().defaultTo(sql`gen_random_uuid()`))
    .addColumn('message_id', 'uuid', (c) => c.notNull().unique())
    .addColumn('device_id', 'uuid', (c) => c.notNull().references('devices.id').onDelete('cascade'))
    .addColumn('status', 'varchar(16)', (c) => c.notNull())
    .addColumn('detail', 'text')
    .addColumn('occurred_at', 'timestamptz', (c) => c.notNull())
    .addColumn('created_at', 'timestamptz', (c) => c.notNull().defaultTo(sql`now()`))
    .execute()

  await db.schema
    .createTable('device_status')
    .addColumn('device_id', 'uuid', (c) =>
      c.primaryKey().references('devices.id').onDelete('cascade'),
    )
    .addColumn('status', 'varchar(16)', (c) => c.notNull())
    .addColumn('detail', 'text')
    .addColumn('last_message_at', 'timestamptz')
    .addColumn('updated_at', 'timestamptz', (c) => c.notNull())
    .execute()
}

export async function down(db: Kysely<unknown>): Promise<void> {
  await db.schema.dropTable('device_status').execute()
  await db.schema.dropTable('connection_events').execute()
  await db.schema.dropTable('raw_messages').execute()
  await db.schema.dropTable('devices').execute()
  await db.schema.dropTable('agents').execute()
}
```

- [ ] **Step 5: Viết `TenantMigratorService`**

`apps/core/src/tenant/tenant-migrator.service.ts`:
```ts
import { Injectable } from '@nestjs/common'
import {
  Kysely,
  Migrator,
  PostgresDialect,
  type Migration,
  type MigrationProvider,
} from 'kysely'
import pg from 'pg'
import type { AdminDatabase } from '../admin-db/schema.js'
import * as initial from './migrations/001-initial.js'
import type { TenantDatabase } from './tenant-schema.js'

const migrations: Record<string, Migration> = { '001-initial': initial }

class StaticProvider implements MigrationProvider {
  async getMigrations(): Promise<Record<string, Migration>> {
    return migrations
  }
}

@Injectable()
export class TenantMigratorService {
  private readonly pools = new Map<string, Kysely<TenantDatabase>>()

  constructor(
    private readonly admin: Kysely<AdminDatabase>,
    private readonly urlTemplate: string,
  ) {}

  /** URL kết nối tới DB nghiệp vụ của một tenant. */
  private async urlFor(tenantId: string): Promise<{ url: string; databaseName: string }> {
    const tenant = await this.admin
      .selectFrom('tenants')
      .select(['database_name', 'database_url'])
      .where('id', '=', tenantId)
      .executeTakeFirst()
    if (!tenant) throw new Error(`không tìm thấy tenant ${tenantId}`)
    return {
      url: tenant.database_url ?? this.urlTemplate.replace('{db}', tenant.database_name),
      databaseName: tenant.database_name,
    }
  }

  private async connect(tenantId: string): Promise<Kysely<TenantDatabase>> {
    const existing = this.pools.get(tenantId)
    if (existing) return existing
    const { url } = await this.urlFor(tenantId)
    const db = new Kysely<TenantDatabase>({
      dialect: new PostgresDialect({ pool: new pg.Pool({ connectionString: url, max: 4 }) }),
    })
    this.pools.set(tenantId, db)
    return db
  }

  /** Tạo database của tenant nếu chưa có, rồi chạy migration. */
  async provisionDatabase(tenantId: string): Promise<void> {
    const { url, databaseName } = await this.urlFor(tenantId)
    const rootUrl = new URL(url)
    rootUrl.pathname = '/postgres'

    const root = new pg.Client({ connectionString: rootUrl.toString() })
    await root.connect()
    try {
      const exists = await root.query('select 1 from pg_database where datname = $1', [databaseName])
      if (exists.rowCount === 0) {
        // Tên database lấy từ cột đã ràng buộc varchar(63) trong DB quản trị;
        // vẫn chặn thêm ở đây vì CREATE DATABASE không nhận tham số bind.
        if (!/^[a-z0-9_]{1,63}$/.test(databaseName)) {
          throw new Error(`tên database không hợp lệ: ${databaseName}`)
        }
        await root.query(`CREATE DATABASE ${databaseName}`)
      }
    } finally {
      await root.end()
    }

    await this.migrateTenant(tenantId)
  }

  async migrateTenant(tenantId: string): Promise<void> {
    const db = await this.connect(tenantId)
    const migrator = new Migrator({ db, provider: new StaticProvider() })
    const { error, results } = await migrator.migrateToLatest()
    if (error) {
      const failed = results?.find((r) => r.status === 'Error')?.migrationName ?? 'không rõ'
      throw new Error(`migration tenant ${tenantId} thất bại ở "${failed}": ${String(error)}`)
    }
    for (const result of results ?? []) {
      if (result.status !== 'Success') continue
      await this.admin
        .insertInto('tenant_migrations')
        .values({ tenant_id: tenantId, migration_name: result.migrationName })
        .onConflict((oc) => oc.columns(['tenant_id', 'migration_name']).doNothing())
        .execute()
    }
  }

  /**
   * Chạy migration cho mọi tenant đang hoạt động. Một tenant hỏng không được
   * chặn các tenant còn lại, nên lỗi được gom lại chứ không ném ra ngay.
   */
  async migrateAll(): Promise<{ ok: string[]; failed: Array<{ tenantId: string; error: string }> }> {
    const tenants = await this.admin
      .selectFrom('tenants')
      .select('id')
      .where('status', '=', 'active')
      .execute()

    const ok: string[] = []
    const failed: Array<{ tenantId: string; error: string }> = []
    for (const tenant of tenants) {
      try {
        await this.migrateTenant(tenant.id)
        ok.push(tenant.id)
      } catch (error) {
        failed.push({ tenantId: tenant.id, error: error instanceof Error ? error.message : String(error) })
      }
    }
    return { ok, failed }
  }

  async closeAll(): Promise<void> {
    await Promise.all([...this.pools.values()].map((db) => db.destroy()))
    this.pools.clear()
  }
}
```

- [ ] **Step 6: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test test/tenant-migrator.test.ts`
Expected: 4 test PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): tenant business schema and per-tenant migration runner"
```

---

## Task 5: Core — TenantRegistry và cô lập tenant

Đây là task quan trọng nhất về mặt an toàn trong toàn bộ giai đoạn 1. Nó dựng **cửa duy nhất** để truy cập DB nghiệp vụ, và bộ test khẳng định dữ liệu hai tenant không bao giờ chạm nhau.

**Files:**
- Create: `apps/core/src/tenant/tenant-registry.service.ts`, `apps/core/src/tenant/tenant-context.ts`, `apps/core/src/tenant/agent-key.service.ts`, `apps/core/src/tenant/tenant.module.ts`
- Test: `apps/core/test/tenant-isolation.test.ts`

**Interfaces:**
- Consumes: Task 3 (`AdminDbService`), Task 4 (`TenantDatabase`, `TenantMigratorService`)
- Produces:
  - `TenantRegistryService.forTenant(tenantId: string): Promise<Kysely<TenantDatabase>>`
  - `TenantRegistryService.evictIdle(maxIdleMs: number): Promise<number>` — đóng pool nhàn rỗi, trả về số pool đã đóng
  - `TenantContext.run<T>(tenantId: string, fn: () => Promise<T>): Promise<T>`
  - `TenantContext.currentTenantId(): string` — ném lỗi nếu gọi ngoài phạm vi `run`
  - `AgentKeyService.issue(tenantId: string, label: string): Promise<{ keyId: string; plainKey: string }>`
  - `AgentKeyService.verify(plainKey: string): Promise<{ tenantId: string; keyId: string } | null>`

**Ghi chú thiết kế:** `currentTenantId()` **ném lỗi** thay vì trả `null` khi không có ngữ cảnh. Nếu trả `null` thì một chỗ quên gọi `run` sẽ lặng lẽ đọc nhầm database; ném lỗi biến sai sót đó thành lỗi ồn ào ngay lần chạy đầu tiên.

- [ ] **Step 1: Viết test cô lập tenant**

`apps/core/test/tenant-isolation.test.ts`:
```ts
import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { runAdminMigrations } from '../src/admin-db/migrator.js'
import type { AdminDatabase } from '../src/admin-db/schema.js'
import { AgentKeyService } from '../src/tenant/agent-key.service.js'
import { TenantContext } from '../src/tenant/tenant-context.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'

const ROOT = 'postgres://lis:lis@localhost:55432/postgres'
const ADMIN_DB = 'lis_admin_iso_test'
const TEMPLATE = 'postgres://lis:lis@localhost:55432/{db}'

let admin: Kysely<AdminDatabase>
let migrator: TenantMigratorService
let registry: TenantRegistryService
let keys: AgentKeyService
let tenantA: string
let tenantB: string

async function resetDatabase(name: string): Promise<void> {
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.query(`CREATE DATABASE ${name}`)
  await root.end()
}

async function dropIfExists(name: string): Promise<void> {
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.end()
}

async function seedAgentAndDevice(tenantId: string, deviceKey: string): Promise<string> {
  const db = await registry.forTenant(tenantId)
  const agent = await db
    .insertInto('agents')
    .values({
      agent_key_id: crypto.randomUUID(),
      hostname: `host-${deviceKey}`,
      agent_version: '0.1.0',
    })
    .returningAll()
    .executeTakeFirstOrThrow()
  const device = await db
    .insertInto('devices')
    .values({
      agent_id: agent.id,
      device_key: deviceKey,
      name: `Máy ${deviceKey}`,
      transport: 'tcp',
      driver: 'astm-e1394',
    })
    .returningAll()
    .executeTakeFirstOrThrow()
  return device.id
}

beforeAll(async () => {
  await resetDatabase(ADMIN_DB)
  await dropIfExists('lis_t_iso_a')
  await dropIfExists('lis_t_iso_b')

  admin = new Kysely<AdminDatabase>({
    dialect: new PostgresDialect({
      pool: new pg.Pool({ connectionString: `postgres://lis:lis@localhost:55432/${ADMIN_DB}` }),
    }),
  })
  await runAdminMigrations(admin)

  const a = await admin
    .insertInto('tenants')
    .values({ code: 'iso-a', name: 'Bệnh viện A', database_name: 'lis_t_iso_a', status: 'active' })
    .returningAll()
    .executeTakeFirstOrThrow()
  const b = await admin
    .insertInto('tenants')
    .values({ code: 'iso-b', name: 'Bệnh viện B', database_name: 'lis_t_iso_b', status: 'active' })
    .returningAll()
    .executeTakeFirstOrThrow()
  tenantA = a.id
  tenantB = b.id

  migrator = new TenantMigratorService(admin, TEMPLATE)
  await migrator.provisionDatabase(tenantA)
  await migrator.provisionDatabase(tenantB)

  registry = new TenantRegistryService(admin, TEMPLATE)
  keys = new AgentKeyService(admin)
}, 90_000)

afterAll(async () => {
  await registry.closeAll()
  await migrator.closeAll()
  await admin.destroy()
})

describe('cô lập giữa các tenant', () => {
  it('dữ liệu ghi cho tenant A không xuất hiện ở tenant B', async () => {
    await seedAgentAndDevice(tenantA, 'may-a')

    const dbB = await registry.forTenant(tenantB)
    const devicesB = await dbB.selectFrom('devices').selectAll().execute()
    expect(devicesB).toEqual([])
  })

  it('và ngược lại', async () => {
    await seedAgentAndDevice(tenantB, 'may-b')

    const dbA = await registry.forTenant(tenantA)
    const namesA = (await dbA.selectFrom('devices').select('device_key').execute()).map(
      (r) => r.device_key,
    )
    expect(namesA).toEqual(['may-a'])
  })

  it('forTenant trả về cùng một pool khi gọi lại', async () => {
    const first = await registry.forTenant(tenantA)
    const second = await registry.forTenant(tenantA)
    expect(first).toBe(second)
  })

  it('evictIdle đóng pool không dùng tới', async () => {
    await registry.forTenant(tenantA)
    const closed = await registry.evictIdle(0)
    expect(closed).toBeGreaterThan(0)

    // Sau khi đóng, gọi lại vẫn phải hoạt động.
    const db = await registry.forTenant(tenantA)
    const rows = await db.selectFrom('devices').selectAll().execute()
    expect(rows.length).toBe(1)
  })

  it('forTenant từ chối tenant không tồn tại', async () => {
    await expect(registry.forTenant(crypto.randomUUID())).rejects.toThrow(/không tìm thấy tenant/)
  })

  it('forTenant từ chối tenant đã bị tạm ngưng', async () => {
    const suspended = await admin
      .insertInto('tenants')
      .values({
        code: 'iso-c',
        name: 'Bệnh viện C',
        database_name: 'lis_t_iso_c',
        status: 'suspended',
      })
      .returningAll()
      .executeTakeFirstOrThrow()

    await expect(registry.forTenant(suspended.id)).rejects.toThrow(/tạm ngưng/)
  })
})

describe('TenantContext', () => {
  it('trả về tenant hiện tại bên trong run', async () => {
    const seen = await TenantContext.run(tenantA, async () => TenantContext.currentTenantId())
    expect(seen).toBe(tenantA)
  })

  it('ném lỗi khi gọi ngoài phạm vi run', () => {
    expect(() => TenantContext.currentTenantId()).toThrow(/không có ngữ cảnh tenant/)
  })

  it('giữ đúng tenant khi hai luồng chạy xen kẽ', async () => {
    const [x, y] = await Promise.all([
      TenantContext.run(tenantA, async () => {
        await new Promise((r) => setTimeout(r, 20))
        return TenantContext.currentTenantId()
      }),
      TenantContext.run(tenantB, async () => TenantContext.currentTenantId()),
    ])
    expect(x).toBe(tenantA)
    expect(y).toBe(tenantB)
  })
})

describe('AgentKeyService', () => {
  it('cấp khoá và xác thực được đúng tenant', async () => {
    const issued = await keys.issue(tenantA, 'PC sinh hóa')
    const verified = await keys.verify(issued.plainKey)
    expect(verified?.tenantId).toBe(tenantA)
  })

  it('không lưu khoá gốc trong database', async () => {
    const issued = await keys.issue(tenantB, 'PC huyết học')
    const row = await admin
      .selectFrom('agent_keys')
      .select('key_hash')
      .where('id', '=', issued.keyId)
      .executeTakeFirstOrThrow()
    expect(row.key_hash).not.toContain(issued.plainKey)
  })

  it('từ chối khoá sai', async () => {
    expect(await keys.verify('lis_agent_khong_ton_tai')).toBeNull()
  })

  it('từ chối khoá đã thu hồi', async () => {
    const issued = await keys.issue(tenantA, 'PC sắp thu hồi')
    await admin
      .updateTable('agent_keys')
      .set({ status: 'revoked' })
      .where('id', '=', issued.keyId)
      .execute()
    expect(await keys.verify(issued.plainKey)).toBeNull()
  })
})
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test test/tenant-isolation.test.ts`
Expected: FAIL — không tìm thấy `tenant-registry.service.js`.

- [ ] **Step 3: Viết `TenantContext`**

`apps/core/src/tenant/tenant-context.ts`:
```ts
import { AsyncLocalStorage } from 'node:async_hooks'

const storage = new AsyncLocalStorage<{ tenantId: string }>()

/**
 * Ngữ cảnh tenant của request hoặc kết nối hiện tại.
 *
 * Mọi truy cập DB nghiệp vụ phải nằm trong một `run`. `currentTenantId` ném lỗi
 * thay vì trả null khi thiếu ngữ cảnh: nếu trả null thì một chỗ quên gọi `run`
 * sẽ lặng lẽ đọc nhầm database của bệnh viện khác.
 */
export const TenantContext = {
  run<T>(tenantId: string, fn: () => Promise<T>): Promise<T> {
    return storage.run({ tenantId }, fn)
  },

  currentTenantId(): string {
    const store = storage.getStore()
    if (!store) {
      throw new Error('không có ngữ cảnh tenant: truy cập DB nghiệp vụ ngoài TenantContext.run')
    }
    return store.tenantId
  },
}
```

- [ ] **Step 4: Viết `TenantRegistryService`**

`apps/core/src/tenant/tenant-registry.service.ts`:
```ts
import { Injectable, type OnModuleDestroy } from '@nestjs/common'
import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import type { AdminDatabase } from '../admin-db/schema.js'
import type { TenantDatabase } from './tenant-schema.js'

interface Entry {
  db: Kysely<TenantDatabase>
  lastUsedAt: number
}

/**
 * Cửa duy nhất để lấy connection tới DB nghiệp vụ của một tenant.
 *
 * Pool được tạo lười và đóng lại khi nhàn rỗi: với hàng chục tenant, mỗi tenant
 * một pool đầy đủ sẽ ăn hết số kết nối Postgres cho phép.
 */
@Injectable()
export class TenantRegistryService implements OnModuleDestroy {
  private readonly entries = new Map<string, Entry>()

  constructor(
    private readonly admin: Kysely<AdminDatabase>,
    private readonly urlTemplate: string,
  ) {}

  async forTenant(tenantId: string): Promise<Kysely<TenantDatabase>> {
    const cached = this.entries.get(tenantId)
    if (cached) {
      cached.lastUsedAt = Date.now()
      return cached.db
    }

    const tenant = await this.admin
      .selectFrom('tenants')
      .select(['database_name', 'database_url', 'status'])
      .where('id', '=', tenantId)
      .executeTakeFirst()

    if (!tenant) throw new Error(`không tìm thấy tenant ${tenantId}`)
    if (tenant.status !== 'active') throw new Error(`tenant ${tenantId} đang tạm ngưng`)

    const url = tenant.database_url ?? this.urlTemplate.replace('{db}', tenant.database_name)
    const db = new Kysely<TenantDatabase>({
      dialect: new PostgresDialect({
        pool: new pg.Pool({ connectionString: url, max: 4, idleTimeoutMillis: 10_000 }),
      }),
    })
    this.entries.set(tenantId, { db, lastUsedAt: Date.now() })
    return db
  }

  /** Đóng các pool không được dùng trong `maxIdleMs` mili giây. */
  async evictIdle(maxIdleMs: number): Promise<number> {
    const cutoff = Date.now() - maxIdleMs
    const stale = [...this.entries.entries()].filter(([, entry]) => entry.lastUsedAt <= cutoff)
    for (const [tenantId, entry] of stale) {
      this.entries.delete(tenantId)
      await entry.db.destroy()
    }
    return stale.length
  }

  async closeAll(): Promise<void> {
    const all = [...this.entries.values()]
    this.entries.clear()
    await Promise.all(all.map((entry) => entry.db.destroy()))
  }

  async onModuleDestroy(): Promise<void> {
    await this.closeAll()
  }
}
```

- [ ] **Step 5: Viết `AgentKeyService`**

`apps/core/src/tenant/agent-key.service.ts`:
```ts
import { randomBytes, createHash, timingSafeEqual } from 'node:crypto'
import { Injectable } from '@nestjs/common'
import type { Kysely } from 'kysely'
import type { AdminDatabase } from '../admin-db/schema.js'

const KEY_PREFIX = 'lis_agent_'

function hashKey(plainKey: string): string {
  return `sha256:${createHash('sha256').update(plainKey).digest('hex')}`
}

@Injectable()
export class AgentKeyService {
  constructor(private readonly admin: Kysely<AdminDatabase>) {}

  /** Cấp khoá mới. Khoá gốc chỉ trả về đúng một lần này; database chỉ giữ hash. */
  async issue(tenantId: string, label: string): Promise<{ keyId: string; plainKey: string }> {
    const plainKey = KEY_PREFIX + randomBytes(24).toString('base64url')
    const row = await this.admin
      .insertInto('agent_keys')
      .values({
        tenant_id: tenantId,
        key_hash: hashKey(plainKey),
        agent_label: label,
        status: 'active',
      })
      .returning('id')
      .executeTakeFirstOrThrow()
    return { keyId: row.id, plainKey }
  }

  async verify(plainKey: string): Promise<{ tenantId: string; keyId: string } | null> {
    if (!plainKey.startsWith(KEY_PREFIX)) return null
    const expected = hashKey(plainKey)
    const row = await this.admin
      .selectFrom('agent_keys')
      .select(['id', 'tenant_id', 'key_hash', 'status'])
      .where('key_hash', '=', expected)
      .executeTakeFirst()

    if (!row || row.status !== 'active') return null

    // So sánh thời gian hằng số để không rò rỉ thông tin qua thời gian phản hồi.
    const a = Buffer.from(row.key_hash)
    const b = Buffer.from(expected)
    if (a.length !== b.length || !timingSafeEqual(a, b)) return null

    await this.admin
      .updateTable('agent_keys')
      .set({ last_seen_at: new Date() })
      .where('id', '=', row.id)
      .execute()

    return { tenantId: row.tenant_id, keyId: row.id }
  }
}
```

- [ ] **Step 6: Viết `TenantModule` nối các provider vào Nest**

`apps/core/src/tenant/tenant.module.ts`:
```ts
import { Global, Module } from '@nestjs/common'
import { AdminDbService } from '../admin-db/admin-db.service.js'
import { loadConfig } from '../config.js'
import { AgentKeyService } from './agent-key.service.js'
import { TenantMigratorService } from './tenant-migrator.service.js'
import { TenantRegistryService } from './tenant-registry.service.js'

@Global()
@Module({
  providers: [
    {
      provide: TenantRegistryService,
      inject: [AdminDbService],
      useFactory: (admin: AdminDbService) =>
        new TenantRegistryService(admin.db, loadConfig().tenantDatabaseUrlTemplate),
    },
    {
      provide: TenantMigratorService,
      inject: [AdminDbService],
      useFactory: (admin: AdminDbService) =>
        new TenantMigratorService(admin.db, loadConfig().tenantDatabaseUrlTemplate),
    },
    {
      provide: AgentKeyService,
      inject: [AdminDbService],
      useFactory: (admin: AdminDbService) => new AgentKeyService(admin.db),
    },
  ],
  exports: [TenantRegistryService, TenantMigratorService, AgentKeyService],
})
export class TenantModule {}
```

Cập nhật `apps/core/src/app.module.ts`:
```ts
import { Module } from '@nestjs/common'
import { AdminDbModule } from './admin-db/admin-db.module.js'
import { TenantModule } from './tenant/tenant.module.js'

@Module({ imports: [AdminDbModule, TenantModule] })
export class AppModule {}
```

- [ ] **Step 7: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test`
Expected: toàn bộ test PASS, gồm 6 test cô lập tenant, 3 test TenantContext, 4 test AgentKeyService.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(core): tenant registry, tenant context and agent key authentication"
```

---

## Task 6: Core — cổng WebSocket cho Agent

**Files:**
- Create: `apps/core/src/agent-gateway/agent.gateway.ts`, `apps/core/src/agent-gateway/agent-session.ts`, `apps/core/src/agent-gateway/agent-gateway.module.ts`
- Modify: `apps/core/src/main.ts`, `apps/core/src/app.module.ts`
- Test: `apps/core/test/agent-gateway.test.ts`

**Interfaces:**
- Consumes: Task 2 (`parseAgentToCore`, `AgentToCore`, `CoreToAgent`, `PROTOCOL_VERSION`), Task 5 (`AgentKeyService`, `TenantRegistryService`, `TenantContext`), Task 7 sẽ cắm vào qua `IngestService`
- Produces:
  - `AgentGateway` — server WebSocket ở `/agent`, xác thực bằng query `?key=<plainKey>`
  - `AgentSession` — `{ tenantId, agentKeyId, agentId, socket }`
  - `AgentGateway.connectedAgents(): AgentSession[]`

**Giao thức bắt tay:** Agent mở `wss://core/agent?key=lis_agent_xxx`. Core xác thực khoá; sai thì đóng với mã `4401`. Đúng thì chờ message `hello`; nếu `protocolVersion` khác thì đóng với mã `4400`. Nhận `hello` xong Core upsert bản ghi `agents` trong DB của tenant rồi trả `ack`.

**Vì sao đặt khoá ở query string chứ không ở header:** `WebSocket` của trình duyệt không cho đặt header tuỳ ý, và tab Terminal thô trong Agent Console sẽ dùng lại chính lớp client này. Toàn bộ đường truyền là `wss` nên khoá không đi trên mạng dạng rõ; đổi lại phải chắc chắn **không log query string** ở reverse proxy — ghi việc này vào tài liệu vận hành.

- [ ] **Step 1: Viết test**

`apps/core/test/agent-gateway.test.ts`:
```ts
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import { PROTOCOL_VERSION, newMessageId } from '@lis/shared'
import { WsAdapter } from '@nestjs/platform-ws'
import WebSocket from 'ws'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AppModule } from '../src/app.module.js'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
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
  app.useWebSocketAdapter(new WsAdapter(app))
  await app.init()
  await app.listen(0)
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
```

`apps/core/test/helpers.ts`:
```ts
import type { Kysely } from 'kysely'
import pg from 'pg'
import type { AdminDatabase } from '../src/admin-db/schema.js'
import type { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'

const ROOT = 'postgres://lis:lis@localhost:55432/postgres'

/** Xoá và tạo lại database quản trị dùng cho test. Đọc tên từ ADMIN_DATABASE_URL. */
export async function resetAdminDatabase(): Promise<void> {
  const url = new URL(process.env.ADMIN_DATABASE_URL ?? '')
  const name = url.pathname.slice(1)
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.query(`CREATE DATABASE ${name}`)
  await root.end()
}

/** Tạo tenant, tạo database nghiệp vụ và chạy migration. Trả về tenantId. */
export async function prepareTestTenant(
  admin: Kysely<AdminDatabase>,
  migrator: TenantMigratorService,
  code: string,
  databaseName: string,
): Promise<string> {
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${databaseName} WITH (FORCE)`)
  await root.end()

  const tenant = await admin
    .insertInto('tenants')
    .values({ code, name: code, database_name: databaseName, status: 'active' })
    .returningAll()
    .executeTakeFirstOrThrow()
  await migrator.provisionDatabase(tenant.id)
  return tenant.id
}
```

Tạo `apps/core/.env.test`:
```
CORE_PORT=0
ADMIN_DATABASE_URL=postgres://lis:lis@localhost:55432/lis_admin_test_e2e
TENANT_DATABASE_URL_TEMPLATE=postgres://lis:lis@localhost:55432/{db}
```

Cập nhật `apps/core/vitest.config.ts` để nạp file này:
```ts
import { config } from 'dotenv'
import { defineConfig } from 'vitest/config'

config({ path: new URL('.env.test', import.meta.url).pathname })

export default defineConfig({
  test: {
    include: ['src/**/*.test.ts', 'test/**/*.test.ts'],
    hookTimeout: 120_000,
    testTimeout: 60_000,
    fileParallelism: false,
  },
})
```

Run: `pnpm --filter @lis/core add -D dotenv@16.4.7 @nestjs/testing@11`

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test test/agent-gateway.test.ts`
Expected: FAIL — chưa có gateway nên kết nối bị từ chối với mã khác 4401.

- [ ] **Step 3: Viết `agent-session.ts`**

`apps/core/src/agent-gateway/agent-session.ts`:
```ts
import type { WebSocket } from 'ws'

export interface AgentSession {
  tenantId: string
  agentKeyId: string
  /** agents.id trong DB nghiệp vụ; chỉ có sau khi nhận hello. */
  agentId: string | null
  hostname: string | null
  socket: WebSocket
}
```

- [ ] **Step 4: Viết `agent.gateway.ts`**

`apps/core/src/agent-gateway/agent.gateway.ts`:
```ts
import { Injectable, Logger, type OnModuleDestroy } from '@nestjs/common'
import {
  PROTOCOL_VERSION,
  ProtocolError,
  parseAgentToCore,
  type AgentToCore,
  type CoreToAgent,
} from '@lis/shared'
import type { IncomingMessage, Server as HttpServer } from 'node:http'
import { WebSocketServer, type WebSocket } from 'ws'
import { AgentKeyService } from '../tenant/agent-key.service.js'
import { TenantContext } from '../tenant/tenant-context.js'
import { TenantRegistryService } from '../tenant/tenant-registry.service.js'
import { IngestService } from '../ingest/ingest.service.js'
import type { AgentSession } from './agent-session.js'

const CLOSE_BAD_PROTOCOL = 4400
const CLOSE_UNAUTHORIZED = 4401

@Injectable()
export class AgentGateway implements OnModuleDestroy {
  private readonly logger = new Logger(AgentGateway.name)
  private readonly sessions = new Map<WebSocket, AgentSession>()
  private wss: WebSocketServer | null = null

  constructor(
    private readonly keys: AgentKeyService,
    private readonly registry: TenantRegistryService,
    private readonly ingest: IngestService,
  ) {}

  /** Gắn server WebSocket vào HTTP server của Nest ở đường dẫn /agent. */
  attach(server: HttpServer): void {
    this.wss = new WebSocketServer({ server, path: '/agent' })
    this.wss.on('connection', (socket, request) => {
      void this.onConnection(socket, request)
    })
  }

  connectedAgents(): AgentSession[] {
    return [...this.sessions.values()]
  }

  private send(socket: WebSocket, message: CoreToAgent): void {
    socket.send(JSON.stringify(message))
  }

  private async onConnection(socket: WebSocket, request: IncomingMessage): Promise<void> {
    const url = new URL(request.url ?? '/', 'http://localhost')
    const key = url.searchParams.get('key')

    const verified = key ? await this.keys.verify(key) : null
    if (!verified) {
      socket.close(CLOSE_UNAUTHORIZED, 'khoá agent không hợp lệ')
      return
    }

    const session: AgentSession = {
      tenantId: verified.tenantId,
      agentKeyId: verified.keyId,
      agentId: null,
      hostname: null,
      socket,
    }
    this.sessions.set(socket, session)

    socket.on('message', (data) => {
      void this.onMessage(session, data.toString())
    })
    socket.on('close', () => {
      this.sessions.delete(socket)
    })
  }

  private async onMessage(session: AgentSession, text: string): Promise<void> {
    let message: AgentToCore
    try {
      message = parseAgentToCore(JSON.parse(text) as unknown)
    } catch (error) {
      // Message hỏng không làm đứt kết nối: Agent còn hàng đợi phía sau cần
      // tiếp tục đẩy lên. Chỉ báo lỗi cho riêng message này.
      const reason = error instanceof ProtocolError ? error.message : 'message không đọc được'
      this.send(session.socket, { type: 'nack', messageId: 'unknown', reason, retryable: false })
      return
    }

    if (message.type === 'hello') {
      if (message.protocolVersion !== PROTOCOL_VERSION) {
        session.socket.close(
          CLOSE_BAD_PROTOCOL,
          `phiên bản giao thức không khớp: Core dùng ${PROTOCOL_VERSION}`,
        )
        return
      }
      await TenantContext.run(session.tenantId, async () => {
        const db = await this.registry.forTenant(session.tenantId)
        const agent = await db
          .insertInto('agents')
          .values({
            agent_key_id: session.agentKeyId,
            hostname: message.hostname,
            agent_version: message.agentVersion,
            last_seen_at: new Date(),
          })
          .onConflict((oc) =>
            oc.column('agent_key_id').doUpdateSet({
              hostname: message.hostname,
              agent_version: message.agentVersion,
              last_seen_at: new Date(),
            }),
          )
          .returning('id')
          .executeTakeFirstOrThrow()
        session.agentId = agent.id
        session.hostname = message.hostname
      })
      this.send(session.socket, { type: 'ack', messageId: message.messageId })
      return
    }

    if (!session.agentId) {
      this.send(session.socket, {
        type: 'nack',
        messageId: message.messageId,
        reason: 'phải gửi hello trước',
        retryable: true,
      })
      return
    }

    try {
      await TenantContext.run(session.tenantId, () =>
        this.ingest.handle(session.tenantId, session.agentId!, message),
      )
      this.send(session.socket, { type: 'ack', messageId: message.messageId })
    } catch (error) {
      this.logger.error(`xử lý message thất bại: ${String(error)}`)
      this.send(session.socket, {
        type: 'nack',
        messageId: message.messageId,
        reason: error instanceof Error ? error.message : 'lỗi không xác định',
        retryable: true,
      })
    }
  }

  async onModuleDestroy(): Promise<void> {
    for (const session of this.sessions.values()) session.socket.terminate()
    this.sessions.clear()
    await new Promise<void>((resolve) => {
      if (!this.wss) return resolve()
      this.wss.close(() => resolve())
    })
  }
}
```

- [ ] **Step 5: Nối gateway vào ứng dụng**

`apps/core/src/agent-gateway/agent-gateway.module.ts`:
```ts
import { Module } from '@nestjs/common'
import { IngestModule } from '../ingest/ingest.module.js'
import { AgentGateway } from './agent.gateway.js'

@Module({ imports: [IngestModule], providers: [AgentGateway], exports: [AgentGateway] })
export class AgentGatewayModule {}
```

`apps/core/src/main.ts`:
```ts
import 'reflect-metadata'
import { NestFactory } from '@nestjs/core'
import { AgentGateway } from './agent-gateway/agent.gateway.js'
import { AppModule } from './app.module.js'
import { loadConfig } from './config.js'

async function bootstrap(): Promise<void> {
  const config = loadConfig()
  const app = await NestFactory.create(AppModule)
  await app.listen(config.port)
  app.get(AgentGateway).attach(app.getHttpServer())
  console.log(`LIS Connect Core đang chạy ở cổng ${config.port}`)
}

void bootstrap()
```

Trong test, `app.listen(0)` chạy trước khi lấy `AgentGateway`; thêm dòng `app.get(AgentGateway).attach(app.getHttpServer())` ngay sau `app.listen(0)` trong `beforeAll`.

Cập nhật `apps/core/src/app.module.ts` để thêm `AgentGatewayModule` vào `imports`.

- [ ] **Step 6: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test test/agent-gateway.test.ts`
Expected: 6 test PASS. (Task 7 cung cấp `IngestService`; nếu thực hiện task này trước, tạm thời tạo `IngestService` với `handle()` rỗng rồi hoàn thiện ở Task 7.)

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): websocket gateway authenticating agents by key"
```
