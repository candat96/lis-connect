import { readFileSync } from 'node:fs'
import { join } from 'node:path'

/**
 * Cắt khoảng trắng thừa và quy chuỗi rỗng về null.
 *
 * Dữ liệu nguồn đến từ cột CHAR của SQL Server nên có đệm khoảng trắng:
 * `ResultMapping.TestCode` chứa "141ULeu        ", `InsTest.Type` chứa "P    ".
 * Không cắt thì mọi so khớp mã sẽ hỏng.
 */
export function cleanString(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const trimmed = value.trim()
  return trimmed.length > 0 ? trimmed : null
}

function cleanNumber(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string') {
    const parsed = Number(value.trim())
    return Number.isFinite(parsed) ? parsed : null
  }
  return null
}

function cleanBool(value: unknown): boolean {
  return value === true
}

export interface LegacyAnalyte {
  code: string
  name: string
  nameEn: string | null
  category: string | null
  unit: string | null
  normalRangeText: string | null
  lowerLimit: number | null
  higherLimit: number | null
  commentLower: string | null
  commentHigher: string | null
  commentGray: string | null
  criteria: string | null
  resultIsComment: boolean
  printOrder: number | null
  isProfileHead: boolean
}

export interface LegacyInstrument {
  insId: number
  name: string
  protocol: string | null
  conType: string | null
  location: string | null
  active: boolean
}

export interface LegacyInsTest {
  insId: number
  analyteCode: string
  channelCode: string
  round: number | null
  allowInsert: boolean
  omit: boolean
  keepHigherSign: boolean
  keepLowerSign: boolean
}

export interface LegacyInterfacePara {
  hostId: string | null
  insId: number
  settings: string | null
  commPort: number | null
  remoteIp: string | null
  remotePort: number | null
}

export interface LegacyResultMapping {
  insId: number | null
  analyteCode: string
  resultIn: string
  resultOut: string | null
  comment: string | null
  color: number | null
}

export interface LegacyProfile {
  code: string
  name: string
}

export interface LegacyProfileTest {
  profileCode: string
  analyteCode: string
}

export interface LegacyHisMap {
  hisCode: string
  analyteCode: string
}

export interface LegacySource {
  analytes: LegacyAnalyte[]
  instruments: LegacyInstrument[]
  insTests: LegacyInsTest[]
  interfaceParas: LegacyInterfacePara[]
  resultMappings: LegacyResultMapping[]
  profiles: LegacyProfile[]
  profileTests: LegacyProfileTest[]
  hisMaps: LegacyHisMap[]
}

function readRows(dir: string, file: string): Record<string, unknown>[] {
  let text: string
  try {
    text = readFileSync(join(dir, file), 'utf8')
  } catch (error) {
    throw new Error(`không đọc được tệp nguồn ${file}: ${String(error)}`)
  }
  try {
    const parsed: unknown = JSON.parse(text)
    if (Array.isArray(parsed)) return parsed as Record<string, unknown>[]
    if (parsed && typeof parsed === 'object') {
      const first = Object.values(parsed).find((v) => Array.isArray(v))
      if (Array.isArray(first)) return first as Record<string, unknown>[]
    }
    throw new Error('không tìm thấy mảng dữ liệu')
  } catch (error) {
    throw new Error(`tệp nguồn ${file} không phải JSON hợp lệ: ${String(error)}`)
  }
}

