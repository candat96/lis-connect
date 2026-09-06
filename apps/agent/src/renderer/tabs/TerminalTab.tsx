import { useEffect, useMemo, useRef, useState, type JSX } from 'react'
import type { DeviceConfig } from '../../main/config-store.js'
import { api, type TrafficLine } from '../api.js'

/** Số dòng tối đa giữ lại cho MỖI máy, độc lập nhau — máy ồn ào không được
 * đẩy log của máy khác ra khỏi bộ đệm (xem báo cáo Task 20). Cửa sổ theo dõi
 * riêng (DeviceWindow.tsx) dùng lại cùng hằng số này cho bộ đệm riêng của nó. */
export const MAX_LINES_PER_DEVICE = 500
/** Bộ đệm gộp cho chế độ xem "Tất cả các máy". Rộng hơn bộ đệm từng máy vì nó
 * cộng dồn lưu lượng của mọi máy, nhưng vẫn có trần để tránh phình bộ nhớ khi
 * Agent chạy nhiều giờ liền. */
const MAX_LINES_ALL = 2000
/** Danh sách máy có thể đổi lúc đang chạy (thêm/xoá ở tab Máy xét nghiệm) —
 * nạp lại định kỳ để tab này không cần người dùng phải bấm lại tab mới thấy. */
const CONFIG_REFRESH_MS = 5000

/** Giá trị lựa chọn đặc biệt cho "xem gộp tất cả các máy" trong bộ chọn máy. */
const ALL_DEVICES = '__all__'

interface DeviceOption {
  deviceKey: string
  name: string
  /** false = có lưu lượng đến từ mã máy này nhưng chưa (hoặc không còn) khai
   * báo ở tab Máy xét nghiệm. Vẫn phải xem được — nguyên tắc của dự án là
   * không bao giờ vứt dữ liệu từ máy xét nghiệm. */
  known: boolean
}

/**
 * Chuyển chuỗi hex thành text hiển thị được, không dùng `Buffer` — đối tượng
 * này không có sẵn trong renderer của Electron khi `nodeIntegration` tắt.
 * Chuyển bằng JavaScript thuần để không phải thêm phụ thuộc `buffer` và
 * không phải sửa `electron.vite.config.ts` để polyfill global.
 */
// Xuất ra để DeviceWindow.tsx (cửa sổ theo dõi riêng từng máy) dùng lại, tránh
// lặp lại cùng một phép chuyển đổi hex ở hai nơi.
export function hexToText(hex: string): string {
  let result = ''
  for (let i = 0; i + 1 < hex.length; i += 2) {
    const byte = Number.parseInt(hex.slice(i, i + 2), 16)
    result += byte >= 32 && byte < 127 ? String.fromCharCode(byte) : '.'
  }
  return result
}

export function hexPretty(hex: string): string {
  return (hex.match(/.{1,2}/g) ?? []).join(' ').toUpperCase()
}

