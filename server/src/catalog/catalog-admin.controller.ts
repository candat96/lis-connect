import {
  BadRequestException, Body, ConflictException, Controller, Delete, Get, HttpCode, NotFoundException, Param, ParseUUIDPipe, Post, Put, UseGuards,
} from '@nestjs/common';
import { Type } from 'class-transformer';
import {
  ArrayMaxSize, IsArray, IsBoolean, IsIn, IsInt, IsNotEmpty, IsNumber, IsOptional, IsString, IsUUID, Matches, Max, MaxLength, Min, ValidateNested,
} from 'class-validator';
import { EntityManager } from 'typeorm';
import { AuthGuard, Ctx, RequestContext } from '../auth/auth.guard';
import { requireAdmin } from '../auth/roles';

const VALUE_TYPES = ['so', 'dinh-tinh', 'chu'];
const FLAGS = ['Low', 'High', 'CriticalLow', 'CriticalHigh', 'Abnormal'];

class RangeDto {
  @IsOptional() @IsIn(['M', 'F'], { message: 'Giới tính không hợp lệ' }) sex?: 'M' | 'F' | null;
  @IsOptional() @IsInt() @Min(0) @Max(60000) ageFromDays?: number | null;
  @IsOptional() @IsInt() @Min(0) @Max(60000) ageToDays?: number | null;
  /** Khoảng riêng cho một máy; null = áp cho mọi máy. */
  @IsOptional() @IsUUID() instrumentId?: string | null;
  @IsOptional() @IsNumber() low?: number | null;
  @IsOptional() @IsNumber() high?: number | null;
  @IsOptional() @IsNumber() criticalLow?: number | null;
  @IsOptional() @IsNumber() criticalHigh?: number | null;
  @IsOptional() @IsString() @MaxLength(200) textRange?: string | null;
}

class QualitativeRuleDto {
  @IsOptional() @IsUUID() instrumentId?: string | null;
  @IsString() @IsNotEmpty({ message: 'Nhập giá trị máy gửi' }) @MaxLength(100) rawValue: string;
  @IsString() @IsNotEmpty({ message: 'Nhập chữ hiển thị' }) @MaxLength(100) displayValue: string;
  @IsOptional() @IsIn(FLAGS, { message: 'Cờ không hợp lệ' }) flag?: string | null;
}

class ServiceDto {
  @IsString() @IsNotEmpty({ message: 'Nhập mã dịch vụ' }) @MaxLength(64)
  @Matches(/^\S+$/, { message: 'Mã dịch vụ không được có khoảng trắng' })
  code: string;
  @IsString() @IsNotEmpty({ message: 'Nhập tên dịch vụ' }) @MaxLength(500) name: string;
  @IsOptional() @IsString() @MaxLength(100) groupName?: string | null;
  @IsUUID(undefined, { message: 'Chọn loại mẫu' }) specimenTypeId: string;
  @IsOptional() @IsString() @MaxLength(50) unit?: string | null;
  @IsIn(VALUE_TYPES, { message: 'Kiểu kết quả không hợp lệ' }) valueType: string;
  @IsOptional() @IsInt() @Min(0) @Max(6) decimals?: number | null;
  @IsOptional() @IsInt() @Min(0) @Max(100000) printOrder?: number | null;
  @IsBoolean() isPanel: boolean;
  @IsBoolean() autoApprove: boolean;
  @IsBoolean() active: boolean;
  @IsOptional() @IsString() @MaxLength(64) hisCode?: string | null;
  /** Chỉ số con của gói, theo thứ tự in. Bỏ qua khi không phải gói. */
  @IsArray() @ArrayMaxSize(300) @IsUUID(undefined, { each: true }) childIds: string[];
  @IsArray() @ArrayMaxSize(100) @ValidateNested({ each: true }) @Type(() => RangeDto) ranges: RangeDto[];
  @IsArray() @ArrayMaxSize(100) @ValidateNested({ each: true }) @Type(() => QualitativeRuleDto) qualitativeRules: QualitativeRuleDto[];
}

export interface ServiceDetail {
  id: string;
  code: string;
  name: string;
  groupName: string | null;
  specimenTypeId: string | null;
  unit: string | null;
  valueType: string;
  decimals: number | null;
  printOrder: number | null;
  isPanel: boolean;
  autoApprove: boolean;
  active: boolean;
  hisCode: string | null;
  children: { id: string; code: string; name: string }[];
  ranges: RangeDto[];
  qualitativeRules: QualitativeRuleDto[];
}

