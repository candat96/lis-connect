import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import type { Kysely } from 'kysely'
import type { AdminDatabase } from '../admin-db/schema.js'
import type { TenantRegistryService } from '../tenant/tenant-registry.service.js'
import {
  buildDecisionTemplate,
  loadDecisions,
  saveDecisions,
  SKIP_MARKER,
  type DecisionFile,
} from './legacy-decisions.js'
import { formatReport } from './legacy-report.js'
import { loadLegacySource } from './legacy-source.js'
import { validateLegacySource } from './legacy-validator.js'
import { writeLegacyCatalogue } from './legacy-writer.js'

/**
 * Đọc tệp quyết định, đổi lỗi JSON thành thông báo tiếng Việt chỉ đúng chỗ hỏng.
 *
 * `JSON.parse` ném ra tiếng Anh kèm vị trí tính bằng byte offset — vô dụng với người
 * vận hành đang mở tệp trong Notepad.
 */
function readDecisionsOrExplain(path: string): DecisionFile {
  try {
    return loadDecisions(path)
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    const at = /position (\d+)/.exec(message)
    let where = ''
    if (at) {
      const offset = Number(at[1])
      const before = readFileSync(path, 'utf8').slice(0, offset)
      const line = before.split('\n').length
      const column = offset - before.lastIndexOf('\n')
      where = ` (dòng ${line}, cột ${column})`
    }
    throw new Error(
      `tệp quyết định "${path}" không đọc được${where}. ` +
        'Hãy kiểm tra dấu phẩy, dấu ngoặc kép và dấu ngoặc nhọn, hoặc xoá tệp đi để công cụ sinh lại. ' +
        `Chi tiết kỹ thuật: ${message}`,
    )
  }
}

/**
 * Chuyển ghi chú của kỹ thuật viên từ tệp cũ sang khung mới.
 *
 * `buildDecisionTemplate` chỉ mang theo `chosen`; trường `note` là chỗ người ta viết
 * "đã hỏi bác sĩ Hoà, chờ trả lời" và không được mất qua mỗi lần chạy.
 */
function carryOverNotes(next: DecisionFile, previous: DecisionFile | undefined): DecisionFile {
  if (!previous) return next
  const notes = new Map<string, string>()
  for (const c of previous.channels) {
    if (c.note) notes.set(`${c.insId} ${c.channelCode}`, c.note)
  }
  for (const c of next.channels) {
    const note = notes.get(`${c.insId} ${c.channelCode}`)
    if (note !== undefined) c.note = note
  }
  return next
}

export interface MigrateLegacyOptions {
  tenantCode: string
  sourceDir: string
  decisionsPath: string
  apply: boolean
}

/**
 * Tên mặc định của tệp quyết định.
 *
 * Đặt ở thư mục làm việc hiện tại chứ KHÔNG phải trong thư mục dữ liệu nguồn: thư mục
 * nguồn là bản trích từ hệ cũ, phải giữ nguyên vẹn để còn đối chiếu và trích lại.
 * Tên có mã cơ sở để chạy nhiều cơ sở trong cùng một thư mục không giẫm lên nhau.
 */
function defaultDecisionsPath(tenantCode: string): string {
  return resolve(process.cwd(), `quyet-dinh-ma-kenh-${tenantCode}.json`)
}

export function parseMigrateLegacyArgs(args: string[]): MigrateLegacyOptions {
  let tenantCode: string | undefined
  let sourceDir: string | undefined
  let decisionsPath: string | undefined
  let apply = false

  for (let i = 0; i < args.length; i += 1) {
    const arg = args[i]
    if (arg === '--apply') {
      apply = true
    } else if (arg === '--tenant') {
      tenantCode = args[++i]
    } else if (arg === '--source') {
      sourceDir = args[++i]
    } else if (arg === '--decisions') {
      decisionsPath = args[++i]
    } else {
      throw new Error(`tham số không hiểu: ${arg}`)
    }
  }

  if (!tenantCode || !sourceDir) {
    throw new Error(
      'cú pháp: migrate-legacy --tenant <mã cơ sở> --source <thư mục> [--decisions <tệp>] [--apply]',
    )
  }

  return {
    tenantCode,
    sourceDir: resolve(sourceDir),
    decisionsPath: decisionsPath ? resolve(decisionsPath) : defaultDecisionsPath(tenantCode),
    apply,
  }
}

