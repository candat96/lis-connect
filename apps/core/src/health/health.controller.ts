import { Controller, Get, Inject, ServiceUnavailableException } from '@nestjs/common'
import { sql } from 'kysely'
import { AdminDbService } from '../admin-db/admin-db.service.js'

@Controller('health')
export class HealthController {
  constructor(@Inject(AdminDbService) private readonly admin: AdminDbService) {}

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
