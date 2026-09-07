import { useEffect, useRef, useState, type JSX, type KeyboardEvent } from 'react'
import { catalogueApi, errorMessageOf, type AnalyteView } from '../api.js'
import { CheckCircleIcon, SearchIcon, WarningTriangleIcon } from '../statusMeta.js'

/**
 * Ô nhập mã xét nghiệm có gợi ý, dùng chung cho hai màn hình ghi (ánh xạ mã
 * kênh, luật ghi đè kết quả).
 *
 * Vấn đề gốc: hai màn hình đó bắt người dùng gõ tay mã xét nghiệm giữa 2619
 * mã, không gợi ý gì. Gõ nhầm thành một mã CÓ THẬT thì lưu im lặng sang xét
 * nghiệm khác — không có gì báo, và hậu quả là kết quả xét nghiệm gắn nhầm
 * chỉ số cho bệnh nhân.
 *
 * Ba việc component này làm:
 *   1. Gợi ý mã + TÊN khi gõ, hoãn 300ms để không bắn một request mỗi phím —
 *      dùng lại đúng API tìm kiếm của màn hình danh mục.
 *   2. Sau khi gõ xong (dù gõ tay hay chọn từ gợi ý), đối chiếu mã đang có
 *      trong ô với kết quả tìm kiếm: khớp thì hiện TÊN xét nghiệm để người
 *      dùng xác nhận đúng ý; không khớp dòng nào thì cảnh báo thấy được —
 *      nhưng KHÔNG chặn lưu, vì có thể là mã hợp lệ mà tìm kiếm không ra kịp.
 *   3. Vẫn là một ô nhập tự do: gõ tay, dán, sửa đều hoạt động bình thường.
 */

const SEARCH_DEBOUNCE_MS = 300
const SUGGESTION_LIMIT = 8

