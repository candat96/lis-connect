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
    analyteAliases: [],
  }
}

function analyte(code: string, source: 'legacy' | 'legacy-new' = 'legacy') {
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
    source,
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

describe('validateLegacySource — nhóm dựa trên cờ isProfileHead', () => {
  it('báo mã có isProfileHead nhưng không có thành viên nào', () => {
    const src = emptySource()
    const head = { ...analyte('780FSH'), isProfileHead: true }
    const headWithMember = { ...analyte('766HE3'), isProfileHead: true }
    src.analytes = [head, headWithMember, analyte('027TB')]
    src.profileTests = [{ profileCode: '766HE3', analyteCode: '027TB' }]

    expect(validateLegacySource(src).profilesWithoutMembers).toEqual(['780FSH'])
  })

  it('gộp cả nhóm từ Profile.json lẫn mã có isProfileHead, không đếm trùng', () => {
    const src = emptySource()
    src.analytes = [{ ...analyte('TRONG'), isProfileHead: true }]
    src.profiles = [{ code: 'TRONG', name: 'Nhóm rỗng' }]
    expect(validateLegacySource(src).profilesWithoutMembers).toEqual(['TRONG'])
  })
})

describe('validateLegacySource — mồ côi có tính tới bảng bí danh', () => {
  it('không coi là mồ côi khi mã phân giải được qua bí danh', () => {
    const src = emptySource()
    src.analytes = [analyte('143ProNT')]
    src.analyteAliases = [{ alias: '143ProU', canonical: '143ProNT' }]
    src.resultMappings = [
      { insId: 19, analyteCode: '143ProU', resultIn: '1', resultOut: '1', comment: null, color: 0 },
    ]
    expect(validateLegacySource(src).orphans).toEqual([])
  })

  it('vẫn báo mồ côi, và ghi rõ là nhập nhằng, khi bí danh trỏ tới hai mã chính', () => {
    const src = emptySource()
    src.analytes = [analyte('000CayDNgT'), analyte('000CayDNT')]
    src.analyteAliases = [
      { alias: '000CDNT', canonical: '000CayDNgT' },
      { alias: '000CDNT', canonical: '000CayDNT' },
    ]
    src.hisMaps = [{ hisCode: 'A/01', analyteCode: '000CDNT' }]

    const report = validateLegacySource(src)
    expect(report.orphans).toHaveLength(1)
    expect(report.orphans[0]).toMatchObject({ kind: 'his-map', resolution: 'ambiguous' })
    expect(report.orphans[0]!.candidates).toEqual(['000CayDNT', '000CayDNgT'])
  })

  it('ghi rõ resolution=unknown khi không có bí danh nào dùng được', () => {
    const src = emptySource()
    src.analytes = [analyte('128LDL')]
    src.insTests = [insTest(9, '233GLU', 'GLU')]
    expect(validateLegacySource(src).orphans[0]).toMatchObject({ resolution: 'unknown' })
  })
})

describe('validateLegacySource — báo cáo bí danh nhập nhằng', () => {
  it('báo bí danh trùng tên với một mã chính đã tồn tại', () => {
    const src = emptySource()
    src.analytes = [analyte('000CD'), analyte('000CayDom')]
    src.analytes[0]!.name = 'Cấy dịch tìm vi khuẩn/nấm'
    src.analytes[1]!.name = 'Cấy đờm'
    src.analyteAliases = [{ alias: '000CD', canonical: '000CayDom' }]

    const report = validateLegacySource(src)
    expect(report.shadowedAliases).toEqual([
      {
        alias: '000CD',
        primaryName: 'Cấy dịch tìm vi khuẩn/nấm',
        aliasTargetCode: '000CayDom',
        aliasTargetName: 'Cấy đờm',
      },
    ])
  })

  it('không báo bí danh không trùng với mã chính nào', () => {
    const src = emptySource()
    src.analytes = [analyte('143ProNT')]
    src.analyteAliases = [{ alias: '143ProU', canonical: '143ProNT' }]
    expect(validateLegacySource(src).shadowedAliases).toEqual([])
  })

  it('báo bí danh trỏ tới nhiều mã chính và đánh dấu KHÔNG tự phân giải được', () => {
    const src = emptySource()
    src.analytes = [analyte('000CayDNgT'), analyte('000CayDNT')]
    src.analytes[0]!.name = 'Cấy dịch ngực (T)'
    src.analytes[1]!.name = 'Cấy dịch não tủy'
    src.analyteAliases = [
      { alias: '000CDNT', canonical: '000CayDNgT' },
      { alias: '000CDNT', canonical: '000CayDNT' },
    ]

    const report = validateLegacySource(src)
    expect(report.ambiguousAliases).toHaveLength(1)
    expect(report.ambiguousAliases[0]!.autoResolvable).toBe(false)
    expect(report.ambiguousAliases[0]!.candidates).toEqual([
      { code: '000CayDNT', name: 'Cấy dịch não tủy' },
      { code: '000CayDNgT', name: 'Cấy dịch ngực (T)' },
    ])
  })

  it('không coi là nhập nhằng khi chỉ một trong hai mã chính thật sự tồn tại', () => {
    const src = emptySource()
    src.analytes = [analyte('000CayDNgT')]
    src.analyteAliases = [
      { alias: '000CDNT', canonical: '000CayDNgT' },
      { alias: '000CDNT', canonical: 'khong-ton-tai' },
    ]
    expect(validateLegacySource(src).ambiguousAliases).toEqual([])
  })
})

describe('validateLegacySource — chuyển tiếp báo cáo dòng bị loại', () => {
  it('đưa nguyên danh sách dòng bị loại của bộ đọc vào báo cáo', () => {
    const src = emptySource()
    src.droppedRows = [
      { file: 'TestCode.json', key: 'AutoID=213', code: '780FSH-1', reason: 'thiếu tên xét nghiệm' },
    ]
    expect(validateLegacySource(src).droppedSourceRows).toEqual(src.droppedRows)
  })

  it('bộ nguồn dựng tay không khai trường đó thì báo cáo là mảng rỗng', () => {
    expect(validateLegacySource(emptySource()).droppedSourceRows).toEqual([])
  })
})

describe('validateLegacySource — khử trùng lặp', () => {
  it('mã lặp ba lần chỉ báo một lần', () => {
    const src = emptySource()
    src.analytes = [analyte('123AST'), analyte('123AST'), analyte('123AST')]
    expect(validateLegacySource(src).duplicateAnalyteCodes).toEqual(['123AST'])
  })

  it('mã chứa ký tự đại diện lặp chỉ báo một lần', () => {
    const src = emptySource()
    src.analytes = [analyte('034NEU%'), analyte('034NEU%'), analyte('XNNT21_04')]
    expect(validateLegacySource(src).wildcardCodes).toEqual(['034NEU%', 'XNNT21_04'])
  })
})

describe('validateLegacySource — khoá gộp máy + mã kênh', () => {
  it('không nhầm máy 1 kênh "19" với máy 11 kênh "9"', () => {
    const src = emptySource()
    src.analytes = [analyte('A'), analyte('B')]
    src.insTests = [insTest(1, 'A', '19'), insTest(11, 'B', '9')]
    expect(validateLegacySource(src).channelConflicts).toEqual([])
  })

  it('giữ nguyên mã kênh chứa ký tự phân cách, không tách chuỗi để lấy lại', () => {
    const src = emptySource()
    src.analytes = [analyte('A'), analyte('B')]
    src.insTests = [insTest(1, 'A', 'GLU 2|3'), insTest(1, 'B', 'GLU 2|3')]
    const report = validateLegacySource(src)
    expect(report.channelConflicts).toHaveLength(1)
    expect(report.channelConflicts[0]).toMatchObject({ insId: 1, channelCode: 'GLU 2|3' })
  })
})
