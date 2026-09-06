import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanString, isAnalyteActive, loadLegacySource } from './legacy-source.js'

let dir: string

function write(name: string, rows: unknown[]): void {
  writeFileSync(join(dir, name), JSON.stringify(rows), 'utf8')
}

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'lis-legacy-'))
  write('TestCode.json', [])
  write('TestCodeNew.json', [])
  write('Instrument.json', [])
  write('InsTest.json', [])
  write('InterfacePara.json', [])
  write('ResultMapping.json', [])
  write('Profile.json', [])
  write('ProfileTest.json', [])
  write('TestCode_HIS.json', [])
})

afterEach(() => {
  rmSync(dir, { recursive: true, force: true })
})

describe('cleanString', () => {
  it('cắt khoảng trắng thừa của cột CHAR trong SQL Server', () => {
    expect(cleanString('141ULeu        ')).toBe('141ULeu')
    expect(cleanString('P    ')).toBe('P')
  })

  it('trả null cho chuỗi rỗng hoặc toàn khoảng trắng', () => {
    expect(cleanString('')).toBeNull()
    expect(cleanString('   ')).toBeNull()
    expect(cleanString(null)).toBeNull()
    expect(cleanString(undefined)).toBeNull()
  })

  it('giữ nguyên ký tự phần trăm trong mã', () => {
    expect(cleanString('034NEU%')).toBe('034NEU%')
  })
})

