import { existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { isAnalyteActive, loadLegacySource } from './legacy-source.js'
import { validateLegacySource } from './legacy-validator.js'

/**
 * Test hồi quy trên DỮ LIỆU THẬT của bệnh viện.
 *
 * Cho tới trước test này, mọi con số của bộ đọc/bộ kiểm tra chỉ được xác minh bằng
 * script tạm rồi xoá, nên chúng lặng lẽ sai được — và đã từng sai (bộ kiểm tra báo 36
 * luật ghi đè mồ côi trong khi công cụ ghi dữ liệu chỉ thấy 14, vì bộ kiểm tra không
 * dùng bảng bí danh).
 *
 * Khi một con số ở đây đổi, ĐỪNG sửa số cho khớp code. Con số đổi nghĩa là hoặc dữ liệu
 * nguồn được trích xuất lại, hoặc code đổi hành vi — cả hai đều cần người xem lại.
 */
const DATA_DIR = fileURLToPath(new URL('../../../../docs/khao-sat/seed/du-lieu/', import.meta.url))

describe('dữ liệu thật của bệnh viện', () => {
  it('thư mục dữ liệu nguồn có mặt', () => {
    expect(existsSync(DATA_DIR)).toBe(true)
  })

  const src = loadLegacySource(DATA_DIR)
  const report = validateLegacySource(src)

  it('đọc đủ 2619 xét nghiệm (390 từ TestCode + 2229 từ TestCodeNew)', () => {
    expect(src.analytes).toHaveLength(2619)
    expect(src.analytes.filter((a) => a.source === 'legacy')).toHaveLength(390)
    expect(src.analytes.filter((a) => a.source === 'legacy-new')).toHaveLength(2229)
  })

  it('đọc 689 dòng bí danh trên 688 khoá bí danh duy nhất', () => {
    expect(src.analyteAliases).toHaveLength(689)
    expect(new Set(src.analyteAliases.map((a) => a.alias)).size).toBe(688)
  })

  it('phát hiện 7 mã kênh có nhiều xét nghiệm', () => {
    expect(report.channelConflicts).toHaveLength(7)
  })

  it('còn 14 dòng luật ghi đè mồ côi trên 7 mã sau khi phân giải bí danh', () => {
    const orphans = report.orphans.filter((o) => o.kind === 'result-mapping')
    expect(orphans).toHaveLength(14)
    expect([...new Set(orphans.map((o) => o.analyteCode))].sort()).toEqual([
      'XNNT21_04',
      'XNNT21_05',
      'XNNT21_06',
      'XNNT21_07',
      'XNNT21_08',
      'XNNT21_09',
      'XNNT21_10',
    ])
  })

  it('còn 16 ánh xạ HIS mồ côi trên 7 mã', () => {
    const orphans = report.orphans.filter((o) => o.kind === 'his-map')
    expect(orphans).toHaveLength(16)
    expect(new Set(orphans.map((o) => o.analyteCode)).size).toBe(7)
  })

  it('còn 2 ánh xạ mã kênh mồ côi', () => {
    expect(report.orphans.filter((o) => o.kind === 'ins-test')).toHaveLength(2)
  })

  it('liệt kê 48 mã chứa ký tự đại diện, không trùng lặp', () => {
    expect(report.wildcardCodes).toHaveLength(48)
    expect(new Set(report.wildcardCodes).size).toBe(48)
    // 43 trong số đó chứa '%', phần còn lại chứa '_' (họ mã XNNT21_xx).
    expect(report.wildcardCodes.filter((c) => c.includes('%'))).toHaveLength(43)
  })

  it('không có mã xét nghiệm nào bị trùng', () => {
    expect(report.duplicateAnalyteCodes).toEqual([])
  })

  it('báo đủ 319 mã có isProfileHead, còn 267 mã cha rỗng sau khi tính cả nhóm suy diễn', () => {
    expect(src.analytes.filter((a) => a.isProfileHead)).toHaveLength(319)
    // 319 mã cha - 52 mã cha nhận được con qua suy diễn thụt lề = 267 mã cha rỗng thật sự.
    expect(report.profilesWithoutMembers).toHaveLength(267)
    // Hai nhóm trong Profile.json đều CÓ thành viên nên không được lọt vào danh sách.
    expect(report.profilesWithoutMembers).not.toContain(src.profiles[0]!.code)
  })

  it('suy ra đúng 52 nhóm cha–con từ thụt lề tên, 242 quan hệ, trên 292 mã có TestHead=true', () => {
    // Đo độc lập bằng script Python trên cùng dữ liệu thật — nếu số này đổi thì đi
    // truy nguồn (dữ liệu trích lại, hoặc thuật toán suy diễn đổi hành vi), đừng sửa
    // số cho khớp code.
    const inferredProfiles = src.inferredProfiles ?? []
    const inferredProfileMembers = src.inferredProfileMembers ?? []
    expect(inferredProfiles).toHaveLength(52)
    expect(inferredProfileMembers).toHaveLength(242)

    // Hai mã cha phổ biến nhất phòng xét nghiệm nào cũng làm phải có mặt và có con thật.
    // (920IONDO — Điện giải đồ — KHÔNG được suy ra con nào: ngay dòng sau nó trong
    // nguồn là "920SK — Số Khúm:", tên không thụt lề, nên chuỗi bị đóng trước khi tới
    // các dòng thật sự thụt lề "921Na"/"922K". Đây là hạn chế thật của thuật toán suy
    // theo đúng mô tả spec — không phải lỗi đọc dữ liệu — và là đúng lý do cả nhóm này
    // vẫn cần kỹ thuật viên xác nhận/bổ sung tay chứ không phải phần mềm tự đoán thêm.)
    for (const parent of ['002HD', '400TPTNT']) {
      const profile = inferredProfiles.find((p) => p.code === parent)
      expect(profile, `thiếu nhóm suy diễn cho ${parent}`).toBeDefined()
      const members = inferredProfileMembers.filter((m) => m.profileCode === parent)
      expect(members.length, `${parent} không có con nào`).toBeGreaterThan(0)
    }
    expect(inferredProfiles.find((p) => p.code === '920IONDO')).toBeUndefined()

    // 780FSH, 781LH, 782E2: TOÀN BỘ con (780FSH-1..3, v.v.) có TestName=null ở nguồn
    // nên bị loại; không dòng thụt lề nào còn lại để suy ra — đúng nghĩa rỗng.
    for (const emptyParent of ['780FSH', '781LH', '782E2']) {
      expect(inferredProfiles.find((p) => p.code === emptyParent)).toBeUndefined()
      expect(inferredProfileMembers.filter((m) => m.profileCode === emptyParent)).toHaveLength(0)
    }

    // 783PROGES và 766HE3: mất đúng những con có TestName=null (783PROGES-1..3), nhưng
    // KHÔNG rỗng hoàn toàn — các dòng thụt lề còn lại ngay sau đó (784PROLAC, 786TESTOS,
    // 787SHBG, 766HE4) vẫn được suy ra. Đây chính là "cụt con" mà docstring
    // `LegacyDroppedRow` mô tả, khác với 780FSH/781LH/782E2 rỗng hoàn toàn ở trên.
    expect(inferredProfileMembers.filter((m) => m.profileCode === '783PROGES').map((m) => m.analyteCode)).toEqual([
      '784PROLAC',
      '786TESTOS',
      '787SHBG',
    ])
    expect(inferredProfileMembers.filter((m) => m.profileCode === '766HE3').map((m) => m.analyteCode)).toEqual([
      '766HE4',
    ])
  })

  it('đọc cột Active: 17 xét nghiệm của TestCodeNew đã ngừng dùng', () => {
    const inactive = src.analytes.filter((a) => !isAnalyteActive(a))
    expect(inactive).toHaveLength(17)
    expect(inactive.every((a) => a.source === 'legacy-new')).toBe(true)
  })

  it('báo cáo đủ 185 dòng bị loại ở khâu đọc, không dòng nào biến mất trong im lặng', () => {
    const dropped = report.droppedSourceRows
    expect(dropped).toHaveLength(185)
    const byFile = new Map<string, number>()
    for (const d of dropped) byFile.set(d.file, (byFile.get(d.file) ?? 0) + 1)
    expect(Object.fromEntries(byFile)).toEqual({
      'TestCode.json': 14,
      'ResultMapping.json': 2,
      'TestCode_HIS.json': 169,
    })
    // Mọi mục đều phải có khoá nhận dạng dòng và lý do đọc được.
    expect(dropped.every((d) => d.key.length > 0 && d.reason.length > 0)).toBe(true)
    expect(dropped.filter((d) => d.file === 'TestCode.json').every((d) => d.key.startsWith('AutoID='))).toBe(true)
  })

  it('13 chỉ số con của các mã cha bị loại đều được nêu tên trong báo cáo', () => {
    const droppedCodes = new Set(
      report.droppedSourceRows.filter((d) => d.file === 'TestCode.json').map((d) => d.code),
    )
    for (const child of [
      '780FSH-1', '780FSH-2', '780FSH-3',
      '781LH-1', '781LH-2', '781LH-3',
      '782E2-1', '782E2-2', '782E2-3',
      '783PROGES-1', '783PROGES-2', '783PROGES-3',
      '767ROMA1',
    ]) {
      expect(droppedCodes).toContain(child)
    }
    // Các mã cha thì vẫn phải nằm trong danh mục — nếu không, cây cha-con cụt cả gốc.
    const codes = new Set(src.analytes.map((a) => a.code))
    for (const parent of ['780FSH', '781LH', '782E2', '783PROGES', '766HE3']) {
      expect(codes).toContain(parent)
    }
  })

  it('báo 7 bí danh trùng tên với một mã chính đã tồn tại', () => {
    expect(report.shadowedAliases.map((s) => s.alias)).toEqual([
      '000CD',
      '1438BRCA',
      '1438BRCA1',
      '1438BRCA2',
      '1438BRCA3',
      '604PCRVK',
      '604PCRVK1',
    ])
    const cd = report.shadowedAliases.find((s) => s.alias === '000CD')!
    // Hai bên là xét nghiệm khác hẳn nhau — đó chính là lý do phải báo ra.
    expect(cd.primaryName).not.toBe(cd.aliasTargetName)
  })

  it('báo bí danh 000CDNT trỏ tới hai mã chính và KHÔNG tự phân giải được', () => {
    expect(report.ambiguousAliases).toHaveLength(1)
    const amb = report.ambiguousAliases[0]!
    expect(amb.alias).toBe('000CDNT')
    expect(amb.autoResolvable).toBe(false)
    expect(amb.candidates.map((c) => c.code)).toEqual(['000CayDNT', '000CayDNgT'])
    // Cấy dịch ngực và cấy dịch não tủy là hai xét nghiệm khác nhau; phần mềm không đoán.
    expect(new Set(amb.candidates.map((c) => c.name)).size).toBe(2)
  })
})