export interface CatalogMeta {
  specimenTypes: { id: string; code: string; name: string }[];
  groups: string[];
  instruments: { id: string; name: string }[];
}

/**
 * Sửa danh mục dịch vụ / chỉ số (chỉ Quản trị) — cùng quy tắc với app .NET (CatalogStore, ReferenceRangeValidator):
 * mã không trùng, chỉ số con phải là chỉ số lá và không lồng nhiều cấp, khoảng tham chiếu thấp ≤ cao.
 * Một lần lưu ghi dịch vụ + chỉ số con + khoảng tham chiếu + luật định tính trong cùng giao dịch.
 */
@Controller('api/catalog')
@UseGuards(AuthGuard)
export class CatalogAdminController {
  /** Dữ liệu cho form: loại mẫu, các nhóm đang dùng, danh sách máy (cho khoảng tham chiếu riêng theo máy). */
  @Get('meta')
  async meta(@Ctx() ctx: RequestContext): Promise<CatalogMeta> {
    const [specimenTypes, groups, instruments] = await Promise.all([
      ctx.db.query('SELECT id, code, name FROM specimen_type ORDER BY name'),
      ctx.db.query(`SELECT DISTINCT group_name FROM service WHERE group_name IS NOT NULL AND group_name <> '' ORDER BY 1`),
      ctx.db.query('SELECT id, name FROM instrument ORDER BY name'),
    ]);
    return { specimenTypes, groups: groups.map((g: { group_name: string }) => g.group_name), instruments };
  }

  @Get('services/:id')
  async detail(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string): Promise<ServiceDetail> {
    const [s] = await ctx.db.query(
      `SELECT id, code, name, group_name, specimen_type_id, unit, value_type, decimals, print_order, is_panel, auto_approve, active, his_code
       FROM service WHERE id = $1`, [id]);
    if (!s) throw new NotFoundException('Không tìm thấy dịch vụ');

    const [children, ranges, rules] = await Promise.all([
      ctx.db.query(
        'SELECT sv.id, sv.code, sv.name FROM service_child sc JOIN service sv ON sv.id = sc.child_id WHERE sc.parent_id = $1 ORDER BY sc.sort_order, sv.code', [id]),
      ctx.db.query(
        `SELECT sex, age_from_days, age_to_days, instrument_id, low::float8 AS low, high::float8 AS high,
                critical_low::float8 AS critical_low, critical_high::float8 AS critical_high, text_range
         FROM reference_range WHERE service_id = $1 ORDER BY instrument_id NULLS FIRST, sex NULLS FIRST, age_from_days NULLS FIRST, created_at`, [id]),
      ctx.db.query('SELECT instrument_id, raw_value, display_value, flag FROM qualitative_rule WHERE service_id = $1 ORDER BY raw_value', [id]),
    ]);

    return {
      id: s.id, code: s.code, name: s.name, groupName: s.group_name, specimenTypeId: s.specimen_type_id, unit: s.unit, valueType: s.value_type,
      decimals: s.decimals, printOrder: s.print_order, isPanel: s.is_panel, autoApprove: s.auto_approve, active: s.active, hisCode: s.his_code,
      children,
      ranges: ranges.map((r: Record<string, never>) => ({
        sex: r.sex, ageFromDays: r.age_from_days, ageToDays: r.age_to_days, instrumentId: r.instrument_id,
        low: r.low, high: r.high, criticalLow: r.critical_low, criticalHigh: r.critical_high, textRange: r.text_range,
      })),
      qualitativeRules: rules.map((r: Record<string, never>) => ({ instrumentId: r.instrument_id, rawValue: r.raw_value, displayValue: r.display_value, flag: r.flag })),
    };
  }

  @Post('services')
  async create(@Ctx() ctx: RequestContext, @Body() body: ServiceDto): Promise<{ id: string }> {
    requireAdmin(ctx);
    return { id: await this.save(ctx, null, body) };
  }

  @Put('services/:id')
  @HttpCode(204)
  async update(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string, @Body() body: ServiceDto): Promise<void> {
    requireAdmin(ctx);
    await this.save(ctx, id, body);
  }

