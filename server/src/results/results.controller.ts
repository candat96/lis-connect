import { BadRequestException, Controller, Get, Param, ParseUUIDPipe, Query, UseGuards } from '@nestjs/common';
import { AuthGuard, Ctx, RequestContext } from '../auth/auth.guard';
import { ResultsService, SampleDetail, SampleListItem } from './results.service';

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;
const STATUSES = ['cho-ket-qua', 'cho-duyet', 'da-duyet'];

@Controller('api/samples')
@UseGuards(AuthGuard)
export class ResultsController {
  constructor(private readonly results: ResultsService) {}

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
}
