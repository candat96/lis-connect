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
- **Đúng một Postgres host cho toàn hệ thống.** Bên trong nó: một database quản trị
  `lis_admin` và mỗi khách một database con `lis_t_<mã khách>`. Không có tenant nào
  trỏ sang host khác.
- Tên bảng và cột trong Postgres viết `snake_case`. Kiểu TypeScript viết `PascalCase`, biến `camelCase`.
- Ngôn ngữ giao diện: tiếng Việt có dấu đầy đủ. Mã nguồn, tên hàm, tên biến, thông điệp commit: tiếng Anh.
- Mọi thời điểm lưu dạng `timestamptz`, sinh bằng `now()` của Postgres hoặc `new Date()` UTC. Không lưu giờ địa phương.
- Commit sau mỗi task. Thông điệp theo Conventional Commits.
- **Trong `apps/core`, mọi tham số constructor được tiêm phải khai `@Inject(...)` tường minh.**
  `esbuild` — thứ mà cả `vitest` lẫn `tsx` dùng — không phát metadata `design:paramtypes`, nên
  cơ chế suy luận kiểu ngầm của NestJS lặng lẽ tiêm `undefined` thay vì báo lỗi lúc khởi động.
  Tệp test nào không đi qua `main.ts` cũng phải tự `import 'reflect-metadata'` ở dòng đầu.
- Monorepo dùng **TypeScript project references**: gốc repo có `tsconfig.json` với mảng
  `references`, và mọi package đặt `composite: true` (kế thừa từ `tsconfig.base.json`).
  **Mỗi task tạo package mới phải thêm một mục vào `references` của `tsconfig.json` gốc**,
  nếu không `pnpm typecheck` sẽ bỏ qua package đó.
- Package nào để test ở thư mục `test/` cạnh `src/` thì `tsconfig.json` của nó phải giữ
  `rootDir: "src"` và `include: ["src"]` — để `dist/` phẳng và **mã kiểm thử không lọt vào
  bản dựng production** — kèm một `tsconfig.test.json` riêng (`composite: false`,
  `noEmit: true`, `include: ["src", "test"]`) để test vẫn được kiểm kiểu. Lệnh
  `pnpm typecheck` chạy cả hai.
- **Mọi task có giao diện (Task 15, Task 16) BẮT BUỘC gọi skill `ui-ux-pro-max`
  trước khi viết dòng JSX đầu tiên**, và thiết kế theo hướng dẫn skill trả về. Các
  đoạn JSX và CSS trong kế hoạch này là bản dựng chức năng để chốt luồng dữ liệu và
  hợp đồng IPC/API — không phải bản thiết kế cuối cùng. Skill quyết định hệ thống
  màu, khoảng cách, kiểu chữ, trạng thái rỗng, trạng thái tải và cách thể hiện lỗi.

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

**Ghi chú thiết kế:** toàn hệ thống dùng **đúng một Postgres host**. Bên trong host đó có database quản trị `lis_admin` và mỗi khách một database con `lis_t_<mã>`. `tenantDatabaseUrlTemplate` chứa `{db}`, được thay bằng `tenants.database_name` — nên một biến môi trường duy nhất mô tả được toàn bộ. Không có cơ chế trỏ tenant sang host khác; nếu về sau cần, đó là thêm một cột, làm sau.

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
pnpm --filter @lis/core add @nestjs/common@11 @nestjs/core@11 @nestjs/platform-express@11 reflect-metadata@0.2.2 rxjs@7.8.1 kysely@0.27.5 pg@8.13.1 ws@8.18.0 @lis/shared@workspace:*
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
    expect(tenant.database_name).toBe('lis_t_bva')
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

  /** URL kết nối tới DB nghiệp vụ của một tenant, trên cùng một Postgres host. */
  private async urlFor(tenantId: string): Promise<{ url: string; databaseName: string }> {
    const tenant = await this.admin
      .selectFrom('tenants')
      .select(['database_name'])
      .where('id', '=', tenantId)
      .executeTakeFirst()
    if (!tenant) throw new Error(`không tìm thấy tenant ${tenantId}`)
    return {
      url: this.urlTemplate.replace('{db}', tenant.database_name),
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
      .select(['database_name', 'status'])
      .where('id', '=', tenantId)
      .executeTakeFirst()

    if (!tenant) throw new Error(`không tìm thấy tenant ${tenantId}`)
    if (tenant.status !== 'active') throw new Error(`tenant ${tenantId} đang tạm ngưng`)

    const url = this.urlTemplate.replace('{db}', tenant.database_name)
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

> **Thứ tự thực thi:** task này dùng `IngestService` do Task 7 tạo ra. Khi thực thi
> tuần tự, hãy **làm Task 7 trước Task 6**. Đánh số giữ nguyên vì Task 6 mới là thứ
> định nghĩa hợp đồng mạng mà Task 7 phục vụ.

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

Gateway tự gắn `WebSocketServer` vào HTTP server của Nest thay vì dùng `WsAdapter`.
Lý do: `WsAdapter` chiếm toàn bộ đường nâng cấp WebSocket của ứng dụng, trong khi
giai đoạn sau còn cần thêm kênh WebSocket riêng cho Web UI ở đường dẫn khác.

Cập nhật `apps/core/src/app.module.ts` để thêm `AgentGatewayModule` vào `imports`.

- [ ] **Step 6: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test test/agent-gateway.test.ts`
Expected: 6 test PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): websocket gateway authenticating agents by key"
```

---

## Task 7: Core — tiếp nhận message idempotent

**Files:**
- Create: `apps/core/src/ingest/ingest.service.ts`, `apps/core/src/ingest/ingest.module.ts`
- Test: `apps/core/test/ingest.test.ts`

**Interfaces:**
- Consumes: Task 2 (`AgentToCore`), Task 5 (`TenantRegistryService`)
- Produces:
  - `IngestService.handle(tenantId: string, agentId: string, message: AgentToCore): Promise<void>`
  - `IngestService.ensureDevice(tenantId: string, agentId: string, deviceKey: string): Promise<string>` — trả về `devices.id`, tạo mới nếu chưa có

**Quy tắc cốt lõi:** cùng một `messageId` gửi lên hai lần chỉ được sinh **đúng một** bản ghi. Thực hiện bằng ràng buộc `unique` trên `message_id` cộng `onConflict().doNothing()`, không dùng "kiểm tra rồi ghi" — vì hai message trùng đến đồng thời sẽ lọt qua kiểu kiểm tra đó.

**Về `ensureDevice`:** ở giai đoạn 1, Agent là nơi khai báo máy (qua Agent Console), Core chỉ ghi nhận. Nên khi thấy `deviceKey` lạ, Core tạo bản ghi `devices` với tên tạm bằng chính `deviceKey`. Giai đoạn sau, khi cấu hình máy được quản lý từ Web UI, hành vi này đổi thành từ chối máy chưa đăng ký.

- [ ] **Step 1: Viết test**

`apps/core/test/ingest.test.ts`:
```ts
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import { newMessageId } from '@lis/shared'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
import { AppModule } from '../src/app.module.js'
import { IngestService } from '../src/ingest/ingest.service.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'
import { prepareTestTenant, resetAdminDatabase } from './helpers.js'

let app: INestApplication
let ingest: IngestService
let registry: TenantRegistryService
let tenantId: string
let agentId: string

beforeAll(async () => {
  await resetAdminDatabase()
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()

  const admin = app.get(AdminDbService)
  const migrator = app.get(TenantMigratorService)
  registry = app.get(TenantRegistryService)
  ingest = app.get(IngestService)

  tenantId = await prepareTestTenant(admin.db, migrator, 'ing-a', 'lis_t_ing_a')

  const db = await registry.forTenant(tenantId)
  const agent = await db
    .insertInto('agents')
    .values({ agent_key_id: crypto.randomUUID(), hostname: 'PC-1', agent_version: '0.1.0' })
    .returningAll()
    .executeTakeFirstOrThrow()
  agentId = agent.id
}, 90_000)

afterAll(async () => {
  await app.close()
})

function rawMessage(messageId: string, payload: string) {
  return {
    type: 'raw_message' as const,
    messageId,
    sentAt: new Date().toISOString(),
    deviceId: 'may-sinh-hoa',
    direction: 'inbound' as const,
    payloadBase64: Buffer.from(payload, 'utf8').toString('base64'),
  }
}

describe('IngestService', () => {
  it('lưu raw message và tự tạo device khi thấy device_key lạ', async () => {
    await ingest.handle(tenantId, agentId, rawMessage(newMessageId(), 'H|\\^&|||Mindray'))

    const db = await registry.forTenant(tenantId)
    const devices = await db.selectFrom('devices').selectAll().execute()
    expect(devices.length).toBe(1)
    expect(devices[0]!.device_key).toBe('may-sinh-hoa')

    const raws = await db.selectFrom('raw_messages').selectAll().execute()
    expect(raws.length).toBe(1)
    expect(raws[0]!.payload.toString('utf8')).toBe('H|\\^&|||Mindray')
    expect(raws[0]!.parse_status).toBe('pending')
  })

  it('gửi lại cùng messageId không tạo bản ghi thứ hai', async () => {
    const id = newMessageId()
    await ingest.handle(tenantId, agentId, rawMessage(id, 'R|1|^^^Glu|5.4'))
    await ingest.handle(tenantId, agentId, rawMessage(id, 'R|1|^^^Glu|5.4'))

    const db = await registry.forTenant(tenantId)
    const rows = await db
      .selectFrom('raw_messages')
      .selectAll()
      .where('message_id', '=', id)
      .execute()
    expect(rows.length).toBe(1)
  })

  it('hai messageId khác nhau với nội dung giống hệt vẫn tạo hai bản ghi', async () => {
    const db = await registry.forTenant(tenantId)
    const before = await db.selectFrom('raw_messages').selectAll().execute()

    await ingest.handle(tenantId, agentId, rawMessage(newMessageId(), 'R|2|^^^Ure|6.1'))
    await ingest.handle(tenantId, agentId, rawMessage(newMessageId(), 'R|2|^^^Ure|6.1'))

    const after = await db.selectFrom('raw_messages').selectAll().execute()
    expect(after.length).toBe(before.length + 2)
  })

  it('lưu connection event và cập nhật device_status', async () => {
    await ingest.handle(tenantId, agentId, {
      type: 'connection_event',
      messageId: newMessageId(),
      sentAt: new Date().toISOString(),
      deviceId: 'may-sinh-hoa',
      status: 'disconnected',
      detail: 'cổng COM3 bị rút',
    })

    const db = await registry.forTenant(tenantId)
    const status = await db.selectFrom('device_status').selectAll().executeTakeFirstOrThrow()
    expect(status.status).toBe('disconnected')
    expect(status.detail).toBe('cổng COM3 bị rút')
  })

  it('connection event mới hơn ghi đè trạng thái cũ', async () => {
    await new Promise((r) => setTimeout(r, 5))
    await ingest.handle(tenantId, agentId, {
      type: 'connection_event',
      messageId: newMessageId(),
      sentAt: new Date().toISOString(),
      deviceId: 'may-sinh-hoa',
      status: 'connected',
    })

    const db = await registry.forTenant(tenantId)
    const status = await db.selectFrom('device_status').selectAll().executeTakeFirstOrThrow()
    expect(status.status).toBe('connected')
    expect(status.detail).toBeNull()
  })

  it('heartbeat cập nhật last_seen_at của agent', async () => {
    await ingest.handle(tenantId, agentId, {
      type: 'heartbeat',
      messageId: newMessageId(),
      sentAt: new Date().toISOString(),
      queueDepth: 3,
    })

    const db = await registry.forTenant(tenantId)
    const agent = await db
      .selectFrom('agents')
      .selectAll()
      .where('id', '=', agentId)
      .executeTakeFirstOrThrow()
    expect(agent.last_seen_at).not.toBeNull()
  })

  it('ghi cho tenant này không rò sang tenant khác', async () => {
    const admin = app.get(AdminDbService)
    const migrator = app.get(TenantMigratorService)
    const otherTenant = await prepareTestTenant(admin.db, migrator, 'ing-b', 'lis_t_ing_b')

    const otherDb = await registry.forTenant(otherTenant)
    const rows = await otherDb.selectFrom('raw_messages').selectAll().execute()
    expect(rows).toEqual([])
  })
})
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test test/ingest.test.ts`
Expected: FAIL — chưa có `IngestService`.

- [ ] **Step 3: Viết `IngestService`**

`apps/core/src/ingest/ingest.service.ts`:
```ts
import { Injectable } from '@nestjs/common'
import type { AgentToCore } from '@lis/shared'
import { TenantRegistryService } from '../tenant/tenant-registry.service.js'

@Injectable()
export class IngestService {
  private readonly deviceIdCache = new Map<string, string>()

  constructor(private readonly registry: TenantRegistryService) {}

  /**
   * Tìm devices.id theo device_key, tạo mới nếu chưa có.
   *
   * Giai đoạn 1: Agent là nơi khai báo máy, Core chỉ ghi nhận. Giai đoạn sau,
   * khi cấu hình máy được quản lý từ Web UI, hành vi này đổi thành từ chối máy
   * chưa đăng ký.
   */
  async ensureDevice(tenantId: string, agentId: string, deviceKey: string): Promise<string> {
    const cacheKey = `${tenantId}:${agentId}:${deviceKey}`
    const cached = this.deviceIdCache.get(cacheKey)
    if (cached) return cached

    const db = await this.registry.forTenant(tenantId)
    const row = await db
      .insertInto('devices')
      .values({
        agent_id: agentId,
        device_key: deviceKey,
        name: deviceKey,
        transport: 'tcp',
        driver: 'unknown',
      })
      .onConflict((oc) =>
        oc.columns(['agent_id', 'device_key']).doUpdateSet({ device_key: deviceKey }),
      )
      .returning('id')
      .executeTakeFirstOrThrow()

    this.deviceIdCache.set(cacheKey, row.id)
    return row.id
  }

  async handle(tenantId: string, agentId: string, message: AgentToCore): Promise<void> {
    const db = await this.registry.forTenant(tenantId)

    switch (message.type) {
      case 'hello':
        // Gateway đã xử lý hello trước khi gọi tới đây.
        return

      case 'heartbeat':
        await db
          .updateTable('agents')
          .set({ last_seen_at: new Date() })
          .where('id', '=', agentId)
          .execute()
        return

      case 'raw_message': {
        const deviceId = await this.ensureDevice(tenantId, agentId, message.deviceId)
        // Idempotent bằng ràng buộc unique trên message_id, không phải bằng
        // "kiểm tra rồi ghi" — hai bản sao đến đồng thời sẽ lọt qua kiểu đó.
        await db
          .insertInto('raw_messages')
          .values({
            message_id: message.messageId,
            device_id: deviceId,
            direction: message.direction,
            payload: Buffer.from(message.payloadBase64, 'base64'),
            sent_at: new Date(message.sentAt),
          })
          .onConflict((oc) => oc.column('message_id').doNothing())
          .execute()

        await db
          .updateTable('device_status')
          .set({ last_message_at: new Date(), updated_at: new Date() })
          .where('device_id', '=', deviceId)
          .execute()
        return
      }

      case 'connection_event': {
        const deviceId = await this.ensureDevice(tenantId, agentId, message.deviceId)
        const occurredAt = new Date(message.sentAt)

        await db
          .insertInto('connection_events')
          .values({
            message_id: message.messageId,
            device_id: deviceId,
            status: message.status,
            detail: message.detail ?? null,
            occurred_at: occurredAt,
          })
          .onConflict((oc) => oc.column('message_id').doNothing())
          .execute()

        await db
          .insertInto('device_status')
          .values({
            device_id: deviceId,
            status: message.status,
            detail: message.detail ?? null,
            updated_at: occurredAt,
          })
          .onConflict((oc) =>
            oc
              .column('device_id')
              .doUpdateSet({
                status: message.status,
                detail: message.detail ?? null,
                updated_at: occurredAt,
              })
              // Sự kiện tới muộn nhưng xảy ra sớm hơn không được ghi đè
              // trạng thái mới hơn.
              .where('device_status.updated_at', '<', occurredAt),
          )
          .execute()
        return
      }
    }
  }
}
```

`apps/core/src/ingest/ingest.module.ts`:
```ts
import { Module } from '@nestjs/common'
import { IngestService } from './ingest.service.js'

@Module({ providers: [IngestService], exports: [IngestService] })
export class IngestModule {}
```

Thêm `IngestModule` vào `imports` của `AppModule`.

- [ ] **Step 4: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test`
Expected: toàn bộ test PASS, gồm 7 test ingest.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): idempotent ingestion of raw messages and connection events"
```

---

## Task 8: Core — API đọc cho Web UI

**Files:**
- Create: `apps/core/src/devices/devices.controller.ts`, `apps/core/src/devices/devices.service.ts`, `apps/core/src/devices/devices.module.ts`
- Create: `apps/core/src/auth/dev-tenant.guard.ts`
- Test: `apps/core/test/devices-api.test.ts`

**Interfaces:**
- Consumes: Task 5 (`TenantRegistryService`, `TenantContext`), Task 7 (dữ liệu đã ghi)
- Produces:
  - `GET /api/devices` → `Array<{ id, deviceKey, name, transport, driver, status, lastMessageAt }>`
  - `GET /api/devices/:id/raw-messages?limit=100` → `Array<{ id, direction, receivedAt, parseStatus, payloadBase64, payloadText }>`
  - `GET /api/devices/:id/connection-events?limit=100` → `Array<{ id, status, detail, occurredAt }>`

**Về xác thực ở giai đoạn 1:** Web UI chưa có đăng nhập thật (đó là việc của giai đoạn 2). Tạm dùng `DevTenantGuard` đọc tenant từ header `X-Tenant-Id`, và **guard này từ chối chạy khi `NODE_ENV === 'production'`** — để không có đường nào một bản triển khai thật lại chấp nhận tenant do client tự khai.

- [ ] **Step 1: Viết test**

`apps/core/test/devices-api.test.ts`:
```ts
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import { newMessageId } from '@lis/shared'
import request from 'supertest'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AdminDbService } from '../src/admin-db/admin-db.service.js'
import { AppModule } from '../src/app.module.js'
import { IngestService } from '../src/ingest/ingest.service.js'
import { TenantMigratorService } from '../src/tenant/tenant-migrator.service.js'
import { TenantRegistryService } from '../src/tenant/tenant-registry.service.js'
import { prepareTestTenant, resetAdminDatabase } from './helpers.js'

let app: INestApplication
let tenantA: string
let tenantB: string
let deviceAId: string

beforeAll(async () => {
  await resetAdminDatabase()
  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()

  const admin = app.get(AdminDbService)
  const migrator = app.get(TenantMigratorService)
  const registry = app.get(TenantRegistryService)
  const ingest = app.get(IngestService)

  tenantA = await prepareTestTenant(admin.db, migrator, 'api-a', 'lis_t_api_a')
  tenantB = await prepareTestTenant(admin.db, migrator, 'api-b', 'lis_t_api_b')

  const db = await registry.forTenant(tenantA)
  const agent = await db
    .insertInto('agents')
    .values({ agent_key_id: crypto.randomUUID(), hostname: 'PC-A', agent_version: '0.1.0' })
    .returningAll()
    .executeTakeFirstOrThrow()

  await ingest.handle(tenantA, agent.id, {
    type: 'raw_message',
    messageId: newMessageId(),
    sentAt: new Date().toISOString(),
    deviceId: 'may-a',
    direction: 'inbound',
    payloadBase64: Buffer.from('R|1|^^^Glu|5.4', 'utf8').toString('base64'),
  })
  await ingest.handle(tenantA, agent.id, {
    type: 'connection_event',
    messageId: newMessageId(),
    sentAt: new Date().toISOString(),
    deviceId: 'may-a',
    status: 'connected',
  })

  deviceAId = (await db.selectFrom('devices').select('id').executeTakeFirstOrThrow()).id
}, 90_000)

afterAll(async () => {
  await app.close()
})

describe('GET /api/devices', () => {
  it('trả về máy của tenant trong header', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/devices')
      .set('X-Tenant-Id', tenantA)
      .expect(200)

    expect(res.body).toHaveLength(1)
    expect(res.body[0]).toMatchObject({ deviceKey: 'may-a', status: 'connected' })
  })

  it('không trả về máy của tenant khác', async () => {
    const res = await request(app.getHttpServer())
      .get('/api/devices')
      .set('X-Tenant-Id', tenantB)
      .expect(200)

    expect(res.body).toEqual([])
  })

  it('từ chối request thiếu header tenant', async () => {
    await request(app.getHttpServer()).get('/api/devices').expect(401)
  })
})

describe('GET /api/devices/:id/raw-messages', () => {
  it('trả về payload dạng base64 kèm bản đọc được', async () => {
    const res = await request(app.getHttpServer())
      .get(`/api/devices/${deviceAId}/raw-messages`)
      .set('X-Tenant-Id', tenantA)
      .expect(200)

    expect(res.body).toHaveLength(1)
    expect(res.body[0].payloadText).toBe('R|1|^^^Glu|5.4')
    expect(res.body[0].parseStatus).toBe('pending')
  })

  it('không cho tenant khác đọc message của máy này', async () => {
    await request(app.getHttpServer())
      .get(`/api/devices/${deviceAId}/raw-messages`)
      .set('X-Tenant-Id', tenantB)
      .expect(404)
  })
})

describe('GET /api/devices/:id/connection-events', () => {
  it('trả về lịch sử kết nối mới nhất trước', async () => {
    const res = await request(app.getHttpServer())
      .get(`/api/devices/${deviceAId}/connection-events`)
      .set('X-Tenant-Id', tenantA)
      .expect(200)

    expect(res.body[0]).toMatchObject({ status: 'connected' })
  })
})
```

Run: `pnpm --filter @lis/core add -D supertest@7.0.0 @types/supertest@6.0.2`

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test test/devices-api.test.ts`
Expected: FAIL — mọi route trả 404.

- [ ] **Step 3: Viết guard**

`apps/core/src/auth/dev-tenant.guard.ts`:
```ts
import { CanActivate, ExecutionContext, Injectable, UnauthorizedException } from '@nestjs/common'
import { TenantContext } from '../tenant/tenant-context.js'

/**
 * Xác thực tạm cho giai đoạn 1: lấy tenant từ header X-Tenant-Id.
 *
 * Guard này CHỦ Ý không chạy ở production. Giai đoạn 2 thay bằng JWT thật; cho tới
 * lúc đó, một bản triển khai thật không được phép chấp nhận tenant do client tự khai.
 */
@Injectable()
export class DevTenantGuard implements CanActivate {
  canActivate(context: ExecutionContext): boolean {
    if (process.env.NODE_ENV === 'production') {
      throw new Error('DevTenantGuard không được dùng ở production')
    }
    const request = context.switchToHttp().getRequest<{ headers: Record<string, string> }>()
    const tenantId = request.headers['x-tenant-id']
    if (!tenantId) throw new UnauthorizedException('thiếu header X-Tenant-Id')
    ;(request as unknown as { tenantId: string }).tenantId = tenantId
    return true
  }
}

/** Bọc handler trong ngữ cảnh tenant. Dùng ở controller. */
export function withTenant<T>(tenantId: string, fn: () => Promise<T>): Promise<T> {
  return TenantContext.run(tenantId, fn)
}
```

- [ ] **Step 4: Viết service và controller**

`apps/core/src/devices/devices.service.ts`:
```ts
import { Injectable, NotFoundException } from '@nestjs/common'
import { TenantRegistryService } from '../tenant/tenant-registry.service.js'

export interface DeviceSummary {
  id: string
  deviceKey: string
  name: string
  transport: string
  driver: string
  status: string
  lastMessageAt: string | null
}

export interface RawMessageView {
  id: string
  direction: string
  receivedAt: string
  parseStatus: string
  payloadBase64: string
  /** Bản đọc được của payload; ký tự điều khiển được thay bằng dấu chấm. */
  payloadText: string
}

export interface ConnectionEventView {
  id: string
  status: string
  detail: string | null
  occurredAt: string
}

const CONTROL_CHARS = new RegExp('[\u0000-\u001f\u007f]', 'g')

function toPrintableText(payload: Buffer): string {
  return payload.toString('utf8').replace(CONTROL_CHARS, '.')
}

@Injectable()
export class DevicesService {
  constructor(private readonly registry: TenantRegistryService) {}

  async list(tenantId: string): Promise<DeviceSummary[]> {
    const db = await this.registry.forTenant(tenantId)
    const rows = await db
      .selectFrom('devices')
      .leftJoin('device_status', 'device_status.device_id', 'devices.id')
      .select([
        'devices.id',
        'devices.device_key',
        'devices.name',
        'devices.transport',
        'devices.driver',
        'device_status.status',
        'device_status.last_message_at',
      ])
      .orderBy('devices.name')
      .execute()

    return rows.map((r) => ({
      id: r.id,
      deviceKey: r.device_key,
      name: r.name,
      transport: r.transport,
      driver: r.driver,
      status: r.status ?? 'disconnected',
      lastMessageAt: r.last_message_at?.toISOString() ?? null,
    }))
  }

  private async assertDeviceBelongsToTenant(tenantId: string, deviceId: string): Promise<void> {
    const db = await this.registry.forTenant(tenantId)
    const found = await db
      .selectFrom('devices')
      .select('id')
      .where('id', '=', deviceId)
      .executeTakeFirst()
    if (!found) throw new NotFoundException('không tìm thấy máy xét nghiệm')
  }

  async rawMessages(tenantId: string, deviceId: string, limit: number): Promise<RawMessageView[]> {
    await this.assertDeviceBelongsToTenant(tenantId, deviceId)
    const db = await this.registry.forTenant(tenantId)
    const rows = await db
      .selectFrom('raw_messages')
      .selectAll()
      .where('device_id', '=', deviceId)
      .orderBy('received_at', 'desc')
      .limit(limit)
      .execute()

    return rows.map((r) => ({
      id: r.id,
      direction: r.direction,
      receivedAt: r.received_at.toISOString(),
      parseStatus: r.parse_status,
      payloadBase64: r.payload.toString('base64'),
      payloadText: toPrintableText(r.payload),
    }))
  }

  async connectionEvents(
    tenantId: string,
    deviceId: string,
    limit: number,
  ): Promise<ConnectionEventView[]> {
    await this.assertDeviceBelongsToTenant(tenantId, deviceId)
    const db = await this.registry.forTenant(tenantId)
    const rows = await db
      .selectFrom('connection_events')
      .selectAll()
      .where('device_id', '=', deviceId)
      .orderBy('occurred_at', 'desc')
      .limit(limit)
      .execute()

    return rows.map((r) => ({
      id: r.id,
      status: r.status,
      detail: r.detail,
      occurredAt: r.occurred_at.toISOString(),
    }))
  }
}
```

`apps/core/src/devices/devices.controller.ts`:
```ts
import { Controller, Get, Param, ParseUUIDPipe, Query, Req, UseGuards } from '@nestjs/common'
import { DevTenantGuard, withTenant } from '../auth/dev-tenant.guard.js'
import { DevicesService } from './devices.service.js'

interface TenantRequest {
  tenantId: string
}

function parseLimit(raw: string | undefined): number {
  const value = Number(raw ?? 100)
  if (!Number.isFinite(value) || value < 1) return 100
  return Math.min(value, 500)
}

@Controller('api/devices')
@UseGuards(DevTenantGuard)
export class DevicesController {
  constructor(private readonly devices: DevicesService) {}

  @Get()
  list(@Req() req: TenantRequest) {
    return withTenant(req.tenantId, () => this.devices.list(req.tenantId))
  }

  @Get(':id/raw-messages')
  rawMessages(
    @Req() req: TenantRequest,
    @Param('id', ParseUUIDPipe) id: string,
    @Query('limit') limit?: string,
  ) {
    return withTenant(req.tenantId, () =>
      this.devices.rawMessages(req.tenantId, id, parseLimit(limit)),
    )
  }

  @Get(':id/connection-events')
  connectionEvents(
    @Req() req: TenantRequest,
    @Param('id', ParseUUIDPipe) id: string,
    @Query('limit') limit?: string,
  ) {
    return withTenant(req.tenantId, () =>
      this.devices.connectionEvents(req.tenantId, id, parseLimit(limit)),
    )
  }
}
```

`apps/core/src/devices/devices.module.ts`:
```ts
import { Module } from '@nestjs/common'
import { DevicesController } from './devices.controller.js'
import { DevicesService } from './devices.service.js'

@Module({ controllers: [DevicesController], providers: [DevicesService] })
export class DevicesModule {}
```

Thêm `DevicesModule` vào `imports` của `AppModule`. Bật CORS trong `main.ts` cho Web UI ở cổng 5173:

```ts
app.enableCors({ origin: ['http://localhost:5173'], allowedHeaders: ['Content-Type', 'X-Tenant-Id'] })
```

- [ ] **Step 5: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test test/devices-api.test.ts`
Expected: 6 test PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): read api for devices, raw messages and connection events"
```

---

## Task 9: Agent — khung Electron và kho cấu hình cục bộ

**Files:**
- Create: `apps/agent/package.json`, `apps/agent/tsconfig.json`, `apps/agent/electron.vite.config.ts`, `apps/agent/vitest.config.ts`
- Create: `apps/agent/src/main/index.ts`, `apps/agent/src/main/config-store.ts`, `apps/agent/src/preload/index.ts`
- Create: `apps/agent/src/renderer/index.html`, `apps/agent/src/renderer/main.tsx`, `apps/agent/src/renderer/App.tsx`
- Test: `apps/agent/src/main/config-store.test.ts`

**Interfaces:**
- Consumes: Task 2 (`@lis/shared`)
- Produces:
  - type `AgentConfig` = `{ coreUrl: string; agentKey: string; proxyUrl: string | null; hisPort: number; devices: DeviceConfig[] }`
  - type `DeviceConfig` = `{ deviceKey: string; name: string; driver: string; enabled: boolean; transport: SerialConfig | TcpConfig | FileConfig }`
  - `ConfigStore.load(): AgentConfig`, `ConfigStore.save(config: AgentConfig): void`
  - `ConfigStore.upsertDevice(device: DeviceConfig): void`, `ConfigStore.removeDevice(deviceKey: string): void`

**Vì sao cấu hình là file JSON chứ không phải SQLite:** người triển khai cần đọc và sửa được nó bằng Notepad khi hỗ trợ qua điện thoại, và cần chép được sang máy khác. SQLite chỉ dùng cho hàng đợi và log, nơi cần ghi liên tục.

- [ ] **Step 1: Tạo package và cài phụ thuộc**

`apps/agent/package.json`:
```json
{
  "name": "@lis/agent",
  "version": "0.1.0",
  "main": "out/main/index.js",
  "scripts": {
    "dev": "electron-vite dev",
    "build": "electron-vite build",
    "package": "electron-vite build && electron-builder --win",
    "test": "vitest run"
  }
}
```

Run:
```bash
pnpm --filter @lis/agent add serialport@12.0.0 better-sqlite3@11.7.0 ws@8.18.0 @lis/shared@workspace:*
pnpm --filter @lis/agent add -D electron@33.2.1 electron-vite@2.3.0 electron-builder@25.1.8 vite@6.0.3 @vitejs/plugin-react@4.3.4 react@19.0.0 react-dom@19.0.0 @types/react@19.0.2 @types/react-dom@19.0.2 @types/better-sqlite3@7.6.12 @types/ws@8.5.13 vitest@2.1.8
```

`apps/agent/electron.vite.config.ts`:
```ts
import react from '@vitejs/plugin-react'
import { defineConfig, externalizeDepsPlugin } from 'electron-vite'

export default defineConfig({
  main: { plugins: [externalizeDepsPlugin()] },
  preload: { plugins: [externalizeDepsPlugin()] },
  renderer: { plugins: [react()] },
})
```

`apps/agent/vitest.config.ts`:
```ts
import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: { include: ['src/**/*.test.ts'], environment: 'node' },
})
```

`apps/agent/tsconfig.json`:
```json
{
  "extends": "../../tsconfig.base.json",
  "compilerOptions": { "jsx": "react-jsx", "types": ["node"] },
  "include": ["src"]
}
```

- [ ] **Step 2: Viết test cho ConfigStore**

`apps/agent/src/main/config-store.test.ts`:
```ts
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { ConfigStore, type DeviceConfig } from './config-store.js'

