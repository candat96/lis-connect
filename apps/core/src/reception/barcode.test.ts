import { describe, expect, it } from 'vitest'
import { datePart, formatBarcode, parseBarcodeTail } from './barcode.js'

describe('sinh barcode', () => {
  it('phần ngày theo giờ Việt Nam, không theo giờ UTC', () => {
    // 06/09/2026 lúc 18:30 UTC là 07/09/2026 lúc 01:30 giờ Việt Nam. Lấy nhầm
    // múi giờ là barcode in ra lệch ngày với sổ giao ca của kỹ thuật viên.
    expect(datePart(new Date('2026-09-06T18:30:00Z'))).toBe('070926')
  })

  it('đuôi được đệm 0 cho đủ 7 chữ số', () => {
    expect(formatBarcode(4001, new Date('2026-09-07T03:00:00Z'))).toBe('070926-0004001')
  })

  it('số vượt 7 chữ số vẫn in đủ, không bị cắt', () => {
    // Cắt bớt để vừa khuôn là tạo ra hai ống trùng đuôi — đúng cái bẫy phải tránh.
    expect(formatBarcode(12345678, new Date('2026-09-07T03:00:00Z'))).toBe('070926-12345678')
  })
})

describe('đọc thứ máy gửi về', () => {
  it('nhận barcode đầy đủ và lấy đúng phần đuôi', () => {
    const parsed = parseBarcodeTail('070926-0004001')
    expect(parsed?.digits).toBe('0004001')
    expect(parsed?.serial).toBe(4001n)
    expect(parsed?.hadDatePart).toBe(true)
  })

  it('nhận đuôi trần, giữ nguyên số 0 đứng đầu', () => {
    const parsed = parseBarcodeTail(' 0004001 ')
    expect(parsed?.digits).toBe('0004001')
    expect(parsed?.serial).toBe(4001n)
    expect(parsed?.hadDatePart).toBe(false)
  })

  it('đuôi bị máy cắt mất số 0 vẫn ra đúng giá trị số', () => {
    expect(parseBarcodeTail('4001')?.serial).toBe(4001n)
  })

  it('trả null cho đầu vào không phải chữ số thay vì đoán', () => {
    expect(parseBarcodeTail('')).toBeNull()
    expect(parseBarcodeTail('abc')).toBeNull()
    expect(parseBarcodeTail('070926-')).toBeNull()
    expect(parseBarcodeTail('12A4')).toBeNull()
  })

  it('trả null cho chuỗi số dài bất thường, không để tràn bigint xuống truy vấn', () => {
    expect(parseBarcodeTail('1'.repeat(19))).toBeNull()
  })
})
