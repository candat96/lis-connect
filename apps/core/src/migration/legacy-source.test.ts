import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanString, loadLegacySource } from './legacy-source.js'

let dir: string

function write(name: string, rows: unknown[]): void {
  writeFileSync(join(dir, name), JSON.stringify(rows), 'utf8')
}

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'lis-legacy-'))
  write('TestCode.json', [])
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
  it('đọc được danh mục xét nghiệm và cắt khoảng trắng', () => {
    write('TestCode.json', [
      { TestCode: '123AST  ', TestName: 'AST (GOT)', Unit: 'U/L ', GetComment: false },
    ])
    const src = loadLegacySource(dir)
    expect(src.analytes).toHaveLength(1)
    expect(src.analytes[0]!.code).toBe('123AST')
    expect(src.analytes[0]!.unit).toBe('U/L')
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

  it('ném lỗi rõ ràng khi thiếu tệp', () => {
    rmSync(join(dir, 'TestCode.json'))
    expect(() => loadLegacySource(dir)).toThrow(/TestCode\.json/)
  })

  it('ném lỗi rõ ràng khi tệp không phải JSON hợp lệ', () => {
    writeFileSync(join(dir, 'Profile.json'), '{ hỏng', 'utf8')
    expect(() => loadLegacySource(dir)).toThrow(/Profile\.json/)
  })
})
