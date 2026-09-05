import { createHash, randomBytes, timingSafeEqual } from 'node:crypto'
import { Injectable } from '@nestjs/common'
import type { Kysely } from 'kysely'
import type { AdminDatabase } from '../admin-db/schema.js'

const KEY_PREFIX = 'lis_agent_'

function hashKey(plainKey: string): string {
  return `sha256:${createHash('sha256').update(plainKey).digest('hex')}`
}

@Injectable()
export class AgentKeyService {
  constructor(private readonly admin: Kysely<AdminDatabase>) {}

  /** Cấp khoá mới. Khoá gốc chỉ trả về đúng một lần này; database chỉ giữ hash. */
  async issue(tenantId: string, label: string): Promise<{ keyId: string; plainKey: string }> {
    const plainKey = KEY_PREFIX + randomBytes(24).toString('base64url')
    const row = await this.admin
      .insertInto('agent_keys')
      .values({
        tenant_id: tenantId,
        key_hash: hashKey(plainKey),
        agent_label: label,
        status: 'active',
      })
      .returning('id')
      .executeTakeFirstOrThrow()
    return { keyId: row.id, plainKey }
  }

  async verify(plainKey: string): Promise<{ tenantId: string; keyId: string } | null> {
    if (!plainKey.startsWith(KEY_PREFIX)) return null
    const expected = hashKey(plainKey)
    const row = await this.admin
      .selectFrom('agent_keys')
      .select(['id', 'tenant_id', 'key_hash', 'status'])
      .where('key_hash', '=', expected)
      .executeTakeFirst()

    if (!row || row.status !== 'active') return null

    // So sánh thời gian hằng số để không rò rỉ thông tin qua thời gian phản hồi.
    const a = Buffer.from(row.key_hash)
    const b = Buffer.from(expected)
    if (a.length !== b.length || !timingSafeEqual(a, b)) return null

    await this.admin
      .updateTable('agent_keys')
      .set({ last_seen_at: new Date() })
      .where('id', '=', row.id)
      .execute()

    return { tenantId: row.tenant_id, keyId: row.id }
  }
}