let dir: string
let store: ConfigStore

const serialDevice: DeviceConfig = {
  deviceKey: 'may-sinh-hoa',
  name: 'Máy sinh hóa',
  driver: 'astm-e1394',
  enabled: true,
  transport: {
    kind: 'serial',
    path: 'COM3',
    baudRate: 9600,
    dataBits: 8,
    parity: 'none',
    stopBits: 1,
  },
}

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'lis-agent-'))
  store = new ConfigStore(join(dir, 'config.json'))
})

afterEach(() => {
  rmSync(dir, { recursive: true, force: true })
})

describe('ConfigStore', () => {
  it('trả về cấu hình mặc định khi chưa có file', () => {
    const config = store.load()
    expect(config.coreUrl).toBe('')
    expect(config.agentKey).toBe('')
    expect(config.hisPort).toBe(8080)
    expect(config.devices).toEqual([])
  })

  it('lưu rồi đọc lại giữ nguyên nội dung', () => {
    store.save({
      coreUrl: 'wss://core.example.vn/agent',
      agentKey: 'lis_agent_abc',
      proxyUrl: null,
      hisPort: 8080,
      devices: [serialDevice],
    })

    const reloaded = new ConfigStore(join(dir, 'config.json')).load()
    expect(reloaded.coreUrl).toBe('wss://core.example.vn/agent')
    expect(reloaded.devices[0]?.transport).toMatchObject({ kind: 'serial', path: 'COM3' })
  })

  it('ghi file dạng JSON đọc được bằng trình soạn thảo thường', () => {
    store.save({ coreUrl: 'wss://x', agentKey: 'k', proxyUrl: null, hisPort: 8080, devices: [] })
    const text = readFileSync(join(dir, 'config.json'), 'utf8')
    expect(text).toContain('\n')
    expect(JSON.parse(text).coreUrl).toBe('wss://x')
  })

  it('upsertDevice thêm máy mới', () => {
    store.upsertDevice(serialDevice)
    expect(store.load().devices).toHaveLength(1)
  })

  it('upsertDevice thay thế máy trùng deviceKey thay vì thêm bản sao', () => {
    store.upsertDevice(serialDevice)
    store.upsertDevice({ ...serialDevice, name: 'Máy sinh hóa (đổi tên)' })

    const devices = store.load().devices
    expect(devices).toHaveLength(1)
    expect(devices[0]?.name).toBe('Máy sinh hóa (đổi tên)')
  })

  it('removeDevice xoá đúng máy', () => {
    store.upsertDevice(serialDevice)
    store.upsertDevice({ ...serialDevice, deviceKey: 'may-huyet-hoc', name: 'Máy huyết học' })
    store.removeDevice('may-sinh-hoa')

    const devices = store.load().devices
    expect(devices.map((d) => d.deviceKey)).toEqual(['may-huyet-hoc'])
  })

  it('không sập khi file cấu hình hỏng, mà trả về mặc định', () => {
    const path = join(dir, 'config.json')
    require('node:fs').writeFileSync(path, '{ hỏng', 'utf8')
    const config = new ConfigStore(path).load()
    expect(config.devices).toEqual([])
  })
})
```

- [ ] **Step 3: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/agent test`
Expected: FAIL — không tìm thấy `./config-store.js`.

- [ ] **Step 4: Viết `config-store.ts`**

`apps/agent/src/main/config-store.ts`:
```ts
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'

export interface SerialConfig {
  kind: 'serial'
  path: string
  baudRate: number
  dataBits: 5 | 6 | 7 | 8
  parity: 'none' | 'even' | 'odd'
  stopBits: 1 | 2
}

export interface TcpConfig {
  kind: 'tcp'
  /** 'client': Agent nối tới máy. 'server': Agent lắng nghe, máy nối vào. */
  role: 'client' | 'server'
  host: string
  port: number
}

export interface FileConfig {
  kind: 'file'
  directory: string
  /** Mẫu glob của tên file kết quả, ví dụ '*.txt'. */
  pattern: string
  deleteAfterRead: boolean
}

export type TransportConfig = SerialConfig | TcpConfig | FileConfig

export interface DeviceConfig {
  deviceKey: string
  name: string
  driver: string
  enabled: boolean
  transport: TransportConfig
}

export interface AgentConfig {
  coreUrl: string
  agentKey: string
  proxyUrl: string | null
  hisPort: number
  devices: DeviceConfig[]
}

const DEFAULT_CONFIG: AgentConfig = {
  coreUrl: '',
  agentKey: '',
  proxyUrl: null,
  hisPort: 8080,
  devices: [],
}

/**
 * Cấu hình cục bộ của Agent, lưu dạng JSON.
 *
 * Chọn JSON thay vì SQLite vì người triển khai cần mở được bằng Notepad khi hỗ trợ
 * qua điện thoại và chép được sang máy khác. SQLite chỉ dùng cho hàng đợi và log.
 */
export class ConfigStore {
  constructor(private readonly filePath: string) {}

  load(): AgentConfig {
    if (!existsSync(this.filePath)) return { ...DEFAULT_CONFIG }
    try {
      const parsed = JSON.parse(readFileSync(this.filePath, 'utf8')) as Partial<AgentConfig>
      return {
        coreUrl: parsed.coreUrl ?? DEFAULT_CONFIG.coreUrl,
        agentKey: parsed.agentKey ?? DEFAULT_CONFIG.agentKey,
        proxyUrl: parsed.proxyUrl ?? null,
        hisPort: parsed.hisPort ?? DEFAULT_CONFIG.hisPort,
        devices: parsed.devices ?? [],
      }
    } catch {
      // File hỏng không được làm Agent không khởi động được: nó vẫn phải chạy lên
      // để người dùng mở cửa sổ cấu hình và nhập lại.
      return { ...DEFAULT_CONFIG }
    }
  }

  save(config: AgentConfig): void {
    mkdirSync(dirname(this.filePath), { recursive: true })
    // Ghi ra file tạm rồi đổi tên: mất điện giữa chừng không để lại file hỏng.
    const tempPath = `${this.filePath}.tmp`
    writeFileSync(tempPath, JSON.stringify(config, null, 2), 'utf8')
    renameSync(tempPath, this.filePath)
  }

  upsertDevice(device: DeviceConfig): void {
    const config = this.load()
    const index = config.devices.findIndex((d) => d.deviceKey === device.deviceKey)
    if (index >= 0) config.devices[index] = device
    else config.devices.push(device)
    this.save(config)
  }

  removeDevice(deviceKey: string): void {
    const config = this.load()
    config.devices = config.devices.filter((d) => d.deviceKey !== deviceKey)
    this.save(config)
  }
}
```

- [ ] **Step 5: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/agent test`
Expected: 7 test PASS.

- [ ] **Step 6: Viết khung Electron chạy được**

`apps/agent/src/main/index.ts`:
```ts
import { join } from 'node:path'
import { BrowserWindow, Tray, app, nativeImage } from 'electron'
import { ConfigStore } from './config-store.js'

let window: BrowserWindow | null = null
let tray: Tray | null = null

export const configStore = new ConfigStore(join(app.getPath('userData'), 'config.json'))

function createWindow(): void {
  if (window) {
    window.show()
    return
  }
  window = new BrowserWindow({
    width: 1100,
    height: 720,
    title: 'LIS Connect Agent',
    webPreferences: { preload: join(__dirname, '../preload/index.js'), sandbox: false },
  })
  window.on('closed', () => {
    window = null
  })

  if (process.env.ELECTRON_RENDERER_URL) {
    void window.loadURL(process.env.ELECTRON_RENDERER_URL)
  } else {
    void window.loadFile(join(__dirname, '../renderer/index.html'))
  }
}

app.whenReady().then(() => {
  createWindow()
  tray = new Tray(nativeImage.createEmpty())
  tray.setToolTip('LIS Connect Agent')
  tray.on('click', createWindow)
})

// Đóng cửa sổ KHÔNG thoát ứng dụng: main process phải chạy tiếp để giữ kết nối
// tới máy xét nghiệm. Chỉ thoát khi người dùng chọn thoát từ khay hệ thống.
app.on('window-all-closed', () => {
  // cố ý để trống
})
```

`apps/agent/src/preload/index.ts`:
```ts
import { contextBridge, ipcRenderer } from 'electron'

contextBridge.exposeInMainWorld('agent', {
  invoke: (channel: string, payload?: unknown) => ipcRenderer.invoke(channel, payload),
  on: (channel: string, handler: (payload: unknown) => void) => {
    const listener = (_event: unknown, payload: unknown): void => handler(payload)
    ipcRenderer.on(channel, listener)
    return () => ipcRenderer.off(channel, listener)
  },
})
```

`apps/agent/src/renderer/index.html`:
```html
<!doctype html>
<html lang="vi">
  <head>
    <meta charset="UTF-8" />
    <title>LIS Connect Agent</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" src="./main.tsx"></script>
  </body>
</html>
```

`apps/agent/src/renderer/main.tsx`:
```tsx
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App.js'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
```

`apps/agent/src/renderer/App.tsx`:
```tsx
export function App(): JSX.Element {
  return <h1>LIS Connect Agent</h1>
}
```

- [ ] **Step 7: Chạy thử ứng dụng**

Run: `pnpm --filter @lis/agent dev`
Expected: cửa sổ Electron mở ra hiện dòng chữ "LIS Connect Agent". Đóng cửa sổ thì tiến trình vẫn chạy (icon còn ở khay hệ thống). Dừng bằng Ctrl-C ở terminal.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(agent): electron shell with tray and local config store"
```

---

## Task 10: Agent — ba lớp transport

**Files:**
- Create: `apps/agent/src/main/transports/types.ts`, `serial.ts`, `tcp.ts`, `file.ts`, `factory.ts`
- Test: `apps/agent/src/main/transports/tcp.test.ts`, `apps/agent/src/main/transports/file.test.ts`

**Interfaces:**
- Consumes: Task 9 (`SerialConfig`, `TcpConfig`, `FileConfig`)
- Produces:
  - interface `Transport` với `open(): Promise<void>`, `close(): Promise<void>`, `write(data: Buffer): Promise<void>`, `on(event, handler)`, `isOpen(): boolean`
  - sự kiện: `'data'` → `Buffer` · `'status'` → `{ status: 'connected' | 'disconnected' | 'error'; detail?: string }`
  - `createTransport(config: TransportConfig): Transport`

**Ranh giới quan trọng:** transport **không biết gì về giao thức**. Nó chỉ chuyển byte và báo trạng thái. Driver mới là nơi hiểu ASTM. Nhờ ranh giới này, cùng một driver chạy được trên cả serial lẫn TCP mà không sửa dòng nào.

`SerialTransport` không có test tự động vì không mở được cổng COM thật trong CI; nó được kiểm chứng bằng tay ở Task 15 với cổng ảo `com0com`. `TcpTransport` và `FileTransport` có test đầy đủ.

- [ ] **Step 1: Viết `types.ts`**

`apps/agent/src/main/transports/types.ts`:
```ts
import type { TransportConfig } from '../config-store.js'

export interface TransportStatus {
  status: 'connected' | 'disconnected' | 'error'
  detail?: string
}

export interface TransportEvents {
  data: (chunk: Buffer) => void
  status: (status: TransportStatus) => void
}

/**
 * Kênh truyền byte tới một máy xét nghiệm.
 *
 * Transport KHÔNG biết gì về giao thức: nó chỉ chuyển byte và báo trạng thái.
 * Nhờ vậy cùng một driver chạy được trên cả serial lẫn TCP.
 */
export interface Transport {
  readonly config: TransportConfig
  open(): Promise<void>
  close(): Promise<void>
  write(data: Buffer): Promise<void>
  isOpen(): boolean
  on<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void
  off<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void
}
```

- [ ] **Step 2: Viết test cho TcpTransport**

`apps/agent/src/main/transports/tcp.test.ts`:
```ts
import { createServer, type Server, type Socket } from 'node:net'
import { afterEach, describe, expect, it } from 'vitest'
import { TcpTransport } from './tcp.js'

let server: Server | null = null
let transport: TcpTransport | null = null

async function startEchoServer(): Promise<{ port: number; sockets: Socket[] }> {
  const sockets: Socket[] = []
  server = createServer((socket) => {
    sockets.push(socket)
    socket.on('data', (chunk) => socket.write(chunk))
  })
  await new Promise<void>((resolve) => server!.listen(0, resolve))
  const address = server!.address()
  if (typeof address === 'string' || !address) throw new Error('không lấy được cổng')
  return { port: address.port, sockets }
}

afterEach(async () => {
  await transport?.close()
  transport = null
  await new Promise<void>((resolve) => {
    if (!server) return resolve()
    server.close(() => resolve())
  })
  server = null
})

describe('TcpTransport ở chế độ client', () => {
  it('mở kết nối và báo trạng thái connected', async () => {
    const { port } = await startEchoServer()
    transport = new TcpTransport({ kind: 'tcp', role: 'client', host: '127.0.0.1', port })

    const statuses: string[] = []
    transport.on('status', (s) => statuses.push(s.status))
    await transport.open()

    expect(transport.isOpen()).toBe(true)
    expect(statuses).toContain('connected')
  })

  it('nhận được dữ liệu máy gửi về', async () => {
    const { port } = await startEchoServer()
    transport = new TcpTransport({ kind: 'tcp', role: 'client', host: '127.0.0.1', port })

    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()
    await transport.write(Buffer.from('xin chào', 'utf8'))

    await new Promise((r) => setTimeout(r, 100))
    expect(Buffer.concat(received).toString('utf8')).toBe('xin chào')
  })

  it('báo trạng thái disconnected khi máy đóng kết nối', async () => {
    const { port, sockets } = await startEchoServer()
    transport = new TcpTransport({ kind: 'tcp', role: 'client', host: '127.0.0.1', port })

    const statuses: string[] = []
    transport.on('status', (s) => statuses.push(s.status))
    await transport.open()
    sockets.forEach((s) => s.destroy())

    await new Promise((r) => setTimeout(r, 100))
    expect(statuses).toContain('disconnected')
    expect(transport.isOpen()).toBe(false)
  })

  it('báo lỗi khi không kết nối được', async () => {
    // Cổng 1 gần như chắc chắn không có gì lắng nghe.
    transport = new TcpTransport({ kind: 'tcp', role: 'client', host: '127.0.0.1', port: 1 })
    await expect(transport.open()).rejects.toThrow()
    expect(transport.isOpen()).toBe(false)
  })

  it('write khi chưa mở thì báo lỗi rõ ràng', async () => {
    transport = new TcpTransport({ kind: 'tcp', role: 'client', host: '127.0.0.1', port: 1 })
    await expect(transport.write(Buffer.from('x'))).rejects.toThrow(/chưa mở/)
  })
})

describe('TcpTransport ở chế độ server', () => {
  it('nhận kết nối từ máy và đọc được dữ liệu', async () => {
    transport = new TcpTransport({ kind: 'tcp', role: 'server', host: '127.0.0.1', port: 0 })
    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    const { connect } = await import('node:net')
    const client = connect({ host: '127.0.0.1', port: transport.listeningPort() })
    await new Promise((r) => client.once('connect', r))
    client.write('H|\\^&|')

    await new Promise((r) => setTimeout(r, 100))
    expect(Buffer.concat(received).toString('utf8')).toBe('H|\\^&|')
    client.destroy()
  })
})
```

