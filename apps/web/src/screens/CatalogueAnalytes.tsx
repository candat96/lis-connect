import { Fragment, useCallback, useEffect, useMemo, useState, type JSX } from 'react'
import {
  catalogueApi,
  errorMessageOf,
  type AnalytePatch,
  type AnalyteView,
} from '../api.js'
import {
  CheckCircleIcon,
  PencilIcon,
  SearchIcon,
  UnknownDashedIcon,
  UnpluggedIcon,
  WarningTriangleIcon,
} from '../statusMeta.js'

/**
 * Màn hình danh mục xét nghiệm.
 *
 * Bảng thật có 2619 dòng (hệ cũ có HAI bảng danh mục tách biệt, không phải một
 * bảng 404 dòng như kế hoạch ban đầu ước lượng). Hệ quả kiến trúc:
 *
 *   - Tìm kiếm, lọc nhóm và phân trang đều chạy PHÍA MÁY CHỦ. Không có chỗ nào
 *     trong màn hình này tải toàn bộ danh mục về rồi lọc trong trình duyệt;
 *     API cũng đã chặn cứng `limit` ở 200 nên đường đó không tồn tại.
 *   - Mỗi phím gõ KHÔNG bắn một request: ô tìm kiếm hoãn 300ms.
 *
 * Ô tìm kiếm khớp TIỀN TỐ với mã (gõ `123AS` ra `123AST`) nhưng khớp bất kỳ vị
 * trí nào với tên. Luật này không đoán được, nên nó được nói thẳng ngay dưới ô
 * tìm — coi như một phần của chức năng chứ không phải chú thích trang trí.
 */

const PAGE_SIZE = 50
const SEARCH_DEBOUNCE_MS = 300

/**
 * Ba nguồn dữ liệu, KHÔNG phải hai. Giá trị lạ vẫn hiển thị được.
 *
 * `manual` ở đây nghĩa là "dòng do người dùng TỰ THÊM mới", khác hẳn với cờ
 * `manuallyEdited` (một dòng `legacy` hay `legacy-new` đã bị SỬA vẫn giữ
 * nguyên `source` cũ — hai thứ độc lập, xem `ManuallyEditedBadge` bên dưới).
 */
const SOURCE_META: Record<string, { label: string; toneClass: string }> = {
  legacy: { label: 'Danh mục cũ', toneClass: 'tone-neutral' },
  'legacy-new': { label: 'Danh mục mới', toneClass: 'tone-neutral' },
  manual: { label: 'Tự thêm', toneClass: 'tone-accent' },
}

const SOURCE_FILTER_OPTIONS: Array<{ value: string; label: string }> = [
  { value: '', label: 'Tất cả nguồn gốc' },
  { value: 'legacy', label: 'Danh mục cũ' },
  { value: 'legacy-new', label: 'Danh mục mới' },
  { value: 'manual', label: 'Tự thêm' },
]

const ACTIVE_FILTER_OPTIONS: Array<{ value: string; label: string }> = [
  { value: '', label: 'Tất cả tình trạng' },
  { value: 'true', label: 'Đang dùng' },
  { value: 'false', label: 'Đã ngưng dùng' },
]

const MANUALLY_EDITED_FILTER_OPTIONS: Array<{ value: string; label: string }> = [
  { value: '', label: 'Đã sửa tay hay chưa — tất cả' },
  { value: 'true', label: 'Chỉ dòng đã sửa tay' },
  { value: 'false', label: 'Chỉ dòng chưa sửa tay' },
]

function SourceBadge({ source }: { source: string }): JSX.Element {
  const meta = SOURCE_META[source]
  if (!meta) {
    // Nguồn lạ (dữ liệu mới hơn giao diện): nói thẳng là không rõ, kèm giá trị
    // thô, thay vì im lặng hiển thị như một trong ba nguồn đã biết.
    return (
      <span className="status-badge status-badge-sm tone-warn">
        <UnknownDashedIcon className="status-badge-icon" />
        <span className="status-badge-label">Nguồn không rõ ({source})</span>
      </span>
    )
  }
  return (
    <span className={`status-badge status-badge-sm ${meta.toneClass}`}>
      <span className="status-badge-label">{meta.label}</span>
    </span>
  )
}

