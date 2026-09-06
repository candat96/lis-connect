import { Controller, Get, Inject, Param, ParseUUIDPipe, Query, Req, UseGuards } from '@nestjs/common'
import { JwtTenantGuard, withTenant } from '../auth/jwt-tenant.guard.js'
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
@UseGuards(JwtTenantGuard)
export class DevicesController {
  // `@Inject` tường minh vì bản build test dùng esbuild (qua vitest/tsx),
  // và esbuild không phát ra metadata `design:paramtypes` cho
  // `emitDecoratorMetadata` — nếu chỉ dựa vào kiểu tham số, Nest sẽ không
  // giải quyết được token và tiêm `undefined` vào `devices`.
  constructor(@Inject(DevicesService) private readonly devices: DevicesService) {}

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