- [ ] **Step 3: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/agent test src/main/transports/tcp.test.ts`
Expected: FAIL — không tìm thấy `./tcp.js`.

- [ ] **Step 4: Viết `tcp.ts`**

`apps/agent/src/main/transports/tcp.ts`:
```ts
import { EventEmitter } from 'node:events'
import { createConnection, createServer, type Server, type Socket } from 'node:net'
import type { TcpConfig } from '../config-store.js'
import type { Transport, TransportEvents } from './types.js'

export class TcpTransport implements Transport {
  private readonly emitter = new EventEmitter()
  private socket: Socket | null = null
  private server: Server | null = null

  constructor(readonly config: TcpConfig) {}

  isOpen(): boolean {
    return this.socket !== null && !this.socket.destroyed
  }

  /** Cổng đang lắng nghe ở chế độ server. Ném lỗi nếu chưa mở. */
  listeningPort(): number {
    const address = this.server?.address()
    if (!address || typeof address === 'string') throw new Error('server chưa lắng nghe')
    return address.port
  }

  async open(): Promise<void> {
    if (this.config.role === 'server') return this.openServer()
    return this.openClient()
  }

  private attachSocket(socket: Socket): void {
    this.socket = socket
    socket.on('data', (chunk: Buffer) => this.emitter.emit('data', chunk))
    socket.on('close', () => {
      this.socket = null
      this.emitter.emit('status', { status: 'disconnected' })
    })
    socket.on('error', (error) => {
      this.emitter.emit('status', { status: 'error', detail: error.message })
    })
    this.emitter.emit('status', { status: 'connected' })
  }

  private openClient(): Promise<void> {
    return new Promise((resolve, reject) => {
      const socket = createConnection({ host: this.config.host, port: this.config.port })
      const onError = (error: Error): void => {
        socket.destroy()
        this.emitter.emit('status', { status: 'error', detail: error.message })
        reject(error)
      }
      socket.once('error', onError)
      socket.once('connect', () => {
        socket.off('error', onError)
        this.attachSocket(socket)
        resolve()
      })
    })
  }

  private openServer(): Promise<void> {
    return new Promise((resolve, reject) => {
      const server = createServer((socket) => this.attachSocket(socket))
      server.once('error', reject)
      server.listen(this.config.port, this.config.host, () => {
        this.server = server
        resolve()
      })
    })
  }

  async write(data: Buffer): Promise<void> {
    const socket = this.socket
    if (!socket || socket.destroyed) throw new Error('kết nối TCP chưa mở')
    await new Promise<void>((resolve, reject) => {
      socket.write(data, (error) => (error ? reject(error) : resolve()))
    })
  }

  async close(): Promise<void> {
    this.socket?.destroy()
    this.socket = null
    if (this.server) {
      await new Promise<void>((resolve) => this.server!.close(() => resolve()))
      this.server = null
    }
  }

  on<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.on(event, handler as (...args: unknown[]) => void)
  }

  off<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.off(event, handler as (...args: unknown[]) => void)
  }
}
```

- [ ] **Step 5: Chạy test TCP, xác nhận pass**

Run: `pnpm --filter @lis/agent test src/main/transports/tcp.test.ts`
Expected: 6 test PASS.

- [ ] **Step 6: Viết test cho FileTransport**

`apps/agent/src/main/transports/file.test.ts`:
```ts
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { FileTransport } from './file.js'

let dir: string
let transport: FileTransport | null = null

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'lis-file-'))
})

afterEach(async () => {
  await transport?.close()
  transport = null
  rmSync(dir, { recursive: true, force: true })
})

async function waitFor(check: () => boolean, timeoutMs = 3000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (check()) return
    await new Promise((r) => setTimeout(r, 50))
  }
  throw new Error('hết giờ chờ điều kiện')
}

describe('FileTransport', () => {
  it('đọc file đã có sẵn trong thư mục khi mở', async () => {
    writeFileSync(join(dir, 'kq1.txt'), 'R|1|Glu|5.4', 'utf8')
    transport = new FileTransport({
      kind: 'file',
      directory: dir,
      pattern: '*.txt',
      deleteAfterRead: false,
    })

    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    await waitFor(() => received.length === 1)
    expect(received[0]!.toString('utf8')).toBe('R|1|Glu|5.4')
  })

  it('đọc file mới xuất hiện sau khi đã mở', async () => {
    transport = new FileTransport({
      kind: 'file',
      directory: dir,
      pattern: '*.txt',
      deleteAfterRead: false,
    })
    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    writeFileSync(join(dir, 'kq2.txt'), 'R|2|Ure|6.1', 'utf8')
    await waitFor(() => received.length === 1)
    expect(received[0]!.toString('utf8')).toBe('R|2|Ure|6.1')
  })

  it('bỏ qua file không khớp mẫu', async () => {
    writeFileSync(join(dir, 'ghichu.log'), 'không phải kết quả', 'utf8')
    transport = new FileTransport({
      kind: 'file',
      directory: dir,
      pattern: '*.txt',
      deleteAfterRead: false,
    })
    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    await new Promise((r) => setTimeout(r, 300))
    expect(received).toHaveLength(0)
  })

  it('xoá file sau khi đọc nếu được cấu hình', async () => {
    const path = join(dir, 'kq3.txt')
    writeFileSync(path, 'R|3|Cre|88', 'utf8')
    transport = new FileTransport({
      kind: 'file',
      directory: dir,
      pattern: '*.txt',
      deleteAfterRead: true,
    })
    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    await waitFor(() => !existsSync(path))
    expect(received).toHaveLength(1)
  })

  it('không đọc lại cùng một file hai lần khi không xoá', async () => {
    writeFileSync(join(dir, 'kq4.txt'), 'R|4|Na|140', 'utf8')
    transport = new FileTransport({
      kind: 'file',
      directory: dir,
      pattern: '*.txt',
      deleteAfterRead: false,
    })
    const received: Buffer[] = []
    transport.on('data', (chunk) => received.push(chunk))
    await transport.open()

    await waitFor(() => received.length === 1)
    await new Promise((r) => setTimeout(r, 500))
    expect(received).toHaveLength(1)
  })

  it('báo lỗi khi thư mục không tồn tại', async () => {
    transport = new FileTransport({
      kind: 'file',
      directory: join(dir, 'khong-co'),
      pattern: '*.txt',
      deleteAfterRead: false,
    })
    await expect(transport.open()).rejects.toThrow(/không tồn tại/)
  })
})
```

- [ ] **Step 7: Viết `file.ts`**

`apps/agent/src/main/transports/file.ts`:
```ts
import { EventEmitter } from 'node:events'
import { existsSync, readFileSync, readdirSync, statSync, unlinkSync } from 'node:fs'
import { join } from 'node:path'
import type { FileConfig } from '../config-store.js'
import type { Transport, TransportEvents } from './types.js'

const POLL_INTERVAL_MS = 1000
/** Chờ file ổn định trước khi đọc: máy có thể còn đang ghi dở. */
const STABLE_AFTER_MS = 500

function globToRegExp(pattern: string): RegExp {
  const escaped = pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*')
  return new RegExp(`^${escaped}$`, 'i')
}

export class FileTransport implements Transport {
  private readonly emitter = new EventEmitter()
  private readonly seen = new Set<string>()
  private timer: NodeJS.Timeout | null = null
  private readonly matcher: RegExp

  constructor(readonly config: FileConfig) {
    this.matcher = globToRegExp(config.pattern)
  }

  isOpen(): boolean {
    return this.timer !== null
  }

  async open(): Promise<void> {
    if (!existsSync(this.config.directory)) {
      throw new Error(`thư mục không tồn tại: ${this.config.directory}`)
    }
    this.timer = setInterval(() => this.scan(), POLL_INTERVAL_MS)
    this.emitter.emit('status', { status: 'connected' })
    this.scan()
  }

  /**
   * Quét thư mục bằng polling thay vì fs.watch: thư mục kết quả thường nằm trên
   * ổ mạng, nơi fs.watch không phát sự kiện đáng tin trên Windows.
   */
  private scan(): void {
    let entries: string[]
    try {
      entries = readdirSync(this.config.directory)
    } catch (error) {
      this.emitter.emit('status', {
        status: 'error',
        detail: error instanceof Error ? error.message : String(error),
      })
      return
    }

    for (const name of entries) {
      if (!this.matcher.test(name)) continue
      const path = join(this.config.directory, name)
      if (this.seen.has(path)) continue

      let stat
      try {
        stat = statSync(path)
      } catch {
        continue
      }
      if (!stat.isFile()) continue
      if (Date.now() - stat.mtimeMs < STABLE_AFTER_MS) continue

      try {
        const content = readFileSync(path)
        this.seen.add(path)
        this.emitter.emit('data', content)
        if (this.config.deleteAfterRead) {
          unlinkSync(path)
          this.seen.delete(path)
        }
      } catch (error) {
        this.emitter.emit('status', {
          status: 'error',
          detail: `đọc file ${name} thất bại: ${String(error)}`,
        })
      }
    }
  }

  async write(): Promise<void> {
    throw new Error('transport dạng file chỉ đọc, không gửi được xuống máy')
  }

  async close(): Promise<void> {
    if (this.timer) clearInterval(this.timer)
    this.timer = null
    this.emitter.emit('status', { status: 'disconnected' })
  }

  on<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.on(event, handler as (...args: unknown[]) => void)
  }

  off<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.off(event, handler as (...args: unknown[]) => void)
  }
}
```

Test "đọc file đã có sẵn khi mở" cần file cũ hơn `STABLE_AFTER_MS`; thêm `await new Promise((r) => setTimeout(r, 600))` ngay sau `writeFileSync` trong hai test đầu.

- [ ] **Step 8: Viết `serial.ts` và `factory.ts`**

`apps/agent/src/main/transports/serial.ts`:
```ts
import { EventEmitter } from 'node:events'
import { SerialPort } from 'serialport'
import type { SerialConfig } from '../config-store.js'
import type { Transport, TransportEvents } from './types.js'

export class SerialTransport implements Transport {
  private readonly emitter = new EventEmitter()
  private port: SerialPort | null = null

  constructor(readonly config: SerialConfig) {}

  isOpen(): boolean {
    return this.port?.isOpen ?? false
  }

  async open(): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      const port = new SerialPort(
        {
          path: this.config.path,
          baudRate: this.config.baudRate,
          dataBits: this.config.dataBits,
          parity: this.config.parity,
          stopBits: this.config.stopBits,
          autoOpen: false,
        },
        undefined,
      )
      port.open((error) => {
        if (error) {
          this.emitter.emit('status', { status: 'error', detail: error.message })
          return reject(error)
        }
        this.port = port
        port.on('data', (chunk: Buffer) => this.emitter.emit('data', chunk))
        port.on('close', () => {
          this.port = null
          this.emitter.emit('status', { status: 'disconnected' })
        })
        port.on('error', (err) =>
          this.emitter.emit('status', { status: 'error', detail: err.message }),
        )
        this.emitter.emit('status', { status: 'connected' })
        resolve()
      })
    })
  }

  async write(data: Buffer): Promise<void> {
    const port = this.port
    if (!port?.isOpen) throw new Error('cổng serial chưa mở')
    await new Promise<void>((resolve, reject) => {
      port.write(data, (error) => (error ? reject(error) : resolve()))
    })
  }

  async close(): Promise<void> {
    const port = this.port
    if (!port) return
    await new Promise<void>((resolve) => port.close(() => resolve()))
    this.port = null
  }

  on<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.on(event, handler as (...args: unknown[]) => void)
  }

  off<E extends keyof TransportEvents>(event: E, handler: TransportEvents[E]): void {
    this.emitter.off(event, handler as (...args: unknown[]) => void)
  }
}

/** Liệt kê cổng COM đang có trên máy, dùng cho dropdown trong tab Máy xét nghiệm. */
export async function listSerialPorts(): Promise<
  Array<{ path: string; manufacturer: string | null; friendlyName: string | null }>
> {
  const ports = await SerialPort.list()
  return ports.map((p) => ({
    path: p.path,
    manufacturer: p.manufacturer ?? null,
    friendlyName: (p as { friendlyName?: string }).friendlyName ?? null,
  }))
}
```

`apps/agent/src/main/transports/factory.ts`:
```ts
import type { TransportConfig } from '../config-store.js'
import { FileTransport } from './file.js'
import { SerialTransport } from './serial.js'
import { TcpTransport } from './tcp.js'
import type { Transport } from './types.js'

export function createTransport(config: TransportConfig): Transport {
  switch (config.kind) {
    case 'serial':
      return new SerialTransport(config)
    case 'tcp':
      return new TcpTransport(config)
    case 'file':
      return new FileTransport(config)
  }
}
```

- [ ] **Step 9: Chạy toàn bộ test của agent**

Run: `pnpm --filter @lis/agent test`
Expected: 7 test config + 6 test TCP + 6 test file PASS.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(agent): serial, tcp and file transports with a common interface"
```

---

## Task 11: Máy xét nghiệm giả lập nói ASTM

Công cụ quan trọng nhất của giai đoạn này. Không có nó thì mọi test về khả năng phục hồi đều là lý thuyết, và không demo được cho khách khi chưa có máy thật.

**Files:**
- Create: `tools/simulator/package.json`, `tools/simulator/tsconfig.json`
- Create: `tools/simulator/src/astm-frames.ts`, `tools/simulator/src/simulator.ts`, `tools/simulator/src/cli.ts`
- Test: `tools/simulator/src/simulator.test.ts`

**Interfaces:**
- Consumes: không (độc lập hoàn toàn để chạy được một mình khi đi triển khai)
- Produces:
  - `AstmSimulator` với `start(port: number): Promise<number>`, `stop(): Promise<void>`, `sendResults(results: SimResult[]): Promise<void>`
  - type `SimResult` = `{ sid: string; testCode: string; value: string; unit: string }`
  - type `Misbehaviour` = `'none' | 'bad-checksum' | 'truncate' | 'no-ack' | 'garbage-prefix'`
  - `AstmSimulator` nhận `{ misbehaviour: Misbehaviour }` trong constructor

**Nhắc lại khung ASTM E1381 để người thực thi không phải tra chuẩn:**
- `ENQ` = `0x05`, `ACK` = `0x06`, `NAK` = `0x15`, `EOT` = `0x04`, `STX` = `0x02`, `ETX` = `0x03`, `ETB` = `0x17`, `CR` = `0x0D`, `LF` = `0x0A`
- Một frame: `STX` + số thứ tự frame (1 ký tự `'1'`..`'7'`, quay vòng) + nội dung + `ETX` + 2 ký tự checksum hex hoa + `CR` + `LF`
- Checksum = tổng các byte từ ngay sau `STX` tới hết `ETX`, lấy modulo 256, in dạng hex hoa hai chữ số
- Phiên truyền: bên gửi gửi `ENQ`, chờ `ACK`, gửi lần lượt các frame (mỗi frame chờ `ACK`), gửi `EOT` để kết thúc

- [ ] **Step 1: Tạo package**

`tools/simulator/package.json`:
```json
{
  "name": "@lis/simulator",
  "version": "0.1.0",
  "type": "module",
  "bin": { "lis-simulator": "./src/cli.ts" },
  "scripts": { "test": "vitest run", "start": "node --import tsx/esm src/cli.ts" }
}
```

Run: `pnpm --filter @lis/simulator add -D tsx@4.19.2 vitest@2.1.8`

- [ ] **Step 2: Viết test cho khung ASTM**

`tools/simulator/src/simulator.test.ts`:
```ts
import { connect, type Socket } from 'node:net'
import { afterEach, describe, expect, it } from 'vitest'
import { ACK, ENQ, EOT, ETX, STX, checksum, buildFrame } from './astm-frames.js'
import { AstmSimulator } from './simulator.js'

let sim: AstmSimulator | null = null
let client: Socket | null = null

afterEach(async () => {
  client?.destroy()
  client = null
  await sim?.stop()
  sim = null
})

function collect(socket: Socket): { chunks: Buffer[]; all: () => Buffer } {
  const chunks: Buffer[] = []
  socket.on('data', (chunk) => chunks.push(chunk))
  return { chunks, all: () => Buffer.concat(chunks) }
}

async function waitFor(check: () => boolean, timeoutMs = 3000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (check()) return
    await new Promise((r) => setTimeout(r, 20))
  }
  throw new Error('hết giờ chờ điều kiện')
}

describe('checksum ASTM', () => {
  it('tính đúng theo ví dụ của chuẩn', () => {
    // Nội dung '1H|\^&' + ETX; tổng byte modulo 256.
    const body = Buffer.from([0x31, 0x48, 0x7c, 0x5c, 0x5e, 0x26, ETX])
    expect(checksum(body)).toBe('C4')
  })

  it('luôn trả về hai ký tự hex hoa', () => {
    expect(checksum(Buffer.from([0x01]))).toBe('01')
    expect(checksum(Buffer.from([0xff, 0x02]))).toBe('01')
  })
})

describe('buildFrame', () => {
  it('bọc nội dung bằng STX ... ETX checksum CR LF', () => {
    const frame = buildFrame(1, 'H|\\^&')
    expect(frame[0]).toBe(STX)
    expect(frame.at(-2)).toBe(0x0d)
    expect(frame.at(-1)).toBe(0x0a)
    expect(frame.includes(ETX)).toBe(true)
  })
})

describe('AstmSimulator', () => {
  it('gửi ENQ rồi các frame kết quả rồi EOT khi được yêu cầu', async () => {
    sim = new AstmSimulator({ misbehaviour: 'none' })
    const port = await sim.start(0)

    client = connect({ host: '127.0.0.1', port })
    const received = collect(client)
    await new Promise((r) => client!.once('connect', r))

    void sim.sendResults([{ sid: 'SID001', testCode: 'GLU', value: '5.4', unit: 'mmol/L' }])

    await waitFor(() => received.all().includes(ENQ))
    client.write(Buffer.from([ACK]))

    await waitFor(() => received.all().includes(EOT), 5000)
    const text = received.all().toString('latin1')
    expect(text).toContain('SID001')
    expect(text).toContain('GLU')
    expect(text).toContain('5.4')
  })

  it('với misbehaviour bad-checksum thì checksum sai lệch', async () => {
    sim = new AstmSimulator({ misbehaviour: 'bad-checksum' })
    const port = await sim.start(0)
    client = connect({ host: '127.0.0.1', port })
    const received = collect(client)
    await new Promise((r) => client!.once('connect', r))

    void sim.sendResults([{ sid: 'SID002', testCode: 'URE', value: '6.1', unit: 'mmol/L' }])
    await waitFor(() => received.all().includes(ENQ))
    client.write(Buffer.from([ACK]))
    await waitFor(() => received.all().toString('latin1').includes('SID002'), 5000)

    const frames = received.all().toString('latin1')
    const match = /\x02(.*?)\x03([0-9A-F]{2})/s.exec(frames)
    expect(match).not.toBeNull()
    const body = Buffer.from(`${match![1]}\x03`, 'latin1')
    expect(match![2]).not.toBe(checksum(body))
  })

  it('với misbehaviour truncate thì frame bị cắt giữa chừng', async () => {
    sim = new AstmSimulator({ misbehaviour: 'truncate' })
    const port = await sim.start(0)
    client = connect({ host: '127.0.0.1', port })
    const received = collect(client)
    await new Promise((r) => client!.once('connect', r))

    void sim.sendResults([{ sid: 'SID003', testCode: 'CRE', value: '88', unit: 'umol/L' }])
    await waitFor(() => received.all().includes(ENQ))
    client.write(Buffer.from([ACK]))
    await new Promise((r) => setTimeout(r, 500))

    expect(received.all().includes(EOT)).toBe(false)
  })

  it('với misbehaviour no-ack thì không trả ACK cho ENQ của host', async () => {
    sim = new AstmSimulator({ misbehaviour: 'no-ack' })
    const port = await sim.start(0)
    client = connect({ host: '127.0.0.1', port })
    const received = collect(client)
    await new Promise((r) => client!.once('connect', r))

    client.write(Buffer.from([ENQ]))
    await new Promise((r) => setTimeout(r, 500))
    expect(received.all().includes(ACK)).toBe(false)
  })

  it('với misbehaviour none thì trả ACK cho ENQ của host', async () => {
    sim = new AstmSimulator({ misbehaviour: 'none' })
    const port = await sim.start(0)
    client = connect({ host: '127.0.0.1', port })
    const received = collect(client)
    await new Promise((r) => client!.once('connect', r))

    client.write(Buffer.from([ENQ]))
    await waitFor(() => received.all().includes(ACK))
    expect(received.all().includes(ACK)).toBe(true)
  })
})
```

- [ ] **Step 3: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/simulator test`
Expected: FAIL — không tìm thấy `./astm-frames.js`.

- [ ] **Step 4: Viết `astm-frames.ts`**

`tools/simulator/src/astm-frames.ts`:
```ts
export const ENQ = 0x05
export const ACK = 0x06
export const NAK = 0x15
export const EOT = 0x04
export const STX = 0x02
export const ETX = 0x03
export const ETB = 0x17
export const CR = 0x0d
export const LF = 0x0a

/**
 * Checksum ASTM E1381: tổng các byte từ ngay sau STX tới hết ETX (hoặc ETB),
 * lấy modulo 256, in dạng hex hoa hai chữ số.
 */
export function checksum(bodyWithTerminator: Buffer): string {
  let sum = 0
  for (const byte of bodyWithTerminator) sum = (sum + byte) % 256
  return sum.toString(16).toUpperCase().padStart(2, '0')
}

/**
 * Dựng một frame hoàn chỉnh.
 * frameNumber quay vòng 1..7 theo chuẩn.
 */
export function buildFrame(frameNumber: number, content: string): Buffer {
  const seq = String(frameNumber % 8)
  const body = Buffer.concat([Buffer.from(seq + content, 'latin1'), Buffer.from([ETX])])
  return Buffer.concat([
    Buffer.from([STX]),
    body,
    Buffer.from(checksum(body), 'latin1'),
    Buffer.from([CR, LF]),
  ])
}
```

- [ ] **Step 5: Viết `simulator.ts`**

`tools/simulator/src/simulator.ts`:
```ts
import { createServer, type Server, type Socket } from 'node:net'
import { ACK, ENQ, EOT, buildFrame } from './astm-frames.js'

export interface SimResult {
  sid: string
  testCode: string
  value: string
  unit: string
}

export type Misbehaviour = 'none' | 'bad-checksum' | 'truncate' | 'no-ack' | 'garbage-prefix'

export interface SimulatorOptions {
  misbehaviour: Misbehaviour
}

const ACK_TIMEOUT_MS = 3000

/**
 * Máy xét nghiệm giả lập nói ASTM E1381/E1394 qua TCP.
 *
 * Ngoài đường đi đúng, nó cố tình làm được các hành vi xấu mà máy thật hay mắc:
 * checksum sai, đứt giữa chừng, im lặng không ACK, rác ở đầu luồng. Không có
 * những hành vi này thì phần xử lý lỗi của Agent không bao giờ được kiểm chứng.
 */
export class AstmSimulator {
  private server: Server | null = null
  private socket: Socket | null = null
  private frameNumber = 1

  constructor(private readonly options: SimulatorOptions) {}

  async start(port: number): Promise<number> {
    return new Promise((resolve, reject) => {
      const server = createServer((socket) => {
        this.socket = socket
        socket.on('data', (chunk) => this.onData(chunk))
        socket.on('close', () => {
          this.socket = null
        })
      })
      server.once('error', reject)
      server.listen(port, '127.0.0.1', () => {
        this.server = server
        const address = server.address()
        if (!address || typeof address === 'string') return reject(new Error('không lấy được cổng'))
        resolve(address.port)
      })
    })
  }

  private onData(chunk: Buffer): void {
    if (!chunk.includes(ENQ)) return
    if (this.options.misbehaviour === 'no-ack') return
    this.socket?.write(Buffer.from([ACK]))
  }