/** Xét nghiệm đang dùng hay đã ngừng — luôn hiện cả hai trạng thái bằng nhãn chữ. */
function ActiveBadge({ active }: { active: boolean }): JSX.Element {
  if (active) {
    return (
      <span className="status-badge status-badge-sm tone-ok">
        <CheckCircleIcon className="status-badge-icon" />
        <span className="status-badge-label">Đang dùng</span>
      </span>
    )
  }
  return (
    <span className="status-badge status-badge-sm tone-neutral">
      <UnpluggedIcon className="status-badge-icon" />
      <span className="status-badge-label">Đã ngưng dùng</span>
    </span>
  )
}

/**
 * Chỉ hiện khi TRUE — "chưa sửa tay" là trạng thái mặc định của phần lớn 2619
 * dòng, gắn thêm huy hiệu cho nó sẽ chỉ làm rối bảng. Độc lập với nguồn gốc:
 * huy hiệu này đứng CẠNH `SourceBadge`, không thay thế nó.
 */
function ManuallyEditedBadge({ manuallyEdited }: { manuallyEdited: boolean }): JSX.Element | null {
  if (!manuallyEdited) return null
  return (
    <span className="status-badge status-badge-sm tone-accent">
      <PencilIcon className="status-badge-icon" />
      <span className="status-badge-label">Đã sửa tay</span>
    </span>
  )
}

/** Các trường sửa được tại chỗ, giữ ở dạng chuỗi đúng như người dùng gõ. */
interface Draft {
  unit: string
  decimals: string
  lowerLimit: string
  higherLimit: string
  commentLower: string
  commentHigher: string
  commentGray: string
  printOrder: string
}

type DraftField = keyof Draft

const FIELD_LABEL: Record<DraftField, string> = {
  unit: 'Đơn vị',
  decimals: 'Số chữ số thập phân',
  lowerLimit: 'Ngưỡng dưới',
  higherLimit: 'Ngưỡng trên',
  commentLower: 'Biện luận khi thấp',
  commentHigher: 'Biện luận khi cao',
  commentGray: 'Biện luận vùng xám',
  printOrder: 'Thứ tự in',
}

function textOf(value: string | null): string {
  return value ?? ''
}

function numberTextOf(value: number | null): string {
  return value === null ? '' : String(value)
}

function draftOf(analyte: AnalyteView): Draft {
  return {
    unit: textOf(analyte.unit),
    decimals: numberTextOf(analyte.decimals),
    lowerLimit: numberTextOf(analyte.lowerLimit),
    higherLimit: numberTextOf(analyte.higherLimit),
    commentLower: textOf(analyte.commentLower),
    commentHigher: textOf(analyte.commentHigher),
    commentGray: textOf(analyte.commentGray),
    printOrder: numberTextOf(analyte.printOrder),
  }
}

function formatNumber(value: number | null): string {
  return value === null ? '—' : String(value)
}

/** Chuỗi rỗng nghĩa là "xoá giá trị" (null), không phải 0. */
function parseOptionalNumber(raw: string): number | null {
  const trimmed = raw.trim().replace(',', '.')
  if (trimmed === '') return null
  const value = Number(trimmed)
  return Number.isFinite(value) ? value : Number.NaN
}

type FieldErrors = Partial<Record<DraftField, string>>

/**
 * Kiểm tra hợp lệ toàn bộ bản nháp. Được gọi khi rời ô (`onBlur`) và một lần
 * nữa trước khi lưu — không kiểm theo từng phím, vì gõ dở "1." hay "-" luôn
 * sai giữa chừng và báo đỏ liên tục làm người dùng mất tin vào thông báo.
 */
