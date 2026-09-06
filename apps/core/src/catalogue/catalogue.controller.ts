import {
  Body,
  Controller,
  Delete,
  Get,
  HttpCode,
  Inject,
  Param,
  ParseUUIDPipe,
  Patch,
  Post,
  Put,
  Query,
  Req,
  UseGuards,
} from '@nestjs/common'
import { JwtTenantGuard, withTenant } from '../auth/jwt-tenant.guard.js'
import { CatalogueService } from './catalogue.service.js'

interface TenantRequest {
  tenantId: string
}

const DEFAULT_LIMIT = 50
const MAX_LIMIT = 200

/**
 * Danh mục thật có hơn 2600 xét nghiệm, nên limit luôn bị chặn trên: không có
 * đường nào lấy được toàn bộ danh mục trong một lượt.
 */
function parseLimit(raw: string | undefined): number {
  const value = Number(raw ?? DEFAULT_LIMIT)
  if (!Number.isFinite(value) || value < 1) return DEFAULT_LIMIT
  return Math.min(Math.floor(value), MAX_LIMIT)
}

function parseOffset(raw: string | undefined): number {
  const value = Number(raw ?? 0)
  if (!Number.isFinite(value) || value < 0) return 0
  return Math.floor(value)
}

@Controller('api/catalogue')
@UseGuards(JwtTenantGuard)
export class CatalogueController {
  // `@Inject` tường minh vì bản build test dùng esbuild (qua vitest/tsx), và
  // esbuild không phát metadata `design:paramtypes` cho `emitDecoratorMetadata`
  // — nếu chỉ dựa vào kiểu tham số, Nest sẽ tiêm `undefined` vào `catalogue`.
  constructor(@Inject(CatalogueService) private readonly catalogue: CatalogueService) {}

  @Get('analytes')
  searchAnalytes(
    @Req() req: TenantRequest,
    @Query('q') q?: string,
    @Query('category') category?: string,
    @Query('limit') limit?: string,
    @Query('offset') offset?: string,
  ) {
    return withTenant(req.tenantId, () =>
      this.catalogue.searchAnalytes(req.tenantId, {
        q: typeof q === 'string' ? q : undefined,
        category: typeof category === 'string' && category !== '' ? category : undefined,
        limit: parseLimit(limit),
        offset: parseOffset(offset),
      }),
    )
  }

  @Get('categories')
  categories(@Req() req: TenantRequest) {
    return withTenant(req.tenantId, () => this.catalogue.listCategories(req.tenantId))
  }

  // `:code` giữ nguyên mã nghiệp vụ của hệ cũ, có thể chứa `%` hoặc `_` nên
  // client phải mã hoá URL; Express giải mã lại trước khi tới đây.
  @Patch('analytes/:code')
  updateAnalyte(@Req() req: TenantRequest, @Param('code') code: string, @Body() body: unknown) {
    return withTenant(req.tenantId, () => this.catalogue.updateAnalyte(req.tenantId, code, body))
  }

  @Get('devices/:deviceId/channels')
  listChannels(@Req() req: TenantRequest, @Param('deviceId', ParseUUIDPipe) deviceId: string) {
    return withTenant(req.tenantId, () => this.catalogue.listChannels(req.tenantId, deviceId))
  }

  @Put('devices/:deviceId/channels/:channelCode')
  putChannel(
    @Req() req: TenantRequest,
    @Param('deviceId', ParseUUIDPipe) deviceId: string,
    @Param('channelCode') channelCode: string,
    @Body() body: unknown,
  ) {
    return withTenant(req.tenantId, () =>
      this.catalogue.putChannel(req.tenantId, deviceId, channelCode, body),
    )
  }

  @Delete('devices/:deviceId/channels/:channelCode')
  @HttpCode(204)
  deleteChannel(
    @Req() req: TenantRequest,
    @Param('deviceId', ParseUUIDPipe) deviceId: string,
    @Param('channelCode') channelCode: string,
  ) {
    return withTenant(req.tenantId, () =>
      this.catalogue.deleteChannel(req.tenantId, deviceId, channelCode),
    )
  }

  @Get('devices/:deviceId/result-rules')
  listResultRules(@Req() req: TenantRequest, @Param('deviceId', ParseUUIDPipe) deviceId: string) {
    return withTenant(req.tenantId, () => this.catalogue.listResultRules(req.tenantId, deviceId))
  }

  @Post('result-rules')
  createResultRule(@Req() req: TenantRequest, @Body() body: unknown) {
    return withTenant(req.tenantId, () => this.catalogue.createResultRule(req.tenantId, body))
  }

  @Put('result-rules/:id')
  updateResultRule(
    @Req() req: TenantRequest,
    @Param('id', ParseUUIDPipe) id: string,
    @Body() body: unknown,
  ) {
    return withTenant(req.tenantId, () => this.catalogue.updateResultRule(req.tenantId, id, body))
  }

  @Delete('result-rules/:id')
  @HttpCode(204)
  deleteResultRule(@Req() req: TenantRequest, @Param('id', ParseUUIDPipe) id: string) {
    return withTenant(req.tenantId, () => this.catalogue.deleteResultRule(req.tenantId, id))
  }
}