  private waitForAck(): Promise<void> {
    return new Promise((resolve, reject) => {
      const socket = this.socket
      if (!socket) return reject(new Error('chưa có kết nối'))
      const timer = setTimeout(() => {
        socket.off('data', onData)
        reject(new Error('hết giờ chờ ACK'))
      }, ACK_TIMEOUT_MS)
      const onData = (chunk: Buffer): void => {
        if (!chunk.includes(ACK)) return
        clearTimeout(timer)
        socket.off('data', onData)
        resolve()
      }
      socket.on('data', onData)
    })
  }

  /** Gửi một phiên kết quả lên host, theo đúng trình tự ENQ → frame → EOT. */
  async sendResults(results: SimResult[]): Promise<void> {
    const socket = this.socket
    if (!socket) throw new Error('chưa có host nào kết nối')

    if (this.options.misbehaviour === 'garbage-prefix') {
      socket.write(Buffer.from('rác đầu luồng', 'latin1'))
    }

    socket.write(Buffer.from([ENQ]))
    await this.waitForAck()

    const lines = [
      'H|\\^&|||LIS-SIM^1.0|||||||P|1',
      'P|1',
      ...results.flatMap((r, index) => [
        `O|${index + 1}|${r.sid}||^^^${r.testCode}|R`,
        `R|1|^^^${r.testCode}|${r.value}|${r.unit}||N||F`,
      ]),
      'L|1|N',
    ]

    for (const [index, line] of lines.entries()) {
      let frame = buildFrame(this.frameNumber, line)
      this.frameNumber = (this.frameNumber % 7) + 1

      if (this.options.misbehaviour === 'bad-checksum') {
        // Đổi ký tự checksum cuối để nó chắc chắn sai.
        const copy = Buffer.from(frame)
        const checksumPos = copy.length - 3
        copy[checksumPos] = copy[checksumPos] === 0x41 ? 0x42 : 0x41
        frame = copy
      }

      if (this.options.misbehaviour === 'truncate' && index === 1) {
        socket.write(frame.subarray(0, Math.floor(frame.length / 2)))
        return
      }

      socket.write(frame)
      await this.waitForAck().catch(() => undefined)
    }

    socket.write(Buffer.from([EOT]))
  }

  async stop(): Promise<void> {
    this.socket?.destroy()
    this.socket = null
    if (this.server) {
      await new Promise<void>((resolve) => this.server!.close(() => resolve()))
      this.server = null
    }
  }
}
```

- [ ] **Step 6: Viết CLI để dùng khi đi triển khai**

`tools/simulator/src/cli.ts`:
```ts
import { createInterface } from 'node:readline/promises'
import { AstmSimulator, type Misbehaviour } from './simulator.js'

const port = Number(process.env.SIM_PORT ?? 9100)
const misbehaviour = (process.env.SIM_MISBEHAVIOUR ?? 'none') as Misbehaviour

const sim = new AstmSimulator({ misbehaviour })
const actual = await sim.start(port)
console.log(`Máy giả lập ASTM đang lắng nghe ở 127.0.0.1:${actual} (chế độ: ${misbehaviour})`)
console.log('Nhấn Enter để gửi một phiên kết quả mẫu. Ctrl-C để thoát.')

const rl = createInterface({ input: process.stdin, output: process.stdout })
let counter = 1
for (;;) {
  await rl.question('')
  const sid = `SID${String(counter).padStart(4, '0')}`
  counter += 1
  try {
    await sim.sendResults([
      { sid, testCode: 'GLU', value: '5.4', unit: 'mmol/L' },
      { sid, testCode: 'URE', value: '6.1', unit: 'mmol/L' },
    ])
    console.log(`đã gửi kết quả cho ${sid}`)
  } catch (error) {
    console.error(`gửi thất bại: ${String(error)}`)
  }
}
```

- [ ] **Step 7: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/simulator test`
Expected: 2 test checksum + 1 test buildFrame + 5 test simulator PASS.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(simulator): astm instrument simulator with fault injection"
```

---

## Task 12: Agent — bộ đăng ký driver và driver ASTM E1394

**Files:**
- Create: `apps/agent/src/main/drivers/types.ts`, `registry.ts`, `astm-e1394.ts`
- Create: `apps/agent/src/main/drivers/fixtures/` (kho tin nhắn mẫu)
- Test: `apps/agent/src/main/drivers/astm-e1394.test.ts`

**Interfaces:**
- Consumes: Task 11 (`checksum`, các hằng số ASTM — sao chép vào agent thay vì phụ thuộc vào `@lis/simulator`, vì công cụ giả lập không được đi kèm bản cài cho khách)
- Produces:
  - interface `Driver` với `name: string`, `feed(chunk: Buffer): DriverEvent[]`, `reset(): void`
  - type `DriverEvent` = `{ kind: 'message'; raw: Buffer; parsed: ParsedMessage }` hoặc `{ kind: 'reply'; data: Buffer }` hoặc `{ kind: 'error'; reason: string; raw: Buffer }`
  - type `ParsedMessage` = `{ records: AstmRecord[] }`, `AstmRecord` = `{ type: 'H' | 'P' | 'O' | 'R' | 'L' | 'Q' | 'C'; fields: string[] }`
  - `driverRegistry.get(name: string): Driver`, `driverRegistry.list(): string[]`

**Nguyên tắc:** `feed` nhận từng mẩu byte đến bất kỳ lúc nào (TCP chia gói tuỳ ý, serial còn vụn hơn) và tự gom lại thành frame hoàn chỉnh. Nó **không bao giờ ném lỗi**: dữ liệu hỏng trả về `DriverEvent` loại `error` kèm nguyên văn byte, để tầng trên vẫn lưu được vào `raw_messages`.

Ở giai đoạn 1, driver chỉ tách bản ghi ASTM thành các trường; việc ánh xạ sang `analyte` và tính cờ là việc của giai đoạn 2.

- [ ] **Step 1: Viết test dựa trên kho tin nhắn mẫu**

Tạo `apps/agent/src/main/drivers/fixtures/mindray-bs240-one-sample.txt` với nội dung sau (dùng ký hiệu `<STX>` v.v. rồi test sẽ chuyển thành byte thật):

```
<STX>1H|\^&|||BS-240^1.0|||||||P|1<CR><ETX>C4<CR><LF>
<STX>2P|1||||NGUYEN VAN A||19900101|M<CR><ETX>xx<CR><LF>
<STX>3O|1|SID001||^^^GLU|R<CR><ETX>xx<CR><LF>
<STX>4R|1|^^^GLU|5.4|mmol/L||N||F<CR><ETX>xx<CR><LF>
<STX>5L|1|N<CR><ETX>xx<CR><LF>
```

`apps/agent/src/main/drivers/astm-e1394.test.ts`:
```ts
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
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/agent test src/main/drivers/astm-e1394.test.ts`
Expected: FAIL — không tìm thấy `./astm-constants.js`.

- [ ] **Step 3: Viết hằng số và kiểu**

`apps/agent/src/main/drivers/astm-constants.ts`:
```ts
export const ENQ = 0x05
export const ACK = 0x06
export const NAK = 0x15
export const EOT = 0x04
export const STX = 0x02
export const ETX = 0x03
export const ETB = 0x17
export const CR = 0x0d
export const LF = 0x0a

export function checksum(bodyWithTerminator: Buffer): string {
  let sum = 0
  for (const byte of bodyWithTerminator) sum = (sum + byte) % 256
  return sum.toString(16).toUpperCase().padStart(2, '0')
}
```

Các hằng số này trùng với `tools/simulator`. Chủ ý sao chép chứ không dùng chung package: công cụ giả lập không được đi kèm bản cài giao cho khách, và một tệp mười dòng hằng số của chuẩn không đáng để tạo phụ thuộc giữa sản phẩm và công cụ nội bộ.

`apps/agent/src/main/drivers/types.ts`:
```ts
export type AstmRecordType = 'H' | 'P' | 'O' | 'R' | 'L' | 'Q' | 'C'

export interface AstmRecord {
  type: AstmRecordType
  /** Các trường tách theo dấu '|', giữ nguyên thứ tự và ô rỗng. */
  fields: string[]
}

export interface ParsedMessage {
  records: AstmRecord[]
}

export type DriverEvent =
  | { kind: 'message'; raw: Buffer; parsed: ParsedMessage }
  | { kind: 'reply'; data: Buffer }
  | { kind: 'error'; reason: string; raw: Buffer }

/**
 * Bộ dịch giao thức của một model máy xét nghiệm.
 *
 * `feed` nhận từng mẩu byte đến bất kỳ lúc nào và tự gom thành frame hoàn chỉnh.
 * Nó KHÔNG BAO GIỜ ném lỗi: dữ liệu hỏng trả về sự kiện 'error' kèm nguyên văn
 * byte, để tầng trên vẫn lưu được vào raw_messages.
 */
export interface Driver {
  readonly name: string
  feed(chunk: Buffer): DriverEvent[]
  reset(): void
}
```

- [ ] **Step 4: Viết `astm-e1394.ts`**

`apps/agent/src/main/drivers/astm-e1394.ts`:
```ts
import { ACK, CR, ENQ, EOT, ETB, ETX, LF, NAK, STX, checksum } from './astm-constants.js'
import type { AstmRecord, AstmRecordType, Driver, DriverEvent } from './types.js'

const RECORD_TYPES: AstmRecordType[] = ['H', 'P', 'O', 'R', 'L', 'Q', 'C']

export class AstmE1394Driver implements Driver {
  readonly name = 'astm-e1394'

  private buffer = Buffer.alloc(0)
  private sessionFrames: string[] = []
  private sessionRaw: Buffer[] = []
  private inSession = false

  reset(): void {
    this.buffer = Buffer.alloc(0)
    this.sessionFrames = []
    this.sessionRaw = []
    this.inSession = false
  }

  feed(chunk: Buffer): DriverEvent[] {
    this.buffer = Buffer.concat([this.buffer, chunk])
    const events: DriverEvent[] = []

    for (;;) {
      if (this.buffer.length === 0) break

      const first = this.buffer[0]!

      if (first === ENQ) {
        this.buffer = this.buffer.subarray(1)
        this.inSession = true
        this.sessionFrames = []
        this.sessionRaw = []
        events.push({ kind: 'reply', data: Buffer.from([ACK]) })
        continue
      }

      if (first === EOT) {
        this.buffer = this.buffer.subarray(1)
        if (this.inSession && this.sessionFrames.length > 0) {
          events.push({
            kind: 'message',
            raw: Buffer.concat(this.sessionRaw),
            parsed: { records: this.sessionFrames.map(parseRecord).filter(isRecord) },
          })
        }
        this.inSession = false
        this.sessionFrames = []
        this.sessionRaw = []
        continue
      }

      if (first === STX) {
        const end = findFrameEnd(this.buffer)
        if (end === -1) break // frame chưa về đủ, chờ mẩu tiếp theo

        const frame = this.buffer.subarray(0, end)
        this.buffer = this.buffer.subarray(end)

        const verified = verifyFrame(frame)
        if (!verified.ok) {
          events.push({ kind: 'error', reason: verified.reason, raw: Buffer.from(frame) })
          events.push({ kind: 'reply', data: Buffer.from([NAK]) })
          continue
        }

        this.sessionFrames.push(verified.content)
        this.sessionRaw.push(Buffer.from(frame))
        events.push({ kind: 'reply', data: Buffer.from([ACK]) })
        continue
      }

      // Byte không thuộc khung nào: bỏ đi. Máy thật hay phát rác lúc khởi động
      // hoặc khi dây bị nhiễu; bỏ qua chúng thay vì để hỏng cả phiên sau đó.
      this.buffer = this.buffer.subarray(1)
    }

    return events
  }
}

/** Trả về vị trí ngay sau LF cuối frame, hoặc -1 nếu frame chưa về đủ. */
function findFrameEnd(buffer: Buffer): number {
  for (let i = 1; i < buffer.length; i += 1) {
    if (buffer[i] !== ETX && buffer[i] !== ETB) continue
    // sau ETX/ETB là 2 ký tự checksum, rồi CR, rồi LF
    const endIndex = i + 4
    if (endIndex >= buffer.length) return -1
    if (buffer[i + 3] === CR && buffer[i + 4] === LF) return endIndex + 1
  }
  return -1
}

type Verified = { ok: true; content: string } | { ok: false; reason: string }

function verifyFrame(frame: Buffer): Verified {
  const terminatorIndex = frame.findIndex((b) => b === ETX || b === ETB)
  if (terminatorIndex === -1) return { ok: false, reason: 'frame không có ETX/ETB' }

  const body = frame.subarray(1, terminatorIndex + 1)
  const expected = checksum(body)
  const actual = frame.subarray(terminatorIndex + 1, terminatorIndex + 3).toString('latin1')
  if (expected !== actual) {
    return { ok: false, reason: `checksum sai: nhận ${actual}, tính ra ${expected}` }
  }

  // Bỏ ký tự số thứ tự frame ở đầu body.
  return { ok: true, content: body.subarray(1, body.length - 1).toString('latin1') }
}

function parseRecord(line: string): AstmRecord | null {
  const fields = line.split('|')
  const type = fields[0] as AstmRecordType
  if (!RECORD_TYPES.includes(type)) return null
  return { type, fields }
}

function isRecord(record: AstmRecord | null): record is AstmRecord {
  return record !== null
}
```

- [ ] **Step 5: Viết registry**

`apps/agent/src/main/drivers/registry.ts`:
```ts
import { AstmE1394Driver } from './astm-e1394.js'
import type { Driver } from './types.js'

type DriverFactory = () => Driver

const factories = new Map<string, DriverFactory>([['astm-e1394', () => new AstmE1394Driver()]])

export const driverRegistry = {
  /** Tạo một instance driver mới. Mỗi máy có instance riêng vì driver giữ trạng thái phiên. */
  get(name: string): Driver {
    const factory = factories.get(name)
    if (!factory) throw new Error(`chưa có driver tên "${name}"`)
    return factory()
  },

  list(): string[] {
    return [...factories.keys()]
  },

  register(name: string, factory: DriverFactory): void {
    factories.set(name, factory)
  },
}
```

- [ ] **Step 6: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/agent test src/main/drivers/astm-e1394.test.ts`
Expected: 9 test PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(agent): driver registry and astm e1394 protocol driver"
```

---

## Task 13: Agent — hàng đợi bền và kết nối tới Core

Đây là task quyết định lời hứa "mất mạng vẫn không mất kết quả".

**Files:**
- Create: `apps/agent/src/main/local-db.ts`, `apps/agent/src/main/outbox.ts`, `apps/agent/src/main/core-client.ts`
- Test: `apps/agent/src/main/outbox.test.ts`, `apps/agent/src/main/core-client.test.ts`

**Interfaces:**
- Consumes: Task 2 (`AgentToCore`, `newMessageId`), Task 9 (`AgentConfig`)
- Produces:
  - `LocalDb.open(path: string): LocalDb` — SQLite với bảng `outbox`
  - `Outbox.enqueue(message: AgentToCore): void`
  - `Outbox.peek(limit: number): Array<{ rowId: number; message: AgentToCore }>`
  - `Outbox.ack(rowId: number): void`, `Outbox.depth(): number`, `Outbox.purgeOlderThan(days: number): number`
  - `CoreClient` với `start(): void`, `stop(): Promise<void>`, `isConnected(): boolean`, sự kiện `'status'` → `'connected' | 'disconnected' | 'unauthorized'`

**Ba quyết định:**

1. **Message chỉ bị xoá khỏi hàng đợi sau khi Core gửi `ack`.** Mất điện giữa chừng thì cùng lắm gửi lại một message đã tới nơi — và Core đã idempotent nên không sao. Ngược lại, xoá trước khi ack là mất kết quả vĩnh viễn.
2. **Backoff kết nối lại có trần 30 giây và không bao giờ bỏ cuộc.** Máy xét nghiệm chạy cả đêm; Agent phải còn sống lúc 3 giờ sáng.
3. **Khoá sai (`unauthorized`) thì ngừng thử lại.** Thử lại vô hạn với khoá sai chỉ tạo rác trong log Core và không bao giờ thành công; thay vào đó báo lên giao diện để người dùng nhập lại khoá.

- [ ] **Step 1: Viết test cho Outbox**

`apps/agent/src/main/outbox.test.ts`:
```ts
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { newMessageId } from '@lis/shared'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { LocalDb } from './local-db.js'
import { Outbox } from './outbox.js'

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
})
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/agent test src/main/outbox.test.ts`
Expected: FAIL — không tìm thấy `./local-db.js`.

- [ ] **Step 3: Viết `local-db.ts` và `outbox.ts`**

`apps/agent/src/main/local-db.ts`:
```ts
import { mkdirSync } from 'node:fs'
import { dirname } from 'node:path'
import Database from 'better-sqlite3'

/**
 * Kho dữ liệu cục bộ của Agent: hàng đợi gửi lên Core và nhật ký.
 *
 * Dùng SQLite (không phải file JSON như cấu hình) vì đây là nơi ghi liên tục và
 * phải chịu được mất điện giữa chừng.
 */
export class LocalDb {
  private constructor(readonly raw: Database.Database) {}

  static open(path: string): LocalDb {
    mkdirSync(dirname(path), { recursive: true })
    const raw = new Database(path)
    raw.pragma('journal_mode = WAL')
    raw.exec(`
      CREATE TABLE IF NOT EXISTS outbox (
        row_id     INTEGER PRIMARY KEY AUTOINCREMENT,
        message_id TEXT NOT NULL UNIQUE,
        payload    TEXT NOT NULL,
        created_at TEXT NOT NULL DEFAULT (datetime('now'))
      );
      CREATE INDEX IF NOT EXISTS outbox_created_idx ON outbox (created_at);
    `)
    return new LocalDb(raw)
  }

  close(): void {
    this.raw.close()
  }
}
```

`apps/agent/src/main/outbox.ts`:
```ts
import type { AgentToCore } from '@lis/shared'
import type { LocalDb } from './local-db.js'

export interface OutboxEntry {
  rowId: number
  message: AgentToCore
}

/**
 * Hàng đợi bền của các message chờ gửi lên Core.
 *
 * Message chỉ bị xoá sau khi Core trả ack. Mất điện giữa chừng thì cùng lắm gửi
 * lại một message đã tới nơi, và Core đã idempotent nên không sao; ngược lại,
 * xoá trước khi ack là mất kết quả xét nghiệm vĩnh viễn.
 */
export class Outbox {
  constructor(private readonly db: LocalDb) {}

  enqueue(message: AgentToCore): void {
    this.db.raw
      .prepare('INSERT OR IGNORE INTO outbox (message_id, payload) VALUES (?, ?)')
      .run(message.messageId, JSON.stringify(message))
  }

  peek(limit: number): OutboxEntry[] {
    const rows = this.db.raw
      .prepare('SELECT row_id, payload FROM outbox ORDER BY row_id ASC LIMIT ?')
      .all(limit) as Array<{ row_id: number; payload: string }>
    return rows.map((row) => ({
      rowId: row.row_id,
      message: JSON.parse(row.payload) as AgentToCore,
    }))
  }

  ack(rowId: number): void {
    this.db.raw.prepare('DELETE FROM outbox WHERE row_id = ?').run(rowId)
  }

  depth(): number {
    const row = this.db.raw.prepare('SELECT COUNT(*) AS n FROM outbox').get() as { n: number }
    return row.n
  }

  /** Xoá message cũ hơn số ngày cho trước. Trả về số bản ghi đã xoá. */
  purgeOlderThan(days: number): number {
    const result = this.db.raw
      .prepare(`DELETE FROM outbox WHERE created_at < datetime('now', ?)`)
      .run(`-${days} days`)
    return result.changes
  }
}
```

- [ ] **Step 4: Chạy test outbox, xác nhận pass**

Run: `pnpm --filter @lis/agent test src/main/outbox.test.ts`
Expected: 8 test PASS.

- [ ] **Step 5: Viết test cho CoreClient**

`apps/agent/src/main/core-client.test.ts`:
```ts
import { createServer, type Server } from 'node:http'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { newMessageId } from '@lis/shared'
import { WebSocketServer, type WebSocket } from 'ws'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { CoreClient } from './core-client.js'
import { LocalDb } from './local-db.js'
import { Outbox } from './outbox.js'

let dir: string
let db: LocalDb
let outbox: Outbox
let http: Server
let wss: WebSocketServer
let port: number
let client: CoreClient | null = null

/** Message đã tới server, gom lại để kiểm tra. */
let received: Array<Record<string, unknown>> = []
/** Đặt true để server không ack, phục vụ test gửi lại. */
let withholdAck = false

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

async function waitFor(check: () => boolean, timeoutMs = 5000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (check()) return
    await new Promise((r) => setTimeout(r, 20))
  }
  throw new Error('hết giờ chờ điều kiện')
}

beforeEach(async () => {
  dir = mkdtempSync(join(tmpdir(), 'lis-client-'))
  db = LocalDb.open(join(dir, 'agent.sqlite'))
  outbox = new Outbox(db)
  received = []
  withholdAck = false

  http = createServer()
  wss = new WebSocketServer({ server: http, path: '/agent' })
  wss.on('connection', (socket: WebSocket, request) => {
    const url = new URL(request.url ?? '/', 'http://localhost')
    if (url.searchParams.get('key') !== 'lis_agent_dung') {
      socket.close(4401, 'khoá sai')
      return
    }
    socket.on('message', (data) => {
      const message = JSON.parse(data.toString()) as Record<string, unknown>
      received.push(message)
      if (withholdAck && message.type === 'raw_message') return
      socket.send(JSON.stringify({ type: 'ack', messageId: message.messageId }))
    })
  })
  await new Promise<void>((resolve) => http.listen(0, resolve))
  port = (http.address() as { port: number }).port
})

afterEach(async () => {
  await client?.stop()
  client = null
  wss.close()
  await new Promise<void>((resolve) => http.close(() => resolve()))
  db.close()
  rmSync(dir, { recursive: true, force: true })
})

describe('CoreClient', () => {
  it('kết nối và gửi hello ngay khi khởi động', async () => {
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_dung',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
    })
    client.start()

    await waitFor(() => received.some((m) => m.type === 'hello'))
    expect(client.isConnected()).toBe(true)
  })

  it('gửi các message đang chờ trong hàng đợi sau khi kết nối', async () => {
    outbox.enqueue(rawMessage('kết quả tồn từ lúc mất mạng'))
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_dung',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
    })
    client.start()

    await waitFor(() => outbox.depth() === 0)
    expect(received.some((m) => m.type === 'raw_message')).toBe(true)
  })

  it('không xoá message khỏi hàng đợi khi Core chưa ack', async () => {
    withholdAck = true
    outbox.enqueue(rawMessage('chưa được ack'))
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_dung',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
    })
    client.start()

    await waitFor(() => received.some((m) => m.type === 'raw_message'))
    await new Promise((r) => setTimeout(r, 300))
    expect(outbox.depth()).toBe(1)
  })

  it('báo trạng thái unauthorized và ngừng thử lại khi khoá sai', async () => {
    const statuses: string[] = []
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_sai',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
    })
    client.on('status', (s) => statuses.push(s))
    client.start()

    await waitFor(() => statuses.includes('unauthorized'))
    const countAfterFirst = statuses.filter((s) => s === 'unauthorized').length
    await new Promise((r) => setTimeout(r, 800))
    expect(statuses.filter((s) => s === 'unauthorized').length).toBe(countAfterFirst)
  })

  it('tự kết nối lại sau khi server đóng kết nối', async () => {
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_dung',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
      initialBackoffMs: 50,
    })
    client.start()
    await waitFor(() => client!.isConnected())

    for (const socket of wss.clients) socket.terminate()
    await waitFor(() => !client!.isConnected())
    await waitFor(() => client!.isConnected(), 8000)

    expect(received.filter((m) => m.type === 'hello').length).toBeGreaterThanOrEqual(2)
  })

  it('message đưa vào hàng đợi lúc đang mất kết nối sẽ được gửi khi nối lại', async () => {
    client = new CoreClient({
      url: `ws://localhost:${port}/agent`,
      agentKey: 'lis_agent_dung',
      agentVersion: '0.1.0',
      hostname: 'LAB-PC-01',
      outbox,
      initialBackoffMs: 50,
    })
    client.start()
    await waitFor(() => client!.isConnected())

    for (const socket of wss.clients) socket.terminate()
    await waitFor(() => !client!.isConnected())
    outbox.enqueue(rawMessage('gửi lúc mất mạng'))

    await waitFor(() => outbox.depth() === 0, 10_000)
  })
})
```

- [ ] **Step 6: Viết `core-client.ts`**

`apps/agent/src/main/core-client.ts`:
```ts
import { EventEmitter } from 'node:events'
import { PROTOCOL_VERSION, newMessageId, type CoreToAgent } from '@lis/shared'
import WebSocket from 'ws'
import type { Outbox } from './outbox.js'