describe('loadLegacySource', () => {
  it('đọc được danh mục xét nghiệm từ TestCode.json với source=legacy', () => {
    write('TestCode.json', [
      { TestCode: '123AST  ', TestName: 'AST (GOT)', Unit: 'U/L ', GetComment: false },
    ])
    const src = loadLegacySource(dir)
    expect(src.analytes).toHaveLength(1)
    expect(src.analytes[0]!.code).toBe('123AST')
    expect(src.analytes[0]!.unit).toBe('U/L')
    expect(src.analytes[0]!.source).toBe('legacy')
  })

  it('đọc ánh xạ mã kênh và cắt khoảng trắng ở cả hai mã', () => {
    write('InsTest.json', [
      { InsID: 1, TestCode: '128LDL ', TestcodeIN: ' 19', Round: 2, Omit: false, AllowInsert: false },
    ])
    const src = loadLegacySource(dir)
    expect(src.insTests[0]).toMatchObject({ insId: 1, analyteCode: '128LDL', channelCode: '19', round: 2 })
  })

  it('đọc luật ghi đè kết quả, bỏ qua dòng có mã rỗng', () => {
    write('ResultMapping.json', [
      { InstrumentID: 19, TestCode: '141ULeu        ', ResultIn: '100', Result: '100', Comment: 'pos 2+', Color: 2 },
      { InstrumentID: 19, TestCode: null, ResultIn: 'x', Result: 'y', Comment: null, Color: 0 },
    ])
    const src = loadLegacySource(dir)
    expect(src.resultMappings).toHaveLength(1)
    expect(src.resultMappings[0]!.analyteCode).toBe('141ULeu')
  })

  it('đọc luật áp dụng cho mọi máy khi InstrumentID rỗng (ví dụ diễn giải TPHA)', () => {
    write('ResultMapping.json', [
      { InstrumentID: null, TestCode: '131TPHA', ResultIn: '1:160', Result: '1:160', Comment: 'Dương tính', Color: 0 },
      { InstrumentID: 19, TestCode: '141ULeu', ResultIn: '100', Result: '100', Comment: null, Color: 0 },
    ])
    const src = loadLegacySource(dir)
    expect(src.resultMappings).toHaveLength(2)
    expect(src.resultMappings[0]!.insId).toBeNull()
    expect(src.resultMappings[0]!.analyteCode).toBe('131TPHA')
    expect(src.resultMappings[1]!.insId).toBe(19)
  })

  it('đọc máy xét nghiệm kèm giao thức', () => {
    write('Instrument.json', [
      { InsID: 1, InsName: 'AU640', ProtocolID: 'AU', ConType: 'C', LocationID: 'DK10', Active: true },
    ])
    const src = loadLegacySource(dir)
    expect(src.instruments[0]).toMatchObject({ insId: 1, name: 'AU640', protocol: 'AU', conType: 'C' })
  })

  it('đọc cấu hình cổng', () => {
    write('InterfacePara.json', [
      { HostID: 'DK10.1', InstrumentID: 1, Settings: '9600,8,N,1', CommPort: 6, RemoteIP: null, RemotePort: null },
    ])
    const src = loadLegacySource(dir)
    expect(src.interfaceParas[0]).toMatchObject({ insId: 1, hostId: 'DK10.1', commPort: 6 })
  })

  it('đọc ánh xạ mã HIS', () => {
    write('TestCode_HIS.json', [{ TestCodeHIS: 'A/01', Testcode: '929AG' }])
    const src = loadLegacySource(dir)
    expect(src.hisMaps[0]).toMatchObject({ hisCode: 'A/01', analyteCode: '929AG' })
  })

  it('đọc được danh mục từ TestCodeNew.json với source=legacy-new', () => {
    write('TestCode.json', [])
    write('TestCodeNew.json', [
      { TestCode: '131TPHA', TestName: 'Giang mai (TPHA)', GetComment: false },
    ])
    const src = loadLegacySource(dir)
    expect(src.analytes).toHaveLength(1)
    expect(src.analytes[0]!.code).toBe('131TPHA')
    expect(src.analytes[0]!.source).toBe('legacy-new')
  })

  it('ưu tiên TestCode nếu mã xuất hiện ở cả hai bảng', () => {
    write('TestCode.json', [
      { TestCode: '123AST', TestName: 'AST từ TestCode', Unit: 'U/L', GetComment: false },
    ])
    write('TestCodeNew.json', [
      { TestCode: '123AST', TestName: 'AST từ TestCodeNew', Unit: 'IU/L', GetComment: false },
      { TestCode: '131TPHA', TestName: 'TPHA từ TestCodeNew', GetComment: false },
    ])
    const src = loadLegacySource(dir)
    expect(src.analytes).toHaveLength(2)
    const ast = src.analytes.find((a) => a.code === '123AST')!
    expect(ast.name).toBe('AST từ TestCode')
    expect(ast.source).toBe('legacy')
    const tpha = src.analytes.find((a) => a.code === '131TPHA')!
    expect(tpha.source).toBe('legacy-new')
  })

  it('đọc được ánh xạ mã đổi tên (bí danh) từ TestCodeNew', () => {
    write('TestCode.json', [])
    write('TestCodeNew.json', [
      { TestCode: '143ProNT', TestName: 'Protein niệu', TestCodeNew: '143ProU', GetComment: false },
      { TestCode: '144GluNT', TestName: 'Glucose niệu', TestCodeNew: '144GluU', GetComment: false },
      { TestCode: '145Ketone', TestName: 'Ketone', TestCodeNew: null, GetComment: false }, // Không có bí danh
    ])
    const src = loadLegacySource(dir)
    expect(src.analyteAliases).toHaveLength(2)
    expect(src.analyteAliases).toContainEqual({ alias: '143ProU', canonical: '143ProNT' })
    expect(src.analyteAliases).toContainEqual({ alias: '144GluU', canonical: '144GluNT' })
  })

  it('mã chính LUÔN thắng bí danh khi xuất hiện cùng lúc', () => {
    write('TestCode.json', [
      { TestCode: '143ProU', TestName: 'Mã chính: Protein', GetComment: false },
    ])
    write('TestCodeNew.json', [
      { TestCode: '143ProNT', TestName: 'Từ TestCodeNew', TestCodeNew: '143ProU', GetComment: false },
    ])
    const src = loadLegacySource(dir)
    // Phải có 2 xét nghiệm (một từ TestCode, một từ TestCodeNew)
    expect(src.analytes).toHaveLength(2)
    // Bí danh phải trỏ từ 143ProU (bí danh) sang 143ProNT (mã chính)
    expect(src.analyteAliases).toHaveLength(1)
    expect(src.analyteAliases[0]).toMatchObject({ alias: '143ProU', canonical: '143ProNT' })
    // Nhưng nếu tra mã 143ProU, phải lấy mã chính từ danh mục trước, KHÔNG tra bí danh
    const canonical143ProU = src.analytes.find(a => a.code === '143ProU')
    expect(canonical143ProU?.name).toBe('Mã chính: Protein')
  })

  it('ném lỗi rõ ràng khi thiếu tệp', () => {
    rmSync(join(dir, 'TestCode.json'))
    expect(() => loadLegacySource(dir)).toThrow(/TestCode\.json/)
  })

  it('ném lỗi rõ ràng khi tệp không phải JSON hợp lệ', () => {
    writeFileSync(join(dir, 'Profile.json'), '{ hỏng', 'utf8')
    expect(() => loadLegacySource(dir)).toThrow(/Profile\.json/)
  })
})

