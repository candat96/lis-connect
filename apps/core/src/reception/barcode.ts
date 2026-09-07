/**
 * Sinh và đọc barcode ống bệnh phẩm.
 *
 * ═══ Vì sao không sao chép hệ cũ ═══
 *
 * Hệ cũ in `ddmmyy-<số>` với phần số ĐẾM LẠI TỪ ĐẦU mỗi ngày. Máy xét nghiệm
 * chỉ gửi về phần sau dấu gạch, nên khi nhận kết quả hệ cũ phải tra ngược MỜ:
 * `SID LIKE '%-' + <đuôi>` trong 10 ngày, rồi LẤY BẢN GHI MỚI NHẤT. Tài liệu
 * khảo sát ghi thẳng rủi ro: hai bệnh nhân trùng đuôi trong 10 ngày sẽ khớp
 * nhầm — kết quả của người này gắn sang hồ sơ người khác.
 *
 * ═══ Cách làm ở đây ═══
 *
 * Phần số lấy từ sequence `specimen_barcode_seq` của Postgres: không đặt lại
 * theo ngày, không quay vòng, và cột `specimens.barcode_serial` mang UNIQUE
 * trên toàn bảng. Hai hệ quả:
 *
 * 1. Đuôi máy gửi về xác định DUY NHẤT một ống — vĩnh viễn, không phụ thuộc
 *    cửa sổ 10 ngày hay 30 ngày nào. Tra ngược là phép so khớp CHÍNH XÁC trên
 *    số nguyên có index, không phải `LIKE`.
 * 2. Phần `ddmmyy` chỉ để người đọc bằng mắt và sắp xếp; mất nó cũng không mất
 *    tính duy nhất.
 *
 * Chỗ duy nhất còn có thể nhập nhằng là khi máy CẮT BỚT chữ số (gửi `5678`
 * trong khi ống mang `0095678`). Trường hợp đó được xử lý ở
 * `ReceptionService.findOrderByBarcode`: đếm hết ứng viên rồi TỪ CHỐI nếu có
 * hơn một, không bao giờ đoán.
 */

/** Số chữ số tối thiểu của phần đuôi in trên barcode, đệm 0 phía trước. */
export const BARCODE_SERIAL_DIGITS = 7

/**
 * Đuôi ngắn hơn ngần này bị từ chối thẳng.
 *
 * Hệ cũ chấp nhận từ 3 ký tự. Với dãy không quay vòng, một đuôi 3 chữ số gần
 * như chắc chắn là bản cắt bớt của một số dài hơn, nên nó chỉ dẫn tới hai kết
 * cục: từ chối vì nhập nhằng, hoặc — tệ hơn — khớp đúng một ống một cách tình
 * cờ. Chặn từ đầu và nói rõ lý do.
 */
export const MIN_BARCODE_TAIL_DIGITS = 4

/**
 * Cửa sổ tìm ứng viên khi máy gửi đuôi CẮT BỚT, tính bằng ngày.
 *
 * Chỉ áp dụng cho nhánh suy giảm này; nhánh khớp chính xác không có cửa sổ nào
 * — mẫu chạy lại sau một năm vẫn tra đúng. Con số 30 rộng hơn mốc 10 ngày của
 * hệ cũ theo đúng yêu cầu, và vì nhánh này TỪ CHỐI khi có hơn một ứng viên,
 * nới rộng cửa sổ chỉ làm hệ thống thận trọng hơn chứ không liều hơn.
 */
export const TRUNCATED_TAIL_WINDOW_DAYS = 30

export interface ParsedBarcodeTail {
  /** Chuỗi chữ số đã chuẩn hoá, giữ nguyên số 0 đứng đầu nếu máy có gửi. */
  digits: string
  /** Giá trị số của đuôi, dùng cho phép so khớp chính xác trên `barcode_serial`. */
  serial: bigint
  /** Máy có gửi kèm phần ngày `ddmmyy-` hay không. */
  hadDatePart: boolean
}

/** `ddmmyy` theo giờ Việt Nam — barcode được dán và đọc tại chỗ. */
export function datePart(now: Date): string {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Asia/Ho_Chi_Minh',
    day: '2-digit',
    month: '2-digit',
    year: '2-digit',
  }).formatToParts(now)
  const get = (type: string): string => parts.find((p) => p.type === type)?.value ?? '00'
  return `${get('day')}${get('month')}${get('year')}`
}

/** Dựng barcode đầy đủ từ số thứ tự đã cấp. */
export function formatBarcode(serial: bigint | number | string, now: Date): string {
  return `${datePart(now)}-${String(serial).padStart(BARCODE_SERIAL_DIGITS, '0')}`
}

/**
 * Đọc thứ máy gửi về. Chấp nhận cả barcode đầy đủ lẫn phần đuôi trần.
 *
 * Trả `null` khi đầu vào không phải dạng hiểu được — người gọi phải ném lỗi rõ
 * ràng chứ không được suy đoán tiếp.
 */
export function parseBarcodeTail(raw: string): ParsedBarcodeTail | null {
  const trimmed = raw.trim().replace(/\s+/g, '')
  if (trimmed === '') return null

  const dash = trimmed.lastIndexOf('-')
  const hadDatePart = dash >= 0
  const tail = hadDatePart ? trimmed.slice(dash + 1) : trimmed
  if (!/^\d+$/.test(tail)) return null
  // Chặn tràn bigint từ một chuỗi số dài bất thường trước khi đưa vào truy vấn.
  if (tail.length > 18) return null

  return { digits: tail, serial: BigInt(tail), hadDatePart }
}