export type CoreClientStatus = 'connected' | 'disconnected' | 'unauthorized'

export interface CoreClientOptions {
  url: string
  agentKey: string
  agentVersion: string
  hostname: string
  outbox: Outbox
  initialBackoffMs?: number
  maxBackoffMs?: number
  /** Số message gửi mỗi lượt bơm hàng đợi. */
  batchSize?: number
}

const CLOSE_UNAUTHORIZED = 4401
const HEARTBEAT_INTERVAL_MS = 30_000

/**
 * Kênh nối lên Core.
 *
 * Backoff có trần và KHÔNG BAO GIỜ bỏ cuộc: máy xét nghiệm chạy cả đêm, Agent
 * phải còn sống lúc 3 giờ sáng. Ngoại lệ duy nhất là khoá sai — thử lại vô hạn
 * với khoá sai chỉ tạo rác trong log Core và không bao giờ thành công.
 */
export class CoreClient {
  private readonly emitter = new EventEmitter()
  private socket: WebSocket | null = null
  private backoffMs: number
  private readonly initialBackoffMs: number
  private readonly maxBackoffMs: number
  private readonly batchSize: number
  private reconnectTimer: NodeJS.Timeout | null = null
  private heartbeatTimer: NodeJS.Timeout | null = null
  private stopped = false
  private helloAcked = false
  /** rowId của các message đã gửi và đang chờ ack, tra theo messageId. */
  private readonly inFlight = new Map<string, number>()

  constructor(private readonly options: CoreClientOptions) {
    this.initialBackoffMs = options.initialBackoffMs ?? 1000
    this.maxBackoffMs = options.maxBackoffMs ?? 30_000
    this.batchSize = options.batchSize ?? 20
    this.backoffMs = this.initialBackoffMs
  }

  isConnected(): boolean {
    return this.socket?.readyState === WebSocket.OPEN && this.helloAcked
  }

  on(event: 'status', handler: (status: CoreClientStatus) => void): void {
    this.emitter.on(event, handler)
  }

  start(): void {
    this.stopped = false
    this.connect()
  }

  async stop(): Promise<void> {
    this.stopped = true
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer)
    if (this.heartbeatTimer) clearInterval(this.heartbeatTimer)
    this.reconnectTimer = null
    this.heartbeatTimer = null
    this.socket?.close()
    this.socket = null
  }

  private scheduleReconnect(): void {
    if (this.stopped) return
    this.reconnectTimer = setTimeout(() => this.connect(), this.backoffMs)
    this.backoffMs = Math.min(this.backoffMs * 2, this.maxBackoffMs)
  }

  private connect(): void {
    if (this.stopped) return
    this.helloAcked = false
    this.inFlight.clear()

    const url = `${this.options.url}?key=${encodeURIComponent(this.options.agentKey)}`
    const socket = new WebSocket(url)
    this.socket = socket

    socket.on('open', () => {
      this.backoffMs = this.initialBackoffMs
      socket.send(
        JSON.stringify({
          type: 'hello',
          messageId: newMessageId(),
          sentAt: new Date().toISOString(),
          protocolVersion: PROTOCOL_VERSION,
          agentVersion: this.options.agentVersion,
          hostname: this.options.hostname,
        }),
      )
    })

    socket.on('message', (data) => this.onMessage(data.toString()))

    socket.on('close', (code) => {
      this.socket = null
      this.helloAcked = false
      if (this.heartbeatTimer) clearInterval(this.heartbeatTimer)
      this.heartbeatTimer = null

      if (code === CLOSE_UNAUTHORIZED) {
        this.emitter.emit('status', 'unauthorized')
        // Không hẹn kết nối lại: khoá sai thì thử lại bao nhiêu cũng vô ích.
        return
      }
      this.emitter.emit('status', 'disconnected')
      this.scheduleReconnect()
    })

    socket.on('error', () => {
      // 'close' luôn theo sau 'error'; xử lý ở đó cho gọn.
    })
  }

  private onMessage(text: string): void {
    let message: CoreToAgent
    try {
      message = JSON.parse(text) as CoreToAgent
    } catch {
      return
    }

    if (message.type === 'ack') {
      if (!this.helloAcked) {
        this.helloAcked = true
        this.emitter.emit('status', 'connected')
        this.startHeartbeat()
        this.pump()
        return
      }
      const rowId = this.inFlight.get(message.messageId)
      if (rowId !== undefined) {
        this.options.outbox.ack(rowId)
        this.inFlight.delete(message.messageId)
      }
      this.pump()
      return
    }

    if (message.type === 'nack') {
      const rowId = this.inFlight.get(message.messageId)
      this.inFlight.delete(message.messageId)
      // Message hỏng vĩnh viễn thì bỏ khỏi hàng đợi, nếu không nó chặn mọi
      // message phía sau mãi mãi.
      if (rowId !== undefined && !message.retryable) this.options.outbox.ack(rowId)
      this.pump()
    }
  }

  private startHeartbeat(): void {
    this.heartbeatTimer = setInterval(() => {
      if (!this.isConnected()) return
      this.socket?.send(
        JSON.stringify({
          type: 'heartbeat',
          messageId: newMessageId(),
          sentAt: new Date().toISOString(),
          queueDepth: this.options.outbox.depth(),
        }),
      )
    }, HEARTBEAT_INTERVAL_MS)
  }

  /** Đẩy các message đang chờ lên Core. Gọi lại được nhiều lần, an toàn. */
  pump(): void {
    if (!this.isConnected()) return
    const pending = this.options.outbox.peek(this.batchSize)
    for (const entry of pending) {
      if (this.inFlight.has(entry.message.messageId)) continue
      this.inFlight.set(entry.message.messageId, entry.rowId)
      this.socket?.send(JSON.stringify(entry.message))
    }
  }
}
```

- [ ] **Step 7: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/agent test src/main/core-client.test.ts`
Expected: 6 test PASS.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(agent): durable outbox and resilient core websocket client"
```

---

## Task 14: Agent — DeviceManager ghép transport, driver và outbox

**Files:**
- Create: `apps/agent/src/main/device-manager.ts`, `apps/agent/src/main/ipc.ts`
- Modify: `apps/agent/src/main/index.ts`
- Test: `apps/agent/src/main/device-manager.test.ts`

**Interfaces:**
- Consumes: Task 10 (`createTransport`, `Transport`), Task 12 (`driverRegistry`), Task 13 (`Outbox`, `CoreClient`)
- Produces:
  - `DeviceManager` với `applyConfig(devices: DeviceConfig[]): Promise<void>`, `stopAll(): Promise<void>`, `statusOf(deviceKey: string): TransportStatus['status']`
  - `DeviceManager.testConnection(config: DeviceConfig): Promise<{ ok: boolean; detail: string }>`
  - sự kiện `'traffic'` → `{ deviceKey: string; direction: 'inbound' | 'outbound'; data: Buffer }` — dùng cho tab Terminal thô
  - Kênh IPC: `agent:get-config`, `agent:save-core-settings`, `agent:list-serial-ports`, `agent:upsert-device`, `agent:remove-device`, `agent:test-connection`, `agent:status`, `agent:traffic`

**Đây là nơi duy nhất ba lớp gặp nhau.** Transport không biết giao thức, driver không biết mạng, outbox không biết nội dung. `DeviceManager` nối chúng và không chứa logic giao thức nào của riêng nó.

- [ ] **Step 1: Viết test**

`apps/agent/src/main/device-manager.test.ts`:
```ts
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { AstmSimulator } from '../../../../tools/simulator/src/simulator.js'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { DeviceConfig } from './config-store.js'
import { DeviceManager } from './device-manager.js'
import { LocalDb } from './local-db.js'
import { Outbox } from './outbox.js'

let dir: string
let db: LocalDb
let outbox: Outbox
let manager: DeviceManager
let sim: AstmSimulator
let simPort: number

async function waitFor(check: () => boolean, timeoutMs = 8000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (check()) return
    await new Promise((r) => setTimeout(r, 30))
  }
  throw new Error('hết giờ chờ điều kiện')
}

function deviceConfig(port: number): DeviceConfig {
  return {
    deviceKey: 'may-sinh-hoa',
    name: 'Máy sinh hóa',
    driver: 'astm-e1394',
    enabled: true,
    transport: { kind: 'tcp', role: 'client', host: '127.0.0.1', port },
  }
}

beforeEach(async () => {
  dir = mkdtempSync(join(tmpdir(), 'lis-dm-'))
  db = LocalDb.open(join(dir, 'agent.sqlite'))
  outbox = new Outbox(db)
  manager = new DeviceManager(outbox, () => undefined)

  sim = new AstmSimulator({ misbehaviour: 'none' })
  simPort = await sim.start(0)
})

afterEach(async () => {
  await manager.stopAll()
  await sim.stop()
  db.close()
  rmSync(dir, { recursive: true, force: true })
})

describe('DeviceManager', () => {
  it('mở kết nối tới máy và ghi connection event vào hàng đợi', async () => {
    await manager.applyConfig([deviceConfig(simPort)])

    await waitFor(() => outbox.depth() > 0)
    const messages = outbox.peek(10).map((e) => e.message)
    expect(messages.some((m) => m.type === 'connection_event')).toBe(true)
  })

  it('đưa raw message vào hàng đợi khi máy gửi kết quả', async () => {
    await manager.applyConfig([deviceConfig(simPort)])
    await waitFor(() => manager.statusOf('may-sinh-hoa') === 'connected')

    await sim.sendResults([{ sid: 'SID001', testCode: 'GLU', value: '5.4', unit: 'mmol/L' }])

    await waitFor(() => outbox.peek(50).some((e) => e.message.type === 'raw_message'))
    const raw = outbox.peek(50).find((e) => e.message.type === 'raw_message')
    expect(raw).toBeDefined()
    if (raw?.message.type !== 'raw_message') throw new Error('sai loại message')
    const payload = Buffer.from(raw.message.payloadBase64, 'base64').toString('latin1')
    expect(payload).toContain('SID001')
  })

  it('phát sự kiện traffic cho tab Terminal thô', async () => {
    const traffic: Array<{ deviceKey: string; direction: string }> = []
    manager.on('traffic', (event) => traffic.push(event))

    await manager.applyConfig([deviceConfig(simPort)])
    await waitFor(() => manager.statusOf('may-sinh-hoa') === 'connected')
    await sim.sendResults([{ sid: 'SID002', testCode: 'URE', value: '6.1', unit: 'mmol/L' }])

    await waitFor(() => traffic.some((t) => t.direction === 'inbound'))
    expect(traffic.some((t) => t.direction === 'outbound')).toBe(true)
  })

  it('không mở máy bị tắt (enabled=false)', async () => {
    await manager.applyConfig([{ ...deviceConfig(simPort), enabled: false }])
    await new Promise((r) => setTimeout(r, 300))
    expect(manager.statusOf('may-sinh-hoa')).toBe('disconnected')
  })

  it('applyConfig lần hai đóng máy đã bị gỡ khỏi cấu hình', async () => {
    await manager.applyConfig([deviceConfig(simPort)])
    await waitFor(() => manager.statusOf('may-sinh-hoa') === 'connected')

    await manager.applyConfig([])
    expect(manager.statusOf('may-sinh-hoa')).toBe('disconnected')
  })

  it('testConnection báo ok khi mở được cổng', async () => {
    const result = await manager.testConnection(deviceConfig(simPort))
    expect(result.ok).toBe(true)
  })

  it('testConnection báo lỗi kèm lý do khi không mở được', async () => {
    const result = await manager.testConnection(deviceConfig(1))
    expect(result.ok).toBe(false)
    expect(result.detail.length).toBeGreaterThan(0)
  })

  it('vẫn đưa raw message vào hàng đợi khi driver báo checksum sai', async () => {
    await sim.stop()
    sim = new AstmSimulator({ misbehaviour: 'bad-checksum' })
    simPort = await sim.start(0)

    await manager.applyConfig([deviceConfig(simPort)])
    await waitFor(() => manager.statusOf('may-sinh-hoa') === 'connected')
    await sim.sendResults([{ sid: 'SID003', testCode: 'CRE', value: '88', unit: 'umol/L' }])

    await waitFor(() => outbox.peek(50).some((e) => e.message.type === 'raw_message'))
    // Dữ liệu hỏng vẫn phải được lưu: không bao giờ vứt byte từ máy xét nghiệm.
    expect(outbox.peek(50).some((e) => e.message.type === 'raw_message')).toBe(true)
  })
})
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/agent test src/main/device-manager.test.ts`
Expected: FAIL — không tìm thấy `./device-manager.js`.

- [ ] **Step 3: Viết `device-manager.ts`**

`apps/agent/src/main/device-manager.ts`:
```ts
import { EventEmitter } from 'node:events'
import { newMessageId } from '@lis/shared'
import type { DeviceConfig } from './config-store.js'
import { driverRegistry } from './drivers/registry.js'
import type { Driver } from './drivers/types.js'
import type { Outbox } from './outbox.js'
import { createTransport } from './transports/factory.js'
import type { Transport, TransportStatus } from './transports/types.js'

interface RunningDevice {
  config: DeviceConfig
  transport: Transport
  driver: Driver
  status: TransportStatus['status']
}

export interface TrafficEvent {
  deviceKey: string
  direction: 'inbound' | 'outbound'
  data: Buffer
}

/**
 * Nơi duy nhất ba lớp gặp nhau: transport chuyển byte, driver hiểu giao thức,
 * outbox giữ message cho tới khi Core xác nhận. DeviceManager chỉ nối chúng lại
 * và không chứa logic giao thức nào của riêng nó.
 */
export class DeviceManager {
  private readonly emitter = new EventEmitter()
  private readonly devices = new Map<string, RunningDevice>()

  constructor(
    private readonly outbox: Outbox,
    /** Gọi mỗi khi có message mới, để CoreClient bơm hàng đợi ngay. */
    private readonly notifyOutboxChanged: () => void,
  ) {}

  on(event: 'traffic', handler: (event: TrafficEvent) => void): void {
    this.emitter.on(event, handler)
  }

  statusOf(deviceKey: string): TransportStatus['status'] {
    return this.devices.get(deviceKey)?.status ?? 'disconnected'
  }

  async applyConfig(configs: DeviceConfig[]): Promise<void> {
    const wanted = new Set(configs.filter((c) => c.enabled).map((c) => c.deviceKey))

    for (const [key, running] of this.devices) {
      if (wanted.has(key)) continue
      await running.transport.close()
      this.devices.delete(key)
    }

    for (const config of configs) {
      if (!config.enabled) continue
      if (this.devices.has(config.deviceKey)) continue
      await this.startDevice(config)
    }
  }

  private enqueue(message: Parameters<Outbox['enqueue']>[0]): void {
    this.outbox.enqueue(message)
    this.notifyOutboxChanged()
  }

  private async startDevice(config: DeviceConfig): Promise<void> {
    const transport = createTransport(config.transport)
    const driver = driverRegistry.get(config.driver)
    const running: RunningDevice = { config, transport, driver, status: 'disconnected' }
    this.devices.set(config.deviceKey, running)

    transport.on('status', (status) => {
      running.status = status.status
      this.enqueue({
        type: 'connection_event',
        messageId: newMessageId(),
        sentAt: new Date().toISOString(),
        deviceId: config.deviceKey,
        status: status.status,
        ...(status.detail ? { detail: status.detail } : {}),
      })
      if (status.status !== 'connected') driver.reset()
    })

    transport.on('data', (chunk) => {
      this.emitter.emit('traffic', {
        deviceKey: config.deviceKey,
        direction: 'inbound',
        data: chunk,
      })
      this.handleDriverEvents(running, chunk)
    })

    try {
      await transport.open()
    } catch (error) {
      running.status = 'error'
      this.enqueue({
        type: 'connection_event',
        messageId: newMessageId(),
        sentAt: new Date().toISOString(),
        deviceId: config.deviceKey,
        status: 'error',
        detail: error instanceof Error ? error.message : String(error),
      })
    }
  }

  private handleDriverEvents(running: RunningDevice, chunk: Buffer): void {
    for (const event of running.driver.feed(chunk)) {
      if (event.kind === 'reply') {
        void running.transport.write(event.data).catch(() => undefined)
        this.emitter.emit('traffic', {
          deviceKey: running.config.deviceKey,
          direction: 'outbound',
          data: event.data,
        })
        continue
      }

      // Cả message parse được lẫn dữ liệu hỏng đều được đẩy lên Core dưới dạng
      // raw_message: không bao giờ vứt byte từ máy xét nghiệm.
      const raw = event.kind === 'message' ? event.raw : event.raw
      this.enqueue({
        type: 'raw_message',
        messageId: newMessageId(),
        sentAt: new Date().toISOString(),
        deviceId: running.config.deviceKey,
        direction: 'inbound',
        payloadBase64: raw.toString('base64'),
      })
    }
  }

  /** Mở thử kết nối rồi đóng ngay, phục vụ nút "Test kết nối" trong giao diện. */
  async testConnection(config: DeviceConfig): Promise<{ ok: boolean; detail: string }> {
    const transport = createTransport(config.transport)
    try {
      await transport.open()
      await transport.close()
      return { ok: true, detail: 'mở kết nối thành công' }
    } catch (error) {
      return { ok: false, detail: error instanceof Error ? error.message : String(error) }
    }
  }

  async stopAll(): Promise<void> {
    for (const running of this.devices.values()) await running.transport.close()
    this.devices.clear()
  }
}
```

- [ ] **Step 4: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/agent test src/main/device-manager.test.ts`
Expected: 8 test PASS.

- [ ] **Step 5: Viết lớp IPC nối main với renderer**

`apps/agent/src/main/ipc.ts`:
```ts
import { type BrowserWindow, ipcMain } from 'electron'
import type { AgentConfig, ConfigStore, DeviceConfig } from './config-store.js'
import type { CoreClient } from './core-client.js'
import type { DeviceManager } from './device-manager.js'
import type { Outbox } from './outbox.js'
import { listSerialPorts } from './transports/serial.js'

export interface IpcDeps {
  configStore: ConfigStore
  deviceManager: DeviceManager
  outbox: Outbox
  getCoreClient: () => CoreClient | null
  restartCoreClient: () => void
  getWindow: () => BrowserWindow | null
}

export function registerIpc(deps: IpcDeps): void {
  ipcMain.handle('agent:get-config', () => deps.configStore.load())

  ipcMain.handle('agent:save-core-settings', (_event, payload: Partial<AgentConfig>) => {
    const config = deps.configStore.load()
    deps.configStore.save({
      ...config,
      coreUrl: payload.coreUrl ?? config.coreUrl,
      agentKey: payload.agentKey ?? config.agentKey,
      proxyUrl: payload.proxyUrl ?? config.proxyUrl,
    })
    deps.restartCoreClient()
    return deps.configStore.load()
  })

  ipcMain.handle('agent:list-serial-ports', () => listSerialPorts())

  ipcMain.handle('agent:upsert-device', async (_event, device: DeviceConfig) => {
    deps.configStore.upsertDevice(device)
    await deps.deviceManager.applyConfig(deps.configStore.load().devices)
    return deps.configStore.load()
  })

  ipcMain.handle('agent:remove-device', async (_event, deviceKey: string) => {
    deps.configStore.removeDevice(deviceKey)
    await deps.deviceManager.applyConfig(deps.configStore.load().devices)
    return deps.configStore.load()
  })

  ipcMain.handle('agent:test-connection', (_event, device: DeviceConfig) =>
    deps.deviceManager.testConnection(device),
  )

  ipcMain.handle('agent:status', () => ({
    coreConnected: deps.getCoreClient()?.isConnected() ?? false,
    queueDepth: deps.outbox.depth(),
    devices: deps.configStore.load().devices.map((d) => ({
      deviceKey: d.deviceKey,
      name: d.name,
      status: deps.deviceManager.statusOf(d.deviceKey),
    })),
  }))

  // Đẩy lưu lượng byte lên renderer cho tab Terminal thô.
  deps.deviceManager.on('traffic', (event) => {
    deps.getWindow()?.webContents.send('agent:traffic', {
      deviceKey: event.deviceKey,
      direction: event.direction,
      hex: event.data.toString('hex'),
      at: new Date().toISOString(),
    })
  })
}
```

- [ ] **Step 6: Nối tất cả trong `index.ts`**

`apps/agent/src/main/index.ts` (thay toàn bộ nội dung cũ):
```ts
import { join } from 'node:path'
import { hostname } from 'node:os'
import { BrowserWindow, Tray, app, nativeImage } from 'electron'
import { ConfigStore } from './config-store.js'
import { CoreClient } from './core-client.js'
import { DeviceManager } from './device-manager.js'
import { registerIpc } from './ipc.js'
import { LocalDb } from './local-db.js'
import { Outbox } from './outbox.js'

const AGENT_VERSION = app.getVersion()
const QUEUE_RETENTION_DAYS = 7

let window: BrowserWindow | null = null
let tray: Tray | null = null
let coreClient: CoreClient | null = null

const configStore = new ConfigStore(join(app.getPath('userData'), 'config.json'))
const localDb = LocalDb.open(join(app.getPath('userData'), 'agent.sqlite'))
const outbox = new Outbox(localDb)
const deviceManager = new DeviceManager(outbox, () => coreClient?.pump())

function restartCoreClient(): void {
  const config = configStore.load()
  void coreClient?.stop()
  coreClient = null
  if (!config.coreUrl || !config.agentKey) return

  coreClient = new CoreClient({
    url: config.coreUrl,
    agentKey: config.agentKey,
    agentVersion: AGENT_VERSION,
    hostname: hostname(),
    outbox,
  })
  coreClient.on('status', (status) => {
    window?.webContents.send('agent:core-status', status)
  })
  coreClient.start()
}

function createWindow(): void {
  if (window) {
    window.show()
    return
  }
  window = new BrowserWindow({
    width: 1100,
    height: 720,
    title: 'LIS Connect Agent',
    webPreferences: { preload: join(__dirname, '../preload/index.js'), sandbox: false },
  })
  window.on('closed', () => {
    window = null
  })

  if (process.env.ELECTRON_RENDERER_URL) {
    void window.loadURL(process.env.ELECTRON_RENDERER_URL)
  } else {
    void window.loadFile(join(__dirname, '../renderer/index.html'))
  }
}

app.whenReady().then(async () => {
  registerIpc({
    configStore,
    deviceManager,
    outbox,
    getCoreClient: () => coreClient,
    restartCoreClient,
    getWindow: () => window,
  })

  outbox.purgeOlderThan(QUEUE_RETENTION_DAYS)
  await deviceManager.applyConfig(configStore.load().devices)
  restartCoreClient()

  createWindow()
  tray = new Tray(nativeImage.createEmpty())
  tray.setToolTip('LIS Connect Agent')
  tray.on('click', createWindow)
})

// Đóng cửa sổ KHÔNG thoát ứng dụng: main process phải chạy tiếp để giữ kết nối
// tới máy xét nghiệm.
app.on('window-all-closed', () => {
  // cố ý để trống
})

app.on('before-quit', () => {
  void deviceManager.stopAll()
  void coreClient?.stop()
  localDb.close()
})
```

