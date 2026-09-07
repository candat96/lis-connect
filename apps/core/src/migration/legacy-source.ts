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
  /**
   * Cột `Active` của hệ cũ. `TestCodeNew` có 17 xét nghiệm `Active=False`; nếu không
   * đọc cột này thì chúng di trú sang hệ mới dưới dạng "đang dùng" và hiện ra cho bác sĩ
   * chọn dù bệnh viện đã ngừng làm.
   *
   * Trường này để `?` chỉ vì các bộ nguồn dựng tay trong test (viết trước khi có cột
   * này) không khai nó; `loadLegacySource` LUÔN điền. Đừng đọc thẳng — dùng
   * `isAnalyteActive()` để `undefined` không âm thầm biến thành `false`.
   */
  active?: boolean
  source: 'legacy' | 'legacy-new'
}

/**
 * Xét nghiệm có còn dùng không.
 *
 * Chỉ `active === false` mới là "đã ngừng". `undefined` (bộ nguồn dựng tay không khai
 * cột) được hiểu là đang dùng — đúng như hành vi trước khi cột này tồn tại.
 */
export function isAnalyteActive(analyte: LegacyAnalyte): boolean {
  return analyte.active !== false
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

/**
 * Quan hệ cha–con SUY RA từ thụt lề tên trong `TestCode.json` / `TestCodeNew.json`.
 *
 * Hệ cũ không khai báo hầu hết các nhóm ở `Profile.json`/`ProfileTest.json` (chỉ có
 * đúng 2 nhóm thật ở đó). Quan hệ còn lại — 002HD (Tổng phân tích tế bào máu ngoại
 * vi), 400TPTNT, 920IONDO... — chỉ tồn tại qua cách trình bày: một dòng có
 * `TestHead = true` là mã cha, các dòng NGAY SAU nó có `TestName` bắt đầu bằng
 * khoảng trắng là con của nó, cho tới khi gặp một mã cha khác hoặc một dòng tên
 * không thụt lề. Đây là ĐỀ XUẤT do suy luận cách trình bày, không phải khai báo
 * thật — bộ ghi đánh dấu `source='legacy-inferred'`, `confirmed=false`.
 */
export interface LegacyInferredProfileMember {
  profileCode: string
  analyteCode: string
}

export interface LegacyHisMap {
  hisCode: string
  analyteCode: string
}

export interface LegacyAnalyteAlias {
  alias: string
  canonical: string
}

/**
 * Một dòng dữ liệu nguồn mà bộ đọc KHÔNG dựng được thành bản ghi.
 *
 * Mọi dòng bị loại ở khâu đọc đều phải để lại một mục ở đây. Trước đây 185 dòng
 * (14 `TestCode`, 2 `ResultMapping`, 169 `TestCode_HIS`) biến mất trong im lặng; riêng
 * 13 trong 14 dòng `TestCode` là chỉ số con của các mã cha `780FSH`, `781LH`, `782E2`,
 * `783PROGES`, `766HE3`, nên cây xét nghiệm cha–con dựng lại bị cụt con mà không ai biết.
 */
export interface LegacyDroppedRow {
  /** Tên tệp nguồn, ví dụ `TestCode.json`. */
  file: string
  /** Khoá nhận dạng dòng — `AutoID=213` nếu tệp có cột đó, nếu không thì số thứ tự dòng. */
  key: string
  /** Mã xét nghiệm đọc được (nếu có) — dòng thiếu tên vẫn thường còn mã. */
  code: string | null
  /** Lý do bằng tiếng Việt, đủ để người vận hành mở tệp nguồn ra đối chiếu. */
  reason: string
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
  analyteAliases: LegacyAnalyteAlias[]
  /**
   * Các dòng bị loại ở khâu đọc. `loadLegacySource` luôn điền (có thể là mảng rỗng);
   * để `?` chỉ vì các bộ nguồn dựng tay trong test không khai trường này.
   */
  droppedRows?: LegacyDroppedRow[]
  /**
   * Các "nhóm" cha–con suy ra từ thụt lề — mỗi mã cha có ít nhất một con, kèm tên
   * (lấy từ tên xét nghiệm của chính mã cha đó trong `analytes`). Để `?` vì cùng lý
   * do với `droppedRows`; `loadLegacySource` luôn điền.
   */
  inferredProfiles?: LegacyProfile[]
  /** Quan hệ cha–con suy ra, xem `LegacyInferredProfileMember`. `loadLegacySource` luôn điền. */
  inferredProfileMembers?: LegacyInferredProfileMember[]
}

/**
 * Lấy mảng dữ liệu trong tệp JSON trích xuất.
 *
 * Tệp có thể là mảng trần, hoặc một object bọc ngoài kiểu `{"meta":[…],"rows":[…]}`.
 * Trước đây hàm này lấy mảng ĐẦU TIÊN gặp được, nên một tệp bọc ngoài như vậy trả về
 * 0 dòng mà không báo lỗi. Nay lấy mảng DÀI NHẤT, và nếu có hai mảng cùng dài nhất thì
 * ném lỗi thay vì đoán.
 */
function readRows(dir: string, file: string): Record<string, unknown>[] {
  let text: string
  try {
    text = readFileSync(join(dir, file), 'utf8')
  } catch (error) {
    throw new Error(`không đọc được tệp nguồn ${file}: ${String(error)}`)
  }

  // JSON.parse nằm riêng trong try: câu lệnh ném lỗi "không tìm thấy mảng dữ liệu" mà
  // nằm chung trong khối này sẽ bị chính catch bên dưới bọc lại thành "không phải JSON
  // hợp lệ" — báo sai nguyên nhân, người sau mất thì giờ đi tìm lỗi cú pháp không có thật.
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch (error) {
    throw new Error(`tệp nguồn ${file} không phải JSON hợp lệ: ${String(error)}`)
  }

  if (Array.isArray(parsed)) return parsed as Record<string, unknown>[]

  if (parsed && typeof parsed === 'object') {
    const arrays = Object.entries(parsed as Record<string, unknown>).filter(
      (entry): entry is [string, unknown[]] => Array.isArray(entry[1]),
    )
    if (arrays.length === 1) return arrays[0]![1] as Record<string, unknown>[]
    if (arrays.length > 1) {
      const ranked = [...arrays].sort((a, b) => b[1].length - a[1].length)
      const [best, runnerUp] = ranked as [[string, unknown[]], [string, unknown[]]]
      if (best[1].length === runnerUp[1].length) {
        const listed = ranked.map(([name, rows]) => `${name} (${rows.length} dòng)`).join(', ')
        throw new Error(
          `tệp nguồn ${file} có nhiều mảng dữ liệu dài bằng nhau: ${listed}; không đoán được mảng nào là dữ liệu`,
        )
      }
      return best[1] as Record<string, unknown>[]
    }
  }

  throw new Error(`tệp nguồn ${file}: không tìm thấy mảng dữ liệu`)
}

/** Khoá nhận dạng một dòng nguồn: ưu tiên khoá chính của hệ cũ, không có thì số thứ tự. */
function rowKey(row: Record<string, unknown>, index: number): string {
  for (const field of ['AutoID', 'ID']) {
    const value = row[field]
    if (typeof value === 'number' && Number.isFinite(value)) return `${field}=${value}`
    const text = cleanString(value)
    if (text) return `${field}=${text}`
  }
  return `dòng thứ ${index + 1}`
}

/** Ghép lý do "thiếu cột X, thiếu cột Y" từ danh sách cột bắt buộc còn trống. */
function missingReason(missing: string[]): string {
  return `thiếu ${missing.join(' và ')}`
}

/**
 * Theo dõi mã cha "đang mở" khi đọc TUẦN TỰ một tệp danh mục, để suy ra quan hệ
 * cha–con từ thụt lề tên. Trạng thái được RESET riêng cho mỗi tệp (`TestCode.json`
 * và `TestCodeNew.json` là hai danh sách độc lập; dòng cuối của tệp này không phải
 * cha của dòng đầu tệp kia).
 *
 * Quy tắc, áp cho từng dòng theo đúng thứ tự xuất hiện trong tệp:
 *   - `TestHead = true`: dòng này MỞ một mã cha mới — mọi dòng thụt lề sau đó, cho
 *     tới khi chuỗi bị đóng, là con của nó.
 *   - Dòng có mã và tên, tên bắt đầu bằng khoảng trắng, VÀ đang có mã cha mở: đây
 *     là một con — ghi nhận quan hệ.
 *   - Dòng có mã và tên nhưng tên KHÔNG thụt lề: đóng chuỗi (mã cha đang mở, nếu
 *     có, không còn nhận thêm con nữa).
 *   - Dòng thiếu mã hoặc thiếu tên (đã bị loại ở khâu đọc, xem `droppedRows`): BỎ
 *     QUA, không đóng chuỗi. Dữ liệu thật có đúng trường hợp này — `780FSH-1..3`
 *     là con thật của `780FSH` nhưng `TestName = null` ở nguồn nên bị loại; coi
 *     dòng đó là "đóng chuỗi" sẽ xoá luôn quan hệ của những con thụt lề còn lại
 *     nằm SAU nó trong cùng nhóm.
 */
class ProfileChainTracker {
  private currentHead: string | null = null
  readonly relations: LegacyInferredProfileMember[] = []

  observe(row: Record<string, unknown>, code: string | null, rawName: unknown, name: string | null): void {
    if (row.TestHead === true) {
      this.currentHead = code
      return
    }
    if (!code || !name) return // dòng bị loại — không đóng chuỗi, xem ghi chú ở trên
    const indented = typeof rawName === 'string' && /^\s/.test(rawName)
    if (indented && this.currentHead) {
      this.relations.push({ profileCode: this.currentHead, analyteCode: code })
      return
    }
    if (!indented) this.currentHead = null
  }
}

export function loadLegacySource(dir: string): LegacySource {
  const droppedRows: LegacyDroppedRow[] = []
  const drop = (
    file: string,
    row: Record<string, unknown>,
    index: number,
    code: string | null,
    reason: string,
  ): void => {
    droppedRows.push({ file, key: rowKey(row, index), code, reason })
  }

  const analytes: LegacyAnalyte[] = []
  const analyteCodesSeen = new Set<string>()
  const profileChain = new ProfileChainTracker()

  // Đọc TestCode.json (danh mục cũ đang dùng) — ưu tiên cao
  {
    const file = 'TestCode.json'
    let index = -1
    for (const r of readRows(dir, file)) {
      index += 1
      const code = cleanString(r.TestCode)
      const name = cleanString(r.TestName)
      profileChain.observe(r, code, r.TestName, name)
      // Dòng không có mã hoặc không có tên thì không dựng được bản ghi — nhưng KHÔNG
      // được bỏ trong im lặng: ghi lại để người vận hành đối chiếu với hệ cũ.
      if (!code || !name) {
        const missing: string[] = []
        if (!code) missing.push('mã xét nghiệm (cột TestCode)')
        if (!name) missing.push('tên xét nghiệm (cột TestName)')
        drop(file, r, index, code, missingReason(missing))
        continue
      }
      if (analyteCodesSeen.has(code)) {
        drop(file, r, index, code, `mã "${code}" đã xuất hiện ở dòng trước trong cùng tệp`)
        continue
      }
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
        active: r.Active !== false,
        source: 'legacy',
      })
      analyteCodesSeen.add(code)
    }
  }

  // Đọc TestCodeNew.json (danh mục mới) — hai bảng này không chồng lấn nhau
  // (hiện tại giao nhau bằng 0) nhưng nếu có, dòng thua phải được ghi lại.
  // TestCodeNew chứa các mã được luật ghi đè (ResultMapping) tham chiếu tới
  // mà không có ở TestCode, ví dụ: 131TPHA (giang mai), 141ULeu (bạch cầu niệu).
  // Cột TestCodeNew chứa mã đổi tên — ánh xạ từ mã này sang mã chính ở cột TestCode.
  const analyteAliases: LegacyAnalyteAlias[] = []
  // Tracker riêng: `TestCode.json` và `TestCodeNew.json` là hai danh sách độc lập,
  // dòng cuối tệp này không phải mã cha của dòng đầu tệp kia.
  const profileChainNew = new ProfileChainTracker()
  {
    const file = 'TestCodeNew.json'
    let index = -1
    for (const r of readRows(dir, file)) {
      index += 1
      const code = cleanString(r.TestCode)
      const name = cleanString(r.TestName)
      profileChainNew.observe(r, code, r.TestName, name)
      if (!code || !name) {
        const missing: string[] = []
        if (!code) missing.push('mã xét nghiệm (cột TestCode)')
        if (!name) missing.push('tên xét nghiệm (cột TestName)')
        drop(file, r, index, code, missingReason(missing))
        continue
      }
      // Nếu mã đã có ở TestCode, bỏ phiên bản từ TestCodeNew — và báo, vì đây là hai
      // bản ghi khác nhau của cùng một mã, người vận hành cần biết bản nào thắng.
      if (analyteCodesSeen.has(code)) {
        drop(
          file,
          r,
          index,
          code,
          `mã "${code}" đã có trong danh mục chính (TestCode.json thắng); bản ở TestCodeNew.json tên "${name}" không được di trú`,
        )
        continue
      }

      // Đọc bí danh từ cột TestCodeNew (mã đổi tên)
      const alias = cleanString(r.TestCodeNew)
      if (alias && alias !== code) {
        // Chỉ thêm bí danh nếu nó khác mã chính và cả hai không rỗng
        analyteAliases.push({ alias, canonical: code })
      }

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
        active: r.Active !== false,
        source: 'legacy-new',
      })
      analyteCodesSeen.add(code)
    }
  }

  const instruments: LegacyInstrument[] = []
  {
    const file = 'Instrument.json'
    let index = -1
    for (const r of readRows(dir, file)) {
      index += 1
      const insId = cleanNumber(r.InsID)
      const name = cleanString(r.InsName)
      if (insId === null || !name) {
        const missing: string[] = []
        if (insId === null) missing.push('mã máy (cột InsID)')
        if (!name) missing.push('tên máy (cột InsName)')
        drop(file, r, index, null, missingReason(missing))
        continue
      }
      instruments.push({
        insId,
        name,
        protocol: cleanString(r.ProtocolID),
        conType: cleanString(r.ConType),
        location: cleanString(r.LocationID),
        active: cleanBool(r.Active),
      })
    }
  }

  const insTests: LegacyInsTest[] = []
  {
    const file = 'InsTest.json'
    let index = -1
    for (const r of readRows(dir, file)) {
      index += 1
      const insId = cleanNumber(r.InsID)
      const analyteCode = cleanString(r.TestCode)
      const channelCode = cleanString(r.TestcodeIN)
      if (insId === null || !analyteCode || !channelCode) {
        const missing: string[] = []
        if (insId === null) missing.push('mã máy (cột InsID)')
        if (!analyteCode) missing.push('mã xét nghiệm (cột TestCode)')
        if (!channelCode) missing.push('mã kênh (cột TestcodeIN)')
        drop(file, r, index, analyteCode, missingReason(missing))
        continue
      }
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
  }

  const interfaceParas: LegacyInterfacePara[] = []
  {
    const file = 'InterfacePara.json'
    let index = -1
    for (const r of readRows(dir, file)) {
      index += 1
      const insId = cleanNumber(r.InstrumentID)
      if (insId === null) {
        drop(file, r, index, null, missingReason(['mã máy (cột InstrumentID)']))
        continue
      }
      interfaceParas.push({
        hostId: cleanString(r.HostID),
        insId,
        settings: cleanString(r.Settings),
        commPort: cleanNumber(r.CommPort),
        remoteIp: cleanString(r.RemoteIP),
        remotePort: cleanNumber(r.RemotePort),
      })
    }
  }

  const resultMappings: LegacyResultMapping[] = []
  {
    const file = 'ResultMapping.json'
    let index = -1
    for (const r of readRows(dir, file)) {
      index += 1
      const analyteCode = cleanString(r.TestCode)
      const resultIn = cleanString(r.ResultIn)
      // insId có thể rỗng — chỉ định luật áp dụng cho mọi máy (ví dụ: diễn giải TPHA
      // "1:160 → Dương tính" áp dụng cho tất cả máy xét nghiệm).
      if (!analyteCode || !resultIn) {
        const missing: string[] = []
        if (!analyteCode) missing.push('mã xét nghiệm (cột TestCode)')
        if (!resultIn) missing.push('giá trị máy trả về (cột ResultIn)')
        drop(file, r, index, analyteCode, missingReason(missing))
        continue
      }
      resultMappings.push({
        insId: cleanNumber(r.InstrumentID),
        analyteCode,
        resultIn,
        resultOut: cleanString(r.Result),
        comment: cleanString(r.Comment),
        color: cleanNumber(r.Color),
      })
    }
  }

  const profiles: LegacyProfile[] = []
  {
    const file = 'Profile.json'
    let index = -1
    for (const r of readRows(dir, file)) {
      index += 1
      const code = cleanString(r.ProfileID)
      const name = cleanString(r.ProfileName)
      if (!code || !name) {
        const missing: string[] = []
        if (!code) missing.push('mã nhóm (cột ProfileID)')
        if (!name) missing.push('tên nhóm (cột ProfileName)')
        drop(file, r, index, code, missingReason(missing))
        continue
      }
      profiles.push({ code, name })
    }
  }

  const profileTests: LegacyProfileTest[] = []
  {
    const file = 'ProfileTest.json'
    let index = -1
    for (const r of readRows(dir, file)) {
      index += 1
      const profileCode = cleanString(r.ProfileID)
      const analyteCode = cleanString(r.TestCode)
      if (!profileCode || !analyteCode) {
        const missing: string[] = []
        if (!profileCode) missing.push('mã nhóm (cột ProfileID)')
        if (!analyteCode) missing.push('mã xét nghiệm (cột TestCode)')
        drop(file, r, index, analyteCode, missingReason(missing))
        continue
      }
      profileTests.push({ profileCode, analyteCode })
    }
  }

  const hisMaps: LegacyHisMap[] = []
  {
    const file = 'TestCode_HIS.json'
    let index = -1
    for (const r of readRows(dir, file)) {
      index += 1
      const hisCode = cleanString(r.TestCodeHIS)
      const analyteCode = cleanString(r.Testcode)
      if (!hisCode || !analyteCode) {
        const missing: string[] = []
        if (!hisCode) missing.push('mã HIS (cột TestCodeHIS)')
        if (!analyteCode) missing.push('mã xét nghiệm của LIS (cột Testcode)')
        drop(file, r, index, analyteCode, missingReason(missing))
        continue
      }
      hisMaps.push({ hisCode, analyteCode })
    }
  }

  // Gộp quan hệ suy diễn của cả hai tệp, rồi dựng danh sách "nhóm" (mã cha + tên).
  // Tên nhóm lấy từ chính tên xét nghiệm của mã cha trong `analytes` — mã cha luôn
  // là một xét nghiệm hợp lệ (có tên) trong dữ liệu thật, vì chính nó đã qua được
  // kiểm tra "đủ mã và tên" ở vòng lặp đọc phía trên.
  const inferredProfileMembers = [...profileChain.relations, ...profileChainNew.relations]
  const analyteNameByCode = new Map(analytes.map((a) => [a.code, a.name]))
  const inferredHeadCodes = [...new Set(inferredProfileMembers.map((m) => m.profileCode))]
  const inferredProfiles: LegacyProfile[] = inferredHeadCodes.map((code) => ({
    code,
    name: analyteNameByCode.get(code) ?? code,
  }))

  return {
    analytes,
    instruments,
    insTests,
    interfaceParas,
    resultMappings,
    profiles,
    profileTests,
    inferredProfiles,
    inferredProfileMembers,
    hisMaps,
    analyteAliases,
    droppedRows,
  }
}