export function loadLegacySource(dir: string): LegacySource {
  const analytes: LegacyAnalyte[] = []
  for (const r of readRows(dir, 'TestCode.json')) {
    const code = cleanString(r.TestCode)
    const name = cleanString(r.TestName)
    // Dòng không có mã hoặc không có tên thì không dùng được; bỏ ở đây và để
    // báo cáo đối chiếu ở Task 5 nêu số lượng.
    if (!code || !name) continue
    analytes.push({
      code,
      name,
      nameEn: cleanString(r.TestNameE),
      category: cleanString(r.Category),
      unit: cleanString(r.Unit),
      normalRangeText: cleanString(r.NormalRange),
      lowerLimit: cleanNumber(r.LowerLimit),
      higherLimit: cleanNumber(r.HigherLimit),
      commentLower: cleanString(r.BLLower),
      commentHigher: cleanString(r.BLHigher),
      commentGray: cleanString(r.BLGray),
      criteria: cleanString(r.Criteria),
      resultIsComment: cleanBool(r.GetComment),
      printOrder: cleanNumber(r.PrintOrder),
      isProfileHead: cleanBool(r.Profile) || cleanBool(r.TestHead),
    })
  }

  const instruments: LegacyInstrument[] = []
  for (const r of readRows(dir, 'Instrument.json')) {
    const insId = cleanNumber(r.InsID)
    const name = cleanString(r.InsName)
    if (insId === null || !name) continue
    instruments.push({
      insId,
      name,
      protocol: cleanString(r.ProtocolID),
      conType: cleanString(r.ConType),
      location: cleanString(r.LocationID),
      active: cleanBool(r.Active),
    })
  }

  const insTests: LegacyInsTest[] = []
  for (const r of readRows(dir, 'InsTest.json')) {
    const insId = cleanNumber(r.InsID)
    const analyteCode = cleanString(r.TestCode)
    const channelCode = cleanString(r.TestcodeIN)
    if (insId === null || !analyteCode || !channelCode) continue
    insTests.push({
      insId,
      analyteCode,
      channelCode,
      round: cleanNumber(r.Round),
      allowInsert: cleanBool(r.AllowInsert),
      omit: cleanBool(r.Omit),
      keepHigherSign: cleanBool(r.VALhigher),
      keepLowerSign: cleanBool(r.VALlower),
    })
  }

  const interfaceParas: LegacyInterfacePara[] = []
  for (const r of readRows(dir, 'InterfacePara.json')) {
    const insId = cleanNumber(r.InstrumentID)
    if (insId === null) continue
    interfaceParas.push({
      hostId: cleanString(r.HostID),
      insId,
      settings: cleanString(r.Settings),
      commPort: cleanNumber(r.CommPort),
      remoteIp: cleanString(r.RemoteIP),
      remotePort: cleanNumber(r.RemotePort),
    })
  }

  const resultMappings: LegacyResultMapping[] = []
  for (const r of readRows(dir, 'ResultMapping.json')) {
    const analyteCode = cleanString(r.TestCode)
    const resultIn = cleanString(r.ResultIn)
    // insId có thể rỗng — chỉ định luật áp dụng cho mọi máy (ví dụ: diễn giải TPHA
    // "1:160 → Dương tính" áp dụng cho tất cả máy xét nghiệm).
    if (!analyteCode || !resultIn) continue
    resultMappings.push({
      insId: cleanNumber(r.InstrumentID),
      analyteCode,
      resultIn,
      resultOut: cleanString(r.Result),
      comment: cleanString(r.Comment),
      color: cleanNumber(r.Color),
    })
  }

  const profiles: LegacyProfile[] = []
  for (const r of readRows(dir, 'Profile.json')) {
    const code = cleanString(r.ProfileID)
    const name = cleanString(r.ProfileName)
    if (!code || !name) continue
    profiles.push({ code, name })
  }

  const profileTests: LegacyProfileTest[] = []
  for (const r of readRows(dir, 'ProfileTest.json')) {
    const profileCode = cleanString(r.ProfileID)
    const analyteCode = cleanString(r.TestCode)
    if (!profileCode || !analyteCode) continue
    profileTests.push({ profileCode, analyteCode })
  }

  const hisMaps: LegacyHisMap[] = []
  for (const r of readRows(dir, 'TestCode_HIS.json')) {
    const hisCode = cleanString(r.TestCodeHIS)
    const analyteCode = cleanString(r.Testcode)
    if (!hisCode || !analyteCode) continue
    hisMaps.push({ hisCode, analyteCode })
  }

  return {
    analytes,
    instruments,
    insTests,
    interfaceParas,
    resultMappings,
    profiles,
    profileTests,
    hisMaps,
  }
}
