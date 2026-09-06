import { describe, expect, it } from 'vitest'
import { buildAliasIndex, resolveAnalyteCode } from './legacy-alias.js'
import type { LegacySource } from './legacy-source.js'

function sourceWith(aliases: { alias: string; canonical: string }[]): LegacySource {
  return {
    analytes: [],
    instruments: [],
    insTests: [],
    interfaceParas: [],
    resultMappings: [],
    profiles: [],
    profileTests: [],
    hisMaps: [],
    analyteAliases: aliases,
  }
}

describe('buildAliasIndex', () => {
  it('gộp các bí danh trỏ tới nhiều mã chính vào cùng một mục', () => {
    const index = buildAliasIndex(
      sourceWith([
        { alias: '000CDNT', canonical: '111CDNguc' },
        { alias: '000CDNT', canonical: '222CDNaoTuy' },
        { alias: 'XNNT21_04', canonical: '900XN04' },
      ]),
    )
    expect(index.get('000CDNT')).toEqual(['111CDNguc', '222CDNaoTuy'])
    expect(index.get('XNNT21_04')).toEqual(['900XN04'])
  })

  it('bỏ qua bí danh tự trỏ vào chính nó và bí danh rỗng', () => {
    const index = buildAliasIndex(
      sourceWith([
        { alias: '123AST', canonical: '123AST' },
        { alias: '  ', canonical: '123AST' },
      ]),
    )
    expect(index.size).toBe(0)
  })

  it('không có bảng bí danh thì chỉ mục rỗng, không ném lỗi', () => {
    const src = sourceWith([])
    delete (src as unknown as Record<string, unknown>).analyteAliases
    expect(buildAliasIndex(src).size).toBe(0)
  })
})

describe('resolveAnalyteCode', () => {
  const primary = new Set(['000CD', '333CDom', '900XN04'])

  it('mã chính luôn thắng bí danh trùng tên', () => {
    // 000CD mã chính là "cấy dịch"; bí danh cùng tên trỏ tới "cấy đờm" (333CDom).
    // Đảo thứ tự là gán nhầm tên xét nghiệm cho bệnh nhân.
    const index = buildAliasIndex(sourceWith([{ alias: '000CD', canonical: '333CDom' }]))
    expect(resolveAnalyteCode('000CD', primary, index)).toEqual({ kind: 'primary', code: '000CD' })
  })

  it('quy bí danh về mã chính khi mã không có trong danh mục', () => {
    const index = buildAliasIndex(sourceWith([{ alias: 'XNNT21_04', canonical: '900XN04' }]))
    expect(resolveAnalyteCode('XNNT21_04', primary, index)).toEqual({
      kind: 'alias',
      code: '900XN04',
      via: 'XNNT21_04',
    })
  })

  it('bí danh trỏ tới hai mã chính thì không tự chọn', () => {
    const index = buildAliasIndex(
      sourceWith([
        { alias: '000CDNT', canonical: '333CDom' },
        { alias: '000CDNT', canonical: '900XN04' },
      ]),
    )
    expect(resolveAnalyteCode('000CDNT', primary, index)).toEqual({
      kind: 'ambiguous',
      candidates: ['333CDom', '900XN04'],
    })
  })

  it('bí danh trỏ tới mã không tồn tại thì coi như không phân giải được', () => {
    const index = buildAliasIndex(sourceWith([{ alias: 'hc12', canonical: 'khong-co-that' }]))
    expect(resolveAnalyteCode('hc12', primary, index)).toEqual({ kind: 'unknown' })
  })

  it('mã hoàn toàn xa lạ trả về unknown', () => {
    expect(resolveAnalyteCode('233GLU', primary, new Map())).toEqual({ kind: 'unknown' })
  })
})