function validate(draft: Draft): FieldErrors {
  const errors: FieldErrors = {}

  const decimals = parseOptionalNumber(draft.decimals)
  if (Number.isNaN(decimals)) errors.decimals = 'Phải là một số nguyên, ví dụ 2.'
  else if (decimals !== null && (!Number.isInteger(decimals) || decimals < 0 || decimals > 6)) {
    errors.decimals = 'Phải là số nguyên từ 0 đến 6.'
  }

  const printOrder = parseOptionalNumber(draft.printOrder)
  if (Number.isNaN(printOrder)) errors.printOrder = 'Phải là một số nguyên, ví dụ 120.'
  else if (printOrder !== null && (!Number.isInteger(printOrder) || printOrder < 0)) {
    errors.printOrder = 'Phải là số nguyên không âm.'
  }

  const lower = parseOptionalNumber(draft.lowerLimit)
  if (Number.isNaN(lower)) errors.lowerLimit = 'Phải là một số, ví dụ 3.5. Để trống nếu không có.'
  const higher = parseOptionalNumber(draft.higherLimit)
  if (Number.isNaN(higher)) errors.higherLimit = 'Phải là một số, ví dụ 9.8. Để trống nếu không có.'

  if (
    !Number.isNaN(lower) &&
    !Number.isNaN(higher) &&
    lower !== null &&
    higher !== null &&
    lower > higher
  ) {
    errors.higherLimit = 'Ngưỡng trên phải lớn hơn hoặc bằng ngưỡng dưới.'
  }

  return errors
}

/** Chỉ gửi những trường thật sự đổi — PATCH nào cũng ghi một dòng nhật ký. */
function buildPatch(original: AnalyteView, draft: Draft): AnalytePatch {
  const patch: AnalytePatch = {}
  const base = draftOf(original)

  if (draft.unit !== base.unit) patch.unit = draft.unit.trim() === '' ? null : draft.unit
  if (draft.commentLower !== base.commentLower) {
    patch.commentLower = draft.commentLower === '' ? null : draft.commentLower
  }
  if (draft.commentHigher !== base.commentHigher) {
    patch.commentHigher = draft.commentHigher === '' ? null : draft.commentHigher
  }
  if (draft.commentGray !== base.commentGray) {
    patch.commentGray = draft.commentGray === '' ? null : draft.commentGray
  }
  if (draft.decimals !== base.decimals) patch.decimals = parseOptionalNumber(draft.decimals)
  if (draft.printOrder !== base.printOrder) patch.printOrder = parseOptionalNumber(draft.printOrder)
  if (draft.lowerLimit !== base.lowerLimit) patch.lowerLimit = parseOptionalNumber(draft.lowerLimit)
  if (draft.higherLimit !== base.higherLimit) {
    patch.higherLimit = parseOptionalNumber(draft.higherLimit)
  }
  return patch
}