  /**
   * Xoá dịch vụ chưa từng được dùng. Đã có trong phiếu chỉ định, đang map kênh máy, hoặc đang là chỉ số con
   * của một gói thì KHÔNG xoá — báo rõ lý do để người dùng gỡ trước hoặc chuyển sang "Ngừng dùng".
   */
  @Delete('services/:id')
  @HttpCode(204)
  async remove(@Ctx() ctx: RequestContext, @Param('id', new ParseUUIDPipe()) id: string): Promise<void> {
    requireAdmin(ctx);
    await ctx.db.transaction(async (tx) => {
      const [s] = await tx.query('SELECT name FROM service WHERE id = $1 FOR UPDATE', [id]);
      if (!s) throw new NotFoundException('Không tìm thấy dịch vụ');

      const [used] = await tx.query('SELECT count(*)::int AS n FROM order_item WHERE service_id = $1', [id]);
      if (used.n > 0) throw new ConflictException(`Dịch vụ đã có trong ${used.n} dòng chỉ định nên không xoá được — hãy chuyển sang "Ngừng dùng"`);

      const parents: { name: string }[] = await tx.query(
        'SELECT sv.name FROM service_child sc JOIN service sv ON sv.id = sc.parent_id WHERE sc.child_id = $1 ORDER BY sv.name', [id]);
      if (parents.length > 0) throw new ConflictException(`Đang là chỉ số con của gói "${parents.map((p) => p.name).join('", "')}" — gỡ khỏi gói trước khi xoá`);

      const machines: { name: string }[] = await tx.query(
        'SELECT DISTINCT i.name FROM instrument_channel ic JOIN instrument i ON i.id = ic.instrument_id WHERE ic.service_id = $1 ORDER BY 1', [id]);
      if (machines.length > 0) throw new ConflictException(`Đang được map kênh ở máy ${machines.map((m) => m.name).join(', ')} — xoá kênh đó trước khi xoá dịch vụ`);

      await tx.query('DELETE FROM instrument_service WHERE service_id = $1', [id]);
      await tx.query('DELETE FROM service_child WHERE parent_id = $1', [id]);
      await tx.query('DELETE FROM reference_range WHERE service_id = $1', [id]);
      await tx.query('DELETE FROM qualitative_rule WHERE service_id = $1', [id]);
      await tx.query('DELETE FROM service WHERE id = $1', [id]);
    });
  }

  private async save(ctx: RequestContext, id: string | null, body: ServiceDto): Promise<string> {
    const problems = validateRanges(body.ranges);
    if (problems.length > 0) throw new BadRequestException(problems.join('; '));
    const duplicateRule = firstDuplicate(body.qualitativeRules.map((r) => `${r.instrumentId ?? ''}|${r.rawValue.trim().toLowerCase()}`));
    if (duplicateRule) throw new BadRequestException(`Luật định tính bị lặp giá trị máy gửi "${duplicateRule.split('|')[1]}"`);

    try {
      return await ctx.db.transaction(async (tx) => {
        const params = [
          body.code.trim(), body.name.trim(), body.groupName?.trim() || null, body.specimenTypeId, body.isPanel ? null : body.unit?.trim() || null,
          body.valueType, body.isPanel ? null : body.decimals ?? null, body.printOrder ?? 0, body.isPanel, body.isPanel ? false : body.autoApprove,
          body.active, body.hisCode?.trim() || null,
        ];
        let serviceId = id;
        if (serviceId === null) {
          const [row] = await tx.query(
            `INSERT INTO service (code, name, group_name, specimen_type_id, unit, value_type, decimals, print_order, is_panel, auto_approve, active, his_code)
             VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12) RETURNING id`, params);
          serviceId = row.id as string;
        } else {
          const [, affected] = await tx.query(
            `UPDATE service SET code = $1, name = $2, group_name = $3, specimen_type_id = $4, unit = $5, value_type = $6, decimals = $7,
               print_order = $8, is_panel = $9, auto_approve = $10, active = $11, his_code = $12 WHERE id = $13`, [...params, serviceId]);
          if (affected === 0) throw new NotFoundException('Không tìm thấy dịch vụ');
        }

        await this.saveChildren(tx, serviceId, body.isPanel ? [...new Set(body.childIds)] : [], body.isPanel);

        // Gói không có giá trị riêng nên không giữ khoảng tham chiếu / luật định tính.
        await tx.query('DELETE FROM reference_range WHERE service_id = $1', [serviceId]);
        await tx.query('DELETE FROM qualitative_rule WHERE service_id = $1', [serviceId]);
        if (!body.isPanel) {
          for (const r of body.ranges) {
            await tx.query(
              `INSERT INTO reference_range (service_id, sex, age_from_days, age_to_days, instrument_id, low, high, critical_low, critical_high, text_range)
               VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)`,
              [serviceId, r.sex ?? null, r.ageFromDays ?? null, r.ageToDays ?? null, r.instrumentId ?? null, r.low ?? null, r.high ?? null,
                r.criticalLow ?? null, r.criticalHigh ?? null, r.textRange?.trim() || null]);
          }
          if (body.valueType === 'dinh-tinh') {
            for (const r of body.qualitativeRules) {
              await tx.query(
                'INSERT INTO qualitative_rule (instrument_id, service_id, raw_value, display_value, flag) VALUES ($1, $2, $3, $4, $5)',
                [r.instrumentId ?? null, serviceId, r.rawValue.trim(), r.displayValue.trim(), r.flag ?? null]);
            }
          }
        }
        return serviceId;
      });
    } catch (error) {
      const code = (error as { code?: string }).code;
      if (code === '23505') throw new ConflictException('Mã dịch vụ đã tồn tại');
      if (code === '23503') throw new BadRequestException('Loại mẫu, máy hoặc chỉ số con không tồn tại');
      throw error;
    }
  }

