import { existsSync } from 'node:fs'
import { join, resolve } from 'node:path'
import type { Kysely } from 'kysely'
import type { AdminDatabase } from '../admin-db/schema.js'
import type { TenantRegistryService } from '../tenant/tenant-registry.service.js'
import {
  buildDecisionTemplate,
  loadDecisions,
  saveDecisions,
  SKIP_MARKER,
} from './legacy-decisions.js'
import { formatReport } from './legacy-report.js'
import { loadLegacySource } from './legacy-source.js'
import { validateLegacySource } from './legacy-validator.js'
import { ensureMigrationAgent, writeLegacyCatalogue } from './legacy-writer.js'

export interface MigrateLegacyOptions {
  tenantCode: string
  sourceDir: string
  decisionsPath: string
  apply: boolean
}

/** Tên mặc định của tệp quyết định, đặt cạnh thư mục dữ liệu nguồn. */
const DEFAULT_DECISIONS_FILE = 'quyet-dinh-ma-kenh.json'

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
    decisionsPath: decisionsPath
      ? resolve(decisionsPath)
      : join(resolve(sourceDir), DEFAULT_DECISIONS_FILE),
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

  // Giữ nguyên mọi lựa chọn đã quyết ở lần chạy trước; chỉ bổ sung mục mới.
  const existing = existsSync(options.decisionsPath) ? loadDecisions(options.decisionsPath) : undefined
  const decisions = buildDecisionTemplate(validation, existing)
  saveDecisions(options.decisionsPath, decisions)

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
  const agentId = await ensureMigrationAgent(db)
  const outcome = await writeLegacyCatalogue(db, src, decisions, agentId)
  log(formatReport(validation, outcome))
  log('')
  log(
    'Ghi chú: các máy vừa tạo đang gắn vào một bản ghi Agent giữ chỗ (hostname "di-tru-he-cu").',
  )
  log(
    'Khi Agent thật kết nối lần đầu, nó tạo bản ghi riêng; máy sẽ được gán lại ở khối 2B khi kỹ thuật viên khai báo cổng.',
  )
}