describe('loadLegacySource — báo cáo dòng bị loại', () => {
  it('ghi lại dòng TestCode thiếu tên kèm AutoID và lý do tiếng Việt', () => {
    write('TestCode.json', [
      { AutoID: 213, TestCode: '780FSH-1', TestName: null },
      { AutoID: 214, TestCode: '780FSH', TestName: 'FSH' },
    ])
    const src = loadLegacySource(dir)
    expect(src.analytes).toHaveLength(1)
    expect(src.droppedRows).toHaveLength(1)
    expect(src.droppedRows![0]).toMatchObject({
      file: 'TestCode.json',
      key: 'AutoID=213',
      code: '780FSH-1',
    })
    expect(src.droppedRows![0]!.reason).toContain('tên xét nghiệm')
  })

  it('ghi lại dòng thiếu mã bằng số thứ tự khi tệp không có cột AutoID', () => {
    write('Profile.json', [{ ProfileID: null, ProfileName: 'Không mã' }])
    const src = loadLegacySource(dir)
    expect(src.droppedRows).toHaveLength(1)
    expect(src.droppedRows![0]).toMatchObject({ file: 'Profile.json', key: 'dòng thứ 1', code: null })
  })

  it('ghi lại dòng thua khi hai bảng danh mục có cùng một mã', () => {
    write('TestCode.json', [{ AutoID: 1, TestCode: '123AST', TestName: 'AST từ TestCode' }])
    write('TestCodeNew.json', [{ AutoID: 9, TestCode: '123AST', TestName: 'AST từ TestCodeNew' }])
    const src = loadLegacySource(dir)
    expect(src.analytes).toHaveLength(1)
    expect(src.droppedRows).toHaveLength(1)
    expect(src.droppedRows![0]).toMatchObject({ file: 'TestCodeNew.json', key: 'AutoID=9', code: '123AST' })
    expect(src.droppedRows![0]!.reason).toContain('đã có trong danh mục chính')
  })

  it('ghi lại mọi dòng bị loại ở mọi tệp, không tệp nào im lặng', () => {
    write('TestCode.json', [{ AutoID: 1, TestCode: null, TestName: null }])
    write('TestCodeNew.json', [{ AutoID: 2, TestCode: 'X', TestName: null }])
    write('Instrument.json', [{ AutoID: 3, InsID: null, InsName: 'máy' }])
    write('InsTest.json', [{ AutoID: 4, InsID: 1, TestCode: 'X', TestcodeIN: null }])
    write('InterfacePara.json', [{ InstrumentID: null }])
    write('ResultMapping.json', [{ ID: 64, TestCode: null, ResultIn: 'neg' }])
    write('Profile.json', [{ ProfileID: 'P', ProfileName: null }])
    write('ProfileTest.json', [{ AutoID: 7, ProfileID: null, TestCode: 'X' }])
    write('TestCode_HIS.json', [{ AutoID: 8, TestCodeHIS: 'A/01', Testcode: '   ' }])
    const src = loadLegacySource(dir)
    expect(new Set(src.droppedRows!.map((d) => d.file)).size).toBe(9)
    expect(src.droppedRows).toHaveLength(9)
    expect(src.droppedRows!.find((d) => d.file === 'ResultMapping.json')!.key).toBe('ID=64')
  })

  it('nguồn sạch thì danh sách dòng bị loại rỗng', () => {
    write('TestCode.json', [{ AutoID: 1, TestCode: '123AST', TestName: 'AST' }])
    expect(loadLegacySource(dir).droppedRows).toEqual([])
  })
})

describe('loadLegacySource — cột Active', () => {
  it('đọc Active=false thành xét nghiệm đã ngừng dùng', () => {
    write('TestCodeNew.json', [
      { TestCode: '111A', TestName: 'Còn dùng', Active: true },
      { TestCode: '222B', TestName: 'Đã ngừng', Active: false },
      { TestCode: '333C', TestName: 'Không khai cột Active' },
    ])
    const src = loadLegacySource(dir)
    expect(src.analytes.map((a) => a.active)).toEqual([true, false, true])
    expect(src.analytes.map(isAnalyteActive)).toEqual([true, false, true])
  })

  it('isAnalyteActive coi bộ nguồn dựng tay không khai cột là đang dùng', () => {
    expect(isAnalyteActive({ code: 'X' } as never)).toBe(true)
  })
})

describe('readRows — chọn mảng dữ liệu', () => {
  it('lấy mảng dài nhất khi JSON là object bọc ngoài', () => {
    writeFileSync(
      join(dir, 'Profile.json'),
      JSON.stringify({ meta: [], rows: [{ ProfileID: 'P1', ProfileName: 'Nhóm 1' }] }),
      'utf8',
    )
    expect(loadLegacySource(dir).profiles).toHaveLength(1)
  })

  it('ném lỗi thay vì đoán khi có hai mảng dài bằng nhau', () => {
    writeFileSync(
      join(dir, 'Profile.json'),
      JSON.stringify({ a: [{ ProfileID: 'P1', ProfileName: 'A' }], b: [{ ProfileID: 'P2', ProfileName: 'B' }] }),
      'utf8',
    )
    expect(() => loadLegacySource(dir)).toThrow(/dài bằng nhau/)
  })

  it('báo đúng nguyên nhân "không tìm thấy mảng dữ liệu", không đổ tại JSON hỏng', () => {
    writeFileSync(join(dir, 'Profile.json'), JSON.stringify({ meta: 1 }), 'utf8')
    expect(() => loadLegacySource(dir)).toThrow(/không tìm thấy mảng dữ liệu/)
    expect(() => loadLegacySource(dir)).not.toThrow(/không phải JSON hợp lệ/)
  })
})
