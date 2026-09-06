import { describe, expect, it } from 'vitest'
import type { LegacySource } from './legacy-source.js'
import { validateLegacySource } from './legacy-validator.js'

function emptySource(): LegacySource {
  return {
    analytes: [],
    instruments: [],
    insTests: [],
    interfaceParas: [],
    resultMappings: [],
    profiles: [],
    profileTests: [],
    hisMaps: [],
  }
}

function analyte(code: string) {
  return {
    code,
    name: code,
    nameEn: null,
    category: null,
    unit: null,
    normalRangeText: null,
    lowerLimit: null,
    higherLimit: null,
    commentLower: null,
    commentHigher: null,
    commentGray: null,
    criteria: null,
    resultIsComment: false,
    printOrder: null,
    isProfileHead: false,
  }
}

function insTest(insId: number, analyteCode: string, channelCode: string) {
  return {
    insId,
    analyteCode,
    channelCode,
    round: null,
    allowInsert: false,
    omit: false,
    keepHigherSign: false,
    keepLowerSign: false,
  }
}

describe('validateLegacySource', () => {
  it('phát hiện hai xét nghiệm cùng trỏ tới một mã kênh trên cùng máy', () => {
    const src = emptySource()
    src.analytes = [analyte('128LDL'), analyte('244ACo')]
    src.insTests = [insTest(1, '128LDL', '19'), insTest(1, '244ACo', '19')]

    const report = validateLegacySource(src)
    expect(report.channelConflicts).toHaveLength(1)
    expect(report.channelConflicts[0]).toMatchObject({ insId: 1, channelCode: '19' })
    expect(report.channelConflicts[0]!.candidates.sort()).toEqual(['128LDL', '244ACo'])
  })

  it('không coi là xung đột khi cùng mã kênh nhưng khác máy', () => {
    const src = emptySource()
    src.analytes = [analyte('120GLUS')]
    src.insTests = [insTest(1, '120GLUS', '01'), insTest(15, '120GLUS', '01')]
    expect(validateLegacySource(src).channelConflicts).toEqual([])
  })

  it('phát hiện ánh xạ trỏ tới xét nghiệm không có trong danh mục', () => {
    const src = emptySource()
    src.analytes = [analyte('128LDL')]
    src.insTests = [insTest(9, '233GLU', 'GLU')]

    const report = validateLegacySource(src)
    expect(report.orphans).toHaveLength(1)
    expect(report.orphans[0]).toMatchObject({ kind: 'ins-test', analyteCode: '233GLU' })
  })

  it('phát hiện mã HIS trỏ tới xét nghiệm không tồn tại', () => {
    const src = emptySource()
    src.analytes = [analyte('929AG')]
    src.hisMaps = [
      { hisCode: 'A/01', analyteCode: '929AG' },
      { hisCode: 'B/02', analyteCode: '7.82E+04' },
    ]

    const report = validateLegacySource(src)
    expect(report.orphans.filter((o) => o.kind === 'his-map')).toHaveLength(1)
    expect(report.orphans[0]!.analyteCode).toBe('7.82E+04')
  })

  it('phát hiện luật ghi đè trỏ tới xét nghiệm không tồn tại', () => {
    const src = emptySource()
    src.analytes = [analyte('141ULeu')]
    src.resultMappings = [
      { insId: 19, analyteCode: '141ULeu', resultIn: '100', resultOut: '100', comment: null, color: 2 },
      { insId: 19, analyteCode: 'khong-co', resultIn: 'x', resultOut: 'y', comment: null, color: 0 },
    ]
    const report = validateLegacySource(src)
    expect(report.orphans.filter((o) => o.kind === 'result-mapping')).toHaveLength(1)
  })

  it('phát hiện nhóm không có thành viên nào', () => {
    const src = emptySource()
    src.analytes = [analyte('027TB')]
    src.profiles = [
      { code: 'TPTMau22', name: 'Tổng phân tích máu' },
      { code: 'TRONG', name: 'Nhóm rỗng' },
    ]
    src.profileTests = [{ profileCode: 'TPTMau22', analyteCode: '027TB' }]

    expect(validateLegacySource(src).profilesWithoutMembers).toEqual(['TRONG'])
  })

  it('liệt kê các mã chứa ký tự đại diện để cảnh báo', () => {
    const src = emptySource()
    src.analytes = [analyte('034NEU%'), analyte('123AST')]
    expect(validateLegacySource(src).wildcardCodes).toEqual(['034NEU%'])
  })

  it('phát hiện mã xét nghiệm bị lặp trong danh mục', () => {
    const src = emptySource()
    src.analytes = [analyte('123AST'), analyte('123AST')]
    expect(validateLegacySource(src).duplicateAnalyteCodes).toEqual(['123AST'])
  })

  it('nguồn sạch thì mọi danh sách đều rỗng', () => {
    const src = emptySource()
    src.analytes = [analyte('123AST')]
    src.insTests = [insTest(1, '123AST', '01')]
    const report = validateLegacySource(src)
    expect(report.channelConflicts).toEqual([])
    expect(report.orphans).toEqual([])
    expect(report.profilesWithoutMembers).toEqual([])
    expect(report.duplicateAnalyteCodes).toEqual([])
  })
})
