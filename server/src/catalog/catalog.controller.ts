import { Controller, Get, UseGuards } from '@nestjs/common';
import { AuthGuard, Ctx, RequestContext } from '../auth/auth.guard';
import { formatRange } from '../common/reference-range';
import { ReferenceRangeEntity, Service, ServiceChild, SpecimenType } from '../entities';

export interface CatalogService {
  id: string;
  code: string;
  name: string;
  groupName: string | null;
  specimenName: string | null;
  unit: string | null;
  valueType: string;
  decimals: number | null;
  isPanel: boolean;
  autoApprove: boolean;
  active: boolean;
  /** Khoảng tham chiếu gộp để xem nhanh (vd "Nam: 130 – 170 · Nữ: 120 – 150"). */
  rangeText: string | null;
  /** Chỉ số con của gói, theo thứ tự in. */
  children: CatalogService[];
}

@Controller('api/catalog')
@UseGuards(AuthGuard)
export class CatalogController {
  /** Cây dịch vụ: dịch vụ gốc (kèm chỉ số con nếu là gói), theo nhóm rồi mã. Chỉ số con không lặp lại ở gốc. */
  @Get('services')
  async services(@Ctx() ctx: RequestContext): Promise<CatalogService[]> {
    const [services, links, specimens, ranges] = await Promise.all([
      ctx.db.getRepository(Service).find({ order: { code: 'ASC' } }),
      ctx.db.getRepository(ServiceChild).find({ order: { sortOrder: 'ASC' } }),
      ctx.db.getRepository(SpecimenType).find(),
      ctx.db.getRepository(ReferenceRangeEntity).find(),
    ]);

    const specimenName = new Map(specimens.map((s) => [s.id, s.name]));
    const rangesByService = new Map<string, ReferenceRangeEntity[]>();
    for (const range of ranges) rangesByService.set(range.serviceId, [...(rangesByService.get(range.serviceId) ?? []), range]);

    const toDto = (service: Service): CatalogService => ({
      id: service.id, code: service.code, name: service.name, groupName: service.groupName,
      specimenName: service.specimenTypeId ? specimenName.get(service.specimenTypeId) ?? null : null,
      unit: service.unit, valueType: service.valueType, decimals: service.decimals, isPanel: service.isPanel,
      autoApprove: service.autoApprove, active: service.active,
      rangeText: summarizeRanges(rangesByService.get(service.id) ?? [], service.decimals),
      children: [],
    });

    const byId = new Map(services.map((s) => [s.id, toDto(s)]));
    const childIds = new Set<string>();
    for (const link of links) {
      const parent = byId.get(link.parentId);
      const child = byId.get(link.childId);
      if (parent && child) {
        parent.children.push(child);
        childIds.add(link.childId);
      }
    }

    return [...byId.values()]
      .filter((s) => !childIds.has(s.id))
      .sort((a, b) => (a.groupName ?? '').localeCompare(b.groupName ?? '', 'vi') || a.code.localeCompare(b.code));
  }
}

function summarizeRanges(ranges: ReferenceRangeEntity[], decimals: number | null): string | null {
  const general = ranges.filter((r) => r.instrumentId === null && r.ageFromDays === null && r.ageToDays === null);
  const label = { M: 'Nam', F: 'Nữ' } as const;
  const parts = general
    .map((r) => {
      const text = formatRange(r.textRange, r.low, r.high, decimals);
      return text === null ? null : r.sex ? `${label[r.sex]}: ${text}` : text;
    })
    .filter((p): p is string => p !== null);
  if (parts.length > 0) return parts.join(' · ');
  return ranges.length > 0 ? 'Theo tuổi / máy' : null;
}
