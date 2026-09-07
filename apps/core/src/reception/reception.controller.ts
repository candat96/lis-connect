import {
  Body,
  Controller,
  Get,
  Inject,
  Param,
  ParseUUIDPipe,
  Patch,
  Post,
  Query,
  Req,
  UseGuards,
} from '@nestjs/common'
import { JwtTenantGuard, withTenant } from '../auth/jwt-tenant.guard.js'
import { ReceptionService } from './reception.service.js'

interface TenantRequest {
  tenantId: string
  userId?: string
  username?: string
}

const DEFAULT_LIMIT = 50
const MAX_LIMIT = 200

/** Không có đường nào lấy toàn bộ danh sách trong một lượt: limit luôn chặn trên. */
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

@Controller('api/reception')
@UseGuards(JwtTenantGuard)
export class ReceptionController {
  // `@Inject` tường minh vì bản build test dùng esbuild (qua vitest/tsx), và
  // esbuild không phát metadata `design:paramtypes` cho `emitDecoratorMetadata`
  // — nếu chỉ dựa vào kiểu tham số, Nest sẽ tiêm `undefined` vào `reception`.
  constructor(@Inject(ReceptionService) private readonly reception: ReceptionService) {}

  // ------------------------------------------------------------- bệnh nhân

  @Get('patients')
  searchPatients(
    @Req() req: TenantRequest,
    @Query('q') q?: string,
    @Query('limit') limit?: string,
    @Query('offset') offset?: string,
  ) {
    return withTenant(req.tenantId, () =>
      this.reception.searchPatients(req.tenantId, {
        q: typeof q === 'string' ? q : undefined,
        limit: parseLimit(limit),
        offset: parseOffset(offset),
      }),
    )
  }

  @Get('patients/:id')
  getPatient(@Req() req: TenantRequest, @Param('id', ParseUUIDPipe) id: string) {
    return withTenant(req.tenantId, () => this.reception.getPatient(req.tenantId, id))
  }

  @Post('patients')
  createPatient(@Req() req: TenantRequest, @Body() body: unknown) {
    return withTenant(req.tenantId, () => this.reception.createPatient(req.tenantId, body))
  }

  @Patch('patients/:id')
  updatePatient(
    @Req() req: TenantRequest,
    @Param('id', ParseUUIDPipe) id: string,
    @Body() body: unknown,
  ) {
    return withTenant(req.tenantId, () => this.reception.updatePatient(req.tenantId, id, body))
  }

  // ----------------------------------------- dịch vụ được phép đưa vào phiếu

  /**
   * CHỈ dịch vụ xét nghiệm. Đường riêng, không dùng lại `/api/catalogue/analytes`,
   * để màn hình tạo chỉ định không thể vô tình hiện dịch vụ khám bệnh khi danh
   * mục sau này nhận thêm dữ liệu từ HIS.
   */
  @Get('orderable-analytes')
  orderableAnalytes(
    @Req() req: TenantRequest,
    @Query('q') q?: string,
    @Query('limit') limit?: string,
    @Query('offset') offset?: string,
  ) {
    return withTenant(req.tenantId, () =>
      this.reception.searchOrderableAnalytes(req.tenantId, {
        q: typeof q === 'string' ? q : undefined,
        limit: parseLimit(limit),
        offset: parseOffset(offset),
      }),
    )
  }

  // ----------------------------------------------------------------- phiếu

  @Post('orders')
  createOrder(@Req() req: TenantRequest, @Body() body: unknown) {
    return withTenant(req.tenantId, () => this.reception.createOrder(req.tenantId, body))
  }

  /**
   * Tra phiếu theo barcode — kể cả khi máy chỉ gửi phần đuôi.
   *
   * Khai TRƯỚC `orders/:id` để không bị route tham số nuốt mất.
   */
  @Get('orders/by-barcode')
  findByBarcode(@Req() req: TenantRequest, @Query('barcode') barcode?: string) {
    return withTenant(req.tenantId, () =>
      this.reception.findOrderByBarcode(req.tenantId, typeof barcode === 'string' ? barcode : ''),
    )
  }

  @Get('orders/:id')
  getOrder(@Req() req: TenantRequest, @Param('id', ParseUUIDPipe) id: string) {
    return withTenant(req.tenantId, () => this.reception.getOrder(req.tenantId, id))
  }
}
