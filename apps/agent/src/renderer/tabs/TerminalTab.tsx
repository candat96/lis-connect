import { useEffect, useRef, useState, type JSX } from 'react'
import { api, type TrafficLine } from '../api.js'

const MAX_LINES = 500

/**
 * Chuyển chuỗi hex thành text hiển thị được, không dùng `Buffer` — đối tượng
 * này không có sẵn trong renderer của Electron khi `nodeIntegration` tắt.
 * Chuyển bằng JavaScript thuần để không phải thêm phụ thuộc `buffer` và
 * không phải sửa `electron.vite.config.ts` để polyfill global.
 */
function hexToText(hex: string): string {
  let result = ''
  for (let i = 0; i + 1 < hex.length; i += 2) {
    const byte = Number.parseInt(hex.slice(i, i + 2), 16)
    result += byte >= 32 && byte < 127 ? String.fromCharCode(byte) : '.'
  }
  return result
}

function hexPretty(hex: string): string {
  return (hex.match(/.{1,2}/g) ?? []).join(' ').toUpperCase()
}

export function TerminalTab(): JSX.Element {
  const [lines, setLines] = useState<TrafficLine[]>([])
  const [filter, setFilter] = useState('')
  const [showHex, setShowHex] = useState(false)
  const bottomRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    return api.onTraffic((line) => {
      setLines((current) => [...current, line].slice(-MAX_LINES))
    })
  }, [])

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [lines])

  const visible = filter ? lines.filter((line) => line.deviceKey.includes(filter)) : lines

  return (
    <section className="panel">
      <h2>Terminal thô</h2>
      <p className="hint">
        Xem toàn bộ byte vào (&lt;&lt;) và ra (&gt;&gt;) giữa Agent và máy xét nghiệm. Dùng khi
        dò một máy chưa có driver — dòng viền xanh lá là byte máy gửi lên, viền vàng là byte
        Agent gửi xuống.
      </p>

      <div className="terminal-toolbar">
        <input
          value={filter}
          placeholder="Lọc theo mã máy"
          onChange={(e) => setFilter(e.target.value)}
        />
        <label className="checkbox">
          <input type="checkbox" checked={showHex} onChange={(e) => setShowHex(e.target.checked)} />
          <span>Hiện dạng hex</span>
        </label>
        <button onClick={() => setLines([])}>Xoá màn hình</button>
        <span className="terminal-count">{visible.length} dòng</span>
      </div>

      <pre className="terminal">
        {visible.length === 0 && (
          <div className="terminal-empty">
            Chưa có lưu lượng nào. Đảm bảo máy đã được khai báo ở tab &quot;Máy xét nghiệm&quot;
            và đang gửi dữ liệu.
          </div>
        )}
        {visible.map((line, index) => (
          <div
            key={index}
            className={line.direction === 'inbound' ? 'terminal-line line-in' : 'terminal-line line-out'}
          >
            <span className="ts">{new Date(line.at).toLocaleTimeString('vi-VN')}</span>
            <span className="dir">{line.direction === 'inbound' ? '<<' : '>>'}</span>
            <span className="dev">{line.deviceKey}</span>
            <span className="payload">{showHex ? hexPretty(line.hex) : hexToText(line.hex)}</span>
          </div>
        ))}
        <div ref={bottomRef} />
      </pre>
    </section>
  )
}
