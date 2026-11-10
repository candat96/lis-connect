import { BadRequestException, Body, Controller, Get, HttpCode, Param, ParseUUIDPipe, Post, Put, Query, UseGuards } from '@nestjs/common';
import { IsNotEmpty, IsOptional, IsString, MaxLength } from 'class-validator';
import { AuthGuard, Ctx, RequestContext } from '../auth/auth.guard';
import { requireResultEditor, requireUnapprover } from '../auth/roles';
import { HistoryEntry, ResultsWriteService } from './results-write.service';
import { ResultsService, SampleDetail, SampleListItem } from './results.service';

class SetValueDto {
  @IsString() @MaxLength(500) value: string;
  /** Bắt buộc khi ô đã có giá trị. */
  @IsOptional() @IsString() @MaxLength(500) reason?: string | null;
}

class ReasonDto {
  @IsString() @IsNotEmpty({ message: 'Nhập lý do' }) @MaxLength(500) reason: string;
}

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;
const STATUSES = ['cho-ket-qua', 'cho-duyet', 'da-duyet'];

@Controller('api/samples')
@UseGuards(AuthGuard)
export class ResultsController {
  constructor(private readonly results: ResultsService, private readonly writer: ResultsWriteService) {}

  /** Danh sách mẫu theo ngày chỉ định (yyyy-MM-dd), trạng thái, và ô tìm. */
  @Get()
  list(
    @Ctx() ctx: RequestContext,
    @Query('from') from: string,
    @Query('to') to: string,
    @Query('status') status?: string,
    @Query('q') query?: string,
  ): Promise<SampleListItem[]> {
    if (!ISO_DATE.test(from ?? '') || !ISO_DATE.test(to ?? '') || from > to) {
      throw new BadRequestException('Khoảng ngày không hợp lệ (from, to dạng yyyy-MM-dd)');
    }
    if (status && !STATUSES.includes(status)) throw new BadRequestException('Trạng thái không hợp lệ');
    return this.results.list(ctx.db, from, to, status, query);
  }

  @Get(':id')
  detail(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string): Promise<SampleDetail> {
    return this.results.detail(ctx.db, id);
  }

  /** Duyệt mẫu: mọi kết quả đang có giá trị thành đã duyệt. */
  @Post(':id/approve')
  @HttpCode(204)
  async approve(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string): Promise<void> {
    requireResultEditor(ctx);
    await this.writer.approve(ctx.db, id, ctx.user.id);
  }

  /** Huỷ duyệt (bắt buộc lý do) — chỉ Trưởng khoa / Quản trị. */
  @Post(':id/unapprove')
  @HttpCode(204)
  async unapprove(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string, @Body() body: ReasonDto): Promise<void> {
    requireUnapprover(ctx);
    await this.writer.unapprove(ctx.db, id, ctx.user.id, body.reason);
  }
}

/** Thao tác trên từng chỉ số của mẫu. */
@Controller('api/results')
@UseGuards(AuthGuard)
export class ResultItemsController {
  constructor(private readonly writer: ResultsWriteService) {}

  /** Nhập / sửa tay một giá trị. Ô đã có giá trị thì phải kèm lý do. */
  @Put('items/:orderItemId')
  @HttpCode(204)
  async setValue(@Ctx() ctx: RequestContext, @Param('orderItemId', new ParseUUIDPipe()) orderItemId: string, @Body() body: SetValueDto): Promise<void> {
    requireResultEditor(ctx);
    await this.writer.setManual(ctx.db, orderItemId, body.value, ctx.user.id, body.reason ?? null);
  }

  @Get('items/:orderItemId/history')
  history(@Ctx() ctx: RequestContext, @Param('orderItemId', new ParseUUIDPipe()) orderItemId: string): Promise<HistoryEntry[]> {
    return this.writer.itemHistory(ctx.db, orderItemId);
  }

  /** Dùng giá trị máy gửi lại làm giá trị chính thức (mẫu về chưa duyệt). */
  @Post(':resultId/pending/accept')
  @HttpCode(204)
  async acceptPending(@Ctx() ctx: RequestContext, @Param('resultId', new ParseUUIDPipe()) resultId: string): Promise<void> {
    requireResultEditor(ctx);
    await this.writer.acceptPending(ctx.db, resultId, ctx.user.id);
  }

  /** Bỏ giá trị máy gửi lại, giữ giá trị đã duyệt. */
  @Post(':resultId/pending/reject')
  @HttpCode(204)
  async rejectPending(@Ctx() ctx: RequestContext, @Param('resultId', new ParseUUIDPipe()) resultId: string): Promise<void> {
    requireResultEditor(ctx);
    await this.writer.rejectPending(ctx.db, resultId, ctx.user.id);
  }
}
