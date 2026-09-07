import { useCallback, useEffect, useState, type JSX } from 'react'
import {
  ApiError,
  catalogueApi,
  errorMessageOf,
  type HisMapSummary,
  type HisMapView,
  type UnmappedAnalyteView,
} from '../api.js'
import { AnalytePicker } from '../components/AnalytePicker.js'
import {
  CheckCircleIcon,
  PencilIcon,
  PlusIcon,
  SearchIcon,
  TrashIcon,
  UnlinkIcon,
  WarningTriangleIcon,
} from '../statusMeta.js'

/**
 * Màn hình ánh xạ mã HIS ↔ dịch vụ xét nghiệm.
 *
 * Đây là bảng dịch giữa hệ thống thông tin bệnh viện và LIS: HIS gửi sang mã
 * dịch vụ của nó, LIS phải biết mã đó là xét nghiệm nào của mình. Sai một dòng
 * ở đây là chỉ định của bác sĩ chạy sang xét nghiệm khác — nên mọi quyết định
 * bố cục dưới đây phục vụ việc NHÌN THẤY sai sót:
 *
 *   1. Hai câu hỏi khác nhau, hai chế độ xem có nhãn chữ rõ ràng:
 *      "mã HIS này chạy vào đâu" (bảng ánh xạ) và "xét nghiệm nào HIS CHƯA gọi
 *      được" (danh sách chưa ánh xạ). Câu thứ hai không trả lời được bằng cách
 *      nhìn bảng ánh xạ, vì thứ cần tìm chính là những dòng KHÔNG có ở đó.
 *   2. Số xét nghiệm chưa ánh xạ nằm ngay đầu màn hình, trước cả bảng.
 *   3. Mọi dòng hiện cả MÃ lẫn TÊN xét nghiệm đích. Mã hệ cũ (929AG, 034NEU%)
 *      không đọc ra nghĩa được.
 *   4. Mã HIS là khoá chính: thêm trùng bị Core chặn bằng 409 và ở đây được
 *      dịch thành một hộp thoại GIẢI THÍCH nói rõ mã đó đang trỏ tới đâu, kèm
 *      đường thoát (sửa dòng cũ), chứ không phải câu "Lỗi 409".
 *
 * 650 dòng thật: tìm kiếm và phân trang đều chạy phía máy chủ.
 */

const PAGE_SIZE = 50
const SEARCH_DEBOUNCE_MS = 300

type Tab = 'map' | 'unmapped'

interface EditorState {
  /** null = đang THÊM mới; có giá trị = đang SỬA mã HIS đó (không đổi được mã). */
  editingCode: string | null
  hisServiceCode: string
  analyteCode: string
}

interface FormErrors {
  hisServiceCode?: string
  analyteCode?: string
}

/** Xung đột khoá chính do Core trả về (409) — giữ đủ ngữ cảnh để giải thích. */
interface Conflict {
  hisServiceCode: string
  targetAnalyteCode: string
  serverMessage: string
}

function validate(editor: EditorState): FormErrors {
  const errors: FormErrors = {}
  if (editor.hisServiceCode.trim() === '') {
    errors.hisServiceCode = 'Bắt buộc — đây là mã dịch vụ do phần mềm của bệnh viện gửi sang.'
  }
  if (editor.analyteCode.trim() === '') {
    errors.analyteCode = 'Bắt buộc — chọn xét nghiệm trong danh mục mà mã HIS này trỏ tới.'
  }
  return errors
}

