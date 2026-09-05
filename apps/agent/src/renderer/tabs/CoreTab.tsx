import { useEffect, useState, type JSX } from 'react'
import { api } from '../api.js'

type MessageKind = 'info' | 'ok' | 'bad' | 'warn'

interface StatusMessage {
  kind: MessageKind
  text: string
}

function messageClass(kind: MessageKind): string {
  if (kind === 'ok') return 'message message-ok'
  if (kind === 'bad') return 'message message-bad'
  if (kind === 'warn') return 'message message-warn'
  return 'message'
}

export function CoreTab(): JSX.Element {
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [coreUrl, setCoreUrl] = useState('')
  const [agentKey, setAgentKey] = useState('')
  const [proxyUrl, setProxyUrl] = useState('')
  const [message, setMessage] = useState<StatusMessage | null>(null)

  useEffect(() => {
    void api.getConfig().then((config) => {
      setCoreUrl(config.coreUrl)
      setAgentKey(config.agentKey)
      setProxyUrl(config.proxyUrl ?? '')
      setLoading(false)
    })
    return api.onCoreStatus((status) => {
      if (status === 'connected') setMessage({ kind: 'ok', text: 'Đã kết nối tới Core.' })
      if (status === 'disconnected') {
        setMessage({ kind: 'warn', text: 'Mất kết nối tới Core, đang thử lại.' })
      }
      if (status === 'unauthorized') {
        setMessage({
          kind: 'bad',
          text: 'Khoá Agent không hợp lệ hoặc đã bị thu hồi. Vui lòng nhập lại khoá.',
        })
      }
    })
  }, [])

  const save = async (): Promise<void> => {
    setSaving(true)
    setMessage({ kind: 'info', text: 'Đang lưu và kết nối lại...' })
    try {
      await api.saveCoreSettings({
        coreUrl: coreUrl.trim(),
        agentKey: agentKey.trim(),
        proxyUrl: proxyUrl.trim() === '' ? null : proxyUrl.trim(),
      })
    } catch {
      setMessage({ kind: 'bad', text: 'Lưu cấu hình thất bại. Kiểm tra lại rồi thử lại.' })
    } finally {
      setSaving(false)
    }
  }

  if (loading) {
    return (
      <section className="panel">
        <h2>Kết nối tới Core</h2>
        <p className="loading-text">Đang tải cấu hình...</p>
      </section>
    )
  }

  return (
    <section className="panel">
      <h2>Kết nối tới Core</h2>
      <p className="hint">
        Địa chỉ Core và khoá Agent do quản trị hệ thống cấp. Sau khi lưu, Agent sẽ tự
        kết nối lại — theo dõi đèn trạng thái ở góc trên bên phải.
      </p>

      <label className="field">
        <span>Địa chỉ Core</span>
        <input
          value={coreUrl}
          placeholder="wss://core.example.vn/agent"
          disabled={saving}
          onChange={(e) => setCoreUrl(e.target.value)}
        />
      </label>

      <label className="field">
        <span>Khoá Agent</span>
        <input
          value={agentKey}
          placeholder="lis_agent_..."
          disabled={saving}
          onChange={(e) => setAgentKey(e.target.value)}
        />
      </label>

      <label className="field">
        <span>Proxy (để trống nếu không dùng)</span>
        <input
          value={proxyUrl}
          placeholder="http://proxy.benhvien.local:3128"
          disabled={saving}
          onChange={(e) => setProxyUrl(e.target.value)}
        />
      </label>

      <div className="actions">
        <button className="primary" disabled={saving} onClick={() => void save()}>
          {saving ? 'Đang lưu...' : 'Lưu và kiểm tra kết nối'}
        </button>
      </div>

      {message && (
        <p className={messageClass(message.kind)} role="status" aria-live="polite">
          {message.text}
        </p>
      )}
    </section>
  )
}
