import { mkdtempSync, rmSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import {
  buildDecisionTemplate,
  loadDecisions,
  resolveChannel,
  saveDecisions,
} from './legacy-decisions.js'
import type { ValidationReport } from './legacy-validator.js'

let dir: string

function report(): ValidationReport {
  return {
    channelConflicts: [
      { insId: 1, channelCode: '19', candidates: ['128LDL', '244ACo'] },
      { insId: 2, channelCode: '3', candidates: ['931Cortisol', '932931Cortisol'] },
    ],
    orphans: [],
    profilesWithoutMembers: [],
    wildcardCodes: [],
    duplicateAnalyteCodes: [],
    droppedSourceRows: [],
    shadowedAliases: [],
    ambiguousAliases: [],
  }
}

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'lis-decision-'))
})

afterEach(() => {
  rmSync(dir, { recursive: true, force: true })
})

describe('buildDecisionTemplate', () => {
  it('tạo một mục cho mỗi xung đột, chưa ai quyết', () => {
    const file = buildDecisionTemplate(report())
    expect(file.channels).toHaveLength(2)
    expect(file.channels[0]).toMatchObject({ insId: 1, channelCode: '19', chosen: null })
    expect(file.channels[0]!.candidates).toEqual(['128LDL', '244ACo'])
  })

  it('giữ nguyên lựa chọn đã quyết khi tạo lại từ báo cáo mới', () => {
    const existing = buildDecisionTemplate(report())
    existing.channels[0]!.chosen = '128LDL'
    const merged = buildDecisionTemplate(report(), existing)
    expect(merged.channels[0]!.chosen).toBe('128LDL')
    expect(merged.channels[1]!.chosen).toBeNull()
  })

  it('phân biệt khoá gộp có dấu phân cách: máy 1 kênh "19" khác máy 11 kênh "9"', () => {
    const existing: ReturnType<typeof buildDecisionTemplate> = { channels: [] }
    // Mục cũ: máy 11, kênh "9", đã quyết "XA"
    existing.channels.push({
      insId: 11,
      channelCode: '9',
      candidates: ['XA', 'XB'],
      chosen: 'XA',
    })
    const rep: ValidationReport = {
      channelConflicts: [{ insId: 1, channelCode: '19', candidates: ['128LDL', '244ACo'] }],
      orphans: [],
      profilesWithoutMembers: [],
      wildcardCodes: [],
      duplicateAnalyteCodes: [],
    droppedSourceRows: [],
    shadowedAliases: [],
    ambiguousAliases: [],
    }
    const merged = buildDecisionTemplate(rep, existing)
    // Nếu khoá gộp không có dấu phân cách, "11"+"9" === "1"+"19" và sẽ bị lẫn lộn `chosen`.
    expect(merged.channels[0]!.chosen).toBeNull()
  })
})

describe('lưu và đọc lại', () => {
  it('giữ nguyên nội dung', () => {
    const path = join(dir, 'quyet-dinh.json')
    const file = buildDecisionTemplate(report())
    file.channels[0]!.chosen = '244ACo'
    saveDecisions(path, file)
    expect(loadDecisions(path).channels[0]!.chosen).toBe('244ACo')
  })

  it('ghi ra JSON đọc được bằng trình soạn thảo thường', () => {
    const path = join(dir, 'quyet-dinh.json')
    saveDecisions(path, buildDecisionTemplate(report()))
    const text = readFileSync(path, 'utf8')
    expect(text).toContain('\n')
    expect(text).toContain('channelCode')
  })

  it('ném lỗi khi tệp không tồn tại', () => {
    const path = join(dir, 'khong-ton-tai.json')
    expect(() => loadDecisions(path)).toThrow()
  })

  it('ném lỗi khi JSON hỏng', () => {
    const path = join(dir, 'hong.json')
    writeFileSync(path, '{ khong phai json', 'utf8')
    expect(() => loadDecisions(path)).toThrow()
  })

  it('ném lỗi khi nội dung hợp lệ cú pháp JSON nhưng thiếu trường channels', () => {
    const path = join(dir, 'sai-cau-truc.json')
    writeFileSync(path, JSON.stringify({ foo: 'bar' }), 'utf8')
    expect(() => loadDecisions(path)).toThrow()
  })
})

describe('resolveChannel', () => {
  it('trả về xét nghiệm đã chọn', () => {
    const file = buildDecisionTemplate(report())
    file.channels[0]!.chosen = '128LDL'
    expect(resolveChannel(file, 1, '19')).toEqual({ kind: 'keep', analyteCode: '128LDL' })
  })

  it('cho phép quyết định bỏ hẳn mã kênh đó', () => {
    const file = buildDecisionTemplate(report())
    file.channels[0]!.chosen = '__BO_QUA__'
    expect(resolveChannel(file, 1, '19')).toEqual({ kind: 'skip' })
  })

  it('chưa quyết thì trả về undecided, KHÔNG tự chọn', () => {
    const file = buildDecisionTemplate(report())
    expect(resolveChannel(file, 1, '19')).toEqual({ kind: 'undecided' })
  })

  it('từ chối lựa chọn không nằm trong danh sách ứng viên', () => {
    const file = buildDecisionTemplate(report())
    file.channels[0]!.chosen = 'khong-co-trong-danh-sach'
    expect(() => resolveChannel(file, 1, '19')).toThrow(/không nằm trong danh sách/)
  })

  it('mã kênh không có xung đột thì coi như đã quyết ngầm', () => {
    const file = buildDecisionTemplate(report())
    expect(resolveChannel(file, 99, 'XX')).toEqual({ kind: 'no-conflict' })
  })

  it('phân biệt undecided (mục tồn tại, chosen null) và no-conflict (mục không tồn tại)', () => {
    const file = buildDecisionTemplate(report())
    // mục tồn tại (máy 1, kênh 19) nhưng chưa ai quyết -> undecided
    expect(resolveChannel(file, 1, '19')).toEqual({ kind: 'undecided' })
    // mã kênh này không hề có trong danh sách xung đột -> no-conflict
    expect(resolveChannel(file, 5, '999')).toEqual({ kind: 'no-conflict' })
  })

  it('chosen là chuỗi rỗng không phải null nên không phải undecided; vì rỗng không nằm trong danh sách ứng viên nên bị từ chối như một lựa chọn không hợp lệ', () => {
    const file = buildDecisionTemplate(report())
    file.channels[0]!.chosen = ''
    expect(() => resolveChannel(file, 1, '19')).toThrow(/không nằm trong danh sách/)
  })
})