- [ ] **Step 7: Chạy toàn bộ test của agent**

Run: `pnpm --filter @lis/agent test`
Expected: toàn bộ test PASS.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(agent): device manager wiring transports, drivers and outbox"
```

---

## Task 15: Agent — giao diện ba tab

> **Bắt buộc trước khi bắt đầu:** gọi skill `ui-ux-pro-max` và thiết kế giao diện
> theo hướng dẫn nó trả về. JSX và CSS dưới đây là bản dựng chức năng, dùng để chốt
> luồng dữ liệu và hợp đồng IPC — giữ nguyên tên kênh IPC, tên trường và hành vi,
> nhưng phần trình bày làm lại theo skill.
>
> Bối cảnh cần nói với skill: đây là công cụ kỹ thuật dùng trong phòng xét nghiệm,
> người dùng là kỹ thuật viên triển khai, thường dùng trên màn hình nhỏ và độ phân
> giải thấp, cần đọc được trạng thái kết nối trong một cái liếc, và tab Terminal thô
> phải dễ đọc khi chữ chạy liên tục.

**Files:**
- Create: `apps/agent/src/renderer/api.ts`, `apps/agent/src/renderer/App.tsx`
- Create: `apps/agent/src/renderer/tabs/CoreTab.tsx`, `DevicesTab.tsx`, `TerminalTab.tsx`
- Create: `apps/agent/src/renderer/styles.css`
- Create: `apps/agent/src/renderer/env.d.ts`

**Interfaces:**
- Consumes: Task 14 (các kênh IPC), Task 9 (`AgentConfig`, `DeviceConfig`)
- Produces: giao diện cấu hình tại chỗ với ba tab của giai đoạn 1 (tab Nhật ký thuộc giai đoạn sau)

Giai đoạn 1 làm ba tab: **Kết nối Core**, **Máy xét nghiệm**, **Terminal thô**. Tab Nhật ký để giai đoạn 2, khi đã có gì đáng ghi ngoài lưu lượng byte.

- [ ] **Step 1: Khai báo kiểu cho cầu nối preload**

`apps/agent/src/renderer/env.d.ts`:
```ts
interface AgentBridge {
  invoke: <T>(channel: string, payload?: unknown) => Promise<T>
  on: (channel: string, handler: (payload: unknown) => void) => () => void
}

interface Window {
  agent: AgentBridge
}
```

`apps/agent/src/renderer/api.ts`:
```ts
import type { AgentConfig, DeviceConfig } from '../main/config-store.js'

export interface SerialPortInfo {
  path: string
  manufacturer: string | null
  friendlyName: string | null
}

export interface AgentStatus {
  coreConnected: boolean
  queueDepth: number
  devices: Array<{ deviceKey: string; name: string; status: string }>
}

export interface TrafficLine {
  deviceKey: string
  direction: 'inbound' | 'outbound'
  hex: string
  at: string
}

export const api = {
  getConfig: () => window.agent.invoke<AgentConfig>('agent:get-config'),
  saveCoreSettings: (payload: { coreUrl: string; agentKey: string; proxyUrl: string | null }) =>
    window.agent.invoke<AgentConfig>('agent:save-core-settings', payload),
  listSerialPorts: () => window.agent.invoke<SerialPortInfo[]>('agent:list-serial-ports'),
  upsertDevice: (device: DeviceConfig) =>
    window.agent.invoke<AgentConfig>('agent:upsert-device', device),
  removeDevice: (deviceKey: string) =>
    window.agent.invoke<AgentConfig>('agent:remove-device', deviceKey),
  testConnection: (device: DeviceConfig) =>
    window.agent.invoke<{ ok: boolean; detail: string }>('agent:test-connection', device),
  status: () => window.agent.invoke<AgentStatus>('agent:status'),
  onTraffic: (handler: (line: TrafficLine) => void) =>
    window.agent.on('agent:traffic', (payload) => handler(payload as TrafficLine)),
  onCoreStatus: (handler: (status: string) => void) =>
    window.agent.on('agent:core-status', (payload) => handler(payload as string)),
}
```

- [ ] **Step 2: Viết khung App với ba tab**

`apps/agent/src/renderer/App.tsx`:
```tsx
import { useEffect, useState } from 'react'
import { api, type AgentStatus } from './api.js'
import { CoreTab } from './tabs/CoreTab.js'
import { DevicesTab } from './tabs/DevicesTab.js'
import { TerminalTab } from './tabs/TerminalTab.js'
import './styles.css'

type TabKey = 'core' | 'devices' | 'terminal'

const TABS: Array<{ key: TabKey; label: string }> = [
  { key: 'core', label: 'Kết nối Core' },
  { key: 'devices', label: 'Máy xét nghiệm' },
  { key: 'terminal', label: 'Terminal thô' },
]

export function App(): JSX.Element {
  const [tab, setTab] = useState<TabKey>('core')
  const [status, setStatus] = useState<AgentStatus | null>(null)

  useEffect(() => {
    const refresh = (): void => {
      void api.status().then(setStatus)
    }
    refresh()
    const timer = setInterval(refresh, 2000)
    return () => clearInterval(timer)
  }, [])

  return (
    <div className="app">
      <header className="app-header">
        <h1>LIS Connect Agent</h1>
        <div className="status-strip">
          <span className={status?.coreConnected ? 'dot dot-green' : 'dot dot-red'} />
          <span>{status?.coreConnected ? 'Đã nối Core' : 'Chưa nối Core'}</span>
          <span className="queue">Hàng đợi: {status?.queueDepth ?? 0}</span>
        </div>
      </header>

      <nav className="tabs">
        {TABS.map((item) => (
          <button
            key={item.key}
            className={tab === item.key ? 'tab tab-active' : 'tab'}
            onClick={() => setTab(item.key)}
          >
            {item.label}
          </button>
        ))}
      </nav>

      <main className="content">
        {tab === 'core' && <CoreTab />}
        {tab === 'devices' && <DevicesTab status={status} />}
        {tab === 'terminal' && <TerminalTab />}
      </main>
    </div>
  )
}
```

- [ ] **Step 3: Viết tab Kết nối Core**

`apps/agent/src/renderer/tabs/CoreTab.tsx`:
```tsx
import { useEffect, useState } from 'react'
import { api } from '../api.js'

export function CoreTab(): JSX.Element {
  const [coreUrl, setCoreUrl] = useState('')
  const [agentKey, setAgentKey] = useState('')
  const [proxyUrl, setProxyUrl] = useState('')
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    void api.getConfig().then((config) => {
      setCoreUrl(config.coreUrl)
      setAgentKey(config.agentKey)
      setProxyUrl(config.proxyUrl ?? '')
    })
    return api.onCoreStatus((status) => {
      if (status === 'connected') setMessage('Đã kết nối tới Core.')
      if (status === 'disconnected') setMessage('Mất kết nối tới Core, đang thử lại.')
      if (status === 'unauthorized') {
        setMessage('Khoá Agent không hợp lệ hoặc đã bị thu hồi. Vui lòng nhập lại khoá.')
      }
    })
  }, [])

  const save = async (): Promise<void> => {
    setMessage('Đang lưu và kết nối lại...')
    await api.saveCoreSettings({
      coreUrl: coreUrl.trim(),
      agentKey: agentKey.trim(),
      proxyUrl: proxyUrl.trim() === '' ? null : proxyUrl.trim(),
    })
  }

  return (
    <section className="panel">
      <h2>Kết nối tới Core</h2>

      <label className="field">
        <span>Địa chỉ Core</span>
        <input
          value={coreUrl}
          placeholder="wss://core.example.vn/agent"
          onChange={(e) => setCoreUrl(e.target.value)}
        />
      </label>

      <label className="field">
        <span>Khoá Agent</span>
        <input
          value={agentKey}
          placeholder="lis_agent_..."
          onChange={(e) => setAgentKey(e.target.value)}
        />
      </label>

      <label className="field">
        <span>Proxy (để trống nếu không dùng)</span>
        <input
          value={proxyUrl}
          placeholder="http://proxy.benhvien.local:3128"
          onChange={(e) => setProxyUrl(e.target.value)}
        />
      </label>

      <button className="primary" onClick={() => void save()}>
        Lưu và kiểm tra kết nối
      </button>

      {message && <p className="message">{message}</p>}
    </section>
  )
}
```

- [ ] **Step 4: Viết tab Máy xét nghiệm**

`apps/agent/src/renderer/tabs/DevicesTab.tsx`:
```tsx
import { useEffect, useState } from 'react'
import type { DeviceConfig, TransportConfig } from '../../main/config-store.js'
import { api, type AgentStatus, type SerialPortInfo } from '../api.js'

const EMPTY_DEVICE: DeviceConfig = {
  deviceKey: '',
  name: '',
  driver: 'astm-e1394',
  enabled: true,
  transport: { kind: 'serial', path: '', baudRate: 9600, dataBits: 8, parity: 'none', stopBits: 1 },
}

function statusLabel(status: string): string {
  if (status === 'connected') return 'Đang kết nối'
  if (status === 'error') return 'Lỗi'
  return 'Mất kết nối'
}

export function DevicesTab({ status }: { status: AgentStatus | null }): JSX.Element {
  const [devices, setDevices] = useState<DeviceConfig[]>([])
  const [ports, setPorts] = useState<SerialPortInfo[]>([])
  const [draft, setDraft] = useState<DeviceConfig>(EMPTY_DEVICE)
  const [testResult, setTestResult] = useState<string | null>(null)

  useEffect(() => {
    void api.getConfig().then((config) => setDevices(config.devices))
    void api.listSerialPorts().then(setPorts)
  }, [])

  const updateTransport = (transport: TransportConfig): void =>
    setDraft((current) => ({ ...current, transport }))

  const save = async (): Promise<void> => {
    const config = await api.upsertDevice(draft)
    setDevices(config.devices)
    setDraft(EMPTY_DEVICE)
    setTestResult(null)
  }

  const remove = async (deviceKey: string): Promise<void> => {
    const config = await api.removeDevice(deviceKey)
    setDevices(config.devices)
  }

  const test = async (): Promise<void> => {
    setTestResult('Đang thử mở kết nối...')
    const result = await api.testConnection(draft)
    setTestResult(result.ok ? `Thành công: ${result.detail}` : `Thất bại: ${result.detail}`)
  }

  return (
    <section className="panel">
      <h2>Máy xét nghiệm đã khai báo</h2>
      <table className="table">
        <thead>
          <tr>
            <th>Tên</th>
            <th>Mã máy</th>
            <th>Kết nối</th>
            <th>Trạng thái</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {devices.map((device) => {
            const current =
              status?.devices.find((d) => d.deviceKey === device.deviceKey)?.status ??
              'disconnected'
            return (
              <tr key={device.deviceKey}>
                <td>{device.name}</td>
                <td>{device.deviceKey}</td>
                <td>{device.transport.kind}</td>
                <td>
                  <span className={current === 'connected' ? 'dot dot-green' : 'dot dot-red'} />
                  {statusLabel(current)}
                </td>
                <td>
                  <button onClick={() => void remove(device.deviceKey)}>Xoá</button>
                </td>
              </tr>
            )
          })}
          {devices.length === 0 && (
            <tr>
              <td colSpan={5}>Chưa khai báo máy nào.</td>
            </tr>
          )}
        </tbody>
      </table>

      <h2>Thêm hoặc sửa máy</h2>

      <label className="field">
        <span>Mã máy (không dấu, dùng để nhận diện)</span>
        <input
          value={draft.deviceKey}
          placeholder="may-sinh-hoa"
          onChange={(e) => setDraft({ ...draft, deviceKey: e.target.value })}
        />
      </label>

      <label className="field">
        <span>Tên hiển thị</span>
        <input
          value={draft.name}
          placeholder="Máy sinh hóa BS-240"
          onChange={(e) => setDraft({ ...draft, name: e.target.value })}
        />
      </label>

      <label className="field">
        <span>Kiểu kết nối</span>
        <select
          value={draft.transport.kind}
          onChange={(e) => {
            const kind = e.target.value as TransportConfig['kind']
            if (kind === 'serial') {
              updateTransport({
                kind: 'serial',
                path: ports[0]?.path ?? '',
                baudRate: 9600,
                dataBits: 8,
                parity: 'none',
                stopBits: 1,
              })
            } else if (kind === 'tcp') {
              updateTransport({ kind: 'tcp', role: 'client', host: '192.168.1.50', port: 9100 })
            } else {
              updateTransport({
                kind: 'file',
                directory: '',
                pattern: '*.txt',
                deleteAfterRead: false,
              })
            }
          }}
        >
          <option value="serial">Serial (cổng COM)</option>
          <option value="tcp">TCP/IP</option>
          <option value="file">Thư mục file</option>
        </select>
      </label>

      {draft.transport.kind === 'serial' && (
        <>
          <label className="field">
            <span>Cổng COM</span>
            <select
              value={draft.transport.path}
              onChange={(e) =>
                updateTransport({ ...draft.transport, kind: 'serial', path: e.target.value })
              }
            >
              <option value="">-- chọn cổng --</option>
              {ports.map((port) => (
                <option key={port.path} value={port.path}>
                  {port.path}
                  {port.friendlyName ? ` — ${port.friendlyName}` : ''}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Baudrate</span>
            <select
              value={draft.transport.baudRate}
              onChange={(e) =>
                updateTransport({
                  ...draft.transport,
                  kind: 'serial',
                  baudRate: Number(e.target.value),
                })
              }
            >
              {[1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200].map((rate) => (
                <option key={rate} value={rate}>
                  {rate}
                </option>
              ))}
            </select>
          </label>
        </>
      )}

      {draft.transport.kind === 'tcp' && (
        <>
          <label className="field">
            <span>Vai trò</span>
            <select
              value={draft.transport.role}
              onChange={(e) =>
                updateTransport({
                  ...draft.transport,
                  kind: 'tcp',
                  role: e.target.value as 'client' | 'server',
                })
              }
            >
              <option value="client">Agent nối tới máy</option>
              <option value="server">Máy nối vào Agent</option>
            </select>
          </label>
          <label className="field">
            <span>Địa chỉ IP</span>
            <input
              value={draft.transport.host}
              onChange={(e) =>
                updateTransport({ ...draft.transport, kind: 'tcp', host: e.target.value })
              }
            />
          </label>
          <label className="field">
            <span>Cổng</span>
            <input
              type="number"
              value={draft.transport.port}
              onChange={(e) =>
                updateTransport({
                  ...draft.transport,
                  kind: 'tcp',
                  port: Number(e.target.value),
                })
              }
            />
          </label>
        </>
      )}

      {draft.transport.kind === 'file' && (
        <>
          <label className="field">
            <span>Thư mục</span>
            <input
              value={draft.transport.directory}
              placeholder="D:\\ketqua"
              onChange={(e) =>
                updateTransport({ ...draft.transport, kind: 'file', directory: e.target.value })
              }
            />
          </label>
          <label className="field">
            <span>Mẫu tên file</span>
            <input
              value={draft.transport.pattern}
              onChange={(e) =>
                updateTransport({ ...draft.transport, kind: 'file', pattern: e.target.value })
              }
            />
          </label>
          <label className="field checkbox">
            <input
              type="checkbox"
              checked={draft.transport.deleteAfterRead}
              onChange={(e) =>
                updateTransport({
                  ...draft.transport,
                  kind: 'file',
                  deleteAfterRead: e.target.checked,
                })
              }
            />
            <span>Xoá file sau khi đọc</span>
          </label>
        </>
      )}

      <div className="actions">
        <button onClick={() => void test()}>Test kết nối</button>
        <button className="primary" onClick={() => void save()}>
          Lưu máy
        </button>
      </div>

      {testResult && <p className="message">{testResult}</p>}
    </section>
  )
}
```

- [ ] **Step 5: Viết tab Terminal thô**

`apps/agent/src/renderer/tabs/TerminalTab.tsx`:
```tsx
import { useEffect, useRef, useState } from 'react'
import { api, type TrafficLine } from '../api.js'

const MAX_LINES = 500

function hexToText(hex: string): string {
  const buffer = Buffer.from(hex, 'hex')
  return [...buffer]
    .map((byte) => (byte >= 32 && byte < 127 ? String.fromCharCode(byte) : '.'))
    .join('')
}

function hexPretty(hex: string): string {
  return (hex.match(/.{1,2}/g) ?? []).join(' ').toUpperCase()
}

export function TerminalTab(): JSX.Element {
  const [lines, setLines] = useState<TrafficLine[]>([])
  const [filter, setFilter] = useState('')
  const [showHex, setShowHex] = useState(false)
  const bottomRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    return api.onTraffic((line) => {
      setLines((current) => [...current, line].slice(-MAX_LINES))
    })
  }, [])

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [lines])

  const visible = filter
    ? lines.filter((line) => line.deviceKey.includes(filter))
    : lines

  return (
    <section className="panel">
      <h2>Terminal thô</h2>
      <p className="hint">
        Xem toàn bộ byte vào và ra giữa Agent và máy xét nghiệm. Dùng khi dò một máy chưa
        có driver.
      </p>

      <div className="actions">
        <input
          value={filter}
          placeholder="Lọc theo mã máy"
          onChange={(e) => setFilter(e.target.value)}
        />
        <label className="checkbox">
          <input type="checkbox" checked={showHex} onChange={(e) => setShowHex(e.target.checked)} />
          <span>Hiện dạng hex</span>
        </label>
        <button onClick={() => setLines([])}>Xoá màn hình</button>
      </div>

      <pre className="terminal">
        {visible.map((line, index) => (
          <div key={index} className={line.direction === 'inbound' ? 'line-in' : 'line-out'}>
            <span className="ts">{new Date(line.at).toLocaleTimeString('vi-VN')}</span>
            <span className="dir">{line.direction === 'inbound' ? '<<' : '>>'}</span>
            <span className="dev">{line.deviceKey}</span>
            <span className="payload">{showHex ? hexPretty(line.hex) : hexToText(line.hex)}</span>
          </div>
        ))}
        <div ref={bottomRef} />
      </pre>
    </section>
  )
}
```

`Buffer` không có sẵn trong renderer của Electron khi `sandbox: false` nhưng `nodeIntegration` tắt. Thêm vào `electron.vite.config.ts` để Vite cung cấp polyfill:

```ts
renderer: {
  plugins: [react()],
  define: { global: 'globalThis' },
  resolve: { alias: { buffer: 'buffer/' } },
}
```

Run: `pnpm --filter @lis/agent add buffer@6.0.3` và thêm `import { Buffer } from 'buffer'` ở đầu `TerminalTab.tsx`.

- [ ] **Step 6: Viết CSS**

`apps/agent/src/renderer/styles.css`:
```css
:root {
  font-family: 'Segoe UI', system-ui, sans-serif;
  font-size: 14px;
  color: #1f2933;
}