export function CatalogueHisMap(): JSX.Element {
  const [tab, setTab] = useState<Tab>('map')

  const [queryText, setQueryText] = useState('')
  const [appliedQuery, setAppliedQuery] = useState('')
  const [offset, setOffset] = useState(0)

  const [items, setItems] = useState<HisMapView[]>([])
  const [unmapped, setUnmapped] = useState<UnmappedAnalyteView[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)

  const [summary, setSummary] = useState<HisMapSummary | null>(null)
  const [summaryError, setSummaryError] = useState<string | null>(null)

  const [editor, setEditor] = useState<EditorState | null>(null)
  const [formErrors, setFormErrors] = useState<FormErrors>({})
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [conflict, setConflict] = useState<Conflict | null>(null)
  const [pendingDelete, setPendingDelete] = useState<HisMapView | null>(null)

  // Hoãn gọi khi gõ: mỗi lần tìm là một truy vấn thật trên 650 dòng ánh xạ và
  // 2619 dòng danh mục, không phải lọc mảng trong bộ nhớ.
  useEffect(() => {
    const timer = setTimeout(() => {
      setAppliedQuery(queryText)
      setOffset(0)
    }, SEARCH_DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [queryText])

  const loadSummary = useCallback(() => {
    let cancelled = false
    catalogueApi
      .hisMapSummary()
      .then((value) => {
        if (cancelled) return
        setSummary(value)
        setSummaryError(null)
      })
      .catch((err: unknown) => {
        // Mất các con số tổng không được làm hỏng cả màn hình, nhưng cũng
        // KHÔNG được im lặng: câu hỏi chính của màn hình nằm ở đúng con số này.
        if (cancelled) return
        setSummary(null)
        setSummaryError(errorMessageOf(err))
      })
    return () => {
      cancelled = true
    }
  }, [])

  const reload = useCallback(() => {
    let cancelled = false
    setLoading(true)
    const params = { q: appliedQuery, limit: PAGE_SIZE, offset }
    const promise =
      tab === 'map'
        ? catalogueApi.searchHisMap(params).then((result) => {
            if (cancelled) return
            setItems(result.items)
            setUnmapped([])
            setTotal(result.total)
          })
        : catalogueApi.unmappedAnalytes(params).then((result) => {
            if (cancelled) return
            setUnmapped(result.items)
            setItems([])
            setTotal(result.total)
          })

    promise
      .then(() => {
        if (cancelled) return
        setLoadError(null)
        setLoading(false)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        // Xoá dữ liệu cũ: hiện kết quả của lần tìm TRƯỚC dưới từ khoá MỚI là
        // nói dối người dùng.
        setItems([])
        setUnmapped([])
        setTotal(0)
        setLoadError(errorMessageOf(err))
        setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [tab, appliedQuery, offset])

  useEffect(() => reload(), [reload])
  useEffect(() => loadSummary(), [loadSummary])

  const refreshAll = async (): Promise<void> => {
    reload()
    loadSummary()
  }

  const closeEditor = (): void => {
    setEditor(null)
    setFormErrors({})
    setSaveError(null)
  }

  const startCreate = (analyteCode = ''): void => {
    setEditor({ editingCode: null, hisServiceCode: '', analyteCode })
    setFormErrors({})
    setSaveError(null)
    setNotice(null)
  }

  const startEdit = (row: HisMapView): void => {
    setEditor({
      editingCode: row.hisServiceCode,
      hisServiceCode: row.hisServiceCode,
      analyteCode: row.analyteCode,
    })
    setFormErrors({})
    setSaveError(null)
    setNotice(null)
  }

  const submit = async (): Promise<void> => {
    if (!editor) return
    const errors = validate(editor)
    setFormErrors(errors)
    if (Object.keys(errors).length > 0) {
      setSaveError('CHƯA LƯU — còn ô nhập chưa hợp lệ. Sửa các ô được đánh dấu rồi bấm Lưu lại.')
      return
    }

    const hisServiceCode = editor.hisServiceCode.trim()
    const analyteCode = editor.analyteCode.trim()
    setSaving(true)
    setSaveError(null)
    try {
      if (editor.editingCode === null) {
        await catalogueApi.createHisMap(hisServiceCode, analyteCode)
        setNotice(
          `Đã lưu: mã HIS ${hisServiceCode} từ nay chạy vào xét nghiệm ${analyteCode}.`,
        )
      } else {
        await catalogueApi.updateHisMap(hisServiceCode, analyteCode)
        setNotice(`Đã lưu: mã HIS ${hisServiceCode} chuyển sang xét nghiệm ${analyteCode}.`)
      }
      closeEditor()
      await refreshAll()
    } catch (err: unknown) {
      if (err instanceof ApiError && err.status === 409) {
        // 409 ở đây là quy tắc nghiệp vụ (mã HIS là khoá chính), không phải sự
        // cố kỹ thuật — chuyển sang hộp thoại giải thích.
        setConflict({ hisServiceCode, targetAnalyteCode: analyteCode, serverMessage: err.message })
        setSaveError(null)
      } else {
        setSaveError(
          `CHƯA LƯU — ${errorMessageOf(err)} Nội dung bạn nhập vẫn còn ở đây; sửa lại rồi bấm Lưu.`,
        )
      }
    } finally {
      setSaving(false)
    }
  }

  /** Đường thoát của hộp thoại xung đột: sửa thẳng dòng đang có, có chủ ý. */
  const overwriteConflict = async (): Promise<void> => {
    if (!conflict) return
    setSaving(true)
    try {
      await catalogueApi.updateHisMap(conflict.hisServiceCode, conflict.targetAnalyteCode)
      setNotice(
        `Đã chuyển mã HIS ${conflict.hisServiceCode} sang xét nghiệm ${conflict.targetAnalyteCode}.`,
      )
      setConflict(null)
      closeEditor()
      await refreshAll()
    } catch (err: unknown) {
      setSaveError(`CHƯA LƯU — không sửa được dòng ánh xạ: ${errorMessageOf(err)}`)
      setConflict(null)
    } finally {
      setSaving(false)
    }
  }

  const confirmDelete = async (): Promise<void> => {
    if (!pendingDelete) return
    const target = pendingDelete
    setSaving(true)
    try {
      await catalogueApi.deleteHisMap(target.hisServiceCode)
      setPendingDelete(null)
      setNotice(
        `Đã xoá ánh xạ mã HIS ${target.hisServiceCode}. Từ giờ chỉ định gửi sang bằng mã này sẽ không vào xét nghiệm nào.`,
      )
      await refreshAll()
    } catch (err: unknown) {
      setPendingDelete(null)
      setSaveError(`CHƯA XOÁ — ${errorMessageOf(err)}`)
    } finally {
      setSaving(false)
    }
  }

  const switchTab = (next: Tab): void => {
    setTab(next)
    setOffset(0)
    setQueryText('')
    setAppliedQuery('')
    setNotice(null)
    closeEditor()
  }

  const from = total === 0 ? 0 : offset + 1
  const to = Math.min(offset + PAGE_SIZE, total)

  return (
    <section className="panel">
      <h2>Ánh xạ mã HIS ↔ dịch vụ xét nghiệm</h2>

      <p className="field-hint">
        Phần mềm của bệnh viện (HIS) gửi chỉ định sang bằng MÃ DỊCH VỤ của nó (ví dụ{' '}
        <code>A/01</code>, <code>GLU01</code>). Bảng dưới đây dịch mã đó sang mã xét nghiệm của
        LIS. Một dòng sai ở đây làm chỉ định của bác sĩ chạy sang xét nghiệm khác; một xét nghiệm
        thiếu dòng ở đây thì HIS không gọi được.
      </p>

      {summaryError && (
        <div className="notice notice-warn" role="status">
          <WarningTriangleIcon className="notice-icon" />
          <p>
            Chưa đếm được số xét nghiệm HIS chưa gọi tới được: {summaryError} Bảng bên dưới vẫn
            dùng được bình thường.
          </p>
        </div>
      )}

      {summary && summary.unmappedAnalyteCount > 0 && (
        <div className="notice notice-warn" role="status">
          <UnlinkIcon className="notice-icon" />
          <div>
            <p>
              <strong>
                {summary.unmappedAnalyteCount}/{summary.analyteCount} xét nghiệm CHƯA CÓ MÃ HIS
                NÀO TRỎ TỚI.
              </strong>{' '}
              Bác sĩ chỉ định được các xét nghiệm này ở HIS, nhưng chỉ định đó không sang tới LIS.
              Hiện có {summary.mappingCount} dòng ánh xạ, trỏ tới {summary.mappedAnalyteCount} xét
              nghiệm.
            </p>
            {tab === 'map' && (
              <button type="button" className="btn btn-sm" onClick={() => switchTab('unmapped')}>
                Xem danh sách xét nghiệm chưa có mã HIS
              </button>
            )}
          </div>
        </div>
      )}

      {summary && summary.unmappedAnalyteCount === 0 && (
        <div className="notice notice-ok" role="status">
          <CheckCircleIcon className="notice-icon" />
          <p>
            Cả {summary.analyteCount} xét nghiệm trong danh mục đều có ít nhất một mã HIS trỏ tới
            ({summary.mappingCount} dòng ánh xạ).
          </p>
        </div>
      )}

      <div className="btn-row" style={{ marginBottom: 'var(--space-4)' }}>
        <button
          type="button"
          className={tab === 'map' ? 'btn btn-primary' : 'btn'}
          aria-pressed={tab === 'map'}
          onClick={() => switchTab('map')}
        >
          Bảng ánh xạ mã HIS
          {summary ? ` (${summary.mappingCount} dòng)` : ''}
        </button>
        <button
          type="button"
          className={tab === 'unmapped' ? 'btn btn-primary' : 'btn'}
          aria-pressed={tab === 'unmapped'}
          onClick={() => switchTab('unmapped')}
        >
          Xét nghiệm chưa có mã HIS
          {summary ? ` (${summary.unmappedAnalyteCount} xét nghiệm)` : ''}
        </button>
      </div>

      <div className="toolbar">
        <div className="toolbar-field toolbar-grow">
          <label htmlFor="his-search">
            {tab === 'map'
              ? 'Tìm theo mã HIS, hoặc theo mã/tên xét nghiệm đích'
              : 'Tìm theo mã hoặc tên xét nghiệm'}
          </label>
          <div className="search-box">
            <SearchIcon className="search-icon" />
            <input
              id="his-search"
              type="search"
              value={queryText}
              onChange={(e) => setQueryText(e.target.value)}
              placeholder={tab === 'map' ? 'Ví dụ: A/01, 929AG, Glucose' : 'Ví dụ: 400TPTNT, nước tiểu'}
              autoComplete="off"
            />
          </div>
        </div>
        {tab === 'map' && (
          <div className="toolbar-field">
            <button
              type="button"
              className="btn"
              onClick={() => startCreate()}
              disabled={editor !== null}
            >
              <PlusIcon />
              Thêm ánh xạ
            </button>
          </div>
        )}
      </div>

      {notice && (
        <div className="notice notice-ok" role="status" aria-live="polite">
          <CheckCircleIcon className="notice-icon" />
          <p>{notice}</p>
        </div>
      )}

      {saveError && editor === null && (
        <div className="notice notice-danger" role="alert">
          <WarningTriangleIcon className="notice-icon" />
          <p>{saveError}</p>
        </div>
      )}

      {loadError && (
        <div className="notice notice-danger" role="alert">
          <WarningTriangleIcon className="notice-icon" />
          <div>
            <p>
              <strong>Không tải được dữ liệu.</strong> {loadError}
            </p>
            <button type="button" className="btn btn-sm" onClick={() => reload()}>
              Thử lại
            </button>
          </div>
        </div>
      )}

      {editor && (
        <div className="editor">
          <h4 className="editor-title">
            {editor.editingCode === null
              ? 'Thêm ánh xạ mã HIS mới'
              : `Sửa ánh xạ mã HIS ${editor.editingCode}`}
          </h4>

          {saveError && (
            <div className="notice notice-danger" role="alert">
              <WarningTriangleIcon className="notice-icon" />
              <p>{saveError}</p>
            </div>
          )}

          <div className="editor-grid">
            <div className="editor-field">
              <label htmlFor="his-code">Mã dịch vụ của HIS</label>
              <input
                id="his-code"
                type="text"
                value={editor.hisServiceCode}
                disabled={editor.editingCode !== null}
                onChange={(e) =>
                  setEditor((prev) => (prev ? { ...prev, hisServiceCode: e.target.value } : prev))
                }
                onBlur={() => setFormErrors(validate(editor))}
                aria-invalid={formErrors.hisServiceCode ? true : undefined}
                aria-describedby={formErrors.hisServiceCode ? 'his-code-error' : 'his-code-hint'}
              />
              <p className="field-hint" id="his-code-hint">
                Đúng như HIS gửi sang. Mã này là khoá chính: không sửa được sau khi tạo — muốn đổi
                thì xoá rồi tạo lại.
              </p>
              {formErrors.hisServiceCode && (
                <p className="editor-error" id="his-code-error">
                  {formErrors.hisServiceCode}
                </p>
              )}
            </div>

            <AnalytePicker
              id="his-analyte"
              label="Xét nghiệm của LIS được trỏ tới"
              value={editor.analyteCode}
              onChange={(code) =>
                setEditor((prev) => (prev ? { ...prev, analyteCode: code } : prev))
              }
              onBlur={() => setFormErrors(validate(editor))}
              error={formErrors.analyteCode}
              hint="Gõ mã hoặc tên để tìm. Mã không có trong danh mục sẽ bị máy chủ từ chối."
            />
          </div>

          <div className="btn-row">
            <button
              type="button"
              className="btn btn-primary"
              onClick={() => void submit()}
              disabled={saving}
            >
              {saving && <span className="spinner" />}
              {saving ? 'Đang lưu...' : 'Lưu ánh xạ'}
            </button>
            <button type="button" className="btn" onClick={closeEditor} disabled={saving}>
              Huỷ
            </button>
          </div>
        </div>
      )}

      {loading && <p className="loading-text">Đang tải...</p>}

      {!loading && tab === 'map' && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Mã dịch vụ HIS</th>
                <th scope="col">Xét nghiệm của LIS</th>
                <th scope="col">Thao tác</th>
              </tr>
            </thead>
            <tbody>
              {items.map((row) => (
                <tr key={row.hisServiceCode}>
                  <td>
                    <code>{row.hisServiceCode}</code>
                  </td>
                  <td className="cell-wrap">
                    <code>{row.analyteCode}</code>
                    <span className="cell-sub">
                      {row.analyteName ?? 'KHÔNG CÒN TRONG DANH MỤC — mã đích có thể đã bị xoá'}
                    </span>
                  </td>
                  <td>
                    <div className="btn-row">
                      <button type="button" className="btn btn-sm" onClick={() => startEdit(row)}>
                        <PencilIcon />
                        Sửa
                      </button>
                      <button
                        type="button"
                        className="btn btn-sm btn-danger"
                        onClick={() => setPendingDelete(row)}
                      >
                        <TrashIcon />
                        Xoá
                      </button>
                    </div>
                  </td>
                </tr>
              ))}

              {items.length === 0 && (
                <tr>
                  <td colSpan={3} className="empty-cell">
                    {appliedQuery.trim() !== '' ? (
                      <>
                        <p>
                          <strong>
                            Không có dòng ánh xạ nào khớp “{appliedQuery.trim()}”.
                          </strong>
                        </p>
                        <p>
                          Mã HIS và mã xét nghiệm chỉ khớp từ ĐẦU chuỗi; tên xét nghiệm khớp ở
                          giữa. Thử rút ngắn từ khoá, hoặc xem chế độ “Xét nghiệm chưa có mã HIS”
                          nếu đang tìm một xét nghiệm chưa được ánh xạ.
                        </p>
                      </>
                    ) : (
                      <p>
                        Bảng dịch mã HIS đang trống. Chừng nào chưa có dòng nào, mọi chỉ định từ HIS
                        gửi sang đều không vào được xét nghiệm nào.
                      </p>
                    )}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {!loading && tab === 'unmapped' && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Mã xét nghiệm</th>
                <th scope="col">Tên xét nghiệm</th>
                <th scope="col">Nhóm</th>
                <th scope="col">Tình trạng</th>
                <th scope="col">Thao tác</th>
              </tr>
            </thead>
            <tbody>
              {unmapped.map((row) => (
                <tr key={row.code} className="row-warn">
                  <td>
                    <code>{row.code}</code>
                  </td>
                  <td className="cell-wrap">
                    {row.name}
                    <span className="cell-sub">HIS chưa gọi được xét nghiệm này</span>
                  </td>
                  <td>{row.category ?? '—'}</td>
                  <td>
                    {row.active ? (
                      <span className="status-badge status-badge-sm tone-ok">
                        <CheckCircleIcon className="status-badge-icon" />
                        <span className="status-badge-label">Còn dùng</span>
                      </span>
                    ) : (
                      <span className="status-badge status-badge-sm tone-neutral">
                        <span className="status-badge-label">Hệ cũ đã ngừng dùng</span>
                      </span>
                    )}
                  </td>
                  <td>
                    <button
                      type="button"
                      className="btn btn-sm"
                      onClick={() => {
                        setTab('map')
                        setQueryText('')
                        setAppliedQuery('')
                        setOffset(0)
                        startCreate(row.code)
                      }}
                    >
                      <PlusIcon />
                      Thêm mã HIS cho xét nghiệm này
                    </button>
                  </td>
                </tr>
              ))}

              {unmapped.length === 0 && (
                <tr>
                  <td colSpan={5} className="empty-cell">
                    {appliedQuery.trim() !== '' ? (
                      <p>
                        Không có xét nghiệm chưa ánh xạ nào khớp “{appliedQuery.trim()}”. Nếu xét
                        nghiệm đó tồn tại thì nó ĐÃ có mã HIS trỏ tới — tìm nó ở chế độ “Bảng ánh
                        xạ mã HIS”.
                      </p>
                    ) : (
                      <p>
                        Mọi xét nghiệm trong danh mục đều đã có ít nhất một mã HIS trỏ tới.
                      </p>
                    )}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {!loading && (
        <div className="pager">
          <p className="pager-info">
            {total === 0
              ? 'Không có dòng nào khớp bộ lọc hiện tại.'
              : tab === 'map'
                ? `Đang hiện ${from}–${to} trong ${total} dòng ánh xạ khớp bộ lọc.`
                : `Đang hiện ${from}–${to} trong ${total} xét nghiệm chưa có mã HIS.`}
          </p>
          <div className="btn-row">
            <button
              type="button"
              className="btn btn-sm"
              onClick={() => {
                closeEditor()
                setOffset(Math.max(0, offset - PAGE_SIZE))
              }}
              disabled={offset === 0}
            >
              Trang trước
            </button>
            <button
              type="button"
              className="btn btn-sm"
              onClick={() => {
                closeEditor()
                setOffset(offset + PAGE_SIZE)
              }}
              disabled={to >= total}
            >
              Trang sau
            </button>
          </div>
        </div>
      )}

      {conflict && (
        <div className="dialog-backdrop" role="presentation">
          <div
            className="dialog"
            role="alertdialog"
            aria-modal="true"
            aria-labelledby="his-conflict-title"
          >
            <h3 id="his-conflict-title">Không thêm được — mã HIS này đã có ánh xạ</h3>
            <p>
              Mã dịch vụ <span className="code-inline">{conflict.hisServiceCode}</span> là khoá
              chính của bảng dịch: mỗi mã HIS chỉ được trỏ tới đúng một xét nghiệm.
            </p>
            <p className="field-hint">Máy chủ trả về: {conflict.serverMessage}</p>
            <p>
              Nếu đúng là muốn cho mã này chạy sang xét nghiệm{' '}
              <span className="code-inline">{conflict.targetAnalyteCode}</span>, hãy SỬA dòng đang
              có. Đây là thay đổi đường đi của chỉ định và được ghi vào nhật ký sửa đổi.
            </p>
            <div className="dialog-actions">
              <button
                type="button"
                className="btn"
                onClick={() => setConflict(null)}
                disabled={saving}
              >
                Giữ nguyên ánh xạ cũ
              </button>
              <button
                type="button"
                className="btn btn-danger"
                onClick={() => void overwriteConflict()}
                disabled={saving}
              >
                {saving && <span className="spinner" />}
                Sửa dòng cũ sang {conflict.targetAnalyteCode}
              </button>
            </div>
          </div>
        </div>
      )}

      {pendingDelete && (
        <div className="dialog-backdrop" role="presentation">
          <div
            className="dialog"
            role="alertdialog"
            aria-modal="true"
            aria-labelledby="his-delete-title"
          >
            <h3 id="his-delete-title">Xoá ánh xạ mã HIS?</h3>
            <p>
              Mã dịch vụ <span className="code-inline">{pendingDelete.hisServiceCode}</span> đang
              trỏ tới xét nghiệm{' '}
              <span className="code-inline">{pendingDelete.analyteCode}</span>
              {pendingDelete.analyteName ? ` — ${pendingDelete.analyteName}` : ''}.
            </p>
            <p>
              Sau khi xoá, chỉ định mà HIS gửi sang bằng mã này sẽ{' '}
              <strong>không vào xét nghiệm nào</strong> cho tới khi được ánh xạ lại.
            </p>
            <div className="dialog-actions">
              <button
                type="button"
                className="btn"
                onClick={() => setPendingDelete(null)}
                disabled={saving}
              >
                Không xoá
              </button>
              <button
                type="button"
                className="btn btn-danger"
                onClick={() => void confirmDelete()}
                disabled={saving}
              >
                {saving && <span className="spinner" />}
                Xoá ánh xạ
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  )
}
