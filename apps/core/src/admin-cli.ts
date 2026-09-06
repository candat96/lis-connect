import 'reflect-metadata'
import { randomBytes } from 'node:crypto'
import { NestFactory } from '@nestjs/core'
import { AdminDbService } from './admin-db/admin-db.service.js'
import { AppModule } from './app.module.js'
import { hashPassword } from './auth/auth.service.js'
import {
  parseMigrateLegacyArgs,
  runMigrateLegacy,
} from './migration/migrate-legacy.command.js'
import { AgentKeyService } from './tenant/agent-key.service.js'
import { TenantMigratorService } from './tenant/tenant-migrator.service.js'
import { TenantRegistryService } from './tenant/tenant-registry.service.js'

function databaseNameFor(code: string): string {
  const slug = code.toLowerCase().replace(/[^a-z0-9]/g, '_')
  return `lis_t_${slug}`
}

/** Mật khẩu ngẫu nhiên đủ mạnh cho tài khoản mới cấp/reset — chỉ hiện một lần. */
function generatePassword(): string {
  return randomBytes(18).toString('base64url')
}

/** Vai trò mặc định cho tài khoản tạo qua CLI — chưa có UI phân quyền, chỉ một vai trò kỹ thuật viên. */
const DEFAULT_ROLE = 'ky_thuat_vien'

async function main(): Promise<void> {
  const [command, ...args] = process.argv.slice(2)
  const app = await NestFactory.createApplicationContext(AppModule, { logger: ['error'] })
  const admin = app.get(AdminDbService)
  const migrator = app.get(TenantMigratorService)
  const keys = app.get(AgentKeyService)
  const registry = app.get(TenantRegistryService)

  try {
    if (command === 'migrate-admin') {
      const { runAdminMigrations } = await import('./admin-db/migrator.js')
      await runAdminMigrations(admin.db)
      console.log('đã migrate DB quản trị')
      return
    }

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

    if (command === 'create-user') {
      const [code, username, ...nameParts] = args
      if (!code || !username) {
        throw new Error('cú pháp: create-user <tenantCode> <username> <tên đầy đủ>')
      }
      const tenant = await admin.db
        .selectFrom('tenants')
        .selectAll()
        .where('code', '=', code)
        .executeTakeFirst()
      if (!tenant) throw new Error(`không tìm thấy cơ sở có mã "${code}"`)

      const password = generatePassword()
      await admin.db
        .insertInto('users')
        .values({
          tenant_id: tenant.id,
          username,
          password_hash: hashPassword(password),
          full_name: nameParts.join(' ') || username,
          role: DEFAULT_ROLE,
          status: 'active',
        })
        .execute()

      console.log('MẬT KHẨU (chỉ hiện một lần duy nhất, hãy chép lại ngay):')
      console.log(password)
      return
    }

    if (command === 'reset-password') {
      const [code, username] = args
      if (!code || !username) throw new Error('cú pháp: reset-password <tenantCode> <username>')
      const tenant = await admin.db
        .selectFrom('tenants')
        .selectAll()
        .where('code', '=', code)
        .executeTakeFirst()
      if (!tenant) throw new Error(`không tìm thấy cơ sở có mã "${code}"`)

      const password = generatePassword()
      const result = await admin.db
        .updateTable('users')
        .set({ password_hash: hashPassword(password) })
        .where('tenant_id', '=', tenant.id)
        .where('username', '=', username)
        .executeTakeFirst()
      if (result.numUpdatedRows === 0n) {
        throw new Error(`không tìm thấy người dùng "${username}" ở cơ sở "${code}"`)
      }

      console.log('MẬT KHẨU MỚI (chỉ hiện một lần duy nhất, hãy chép lại ngay):')
      console.log(password)
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

    if (command === 'migrate-legacy') {
      await runMigrateLegacy(admin.db, registry, parseMigrateLegacyArgs(args))
      return
    }

    console.log(
      'các lệnh: migrate-admin, create-tenant, issue-key, create-user, reset-password, migrate-all, migrate-legacy',
    )
    console.log(
      '  migrate-legacy --tenant <mã cơ sở> --source <thư mục> [--decisions <tệp>] [--apply]',
    )
    console.log(
      '    không có --apply: chỉ kiểm tra dữ liệu nguồn, sinh tệp quyết định và in báo cáo, KHÔNG ghi gì.',
    )
  } finally {
    await app.close()
  }
}

void main()