export function AnalytePicker({
  id,
  label,
  value,
  onChange,
  onBlur,
  error,
  hint,
  disabled,
}: {
  id: string
  label: string
  value: string
  onChange: (code: string) => void
  onBlur?: () => void
  error?: string | undefined
  hint?: string | undefined
  disabled?: boolean
}): JSX.Element {
  const [suggestions, setSuggestions] = useState<AnalyteView[]>([])
  const [queriedFor, setQueriedFor] = useState<string | null>(null)
  /**
   * Tổng số dòng khớp ở phía máy chủ, không phải số dòng đang hiện.
   *
   * Cần để biết danh sách gợi ý đã là TOÀN BỘ kết quả hay mới là phần đầu:
   * chỉ khi đã thấy hết mới dám khẳng định một mã là không có trong danh mục.
   */
  const [totalFound, setTotalFound] = useState(0)
  const [loading, setLoading] = useState(false)
  const [searchError, setSearchError] = useState<string | null>(null)
  const [open, setOpen] = useState(false)
  const [highlight, setHighlight] = useState(0)
  const requestIdRef = useRef(0)

  const trimmed = value.trim()

  // Hoãn gọi khi gõ: đúng quy ước dùng ở màn hình danh mục, không bắn một
  // request mỗi phím giữa 2619 mã.
  useEffect(() => {
    if (trimmed === '') {
      requestIdRef.current += 1
      setSuggestions([])
      setQueriedFor('')
      setTotalFound(0)
      setLoading(false)
      setSearchError(null)
      return
    }
    setLoading(true)
    const timer = setTimeout(() => {
      const requestId = ++requestIdRef.current
      catalogueApi
        .searchAnalytes({ q: trimmed, category: '', limit: SUGGESTION_LIMIT, offset: 0 })
        .then((result) => {
          if (requestIdRef.current !== requestId) return
          setSuggestions(result.items)
          setTotalFound(result.total)
          setQueriedFor(trimmed)
          setSearchError(null)
        })
        .catch((err: unknown) => {
          if (requestIdRef.current !== requestId) return
          setSuggestions([])
          setTotalFound(0)
          setQueriedFor(trimmed)
          setSearchError(errorMessageOf(err))
        })
        .finally(() => {
          if (requestIdRef.current === requestId) setLoading(false)
        })
    }, SEARCH_DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [trimmed])

  const matched =
    queriedFor === trimmed && trimmed !== ''
      ? (suggestions.find((s) => s.code.toLowerCase() === trimmed.toLowerCase()) ?? null)
      : null

  /**
   * Chỉ cảnh báo "không có mã này" khi ĐÃ NHÌN THẤY TOÀN BỘ kết quả khớp.
   *
   * Danh sách gợi ý bị chặn ở SUGGESTION_LIMIT dòng. Nếu máy chủ báo còn nhiều
   * hơn thế, mã đúng có thể đang nằm ngoài phần đang hiện — khẳng định nó không
   * tồn tại là nói sai. Một cảnh báo sai ở đây tệ hơn không cảnh báo: nó dạy
   * người dùng bỏ qua đúng cái cảnh báo dùng để chặn gõ nhầm mã.
   */
  const notFound =
    trimmed !== '' &&
    !loading &&
    searchError === null &&
    queriedFor === trimmed &&
    matched === null &&
    totalFound <= SUGGESTION_LIMIT

  const select = (analyte: AnalyteView): void => {
    onChange(analyte.code)
    setOpen(false)
  }

  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>): void => {
    if (!open || suggestions.length === 0) return
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      setHighlight((i) => Math.min(i + 1, suggestions.length - 1))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setHighlight((i) => Math.max(i - 1, 0))
    } else if (e.key === 'Enter') {
      const chosen = suggestions[highlight]
      if (chosen) {
        e.preventDefault()
        select(chosen)
      }
    } else if (e.key === 'Escape') {
      setOpen(false)
    }
  }

  const listId = `${id}-suggestions`
  const errorId = `${id}-error`
  const hintId = `${id}-hint`
  const statusId = `${id}-match-status`
  const describedBy = [
    error ? errorId : null,
    hint ? hintId : null,
    statusId,
  ]
    .filter(Boolean)
    .join(' ')

  return (
    <div className="editor-field analyte-picker">
      <label htmlFor={id}>{label}</label>
      <div className="analyte-picker-input-wrap">
        <SearchIcon className="analyte-picker-icon" />
        <input
          id={id}
          type="text"
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          autoComplete="off"
          value={value}
          disabled={disabled}
          onChange={(e) => {
            onChange(e.target.value)
            setHighlight(0)
            setOpen(true)
          }}
          onFocus={() => setOpen(true)}
          onBlur={() => {
            // Đóng danh sách khi rời ô; lựa chọn bằng chuột dùng onMouseDown
            // nên vẫn kịp chạy trước khi blur đóng danh sách này.
            setOpen(false)
            onBlur?.()
          }}
          onKeyDown={handleKeyDown}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy === '' ? undefined : describedBy}
        />
        {open && trimmed !== '' && suggestions.length > 0 && (
          <ul className="analyte-picker-list" id={listId} role="listbox">
            {suggestions.map((analyte, index) => (
              <li key={analyte.code}>
                <button
                  type="button"
                  role="option"
                  aria-selected={index === highlight}
                  className={`analyte-picker-option${index === highlight ? ' is-highlighted' : ''}`}
                  onMouseDown={(e) => {
                    e.preventDefault()
                    select(analyte)
                  }}
                  onMouseEnter={() => setHighlight(index)}
                >
                  <code>{analyte.code}</code>
                  <span className="analyte-picker-option-name">{analyte.name}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div id={statusId} aria-live="polite">
        {loading && <p className="field-hint">Đang tìm xét nghiệm khớp “{trimmed}”...</p>}
        {!loading && searchError !== null && (
          <p className="field-hint">
            Không tra được danh mục để gợi ý: {searchError} Vẫn có thể gõ mã tay.
          </p>
        )}
        {!loading && searchError === null && matched !== null && (
          <p className="analyte-picker-match analyte-picker-match-ok">
            <CheckCircleIcon className="status-badge-icon" />
            Đã chọn: <strong>{matched.name}</strong>
          </p>
        )}
        {!loading && searchError === null && notFound && (
          <p className="analyte-picker-match analyte-picker-match-warn">
            <WarningTriangleIcon className="status-badge-icon" />
            Không tìm thấy mã <code>{trimmed}</code> trong danh mục xét nghiệm — kiểm tra lại
            chính tả trước khi lưu, hoặc chọn một dòng từ danh sách gợi ý.
          </p>
        )}
      </div>

      {hint && (
        <p className="field-hint" id={hintId}>
          {hint}
        </p>
      )}
      {error && (
        <p className="editor-error" id={errorId}>
          {error}
        </p>
      )}
    </div>
  )
}