/**
 * Nạp nguồn, kiểm tra, cập nhật tệp quyết định, in báo cáo — và chỉ ghi khi có `--apply`.
 *
 * Không có `--apply` thì tuyệt đối không chạm vào database nghiệp vụ: người vận hành
 * cần xem hết xung đột và mồ côi trước khi quyết, chứ không phải sau.
 */
export async function runMigrateLegacy(
  admin: Kysely<AdminDatabase>,
  registry: TenantRegistryService,
  options: MigrateLegacyOptions,
  log: (line: string) => void = console.log,
): Promise<void> {
  const tenant = await admin
    .selectFrom('tenants')
    .selectAll()
    .where('code', '=', options.tenantCode)
    .executeTakeFirst()
  if (!tenant) throw new Error(`không tìm thấy cơ sở có mã "${options.tenantCode}"`)

  const src = loadLegacySource(options.sourceDir)
  const validation = validateLegacySource(src)

  // Giữ nguyên mọi lựa chọn ĐÃ QUYẾT và mọi GHI CHÚ của kỹ thuật viên; chỉ bổ sung mục mới.
  const hadFile = existsSync(options.decisionsPath)
  const existing = hadFile ? readDecisionsOrExplain(options.decisionsPath) : undefined
  const decisions = carryOverNotes(buildDecisionTemplate(validation, existing), existing)

  // Chạy khô KHÔNG được ghi đè tệp có sẵn: kỹ thuật viên có thể đang điền dở, và một
  // lần chạy chỉ để xem báo cáo mà xoá mất công của họ là không chấp nhận được. Tệp chỉ
  // được sinh khi CHƯA có (đó là mục đích của lần chạy khô đầu tiên), hoặc khi `--apply`.
  if (!hadFile) {
    saveDecisions(options.decisionsPath, decisions)
    log(`đã sinh tệp quyết định mới: ${options.decisionsPath}`)
  } else if (options.apply) {
    saveDecisions(options.decisionsPath, decisions)
  }

  const undecided = decisions.channels.filter((c) => c.chosen === null).length
  log(`tệp quyết định: ${options.decisionsPath}`)
  log(
    `mã kênh xung đột: ${decisions.channels.length}, chưa quyết: ${undecided}` +
      (undecided > 0
        ? ` — điền "chosen" bằng một mã ứng viên, hoặc "${SKIP_MARKER}" để bỏ hẳn mã kênh đó`
        : ''),
  )
  log('')

  if (!options.apply) {
    log(formatReport(validation, null))
    return
  }

  if (undecided > 0) {
    log(
      `CẢNH BÁO: còn ${undecided} mã kênh chưa được quyết — những dòng đó sẽ KHÔNG được di trú.`,
    )
    log('')
  }

  const db = await registry.forTenant(tenant.id)
  // Bản ghi Agent giữ chỗ được tạo BÊN TRONG giao dịch (sau khi cầm khoá), nên không
  // truyền agentId vào đây: tạo ở ngoài vừa chạy đua được với một lệnh song song, vừa
  // để lại rác khi giao dịch hỏng giữa chừng.
  const outcome = await writeLegacyCatalogue(db, src, decisions)
  log(formatReport(validation, outcome))
  log('')
  log(
    'Ghi chú: các máy vừa tạo đang gắn vào một bản ghi Agent giữ chỗ (hostname "di-tru-he-cu").',
  )
  log(
    'Khi Agent thật kết nối lần đầu, nó tạo bản ghi riêng; máy sẽ được gán lại ở khối 2B khi kỹ thuật viên khai báo cổng.',
  )
}
