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

    if (command === 'migrate-all') {
      const result = await migrator.migrateAll()
      console.log(`thành công: ${result.ok.length} cơ sở`)
      for (const failure of result.failed) {
        console.error(`THẤT BẠI ${failure.tenantId}: ${failure.error}`)
      }
      if (result.failed.length > 0) process.exitCode = 1
      return
    }

    console.log('các lệnh: migrate-admin, create-tenant, issue-key, migrate-all')
  } finally {
    await app.close()
  }
}

void main()