  /** Đặt lại toàn bộ chỉ số con của gói theo đúng thứ tự. Con phải là chỉ số lá; không lồng nhiều cấp. */
  private async saveChildren(tx: EntityManager, serviceId: string, childIds: string[], isPanel: boolean): Promise<void> {
    if (isPanel) {
      if (childIds.includes(serviceId)) throw new BadRequestException('Dịch vụ không thể là con của chính nó');
      const [asChild] = await tx.query('SELECT sv.name FROM service_child sc JOIN service sv ON sv.id = sc.parent_id WHERE sc.child_id = $1 LIMIT 1', [serviceId]);
      if (asChild) throw new BadRequestException(`Dịch vụ này đang là chỉ số con của gói "${asChild.name}" nên không thể thành gói`);
      if (childIds.length > 0) {
        const panels: { name: string }[] = await tx.query('SELECT name FROM service WHERE id = ANY($1) AND is_panel = true', [childIds]);
        if (panels.length > 0) throw new BadRequestException(`Chỉ số con không được là một gói khác: ${panels.map((p) => p.name).join(', ')}`);
      }
    }
    await tx.query('DELETE FROM service_child WHERE parent_id = $1', [serviceId]);
    for (let i = 0; i < childIds.length; i++) {
      await tx.query('INSERT INTO service_child (parent_id, child_id, sort_order) VALUES ($1, $2, $3)', [serviceId, childIds[i], i]);
    }
  }
}

/** Thấp ≤ cao; nguy hiểm thấp ≤ thấp; cao ≤ nguy hiểm cao; tuổi từ ≤ tuổi đến (như ReferenceRangeValidator). */
export function validateRanges(ranges: RangeDto[]): string[] {
  const errors: string[] = [];
  const has = (v: number | null | undefined): v is number => v !== null && v !== undefined;
  ranges.forEach((r, i) => {
    const row = `Khoảng tham chiếu dòng ${i + 1}`;
    if (has(r.low) && has(r.high) && r.low > r.high) errors.push(`${row}: giá trị thấp phải nhỏ hơn hoặc bằng giá trị cao`);
    if (has(r.criticalLow) && has(r.low) && r.criticalLow > r.low) errors.push(`${row}: nguy hiểm thấp phải nhỏ hơn hoặc bằng giá trị thấp`);
    if (has(r.high) && has(r.criticalHigh) && r.high > r.criticalHigh) errors.push(`${row}: giá trị cao phải nhỏ hơn hoặc bằng nguy hiểm cao`);
    if (has(r.ageFromDays) && has(r.ageToDays) && r.ageFromDays > r.ageToDays) errors.push(`${row}: tuổi từ phải nhỏ hơn hoặc bằng tuổi đến`);
    if (!has(r.low) && !has(r.high) && !has(r.criticalLow) && !has(r.criticalHigh) && !r.textRange?.trim()) {
      errors.push(`${row}: nhập ít nhất một giá trị hoặc chữ tham chiếu`);
    }
  });
  return errors;
}

function firstDuplicate(keys: string[]): string | null {
  const seen = new Set<string>();
  for (const key of keys) {
    if (seen.has(key)) return key;
    seen.add(key);
  }
  return null;
}