export function CatalogueAnalytes(): JSX.Element {
  const [queryText, setQueryText] = useState('')
  const [appliedQuery, setAppliedQuery] = useState('')
  const [category, setCategory] = useState('')
  const [sourceFilter, setSourceFilter] = useState('')
  const [activeFilter, setActiveFilter] = useState('')
  const [manuallyEditedFilter, setManuallyEditedFilter] = useState('')
  const [offset, setOffset] = useState(0)

  const [categories, setCategories] = useState<string[]>([])
  const [categoriesError, setCategoriesError] = useState<string | null>(null)

  const [items, setItems] = useState<AnalyteView[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)

  const [editingCode, setEditingCode] = useState<string | null>(null)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [savedNotice, setSavedNotice] = useState<string | null>(null)

  // Hoãn gọi khi gõ: 2619 dòng nghĩa là mỗi lần tìm là một truy vấn thật trên
  // máy chủ, không phải lọc mảng trong bộ nhớ.
  useEffect(() => {
    const timer = setTimeout(() => {
      setAppliedQuery(queryText)
      setOffset(0)
    }, SEARCH_DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [queryText])

  useEffect(() => {
    let cancelled = false
    catalogueApi
      .categories()
      .then((list) => {
        if (!cancelled) {
          setCategories(list)
          setCategoriesError(null)
        }
      })
      .catch((err: unknown) => {
        // Mất danh sách nhóm không được làm hỏng cả màn hình: bảng vẫn dùng
        // được, chỉ riêng bộ lọc nhóm là không.
        if (!cancelled) setCategoriesError(errorMessageOf(err))
      })
    return () => {
      cancelled = true
    }
  }, [])

  const reload = useCallback(() => {
    let cancelled = false
    setLoading(true)
    catalogueApi
      .searchAnalytes({
        q: appliedQuery,
        category,
        source: sourceFilter,
        active: activeFilter,
        manuallyEdited: manuallyEditedFilter,
        limit: PAGE_SIZE,
        offset,
      })
      .then((result) => {
        if (cancelled) return
        setItems(result.items)
        setTotal(result.total)
        setLoadError(null)
        setLoading(false)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        // Xoá dữ liệu cũ: hiển thị kết quả của lần tìm TRƯỚC dưới từ khoá MỚI
        // là nói dối người dùng.
        setItems([])
        setTotal(0)
        setLoadError(errorMessageOf(err))
        setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [appliedQuery, category, sourceFilter, activeFilter, manuallyEditedFilter, offset])

  useEffect(() => reload(), [reload])

  const cancelEdit = (): void => {
    setEditingCode(null)
    setDraft(null)
    setFieldErrors({})
    setSaveError(null)
  }

  const startEdit = (analyte: AnalyteView): void => {
    setEditingCode(analyte.code)
    setDraft(draftOf(analyte))
    setFieldErrors({})
    setSaveError(null)
    setSavedNotice(null)
  }

  const editing = useMemo(
    () => items.find((item) => item.code === editingCode) ?? null,
    [items, editingCode],
  )

  const handleSave = async (): Promise<void> => {
    if (!editing || !draft) return
    const errors = validate(draft)
    setFieldErrors(errors)
    if (Object.keys(errors).length > 0) {
      setSaveError('CHƯA LƯU — còn ô nhập chưa hợp lệ. Sửa các ô được đánh dấu đỏ rồi bấm Lưu lại.')
      return
    }

    const patch = buildPatch(editing, draft)
    if (Object.keys(patch).length === 0) {
      setSaveError(null)
      setSavedNotice(`Không có thay đổi nào ở xét nghiệm ${editing.code} — không gửi lên máy chủ.`)
      cancelEdit()
      return
    }

    setSaving(true)
    setSaveError(null)
    try {
      const updated = await catalogueApi.updateAnalyte(editing.code, patch)
      setItems((prev) => prev.map((item) => (item.code === updated.code ? updated : item)))
      setSavedNotice(
        `Đã lưu xét nghiệm ${updated.code} — ${updated.name}. Dòng này được đánh dấu "Đã sửa tay".`,
      )
      cancelEdit()
    } catch (err: unknown) {
      // Người dùng tưởng đã lưu trong khi chưa lưu là mất dữ liệu — nên câu
      // đầu tiên phải là CHƯA LƯU, và khung sửa phải giữ nguyên chữ đã gõ.
      setSaveError(
        `CHƯA LƯU — ${errorMessageOf(err)} Nội dung bạn nhập vẫn còn nguyên ở đây; sửa lại rồi bấm Lưu, hoặc bấm Huỷ để bỏ thay đổi.`,
      )
    } finally {
      setSaving(false)
    }
  }

  const setField = (field: DraftField, value: string): void => {
    setDraft((prev) => (prev ? { ...prev, [field]: value } : prev))
  }

  const handleBlur = (): void => {
    setDraft((prev) => {
      if (prev) setFieldErrors(validate(prev))
      return prev
    })
  }

  const from = total === 0 ? 0 : offset + 1
  const to = Math.min(offset + PAGE_SIZE, total)

  return (
    <section className="panel">
      <h2>Danh mục xét nghiệm</h2>

      <p className="field-hint">
        Toàn bộ xét nghiệm phòng xét nghiệm này làm được, kèm đơn vị và ngưỡng bình thường — ví dụ{' '}
        <code>123AST</code> (AST/GOT, 5–40 U/L). Đây là danh sách gốc mà các màn hình khác đều nối
        về.
      </p>

      <div className="toolbar">
        <div className="toolbar-field toolbar-grow">
          <label htmlFor="analyte-search">Tìm xét nghiệm</label>
          <div className="search-box">
            <SearchIcon className="search-icon" />
            <input
              id="analyte-search"
              type="search"
              value={queryText}
              onChange={(e) => setQueryText(e.target.value)}
              placeholder="Nhập mã hoặc tên xét nghiệm"
              aria-describedby="analyte-search-hint"
              autoComplete="off"
            />
          </div>
          <p className="field-hint" id="analyte-search-hint">
            Mã khớp từ ĐẦU mã: gõ <code>123AS</code> sẽ ra <code>123AST</code>, nhưng gõ{' '}
            <code>3AST</code> thì không. Tên (tiếng Việt và tiếng Anh) khớp ở bất kỳ vị trí nào.
          </p>
        </div>

        <div className="toolbar-field">
          <label htmlFor="analyte-category">Nhóm xét nghiệm</label>
          <select
            id="analyte-category"
            value={category}
            onChange={(e) => {
              setCategory(e.target.value)
              setOffset(0)
            }}
            disabled={categoriesError !== null}
          >
            <option value="">Tất cả các nhóm</option>
            {categories.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </select>
          {categoriesError && (
            <p className="editor-error">Không tải được danh sách nhóm: {categoriesError}</p>
          )}
        </div>

        <div className="toolbar-field">
          <label htmlFor="analyte-source">Nguồn gốc</label>
          <select
            id="analyte-source"
            value={sourceFilter}
            onChange={(e) => {
              setSourceFilter(e.target.value)
              setOffset(0)
            }}
          >
            {SOURCE_FILTER_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>

        <div className="toolbar-field">
          <label htmlFor="analyte-active">Tình trạng còn dùng</label>
          <select
            id="analyte-active"
            value={activeFilter}
            onChange={(e) => {
              setActiveFilter(e.target.value)
              setOffset(0)
            }}
          >
            {ACTIVE_FILTER_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>

        <div className="toolbar-field">
          <label htmlFor="analyte-manually-edited">Đã sửa tay</label>
          <select
            id="analyte-manually-edited"
            value={manuallyEditedFilter}
            onChange={(e) => {
              setManuallyEditedFilter(e.target.value)
              setOffset(0)
            }}
          >
            {MANUALLY_EDITED_FILTER_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>
      </div>

      {savedNotice && (
        <div className="notice notice-ok" role="status" aria-live="polite">
          <CheckCircleIcon className="notice-icon" />
          <p>{savedNotice}</p>
        </div>
      )}

      {loadError && (
        <div className="notice notice-danger" role="alert">
          <WarningTriangleIcon className="notice-icon" />
          <div>
            <p>
              <strong>Không tải được danh mục.</strong> {loadError}
            </p>
            <button type="button" className="btn btn-sm" onClick={() => reload()}>
              Thử lại
            </button>
          </div>
        </div>
      )}

      {loading && <p className="loading-text">Đang tải danh mục xét nghiệm...</p>}

      {!loading && !loadError && (
        <>
          <div className="table-wrap">
            <table className="table">
              <caption className="visually-hidden-caption">
                Danh mục xét nghiệm, {total} dòng khớp bộ lọc hiện tại
              </caption>
              <thead>
                <tr>
                  <th scope="col">Mã</th>
                  <th scope="col">Tên xét nghiệm</th>
                  <th scope="col">Nhóm</th>
                  <th scope="col">Đơn vị</th>
                  <th scope="col" className="cell-num">
                    Số lẻ
                  </th>
                  <th scope="col" className="cell-num">
                    Ngưỡng dưới
                  </th>
                  <th scope="col" className="cell-num">
                    Ngưỡng trên
                  </th>
                  <th scope="col" className="cell-num">
                    Thứ tự in
                  </th>
                  <th scope="col">Nguồn dữ liệu</th>
                  <th scope="col">Tình trạng</th>
                  <th scope="col">Thao tác</th>
                </tr>
              </thead>
              <tbody>
                {items.map((analyte) => {
                  const isEditing = analyte.code === editingCode
                  return (
                    <Fragment key={analyte.code}>
                      <tr className={isEditing ? 'row-editing' : undefined}>
                        <td>
                          <code>{analyte.code}</code>
                        </td>
                        <td className="cell-name cell-wrap">
                          {analyte.name}
                          {analyte.nameEn && <span className="cell-sub">{analyte.nameEn}</span>}
                        </td>
                        <td>{analyte.category ?? '—'}</td>
                        <td>{analyte.unit ?? '—'}</td>
                        <td className="cell-num">{formatNumber(analyte.decimals)}</td>
                        <td className="cell-num">{formatNumber(analyte.lowerLimit)}</td>
                        <td className="cell-num">{formatNumber(analyte.higherLimit)}</td>
                        <td className="cell-num">{formatNumber(analyte.printOrder)}</td>
                        <td>
                          <span className="badge-list">
                            <SourceBadge source={analyte.source} />
                            <ManuallyEditedBadge manuallyEdited={analyte.manuallyEdited} />
                          </span>
                        </td>
                        <td>
                          <ActiveBadge active={analyte.active} />
                        </td>
                        <td>
                          <button
                            type="button"
                            className="btn btn-sm"
                            onClick={() => (isEditing ? cancelEdit() : startEdit(analyte))}
                            aria-expanded={isEditing}
                          >
                            <PencilIcon />
                            {isEditing ? 'Đóng' : 'Sửa'}
                          </button>
                        </td>
                      </tr>

                      {isEditing && draft && (
                        <tr>
                          <td className="editor-cell" colSpan={11}>
                            <div className="editor">
                              <h3 className="editor-title">
                                Sửa xét nghiệm <code>{analyte.code}</code> — {analyte.name}
                              </h3>

                              {saveError && (
                                <div className="notice notice-danger" role="alert">
                                  <WarningTriangleIcon className="notice-icon" />
                                  <p>{saveError}</p>
                                </div>
                              )}

                              <div className="editor-grid">
                                <EditorField
                                  field="unit"
                                  draft={draft}
                                  errors={fieldErrors}
                                  onChange={setField}
                                  onBlur={handleBlur}
                                  hint="Ví dụ: mmol/L. Để trống nếu xét nghiệm không có đơn vị."
                                />
                                <EditorField
                                  field="decimals"
                                  draft={draft}
                                  errors={fieldErrors}
                                  onChange={setField}
                                  onBlur={handleBlur}
                                  numeric
                                  hint="Số nguyên 0–6."
                                />
                                <EditorField
                                  field="lowerLimit"
                                  draft={draft}
                                  errors={fieldErrors}
                                  onChange={setField}
                                  onBlur={handleBlur}
                                  numeric
                                  hint="Để trống nghĩa là không đặt ngưỡng."
                                />
                                <EditorField
                                  field="higherLimit"
                                  draft={draft}
                                  errors={fieldErrors}
                                  onChange={setField}
                                  onBlur={handleBlur}
                                  numeric
                                  hint="Để trống nghĩa là không đặt ngưỡng."
                                />
                                <EditorField
                                  field="printOrder"
                                  draft={draft}
                                  errors={fieldErrors}
                                  onChange={setField}
                                  onBlur={handleBlur}
                                  numeric
                                  hint="Số càng nhỏ càng in lên trước."
                                />
                                <EditorField
                                  field="commentLower"
                                  draft={draft}
                                  errors={fieldErrors}
                                  onChange={setField}
                                  onBlur={handleBlur}
                                  multiline
                                />
                                <EditorField
                                  field="commentHigher"
                                  draft={draft}
                                  errors={fieldErrors}
                                  onChange={setField}
                                  onBlur={handleBlur}
                                  multiline
                                />
                                <EditorField
                                  field="commentGray"
                                  draft={draft}
                                  errors={fieldErrors}
                                  onChange={setField}
                                  onBlur={handleBlur}
                                  multiline
                                />
                              </div>

                              <div className="btn-row">
                                <button
                                  type="button"
                                  className="btn btn-primary"
                                  onClick={() => void handleSave()}
                                  disabled={saving}
                                >
                                  {saving && <span className="spinner" />}
                                  {saving ? 'Đang lưu...' : 'Lưu thay đổi'}
                                </button>
                                <button
                                  type="button"
                                  className="btn"
                                  onClick={cancelEdit}
                                  disabled={saving}
                                >
                                  Huỷ
                                </button>
                                <span className="field-hint">
                                  Sau khi lưu, dòng này được đánh dấu “Đã sửa tay” (nguồn gốc dữ
                                  liệu ban đầu vẫn giữ nguyên) và thay đổi được ghi vào nhật ký sửa
                                  đổi.
                                </span>
                              </div>
                            </div>
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  )
                })}

                {items.length === 0 && (
                  <tr>
                    <td colSpan={11} className="empty-cell">
                      {appliedQuery.trim() !== '' ||
                      category !== '' ||
                      sourceFilter !== '' ||
                      activeFilter !== '' ||
                      manuallyEditedFilter !== '' ? (
                        <>
                          <p>
                            <strong>
                              Không tìm thấy xét nghiệm nào khớp
                              {appliedQuery.trim() !== '' && <> “{appliedQuery.trim()}”</>}
                              {category !== '' && <> trong nhóm “{category}”</>}
                              {(sourceFilter !== '' ||
                                activeFilter !== '' ||
                                manuallyEditedFilter !== '') && <> với bộ lọc nguồn/tình trạng đang chọn</>}
                              .
                            </strong>
                          </p>
                          <p>Thử một trong các cách sau:</p>
                          <ul>
                            <li>
                              Nếu đang tìm theo mã: mã chỉ khớp từ ĐẦU, hãy bỏ bớt ký tự ở cuối (ví
                              dụ gõ <code>123</code> thay vì <code>123ASTX</code>).
                            </li>
                            <li>Nếu đang tìm theo tên: rút ngắn còn một từ khoá đặc trưng.</li>
                            {category !== '' && <li>Bỏ bộ lọc nhóm về “Tất cả các nhóm”.</li>}
                            {(sourceFilter !== '' ||
                              activeFilter !== '' ||
                              manuallyEditedFilter !== '') && (
                              <li>Đưa các bộ lọc “Nguồn gốc”, “Tình trạng còn dùng”, “Đã sửa tay” về giá trị “Tất cả”.</li>
                            )}
                          </ul>
                        </>
                      ) : (
                        <p>
                          Danh mục xét nghiệm đang trống. Nếu bệnh viện đã có danh mục ở phần mềm
                          LIS cũ, cần chạy công cụ di trú danh mục trước khi dùng màn hình này.
                        </p>
                      )}
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>

          <div className="pager">
            <p className="pager-info">
              {total === 0
                ? 'Không có dòng nào khớp bộ lọc hiện tại.'
                : `Đang hiện ${from}–${to} trong ${total} xét nghiệm khớp bộ lọc.`}
            </p>
            <div className="btn-row">
              <button
                type="button"
                className="btn btn-sm"
                onClick={() => {
                  cancelEdit()
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
                  cancelEdit()
                  setOffset(offset + PAGE_SIZE)
                }}
                disabled={to >= total}
              >
                Trang sau
              </button>
            </div>
          </div>
        </>
      )}
    </section>
  )
}

/**
 * Một ô nhập trong khung sửa. Lỗi nằm NGAY DƯỚI ô và được nối vào ô bằng
 * `aria-describedby`, để trình đọc màn hình đọc được lỗi của đúng ô đó thay vì
 * bắt người dùng đi tìm một danh sách lỗi ở đầu trang.
 */
function EditorField({
  field,
  draft,
  errors,
  onChange,
  onBlur,
  numeric,
  multiline,
  hint,
}: {
  field: DraftField
  draft: Draft
  errors: FieldErrors
  onChange: (field: DraftField, value: string) => void
  onBlur: () => void
  numeric?: boolean
  multiline?: boolean
  hint?: string
}): JSX.Element {
  const id = `analyte-field-${field}`
  const errorId = `${id}-error`
  const hintId = `${id}-hint`
  const error = errors[field]
  const describedBy = [error ? errorId : null, hint ? hintId : null].filter(Boolean).join(' ')
  return (
    <div className="editor-field">
      <label htmlFor={id}>{FIELD_LABEL[field]}</label>
      {multiline ? (
        <textarea
          id={id}
          value={draft[field]}
          onChange={(e) => onChange(field, e.target.value)}
          onBlur={onBlur}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy === '' ? undefined : describedBy}
        />
      ) : (
        <input
          id={id}
          type="text"
          inputMode={numeric ? 'decimal' : 'text'}
          className={numeric ? 'input-num' : undefined}
          value={draft[field]}
          onChange={(e) => onChange(field, e.target.value)}
          onBlur={onBlur}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy === '' ? undefined : describedBy}
        />
      )}
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