export function TerminalTab(): JSX.Element {
  const [devices, setDevices] = useState<DeviceConfig[]>([])
  const [allLines, setAllLines] = useState<TrafficLine[]>([])
  const [perDevice, setPerDevice] = useState<Record<string, TrafficLine[]>>({})
  const [selected, setSelected] = useState(ALL_DEVICES)
  const [showHex, setShowHex] = useState(false)
  const bottomRef = useRef<HTMLDivElement>(null)

  // Danh sách máy đã khai báo — nạp khi mở tab, rồi làm mới định kỳ vì kỹ
  // thuật viên có thể thêm/xoá máy ở tab khác trong lúc tab này vẫn đang mở.
  useEffect(() => {
    let cancelled = false
    const load = (): void => {
      void api
        .getConfig()
        .then((config) => {
          if (!cancelled) setDevices(config.devices)
        })
        .catch(() => {
          // Không tải được danh sách máy không phải lỗi của máy xét nghiệm —
          // giữ nguyên danh sách cũ, lần làm mới định kỳ tiếp theo sẽ thử lại.
        })
    }
    load()
    const timer = setInterval(load, CONFIG_REFRESH_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  // Mỗi dòng lưu lượng được ghi vào CẢ bộ đệm gộp lẫn bộ đệm riêng của máy đó
  // — hai bộ đệm độc lập, xoá ở bộ đệm riêng không ảnh hưởng bộ đệm gộp và
  // ngược lại (trừ khi người dùng chủ động xoá, xem `clearScreen`).
  useEffect(() => {
    return api.onTraffic((line) => {
      setAllLines((current) => [...current, line].slice(-MAX_LINES_ALL))
      setPerDevice((current) => {
        const existing = current[line.deviceKey] ?? []
        return {
          ...current,
          [line.deviceKey]: [...existing, line].slice(-MAX_LINES_PER_DEVICE),
        }
      })
    })
  }, [])

  const deviceOptions = useMemo((): DeviceOption[] => {
    const known: DeviceOption[] = devices.map((d) => ({
      deviceKey: d.deviceKey,
      name: d.name,
      known: true,
    }))
    const knownKeys = new Set(known.map((d) => d.deviceKey))
    const unknown: DeviceOption[] = Object.keys(perDevice)
      .filter((key) => !knownKeys.has(key))
      .sort()
      .map((key) => ({ deviceKey: key, name: key, known: false }))
    return [...known, ...unknown]
  }, [devices, perDevice])

  const visible = selected === ALL_DEVICES ? allLines : (perDevice[selected] ?? [])

  const selectedOption = deviceOptions.find((d) => d.deviceKey === selected)
  const selectedName =
    selected === ALL_DEVICES ? 'tất cả các máy' : (selectedOption?.name ?? selected)

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [visible])

  const clearScreen = (): void => {
    if (selected === ALL_DEVICES) {
      setAllLines([])
      setPerDevice({})
      return
    }
    const key = selected
    setAllLines((current) => current.filter((line) => line.deviceKey !== key))
    setPerDevice((current) => {
      const next = { ...current }
      delete next[key]
      return next
    })
  }

  return (
    <section className="panel">
      <h2>Terminal thô</h2>
      <p className="hint">
        Xem toàn bộ byte vào (&lt;&lt;) và ra (&gt;&gt;) giữa Agent và máy xét nghiệm. Dùng khi
        dò một máy chưa có driver — dòng viền xanh lá là byte máy gửi lên, viền vàng là byte
        Agent gửi xuống. Chọn một máy để xem riêng, mỗi máy có bộ đệm 500 dòng độc lập nên máy
        ồn ào không làm mất log của máy khác.
      </p>

      <div className="device-picker" role="tablist" aria-label="Chọn máy để xem log">
        <button
          type="button"
          role="tab"
          aria-selected={selected === ALL_DEVICES}
          className={selected === ALL_DEVICES ? 'device-chip active' : 'device-chip'}
          onClick={() => setSelected(ALL_DEVICES)}
        >
          <span className={allLines.length > 0 ? 'dot dot-green' : 'dot dot-muted'} />
          <span className="device-chip-name">Tất cả các máy</span>
          <span className="device-chip-count">{allLines.length}</span>
        </button>
        {deviceOptions.map((option) => {
          const count = perDevice[option.deviceKey]?.length ?? 0
          return (
            <button
              type="button"
              role="tab"
              key={option.deviceKey}
              aria-selected={selected === option.deviceKey}
              title={option.known ? option.name : `${option.name} (chưa khai báo ở tab Máy xét nghiệm)`}
              aria-label={
                option.known
                  ? `${option.name}, ${count} dòng`
                  : `${option.name}, chưa khai báo, ${count} dòng`
              }
              className={selected === option.deviceKey ? 'device-chip active' : 'device-chip'}
              onClick={() => setSelected(option.deviceKey)}
            >
              <span className={count > 0 ? 'dot dot-green' : 'dot dot-muted'} />
              <span className="device-chip-name">{option.name}</span>
              {!option.known && <span className="device-chip-flag">chưa khai báo</span>}
              <span className="device-chip-count">{count}</span>
            </button>
          )
        })}
        {deviceOptions.length === 0 && (
          <span className="loading-text">
            Chưa khai báo máy nào. Dữ liệu từ máy chưa khai báo vẫn sẽ hiện ở đây khi có.
          </span>
        )}
      </div>

      <div className="terminal-toolbar">
        <label className="checkbox">
          <input type="checkbox" checked={showHex} onChange={(e) => setShowHex(e.target.checked)} />
          <span>Hiện dạng hex</span>
        </label>
        <button onClick={clearScreen}>
          Xoá màn hình ({selected === ALL_DEVICES ? 'tất cả các máy' : selectedName})
        </button>
        <span className="terminal-count">{visible.length} dòng</span>
      </div>

      <pre className="terminal">
        {visible.length === 0 && (
          <div className="terminal-empty">
            Đang chờ dữ liệu từ {selectedName}. Đảm bảo máy đã được khai báo ở tab &quot;Máy xét
            nghiệm&quot; (nếu cần) và đang gửi dữ liệu.
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