body { margin: 0; background: #f5f7fa; }

.app { display: flex; flex-direction: column; height: 100vh; }

.app-header {
  display: flex; align-items: center; justify-content: space-between;
  padding: 12px 20px; background: #ffffff; border-bottom: 1px solid #e4e7eb;
}
.app-header h1 { font-size: 16px; margin: 0; }

.status-strip { display: flex; align-items: center; gap: 10px; font-size: 13px; }
.queue { color: #616e7c; }

.dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; margin-right: 6px; }
.dot-green { background: #2fa84f; }
.dot-red { background: #d64545; }

.tabs { display: flex; gap: 4px; padding: 0 20px; background: #ffffff; border-bottom: 1px solid #e4e7eb; }
.tab { border: none; background: none; padding: 10px 16px; cursor: pointer; font-size: 14px; color: #52606d; }
.tab-active { color: #1f2933; border-bottom: 2px solid #2563eb; font-weight: 600; }

.content { flex: 1; overflow: auto; padding: 20px; }

.panel { background: #ffffff; border: 1px solid #e4e7eb; border-radius: 6px; padding: 20px; max-width: 900px; }
.panel h2 { font-size: 15px; margin: 0 0 14px; }

.field { display: flex; flex-direction: column; gap: 4px; margin-bottom: 14px; max-width: 480px; }
.field span { font-size: 13px; color: #52606d; }
.field input, .field select { padding: 7px 9px; border: 1px solid #cbd2d9; border-radius: 4px; font-size: 14px; }
.checkbox { flex-direction: row; align-items: center; gap: 8px; }

.actions { display: flex; gap: 10px; align-items: center; margin: 14px 0; }
button { padding: 7px 14px; border: 1px solid #cbd2d9; background: #ffffff; border-radius: 4px; cursor: pointer; }
button.primary { background: #2563eb; border-color: #2563eb; color: #ffffff; }

.message { padding: 10px 12px; background: #f0f4f8; border-radius: 4px; font-size: 13px; }
.hint { color: #616e7c; font-size: 13px; }

.table { width: 100%; border-collapse: collapse; margin-bottom: 20px; }
.table th, .table td { text-align: left; padding: 8px 10px; border-bottom: 1px solid #e4e7eb; font-size: 13px; }

.terminal {
  background: #1f2933; color: #e4e7eb; padding: 12px; border-radius: 4px;
  height: 380px; overflow: auto; font-family: 'Cascadia Mono', Consolas, monospace; font-size: 12px;
}
.terminal .ts { color: #7b8794; margin-right: 8px; }
.terminal .dir { margin-right: 8px; }
.terminal .dev { color: #9aa5b1; margin-right: 8px; }
.line-in .dir { color: #6fcf97; }
.line-out .dir { color: #f2c94c; }
```

- [ ] **Step 7: Chạy thử toàn bộ đường đi bằng máy giả lập**

Chạy ba cửa sổ terminal:

```bash
# 1. Máy giả lập
SIM_PORT=9100 pnpm --filter @lis/simulator start

# 2. Core
cd apps/core && node --import tsx/esm --env-file=.env src/main.ts

# 3. Agent
pnpm --filter @lis/agent dev
```

Trong Agent Console:
1. Tab **Kết nối Core**: nhập `ws://localhost:3000/agent` và khoá agent (cấp bằng đoạn script ở Task 17 Step 1). Bấm Lưu. Đèn ở góc phải chuyển xanh.
2. Tab **Máy xét nghiệm**: thêm máy `may-sinh-hoa`, kiểu TCP, `127.0.0.1:9100`, driver `astm-e1394`. Bấm **Test kết nối** — báo thành công. Bấm **Lưu máy**.
3. Ở terminal của máy giả lập, nhấn Enter để gửi một phiên kết quả.
4. Tab **Terminal thô**: thấy các dòng `<<` (byte từ máy) và `>>` (ACK Agent gửi lại). Bật "Hiện dạng hex" để đối chiếu.

Expected: hàng đợi ở thanh trên về 0 sau vài giây, nghĩa là Core đã nhận và ack.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(agent): configuration ui with core, devices and raw terminal tabs"
```

---

## Task 16: Web UI — màn hình theo dõi máy và kết quả thô

> **Bắt buộc trước khi bắt đầu:** gọi skill `ui-ux-pro-max` và thiết kế giao diện
> theo hướng dẫn nó trả về. JSX và CSS dưới đây là bản dựng chức năng, dùng để chốt
> hợp đồng API — giữ nguyên đường dẫn API, tên trường và hành vi, nhưng phần trình
> bày làm lại theo skill.
>
> Bối cảnh cần nói với skill: đây là màn hình treo trong phòng xét nghiệm, thường
> hiện cả ngày trên một máy trạm; người dùng là kỹ thuật viên xét nghiệm, không phải
> dân kỹ thuật; thứ quan trọng nhất là **nhận ra ngay máy nào đang mất kết nối**, kể
> cả khi nhìn từ xa vài mét. Đây cũng là màn hình khách hàng nhìn thấy đầu tiên khi
> bạn demo, nên nó quyết định ấn tượng về sản phẩm.

**Files:**
- Create: `apps/web/package.json`, `apps/web/tsconfig.json`, `apps/web/vite.config.ts`, `apps/web/index.html`
- Create: `apps/web/src/main.tsx`, `apps/web/src/api.ts`, `apps/web/src/App.tsx`
- Create: `apps/web/src/screens/DeviceMonitor.tsx`, `apps/web/src/screens/DeviceDetail.tsx`
- Create: `apps/web/src/styles.css`

**Interfaces:**
- Consumes: Task 8 (`GET /api/devices`, `/raw-messages`, `/connection-events`)
- Produces: hai màn hình — danh sách máy với đèn trạng thái, và màn hình chi tiết một máy gồm log kết nối cùng dữ liệu thô

**Về chọn tenant ở giai đoạn 1:** chưa có đăng nhập, nên tenant lấy từ ô nhập ở góc trên và lưu vào `localStorage`. Giai đoạn 2 thay bằng đăng nhập thật; giữ nó ở một chỗ duy nhất (`api.ts`) để lúc đó chỉ phải sửa một tệp.

- [ ] **Step 1: Tạo package**

`apps/web/package.json`:
```json
{
  "name": "@lis/web",
  "version": "0.0.0",
  "type": "module",
  "scripts": { "dev": "vite", "build": "vite build", "preview": "vite preview" }
}
```

Run:
```bash
pnpm --filter @lis/web add react@19.0.0 react-dom@19.0.0
pnpm --filter @lis/web add -D vite@6.0.3 @vitejs/plugin-react@4.3.4 @types/react@19.0.2 @types/react-dom@19.0.2
```

`apps/web/vite.config.ts`:
```ts
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: { port: 5173 },
})
```

`apps/web/index.html`:
```html
<!doctype html>
<html lang="vi">
  <head>
    <meta charset="UTF-8" />
    <title>LIS Connect</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" src="/src/main.tsx"></script>
  </body>
</html>
```

- [ ] **Step 2: Viết lớp gọi API**

`apps/web/src/api.ts`:
```ts
const BASE_URL = import.meta.env.VITE_CORE_URL ?? 'http://localhost:3000'
const TENANT_STORAGE_KEY = 'lis.tenantId'

export interface DeviceSummary {
  id: string
  deviceKey: string
  name: string
  transport: string
  driver: string
  status: string
  lastMessageAt: string | null
}

export interface RawMessageView {
  id: string
  direction: string
  receivedAt: string
  parseStatus: string
  payloadBase64: string
  payloadText: string
}

export interface ConnectionEventView {
  id: string
  status: string
  detail: string | null
  occurredAt: string
}

/**
 * Giai đoạn 1 chưa có đăng nhập: tenant lấy từ ô nhập và lưu ở localStorage.
 * Giai đoạn 2 thay bằng JWT — giữ toàn bộ phần này ở một tệp để lúc đó chỉ phải
 * sửa một chỗ.
 */
export const tenant = {
  get: (): string => localStorage.getItem(TENANT_STORAGE_KEY) ?? '',
  set: (value: string): void => localStorage.setItem(TENANT_STORAGE_KEY, value),
}

async function get<T>(path: string): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    headers: { 'X-Tenant-Id': tenant.get() },
  })
  if (!response.ok) {
    throw new Error(`gọi ${path} thất bại: ${response.status} ${response.statusText}`)
  }
  return (await response.json()) as T
}

export const api = {
  devices: () => get<DeviceSummary[]>('/api/devices'),
  rawMessages: (deviceId: string) =>
    get<RawMessageView[]>(`/api/devices/${deviceId}/raw-messages?limit=100`),
  connectionEvents: (deviceId: string) =>
    get<ConnectionEventView[]>(`/api/devices/${deviceId}/connection-events?limit=100`),
}
```

- [ ] **Step 3: Viết App và màn hình theo dõi**

`apps/web/src/main.tsx`:
```tsx
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App.js'
import './styles.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
```

`apps/web/src/App.tsx`:
```tsx
import { useState } from 'react'
import { DeviceDetail } from './screens/DeviceDetail.js'
import { DeviceMonitor } from './screens/DeviceMonitor.js'
import { tenant } from './api.js'

export function App(): JSX.Element {
  const [tenantId, setTenantId] = useState(tenant.get())
  const [selectedDeviceId, setSelectedDeviceId] = useState<string | null>(null)

  const applyTenant = (value: string): void => {
    tenant.set(value)
    setTenantId(value)
    setSelectedDeviceId(null)
  }

  return (
    <div className="app">
      <header className="app-header">
        <h1>LIS Connect</h1>
        <label className="tenant-picker">
          <span>Mã cơ sở (tenant)</span>
          <input
            value={tenantId}
            placeholder="dán tenant id"
            onChange={(e) => applyTenant(e.target.value)}
          />
        </label>
      </header>

      <main className="content">
        {!tenantId && <p className="empty">Nhập tenant id để xem dữ liệu.</p>}
        {tenantId && !selectedDeviceId && <DeviceMonitor onSelect={setSelectedDeviceId} />}
        {tenantId && selectedDeviceId && (
          <DeviceDetail deviceId={selectedDeviceId} onBack={() => setSelectedDeviceId(null)} />
        )}
      </main>
    </div>
  )
}
```

`apps/web/src/screens/DeviceMonitor.tsx`:
```tsx
import { useEffect, useState } from 'react'
import { api, type DeviceSummary } from '../api.js'

const REFRESH_MS = 5000

function statusClass(status: string): string {
  if (status === 'connected') return 'dot dot-green'
  if (status === 'error') return 'dot dot-red'
  return 'dot dot-amber'
}

function statusLabel(status: string): string {
  if (status === 'connected') return 'Đang kết nối'
  if (status === 'error') return 'Lỗi'
  return 'Mất kết nối'
}

function formatTime(iso: string | null): string {
  if (!iso) return 'chưa có'
  return new Date(iso).toLocaleString('vi-VN')
}

export function DeviceMonitor({ onSelect }: { onSelect: (id: string) => void }): JSX.Element {
  const [devices, setDevices] = useState<DeviceSummary[]>([])
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const load = (): void => {
      api
        .devices()
        .then((data) => {
          setDevices(data)
          setError(null)
        })
        .catch((e: Error) => setError(e.message))
    }
    load()
    const timer = setInterval(load, REFRESH_MS)
    return () => clearInterval(timer)
  }, [])

  if (error) return <p className="error">{error}</p>

  return (
    <section className="panel">
      <h2>Theo dõi máy xét nghiệm</h2>
      <table className="table">
        <thead>
          <tr>
            <th>Trạng thái</th>
            <th>Tên máy</th>
            <th>Mã máy</th>
            <th>Kết nối</th>
            <th>Driver</th>
            <th>Dữ liệu gần nhất</th>
          </tr>
        </thead>
        <tbody>
          {devices.map((device) => (
            <tr key={device.id} className="clickable" onClick={() => onSelect(device.id)}>
              <td>
                <span className={statusClass(device.status)} />
                {statusLabel(device.status)}
              </td>
              <td>{device.name}</td>
              <td>{device.deviceKey}</td>
              <td>{device.transport}</td>
              <td>{device.driver}</td>
              <td>{formatTime(device.lastMessageAt)}</td>
            </tr>
          ))}
          {devices.length === 0 && (
            <tr>
              <td colSpan={6}>Chưa có máy nào gửi dữ liệu lên.</td>
            </tr>
          )}
        </tbody>
      </table>
    </section>
  )
}
```

`apps/web/src/screens/DeviceDetail.tsx`:
```tsx
import { useEffect, useState } from 'react'
import { api, type ConnectionEventView, type RawMessageView } from '../api.js'

export function DeviceDetail({
  deviceId,
  onBack,
}: {
  deviceId: string
  onBack: () => void
}): JSX.Element {
  const [messages, setMessages] = useState<RawMessageView[]>([])
  const [events, setEvents] = useState<ConnectionEventView[]>([])
  const [showHex, setShowHex] = useState(false)

  useEffect(() => {
    const load = (): void => {
      void api.rawMessages(deviceId).then(setMessages)
      void api.connectionEvents(deviceId).then(setEvents)
    }
    load()
    const timer = setInterval(load, 5000)
    return () => clearInterval(timer)
  }, [deviceId])

  return (
    <section className="panel">
      <button onClick={onBack}>← Quay lại danh sách</button>

      <h2>Nhật ký kết nối</h2>
      <table className="table">
        <thead>
          <tr>
            <th>Thời điểm</th>
            <th>Trạng thái</th>
            <th>Chi tiết</th>
          </tr>
        </thead>
        <tbody>
          {events.map((event) => (
            <tr key={event.id}>
              <td>{new Date(event.occurredAt).toLocaleString('vi-VN')}</td>
              <td>{event.status}</td>
              <td>{event.detail ?? ''}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <div className="actions">
        <h2>Dữ liệu thô nhận từ máy</h2>
        <label className="checkbox">
          <input type="checkbox" checked={showHex} onChange={(e) => setShowHex(e.target.checked)} />
          <span>Hiện dạng hex</span>
        </label>
      </div>

      <pre className="terminal">
        {messages.map((message) => (
          <div key={message.id}>
            <span className="ts">{new Date(message.receivedAt).toLocaleTimeString('vi-VN')}</span>
            <span className="dir">{message.direction === 'inbound' ? '<<' : '>>'}</span>
            <span className="status">[{message.parseStatus}]</span>
            <span className="payload">
              {showHex
                ? (atob(message.payloadBase64)
                    .split('')
                    .map((c) => c.charCodeAt(0).toString(16).padStart(2, '0'))
                    .join(' ')
                    .toUpperCase())
                : message.payloadText}
            </span>
          </div>
        ))}
        {messages.length === 0 && <div>Chưa nhận được dữ liệu nào từ máy này.</div>}
      </pre>
    </section>
  )
}
```

`apps/web/src/styles.css`: sao chép nguyên nội dung của `apps/agent/src/renderer/styles.css` và thêm:
```css
.dot-amber { background: #f2994a; }
.clickable { cursor: pointer; }
.clickable:hover { background: #f0f4f8; }
.error { color: #d64545; }
.empty { color: #616e7c; }
.tenant-picker { display: flex; align-items: center; gap: 8px; font-size: 13px; }
.tenant-picker input { padding: 6px 8px; border: 1px solid #cbd2d9; border-radius: 4px; width: 320px; }
.terminal .status { color: #9aa5b1; margin-right: 8px; }
```

- [ ] **Step 4: Chạy thử**

Run: `pnpm --filter @lis/web dev`
Expected: mở `http://localhost:5173`, dán tenant id, thấy máy đã tạo ở Task 15 với đèn xanh; bấm vào máy thấy log kết nối và dữ liệu thô.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(web): device monitor and raw data viewer"
```

---

## Task 17: Kiểm thử đầu-cuối, đóng gói Windows và tài liệu triển khai

**Files:**
- Create: `apps/core/src/admin-cli.ts`
- Create: `test/e2e/full-path.test.ts`, `vitest.e2e.config.ts`
- Create: `apps/agent/electron-builder.yml`, `apps/agent/build/install-autostart.ps1`
- Create: `docs/trien-khai/cai-dat-agent.md`
- Modify: `package.json` (thêm script `test:e2e`)

**Interfaces:**
- Consumes: mọi task trước
- Produces:
  - `pnpm --filter @lis/core admin create-tenant <code> <name>` và `admin issue-key <tenantCode> <label>`
  - `pnpm test:e2e` — chạy toàn bộ đường đi từ máy giả lập tới API đọc
  - `pnpm --filter @lis/agent package` — sinh bộ cài `.exe` cho Windows

- [ ] **Step 1: Viết CLI quản trị**

`apps/core/src/admin-cli.ts`:
```ts
import 'reflect-metadata'
import { NestFactory } from '@nestjs/core'
import { AdminDbService } from './admin-db/admin-db.service.js'
import { AppModule } from './app.module.js'
import { AgentKeyService } from './tenant/agent-key.service.js'
import { TenantMigratorService } from './tenant/tenant-migrator.service.js'

function databaseNameFor(code: string): string {
  const slug = code.toLowerCase().replace(/[^a-z0-9]/g, '_')
  return `lis_t_${slug}`
}

async function main(): Promise<void> {
  const [command, ...args] = process.argv.slice(2)
  const app = await NestFactory.createApplicationContext(AppModule, { logger: ['error'] })
  const admin = app.get(AdminDbService)
  const migrator = app.get(TenantMigratorService)
  const keys = app.get(AgentKeyService)

  try {
    if (command === 'create-tenant') {
      const [code, ...nameParts] = args
      if (!code) throw new Error('cú pháp: create-tenant <code> <tên cơ sở>')
      const tenant = await admin.db
        .insertInto('tenants')
        .values({
          code,
          name: nameParts.join(' ') || code,
          database_name: databaseNameFor(code),
          status: 'active',
        })
        .returningAll()
        .executeTakeFirstOrThrow()
      await migrator.provisionDatabase(tenant.id)
      console.log(`đã tạo cơ sở "${tenant.name}"`)
      console.log(`tenant id: ${tenant.id}`)
      console.log(`database:  ${tenant.database_name}`)
      return
    }

    if (command === 'issue-key') {
      const [code, ...labelParts] = args
      if (!code) throw new Error('cú pháp: issue-key <code cơ sở> <nhãn máy tính>')
      const tenant = await admin.db
        .selectFrom('tenants')
        .selectAll()
        .where('code', '=', code)
        .executeTakeFirst()
      if (!tenant) throw new Error(`không tìm thấy cơ sở có mã "${code}"`)

      const issued = await keys.issue(tenant.id, labelParts.join(' ') || 'PC phòng xét nghiệm')
      console.log('KHOÁ AGENT (chỉ hiện một lần duy nhất, hãy chép lại ngay):')
      console.log(issued.plainKey)
      return
    }

    if (command === 'migrate-all') {
      const result = await migrator.migrateAll()
      console.log(`thành công: ${result.ok.length} cơ sở`)
      for (const failure of result.failed) {
        console.error(`THẤT BẠI ${failure.tenantId}: ${failure.error}`)
      }
      if (result.failed.length > 0) process.exitCode = 1
      return
    }

    console.log('các lệnh: create-tenant, issue-key, migrate-all')
  } finally {
    await app.close()
  }
}

void main()
```

Thêm vào `apps/core/package.json`:
```json
"admin": "node --import tsx/esm --env-file=.env src/admin-cli.ts"
```

- [ ] **Step 2: Viết test đầu-cuối**

`test/e2e/full-path.test.ts`:
```ts
import { execFileSync } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { INestApplication } from '@nestjs/common'
import { Test } from '@nestjs/testing'
import request from 'supertest'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { AdminDbService } from '../../apps/core/src/admin-db/admin-db.service.js'
import { AgentGateway } from '../../apps/core/src/agent-gateway/agent.gateway.js'
import { AppModule } from '../../apps/core/src/app.module.js'
import { AgentKeyService } from '../../apps/core/src/tenant/agent-key.service.js'
import { TenantMigratorService } from '../../apps/core/src/tenant/tenant-migrator.service.js'
import { prepareTestTenant, resetAdminDatabase } from '../../apps/core/test/helpers.js'
import { CoreClient } from '../../apps/agent/src/main/core-client.js'
import { DeviceManager } from '../../apps/agent/src/main/device-manager.js'
import { LocalDb } from '../../apps/agent/src/main/local-db.js'
import { Outbox } from '../../apps/agent/src/main/outbox.js'
import { AstmSimulator } from '../../tools/simulator/src/simulator.js'

let app: INestApplication
let corePort: number
let tenantId: string
let agentKey: string
let dir: string
let db: LocalDb
let outbox: Outbox
let manager: DeviceManager
let client: CoreClient
let sim: AstmSimulator
let simPort: number

async function waitFor(check: () => Promise<boolean> | boolean, timeoutMs = 20_000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (await check()) return
    await new Promise((r) => setTimeout(r, 100))
  }
  throw new Error('hết giờ chờ điều kiện')
}

beforeAll(async () => {
  await resetAdminDatabase()

  const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile()
  app = moduleRef.createNestApplication()
  await app.init()
  await app.listen(0)
  app.get(AgentGateway).attach(app.getHttpServer())
  corePort = (app.getHttpServer().address() as { port: number }).port

  tenantId = await prepareTestTenant(
    app.get(AdminDbService).db,
    app.get(TenantMigratorService),
    'e2e',
    'lis_t_e2e',
  )
  agentKey = (await app.get(AgentKeyService).issue(tenantId, 'PC E2E')).plainKey

  sim = new AstmSimulator({ misbehaviour: 'none' })
  simPort = await sim.start(0)

  dir = mkdtempSync(join(tmpdir(), 'lis-e2e-'))
  db = LocalDb.open(join(dir, 'agent.sqlite'))
  outbox = new Outbox(db)
  manager = new DeviceManager(outbox, () => client?.pump())
  client = new CoreClient({
    url: `ws://localhost:${corePort}/agent`,
    agentKey,
    agentVersion: '0.1.0',
    hostname: 'E2E-PC',
    outbox,
    initialBackoffMs: 100,
  })
  client.start()
  await waitFor(() => client.isConnected())

  await manager.applyConfig([
    {
      deviceKey: 'may-e2e',
      name: 'Máy E2E',
      driver: 'astm-e1394',
      enabled: true,
      transport: { kind: 'tcp', role: 'client', host: '127.0.0.1', port: simPort },
    },
  ])
  await waitFor(() => manager.statusOf('may-e2e') === 'connected')
}, 180_000)

afterAll(async () => {
  await client.stop()
  await manager.stopAll()
  await sim.stop()
  db.close()
  rmSync(dir, { recursive: true, force: true })
  await app.close()
})

describe('đường đi đầu-cuối: máy giả lập → Agent → Core → API', () => {
  it('kết quả từ máy xuất hiện trong API đọc của đúng tenant', async () => {
    await sim.sendResults([{ sid: 'E2E001', testCode: 'GLU', value: '5.4', unit: 'mmol/L' }])

    await waitFor(async () => {
      const res = await request(app.getHttpServer())
        .get('/api/devices')
        .set('X-Tenant-Id', tenantId)
      return res.body.length > 0
    })

    const devices = await request(app.getHttpServer())
      .get('/api/devices')
      .set('X-Tenant-Id', tenantId)
      .expect(200)
    const device = devices.body[0]
    expect(device.deviceKey).toBe('may-e2e')
    expect(device.status).toBe('connected')

    await waitFor(async () => {
      const res = await request(app.getHttpServer())
        .get(`/api/devices/${device.id}/raw-messages`)
        .set('X-Tenant-Id', tenantId)
      return res.body.some((m: { payloadText: string }) => m.payloadText.includes('E2E001'))
    })
  })

  it('hàng đợi của Agent về 0 sau khi Core xác nhận', async () => {
    await waitFor(() => outbox.depth() === 0)
    expect(outbox.depth()).toBe(0)
  })

  it('mất kết nối Core rồi nối lại thì không mất và không nhân đôi kết quả', async () => {
    const device = (
      await request(app.getHttpServer()).get('/api/devices').set('X-Tenant-Id', tenantId)
    ).body[0]

    const before = (
      await request(app.getHttpServer())
        .get(`/api/devices/${device.id}/raw-messages?limit=500`)
        .set('X-Tenant-Id', tenantId)
    ).body.length

    // Ngắt Agent khỏi Core, gửi kết quả trong lúc mất kết nối.
    await client.stop()
    await sim.sendResults([{ sid: 'E2E002', testCode: 'URE', value: '6.1', unit: 'mmol/L' }])
    await waitFor(() => outbox.depth() > 0)

    // Nối lại.
    client = new CoreClient({
      url: `ws://localhost:${corePort}/agent`,
      agentKey,
      agentVersion: '0.1.0',
      hostname: 'E2E-PC',
      outbox,
      initialBackoffMs: 100,
    })
    client.start()
    await waitFor(() => client.isConnected())
    await waitFor(() => outbox.depth() === 0)

    const after = (
      await request(app.getHttpServer())
        .get(`/api/devices/${device.id}/raw-messages?limit=500`)
        .set('X-Tenant-Id', tenantId)
    ).body

    expect(after.length).toBeGreaterThan(before)
    expect(after.some((m: { payloadText: string }) => m.payloadText.includes('E2E002'))).toBe(true)

    // Không có message nào bị ghi hai lần.
    const ids = after.map((m: { id: string }) => m.id)
    expect(new Set(ids).size).toBe(ids.length)
  }, 60_000)
})
```

`vitest.e2e.config.ts` ở gốc repo:
```ts
import { config } from 'dotenv'
import { defineConfig } from 'vitest/config'

config({ path: new URL('apps/core/.env.test', import.meta.url).pathname })

export default defineConfig({
  test: {
    include: ['test/e2e/**/*.test.ts'],
    hookTimeout: 180_000,
    testTimeout: 120_000,
    fileParallelism: false,
  },
})
```

Thêm vào `package.json` gốc: `"test:e2e": "vitest run --config vitest.e2e.config.ts"`.

- [ ] **Step 3: Chạy test đầu-cuối**

Run: `pnpm db:up && pnpm test:e2e`
Expected: 3 test PASS. Đây là bằng chứng giai đoạn 1 hoàn thành.

- [ ] **Step 4: Cấu hình đóng gói Windows**

`apps/agent/electron-builder.yml`:
```yaml
appId: vn.lisconnect.agent
productName: LIS Connect Agent
directories:
  output: dist
  buildResources: build
files:
  - out/**/*
  - package.json
win:
  target:
    - nsis
  artifactName: LIS-Connect-Agent-${version}-setup.exe
nsis:
  oneClick: false
  allowToChangeInstallationDirectory: true
  createDesktopShortcut: true
  include: build/installer.nsh
```

`apps/agent/build/installer.nsh` — đăng ký chạy khi đăng nhập:
```nsis
!macro customInstall
  ExecWait 'powershell -ExecutionPolicy Bypass -File "$INSTDIR\resources\install-autostart.ps1" -ExePath "$INSTDIR\LIS Connect Agent.exe"'
!macroend

!macro customUnInstall
  ExecWait 'schtasks /Delete /TN "LIS Connect Agent" /F'
!macroend
```

`apps/agent/build/install-autostart.ps1`:
```powershell
param([Parameter(Mandatory = $true)][string]$ExePath)

# Chạy khi có người đăng nhập, tự khởi động lại nếu tiến trình chết.
# Đây là thứ giữ cho Agent sống sau khi máy khởi động lại; thiếu nó thì máy xét
# nghiệm gửi kết quả vào hư không.
$action  = New-ScheduledTaskAction -Execute $ExePath
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet `
  -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries `
  -RestartCount 999 `
  -RestartInterval (New-TimeSpan -Minutes 1) `
  -ExecutionTimeLimit (New-TimeSpan -Seconds 0)

Register-ScheduledTask -TaskName 'LIS Connect Agent' `
  -Action $action -Trigger $trigger -Settings $settings -Force
```

Thêm `install-autostart.ps1` vào `extraResources` trong `electron-builder.yml`:
```yaml
extraResources:
  - from: build/install-autostart.ps1
    to: install-autostart.ps1
```

- [ ] **Step 5: Đóng gói và thử trên máy Windows sạch**

Run trên máy Windows: `pnpm --filter @lis/agent package`
Expected: sinh ra `apps/agent/dist/LIS-Connect-Agent-0.1.0-setup.exe`.

Kiểm tra bằng tay trên một máy Windows chưa từng cài:
1. Chạy bộ cài. Ứng dụng mở lên.
2. Mở Task Scheduler, xác nhận có tác vụ `LIS Connect Agent` với trigger At log on.
3. Cấu hình Core và một máy TCP trỏ tới máy giả lập chạy trên cùng máy.
4. Đóng cửa sổ ứng dụng — icon còn ở khay hệ thống, dữ liệu vẫn chảy lên Core.
5. Khởi động lại Windows, đăng nhập — Agent tự chạy, đèn Core chuyển xanh trong vòng một phút.
6. Nếu có sẵn bộ `com0com`: tạo cặp cổng ảo COM10/COM11, cấu hình một máy serial trên COM10, dùng PuTTY gửi vài byte vào COM11, xác nhận tab Terminal thô hiện đúng byte đó.

- [ ] **Step 6: Viết tài liệu triển khai**

`docs/trien-khai/cai-dat-agent.md`:
```markdown
# Cài đặt LIS Connect Agent tại phòng xét nghiệm

## Trước khi tới cơ sở

1. Tạo cơ sở trên Core:
   `pnpm --filter @lis/core admin create-tenant bvA "Bệnh viện A"`
   Ghi lại `tenant id` in ra màn hình.
2. Cấp khoá cho từng máy tính sẽ cài Agent:
   `pnpm --filter @lis/core admin issue-key bvA "PC phòng sinh hóa"`
   **Khoá chỉ hiện một lần.** Chép lại ngay.

## Tại phòng xét nghiệm

1. Chạy bộ cài `LIS-Connect-Agent-<phiên bản>-setup.exe`.
2. Mở tab **Kết nối Core**, nhập địa chỉ Core và khoá vừa cấp. Bấm Lưu.
   Đèn ở góc phải chuyển xanh là đã thông.
3. Mở tab **Máy xét nghiệm**, thêm từng máy:
   - Máy nối dây COM: chọn cổng trong danh sách (tên thiết bị giúp phân biệt các
     bộ USB-Serial), đặt baudrate đúng như cấu hình trên máy xét nghiệm.
   - Máy nối mạng: nhập IP và cổng; chọn "Agent nối tới máy" hoặc "Máy nối vào
     Agent" theo tài liệu của hãng.
   - Máy xuất file: chọn thư mục và mẫu tên file.
   Bấm **Test kết nối** trước khi lưu.
4. Cho máy chạy một mẫu thật hoặc mẫu chuẩn. Mở tab **Terminal thô** để xem byte
   đi về. Nếu thấy dữ liệu vào nhưng Web UI không hiện, phần lớn là do sai driver
   hoặc sai mã máy.

## Bắt buộc: giữ cho Agent sống sau khi khởi động lại

Agent là ứng dụng chạy trong phiên đăng nhập của người dùng, nên máy khởi động lại
mà dừng ở màn hình đăng nhập thì Agent **không chạy** và máy xét nghiệm gửi kết quả
vào hư không.

Bộ cài đã đăng ký sẵn tác vụ `LIS Connect Agent` chạy khi đăng nhập. Ngoài ra phải:

1. Bật **tự động đăng nhập** cho tài khoản dùng chung của phòng xét nghiệm
   (`netplwiz`, bỏ dấu tích "Users must enter a user name and password").
2. Kiểm tra bằng cách khởi động lại máy và xác nhận Agent tự chạy.

Nếu chính sách máy trạm của bệnh viện không cho tự động đăng nhập, báo lại để cân
nhắc bản chạy dạng Windows Service.

## Khi cần hỗ trợ từ xa

Tab **Terminal thô** cho thấy chính xác máy đang nói gì. Chụp màn hình tab này,
kèm mã máy và thời điểm, là đủ để chẩn đoán phần lớn sự cố.
```

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: end-to-end test, windows packaging and deployment guide"
```

---

## Task 18: Đóng gói Core bằng Docker

**Files:**
- Create: `apps/core/Dockerfile`, `.dockerignore`
- Create: `deploy/docker-compose.prod.yml`, `deploy/pgbouncer/pgbouncer.ini`, `deploy/pgbouncer/userlist.txt.example`
- Create: `deploy/caddy/Caddyfile`
- Create: `apps/core/src/health/health.controller.ts`, `apps/core/src/health/health.module.ts`
- Modify: `apps/core/src/admin-db/admin-db.service.ts` (bỏ migration tự động khi khởi động)
- Modify: `apps/core/src/main.ts` (đọc CORS từ biến môi trường)
- Test: `apps/core/test/health.test.ts`

**Interfaces:**
- Consumes: Task 3–8 (toàn bộ Core), Task 17 (`admin-cli.ts` với lệnh `migrate-all`)
- Produces:
  - image `lis-connect-core` chạy được với `ADMIN_DATABASE_URL`, `TENANT_DATABASE_URL_TEMPLATE`, `CORE_PORT`, `CORS_ORIGINS`
  - `GET /health` → `{ status: 'ok' }` khi kết nối được DB quản trị, `503` khi không
  - `docker compose -f deploy/docker-compose.prod.yml up -d` dựng đủ Postgres, PgBouncer, Core và reverse proxy

**Ba quyết định:**

1. **Migration KHÔNG chạy khi container khởi động.** Với nhiều replica, hai container sẽ cùng migrate; và migration DB nghiệp vụ của N tenant chạy lâu, cần biết tenant nào thất bại. Thay bằng một job riêng chạy `admin migrate-all` trước khi rollout.
2. **PgBouncer nằm giữa Core và Postgres ngay từ đầu.** Mỗi replica giữ pool riêng cho mỗi tenant đang hoạt động; ba replica nhân bốn mươi tenant nhân bốn kết nối đã vượt mặc định của Postgres. Đây là ràng buộc của kiến trúc database-per-tenant, không phải tối ưu về sau.
3. **Reverse proxy phải được cấu hình riêng cho `/agent`.** Agent giữ kết nối WebSocket hàng giờ nên cần tắt buffering và đặt timeout dài; và **không được ghi query string vào log**, vì khoá Agent nằm ở đó.

- [ ] **Step 1: Viết test cho endpoint health**

`apps/core/test/health.test.ts`:
```ts
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
```

- [ ] **Step 2: Chạy test, xác nhận fail**

Run: `pnpm --filter @lis/core test test/health.test.ts`
Expected: FAIL — `/health` trả 404.

- [ ] **Step 3: Viết health controller**

`apps/core/src/health/health.controller.ts`:
```ts
import { Controller, Get, ServiceUnavailableException } from '@nestjs/common'
import { sql } from 'kysely'
import { AdminDbService } from '../admin-db/admin-db.service.js'

@Controller('health')
export class HealthController {
  constructor(private readonly admin: AdminDbService) {}

  @Get()
  async check(): Promise<{ status: 'ok'; adminMigration: string }> {
    try {
      const result = await sql<{ name: string }>`
        select name from kysely_migration order by timestamp desc limit 1
      `.execute(this.admin.db)
      return { status: 'ok', adminMigration: result.rows[0]?.name ?? 'chưa migrate' }
    } catch (error) {
      // Trả 503 chứ không phải 500: đây là tín hiệu cho load balancer rút container
      // này ra khỏi vòng phục vụ, không phải lỗi của người gọi.
      throw new ServiceUnavailableException(
        `không kết nối được DB quản trị: ${error instanceof Error ? error.message : String(error)}`,
      )
    }
  }
}
```

`apps/core/src/health/health.module.ts`:
```ts
import { Module } from '@nestjs/common'
import { HealthController } from './health.controller.js'

@Module({ controllers: [HealthController] })
export class HealthModule {}
```

Thêm `HealthModule` vào `imports` của `AppModule`.

- [ ] **Step 4: Bỏ migration tự động khi khởi động**

Trong `apps/core/src/admin-db/admin-db.service.ts`, bỏ `OnModuleInit` và lời gọi `runAdminMigrations`:

```ts
import { Injectable, type OnModuleDestroy } from '@nestjs/common'
import { Kysely, PostgresDialect } from 'kysely'
import pg from 'pg'
import { loadConfig } from '../config.js'
import type { AdminDatabase } from './schema.js'

@Injectable()
export class AdminDbService implements OnModuleDestroy {
  readonly db: Kysely<AdminDatabase>

  constructor() {
    const config = loadConfig()
    this.db = new Kysely<AdminDatabase>({
      dialect: new PostgresDialect({
        pool: new pg.Pool({ connectionString: config.adminDatabaseUrl, max: 10 }),
      }),
    })
  }

  async onModuleDestroy(): Promise<void> {
    await this.db.destroy()
  }
}
```

Migration giờ chỉ chạy qua CLI. Bổ sung lệnh `migrate-admin` vào `apps/core/src/admin-cli.ts`, ngay trước nhánh `create-tenant`:

```ts
    if (command === 'migrate-admin') {
      const { runAdminMigrations } = await import('./admin-db/migrator.js')
      await runAdminMigrations(admin.db)
      console.log('đã migrate DB quản trị')
      return
    }
```

Và cập nhật dòng trợ giúp cuối hàm:
```ts
    console.log('các lệnh: migrate-admin, create-tenant, issue-key, migrate-all')
```

**Hệ quả với test:** các test tích hợp trước đây dựa vào migration tự động lúc `app.init()`. Sửa `apps/core/test/helpers.ts`, thêm lời gọi migration vào `resetAdminDatabase`:

```ts
import { Kysely, PostgresDialect } from 'kysely'
import { runAdminMigrations } from '../src/admin-db/migrator.js'
import type { AdminDatabase } from '../src/admin-db/schema.js'

export async function resetAdminDatabase(): Promise<void> {
  const url = new URL(process.env.ADMIN_DATABASE_URL ?? '')
  const name = url.pathname.slice(1)
  const root = new pg.Client({ connectionString: ROOT })
  await root.connect()
  await root.query(`DROP DATABASE IF EXISTS ${name} WITH (FORCE)`)
  await root.query(`CREATE DATABASE ${name}`)
  await root.end()

  const db = new Kysely<AdminDatabase>({
    dialect: new PostgresDialect({
      pool: new pg.Pool({ connectionString: process.env.ADMIN_DATABASE_URL }),
    }),
  })
  await runAdminMigrations(db)
  await db.destroy()
}
```

- [ ] **Step 5: Đọc CORS từ biến môi trường**

Trong `apps/core/src/config.ts`, thêm vào `CoreConfig` và `loadConfig`:

```ts
export interface CoreConfig {
  port: number
  adminDatabaseUrl: string
  tenantDatabaseUrlTemplate: string
  corsOrigins: string[]
}
```

và trong `loadConfig`, trước `return`:
```ts
  const corsOrigins = (env.CORS_ORIGINS ?? 'http://localhost:5173')
    .split(',')
    .map((origin) => origin.trim())
    .filter((origin) => origin.length > 0)
```
rồi thêm `corsOrigins` vào object trả về.

Trong `apps/core/src/main.ts`, thay dòng CORS cứng bằng:
```ts
  app.enableCors({
    origin: config.corsOrigins,
    allowedHeaders: ['Content-Type', 'X-Tenant-Id', 'Authorization'],
  })
```

- [ ] **Step 6: Chạy test, xác nhận pass**

Run: `pnpm --filter @lis/core test`
Expected: toàn bộ test PASS, gồm 3 test health.

- [ ] **Step 7: Viết Dockerfile**

`.dockerignore` ở gốc repo:
```
node_modules
**/node_modules
**/dist
**/out
.git
docs
apps/agent
tools/simulator
```

`apps/core/Dockerfile` (build từ gốc repo vì là monorepo):
```dockerfile
# syntax=docker/dockerfile:1.7

FROM node:22-bookworm-slim AS base
ENV PNPM_HOME=/pnpm
ENV PATH=$PNPM_HOME:$PATH
RUN corepack enable
WORKDIR /repo

# --- Cài phụ thuộc, tách riêng để tận dụng cache khi mã nguồn đổi ---
FROM base AS deps
COPY pnpm-lock.yaml pnpm-workspace.yaml package.json ./
COPY packages/shared/package.json packages/shared/
COPY apps/core/package.json apps/core/
RUN --mount=type=cache,id=pnpm,target=/pnpm/store \
    pnpm install --frozen-lockfile --filter @lis/core... --prod=false

# --- Biên dịch TypeScript ---
FROM deps AS build
COPY tsconfig.base.json ./
COPY packages/shared packages/shared
COPY apps/core apps/core
RUN pnpm --filter @lis/core exec tsc -p tsconfig.json
RUN pnpm --filter @lis/shared exec tsc -p tsconfig.json

# --- Chỉ giữ phụ thuộc chạy thật ---
FROM base AS prod-deps
COPY pnpm-lock.yaml pnpm-workspace.yaml package.json ./
COPY packages/shared/package.json packages/shared/
COPY apps/core/package.json apps/core/
RUN --mount=type=cache,id=pnpm,target=/pnpm/store \
    pnpm install --frozen-lockfile --filter @lis/core... --prod

# --- Ảnh cuối ---
FROM node:22-bookworm-slim AS runtime
ENV NODE_ENV=production
WORKDIR /repo

COPY --from=prod-deps /repo/node_modules node_modules
COPY --from=prod-deps /repo/apps/core/node_modules apps/core/node_modules
COPY --from=prod-deps /repo/packages/shared/node_modules packages/shared/node_modules
COPY --from=build /repo/apps/core/dist apps/core/dist
COPY --from=build /repo/packages/shared/dist packages/shared/dist
COPY apps/core/package.json apps/core/
COPY packages/shared/package.json packages/shared/

# Chạy bằng người dùng thường, không phải root.
USER node
EXPOSE 3000

HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
  CMD node -e "fetch('http://127.0.0.1:'+(process.env.CORE_PORT||3000)+'/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"

CMD ["node", "apps/core/dist/main.js"]
```

`packages/shared/package.json` phải trỏ `main`/`types` vào `dist` khi chạy production. Thêm trường `exports` để cả hai chế độ cùng chạy:
```json
{
  "name": "@lis/shared",
  "version": "0.0.0",
  "type": "module",
  "main": "./src/index.ts",
  "types": "./src/index.ts",
  "exports": {
    ".": {
      "types": "./dist/index.d.ts",
      "production": "./dist/index.js",
      "default": "./src/index.ts"
    }
  },
  "scripts": { "test": "vitest run" }
}
```

- [ ] **Step 8: Dựng image và chạy thử một mình**

Run:
```bash
docker build -f apps/core/Dockerfile -t lis-connect-core:dev .
docker run --rm --network host \
  -e CORE_PORT=3000 \
  -e ADMIN_DATABASE_URL=postgres://lis:lis@localhost:55432/lis_admin \
  -e TENANT_DATABASE_URL_TEMPLATE=postgres://lis:lis@localhost:55432/{db} \
  lis-connect-core:dev
```
Expected: in ra `LIS Connect Core đang chạy ở cổng 3000`; `curl localhost:3000/health` trả `{"status":"ok",...}`.

- [ ] **Step 9: Viết compose cho môi trường chạy thật**

`deploy/pgbouncer/pgbouncer.ini`:
```ini
[databases]
; Định tuyến mọi database về Postgres thật; tên database do Core truyền vào.
* = host=postgres port=5432

[pgbouncer]
listen_addr = 0.0.0.0
listen_port = 6432
auth_type = scram-sha-256
auth_file = /etc/pgbouncer/userlist.txt

; transaction pooling: Core mở pool riêng cho mỗi tenant, nên số kết nối phía
; client rất lớn còn số kết nối thật cần rất ít.
pool_mode = transaction
max_client_conn = 2000
default_pool_size = 20
min_pool_size = 0
server_idle_timeout = 60

; Prepared statement ở chế độ transaction pooling: bật để Kysely dùng được.
max_prepared_statements = 200
```

`deploy/pgbouncer/userlist.txt.example`:
```
"lis" "SCRAM-SHA-256$4096:...thay bằng hash lấy từ pg_shadow..."
```

Lấy hash bằng:
```bash
docker compose exec postgres psql -U lis -tAc "select rolname||' '||rolpassword from pg_authid where rolname='lis'"
```

`deploy/caddy/Caddyfile`:
```
core.example.vn {
	encode gzip

	# Khoá Agent nằm ở query string của /agent, nên KHÔNG ghi query vào log.
	log {
		output stdout
		format json
	}

	@agent path /agent*
	handle @agent {
		reverse_proxy core:3000 {
			# Agent giữ kết nối hàng giờ; timeout mặc định sẽ cắt ngang.
			transport http {
				read_timeout 0
				write_timeout 0
			}
		}
	}

	handle {
		reverse_proxy core:3000
	}
}
```

`deploy/docker-compose.prod.yml`:
```yaml
name: lis-connect

services:
  postgres:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: postgres
    volumes:
      - postgres-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER}"]
      interval: 10s
      timeout: 5s
      retries: 10

  pgbouncer:
    image: edoburu/pgbouncer:1.23.1
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    volumes:
      - ./pgbouncer/pgbouncer.ini:/etc/pgbouncer/pgbouncer.ini:ro
      - ./pgbouncer/userlist.txt:/etc/pgbouncer/userlist.txt:ro

  core:
    image: lis-connect-core:${CORE_TAG:-latest}
    restart: unless-stopped
    depends_on:
      pgbouncer:
        condition: service_started
    environment:
      CORE_PORT: 3000
      NODE_ENV: production
      # Core nói với PgBouncer, không nói thẳng với Postgres.
      ADMIN_DATABASE_URL: postgres://${POSTGRES_USER}:${POSTGRES_PASSWORD}@pgbouncer:6432/lis_admin
      TENANT_DATABASE_URL_TEMPLATE: postgres://${POSTGRES_USER}:${POSTGRES_PASSWORD}@pgbouncer:6432/{db}
      CORS_ORIGINS: ${CORS_ORIGINS}
    deploy:
      replicas: 2

  caddy:
    image: caddy:2.8-alpine
    restart: unless-stopped
    depends_on:
      - core
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./caddy/Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
      - caddy-config:/config

volumes:
  postgres-data:
  caddy-data:
  caddy-config:
```

**Lưu ý về việc tạo database tenant:** `provisionDatabase` chạy `CREATE DATABASE`, mà PgBouncer ở chế độ transaction pooling không cho phép lệnh này. Vì vậy CLI quản trị phải nối **thẳng vào Postgres**, không qua PgBouncer. Thêm biến môi trường riêng cho CLI trong `apps/core/src/config.ts`:

```ts
export interface CoreConfig {
  port: number
  adminDatabaseUrl: string
  tenantDatabaseUrlTemplate: string
  corsOrigins: string[]
  /** Kết nối thẳng tới Postgres, bỏ qua PgBouncer. Chỉ CLI quản trị dùng, vì
   *  CREATE DATABASE không chạy được qua transaction pooling. */
  directDatabaseUrlTemplate: string
}
```
và trong `loadConfig`:
```ts
  const directDatabaseUrlTemplate =
    env.DIRECT_DATABASE_URL_TEMPLATE ?? tenantDatabaseUrlTemplate
```

Trong `apps/core/src/tenant/tenant.module.ts`, đổi provider `TenantMigratorService` sang dùng `directDatabaseUrlTemplate`:
```ts
    {
      provide: TenantMigratorService,
      inject: [AdminDbService],
      useFactory: (admin: AdminDbService) =>
        new TenantMigratorService(admin.db, loadConfig().directDatabaseUrlTemplate),
    },
```

- [ ] **Step 10: Viết quy trình rollout**

`deploy/README.md`:
```markdown
# Triển khai Core

## Lần đầu

1. Tạo `deploy/.env`:
   ```
   POSTGRES_USER=lis
   POSTGRES_PASSWORD=<mật khẩu mạnh>
   CORS_ORIGINS=https://app.example.vn
   CORE_TAG=0.1.0
   ```
2. Sinh `deploy/pgbouncer/userlist.txt` từ `userlist.txt.example` (xem hướng dẫn
   lấy hash trong file đó).
3. `docker compose -f deploy/docker-compose.prod.yml up -d postgres pgbouncer`
4. Tạo DB quản trị và migrate:
   ```bash
   docker compose -f deploy/docker-compose.prod.yml exec postgres \
     psql -U lis -c "CREATE DATABASE lis_admin"
   docker run --rm --network lis-connect_default \
     -e ADMIN_DATABASE_URL=... -e TENANT_DATABASE_URL_TEMPLATE=... \
     -e DIRECT_DATABASE_URL_TEMPLATE=postgres://lis:...@postgres:5432/{db} \
     lis-connect-core:0.1.0 node apps/core/dist/admin-cli.js migrate-admin
   ```
5. `docker compose -f deploy/docker-compose.prod.yml up -d`

## Mỗi lần lên phiên bản mới

Thứ tự quan trọng: **migrate trước, đổi image sau**.

1. Dựng và đẩy image mới với tag mới.
2. Chạy job migration một lần (KHÔNG phải trong container ứng dụng):
   ```bash
   docker run --rm --network lis-connect_default \
     -e ADMIN_DATABASE_URL=... -e DIRECT_DATABASE_URL_TEMPLATE=... \
     lis-connect-core:<tag mới> node apps/core/dist/admin-cli.js migrate-admin
   docker run --rm --network lis-connect_default \
     -e ADMIN_DATABASE_URL=... -e DIRECT_DATABASE_URL_TEMPLATE=... \
     lis-connect-core:<tag mới> node apps/core/dist/admin-cli.js migrate-all
   ```
   Lệnh `migrate-all` **không dừng ở tenant lỗi đầu tiên** và in ra danh sách
   thất bại. Xử lý từng cơ sở thất bại trước khi đổi image.
3. Đổi `CORE_TAG` trong `.env` rồi `docker compose ... up -d core`.

Vì migration chạy trước, phiên bản Core cũ phải đọc được schema mới trong khoảng
thời gian rollout. Nghĩa là: chỉ thêm cột, không xoá hay đổi tên cột trong cùng một
lần lên phiên bản. Việc xoá cột để sang lần sau, khi mọi replica đã dùng mã mới.

## Thêm một cơ sở khách hàng

```bash
docker compose -f deploy/docker-compose.prod.yml exec core \
  node apps/core/dist/admin-cli.js create-tenant bvA "Bệnh viện A"
docker compose -f deploy/docker-compose.prod.yml exec core \
  node apps/core/dist/admin-cli.js issue-key bvA "PC phòng sinh hóa"
```

Khoá Agent chỉ hiện một lần. Chép ngay và gửi cho người đi cài đặt.

## Sao lưu

Mỗi cơ sở một database, nên sao lưu và khôi phục làm được cho từng khách:

```bash
docker compose -f deploy/docker-compose.prod.yml exec postgres \
  pg_dump -U lis -Fc lis_t_bva > lis_t_bva-$(date +%F).dump
```
```

- [ ] **Step 11: Thử toàn bộ ngăn xếp**

Run:
```bash
docker build -f apps/core/Dockerfile -t lis-connect-core:0.1.0 .
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env up -d
curl -sf http://localhost/health
```
Expected: trả về `{"status":"ok",...}`. Sau đó trỏ một Agent vào `wss://<tên miền>/agent` và xác nhận kết nối đứng vững quá 5 phút, không bị proxy cắt.

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "feat(core): dockerfile, pgbouncer stack and rollout procedure"
```

---

## Định nghĩa hoàn thành giai đoạn 1

Giai đoạn 1 xong khi tất cả các mục sau đúng:

- [ ] `pnpm test` xanh toàn bộ
- [ ] `pnpm test:e2e` xanh: kết quả đi từ máy giả lập tới API đọc của đúng tenant
- [ ] Test cô lập tenant chứng minh dữ liệu hai cơ sở không chạm nhau
- [ ] Ngắt mạng giữa chừng rồi nối lại: không mất kết quả, không nhân đôi kết quả
- [ ] Bộ cài Windows chạy được trên máy sạch; Agent tự khởi động sau khi đăng nhập lại
- [ ] Tab Terminal thô hiện đúng byte khi thử với cổng ảo `com0com`
- [ ] Web UI hiện danh sách máy với đèn trạng thái và xem được dữ liệu thô
- [ ] Tài liệu triển khai đủ để một người chưa từng cài làm được mà không hỏi
- [ ] Core dựng được image Docker, `/health` trả ok, Agent giữ kết nối WebSocket
      qua reverse proxy trên 5 phút không đứt
- [ ] Giao diện Agent Console và Web UI đã đi qua skill `ui-ux-pro-max`
