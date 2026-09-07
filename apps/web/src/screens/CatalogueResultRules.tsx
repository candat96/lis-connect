import { useCallback, useEffect, useMemo, useState, type JSX } from 'react'
import {
  catalogueApi,
  errorMessageOf,
  type DeviceSummary,
  type ResultRulePayload,
  type ResultRuleView,
} from '../api.js'
import { AnalytePicker } from '../components/AnalytePicker.js'
import {
  ArrowDownIcon,
  ArrowUpIcon,
  CheckCircleIcon,
  DeviceChipIcon,
  GlobeIcon,
  LevelNormalIcon,
  PencilIcon,
  PlusIcon,
  TrashIcon,
  UnknownDashedIcon,
  WarningTriangleIcon,
} from '../statusMeta.js'

/**
 * Màn hình luật ghi đè kết quả: (xét nghiệm, giá trị máy trả về) → kết quả
 * hiển thị, biện luận, màu.
 *
 * `GET /api/catalogue/devices/:id/result-rules` trả về CẢ luật chung
 * (`deviceId: null`) lẫn luật riêng của máy — đó là cố ý, vì luật chung THẬT SỰ
 * có áp lên máy đang xem (hiện có 6 luật HPV và 6 luật TPHA là luật chung). Hai
 * hệ quả cho giao diện:
 *
 *   - Giấu luật chung đi thì kỹ thuật viên không hiểu vì sao máy diễn giải kết
 *     quả theo một luật không thấy ở đâu. Nên: hiện đủ.
 *   - Không phân biệt thì họ lại tưởng sửa luật chung chỉ ảnh hưởng một máy.
 *     Nên: nhãn CHỮ cho từng phạm vi, và sửa/xoá luật chung phải qua một hộp
 *     thoại nói rõ nó áp cho mọi máy.
 */

/** Màu là số 0/1/2 theo quy ước hệ cũ — người dùng không phải nhớ con số. */
const COLOR_OPTIONS: Array<{
  value: number | null
  label: string
  toneClass: string
  Icon: (props: { className?: string }) => JSX.Element
}> = [
  { value: null, label: 'Không đặt màu', toneClass: 'tone-neutral', Icon: UnknownDashedIcon },
  { value: 0, label: 'Bình thường', toneClass: 'tone-ok', Icon: LevelNormalIcon },
  { value: 1, label: 'Thấp', toneClass: 'tone-warn', Icon: ArrowDownIcon },
  { value: 2, label: 'Cao', toneClass: 'tone-danger', Icon: ArrowUpIcon },
]

function ColorBadge({ color }: { color: number | null }): JSX.Element {
  const option = COLOR_OPTIONS.find((o) => o.value === color)
  if (!option) {
    return (
      <span className="status-badge status-badge-sm tone-warn">
        <UnknownDashedIcon className="status-badge-icon" />
        <span className="status-badge-label">Giá trị màu lạ ({color})</span>
      </span>
    )
  }
  return (
    <span className={`status-badge status-badge-sm ${option.toneClass}`}>
      <option.Icon className="status-badge-icon" />
      <span className="status-badge-label">{option.label}</span>
    </span>
  )
}

function ScopeBadge({ rule }: { rule: ResultRuleView }): JSX.Element {
  const isCommon = rule.scope === 'common'
  const Icon = isCommon ? GlobeIcon : DeviceChipIcon
  return (
    <span className={`status-badge status-badge-sm ${isCommon ? 'tone-warn' : 'tone-neutral'}`}>
      <Icon className="status-badge-icon" />
      <span className="status-badge-label">{rule.scopeLabel}</span>
    </span>
  )
}

interface RuleForm {
  scope: 'device' | 'common'
  analyteCode: string
  resultIn: string
  resultOut: string
  comment: string
  color: string
}

interface RuleFormErrors {
  analyteCode?: string
  resultIn?: string
}

function emptyForm(): RuleForm {
  return { scope: 'device', analyteCode: '', resultIn: '', resultOut: '', comment: '', color: '' }
}

function formOf(rule: ResultRuleView): RuleForm {
  return {
    scope: rule.scope,
    analyteCode: rule.analyteCode,
    resultIn: rule.resultIn,
    resultOut: rule.resultOut ?? '',
    comment: rule.comment ?? '',
    color: rule.color === null ? '' : String(rule.color),
  }
}

function validateForm(form: RuleForm): RuleFormErrors {
  const errors: RuleFormErrors = {}
  if (form.analyteCode.trim() === '') {
    errors.analyteCode = 'Bắt buộc — mã xét nghiệm phải có trong danh mục.'
  }
  if (form.resultIn.trim() === '') {
    errors.resultIn = 'Bắt buộc — đây là chuỗi máy trả về, ví dụ neg.'
  }
  return errors
}

function payloadOf(form: RuleForm, deviceId: string): ResultRulePayload {
  return {
    deviceId: form.scope === 'common' ? null : deviceId,
    analyteCode: form.analyteCode.trim(),
    resultIn: form.resultIn.trim(),
    resultOut: form.resultOut.trim() === '' ? null : form.resultOut,
    comment: form.comment.trim() === '' ? null : form.comment,
    color: form.color === '' ? null : Number(form.color),
  }
}

export function CatalogueResultRules({
  devices,
  devicesLoading,
}: {
  devices: DeviceSummary[]
  devicesLoading: boolean
}): JSX.Element {
  const [selectedId, setSelectedId] = useState<string>('')
  const [rules, setRules] = useState<ResultRuleView[]>([])
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)

  const [editingId, setEditingId] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [form, setForm] = useState<RuleForm | null>(null)
  const [formErrors, setFormErrors] = useState<RuleFormErrors>({})
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const [pendingSave, setPendingSave] = useState<{ payload: ResultRulePayload; id: string | null } | null>(
    null,
  )
  const [pendingDelete, setPendingDelete] = useState<ResultRuleView | null>(null)

  const selectedDevice = useMemo(
    () => devices.find((d) => d.id === selectedId) ?? null,
    [devices, selectedId],
  )

  const load = useCallback(
    (deviceId: string) => {
      let cancelled = false
      if (deviceId === '') {
        setRules([])
        setLoadError(null)
        setLoading(false)
        return () => {
          cancelled = true
        }
      }
      setLoading(true)
      catalogueApi
        .resultRules(deviceId)
        .then((list) => {
          if (cancelled) return
          setRules(list)
          setLoadError(null)
          setLoading(false)
        })
        .catch((err: unknown) => {
          if (cancelled) return
          setRules([])
          setLoadError(errorMessageOf(err))
          setLoading(false)
        })
      return () => {
        cancelled = true
      }
    },
    [],
  )

  useEffect(() => load(selectedId), [load, selectedId])

  const commonCount = rules.filter((r) => r.scope === 'common').length

  const closeEditor = (): void => {
    setEditingId(null)
    setCreating(false)
    setForm(null)
    setFormErrors({})
    setSaveError(null)
  }

  const startCreate = (): void => {
    setCreating(true)
    setEditingId(null)
    setForm(emptyForm())
    setFormErrors({})
    setSaveError(null)
    setNotice(null)
  }

  const startEdit = (rule: ResultRuleView): void => {
    setCreating(false)
    setEditingId(rule.id)
    setForm(formOf(rule))
    setFormErrors({})
    setSaveError(null)
    setNotice(null)
  }

  const requestSave = (): void => {
    if (!form || selectedId === '') return
    const errors = validateForm(form)
    setFormErrors(errors)
    if (Object.keys(errors).length > 0) {
      setSaveError('CHƯA LƯU — còn ô bắt buộc chưa điền. Sửa các ô được đánh dấu rồi bấm Lưu lại.')
      return
    }
    const payload = payloadOf(form, selectedId)
    // Luật chung áp cho MỌI máy: bắt xác nhận một lần, vì hậu quả vượt xa cái
    // máy đang mở trên màn hình.
    if (form.scope === 'common') {
      setPendingSave({ payload, id: editingId })
      return
    }
    void performSave(payload, editingId)
  }

  const performSave = async (payload: ResultRulePayload, id: string | null): Promise<void> => {
    setSaving(true)
    setSaveError(null)
    try {
      if (id === null) await catalogueApi.createResultRule(payload)
      else await catalogueApi.updateResultRule(id, payload)
      const list = await catalogueApi.resultRules(selectedId)
      setRules(list)
      setNotice(
        `Đã lưu luật: ${payload.analyteCode} — máy trả về “${payload.resultIn}” sẽ hiển thị “${payload.resultOut ?? '(giữ nguyên)'}”.`,
      )
      closeEditor()
    } catch (err: unknown) {
      setSaveError(
        `CHƯA LƯU — ${errorMessageOf(err)} Nội dung bạn nhập vẫn còn ở đây; sửa lại rồi bấm Lưu.`,
      )
    } finally {
      setSaving(false)
      setPendingSave(null)
    }
  }

  const confirmDelete = async (): Promise<void> => {
    if (!pendingDelete) return
    const target = pendingDelete
    setSaving(true)
    try {
      await catalogueApi.deleteResultRule(target.id)
      const list = await catalogueApi.resultRules(selectedId)
      setRules(list)
      setPendingDelete(null)
      setNotice(
        `Đã xoá luật ${target.analyteCode} / “${target.resultIn}”. Từ giờ giá trị này được giữ nguyên như máy trả về.`,
      )
    } catch (err: unknown) {
      setPendingDelete(null)
      setSaveError(`CHƯA XOÁ — ${errorMessageOf(err)}`)
    } finally {
      setSaving(false)
    }
  }

  const setField = <K extends keyof RuleForm>(key: K, value: RuleForm[K]): void => {
    setForm((prev) => (prev ? { ...prev, [key]: value } : prev))
  }

  return (
    <section className="panel">
      <h2>Luật ghi đè kết quả</h2>

      <p className="field-hint">
        Luật ghi đè biến giá trị thô máy trả về thành kết quả hiển thị cho bác sĩ. Ví dụ: máy trả về{' '}
        <code>neg</code> → hiển thị <code>Âm tính</code>.
      </p>

      <div className="toolbar">
        <div className="toolbar-field">
          <label htmlFor="rule-device">Máy xét nghiệm</label>
          <select
            id="rule-device"
            value={selectedId}
            onChange={(e) => {
              setSelectedId(e.target.value)
              closeEditor()
              setNotice(null)
            }}
            disabled={devicesLoading || devices.length === 0}
          >
            <option value="">— Chọn máy —</option>
            {devices.map((device) => (
              <option key={device.id} value={device.id}>
                {device.name} ({device.deviceKey})
              </option>
            ))}
          </select>
          {devicesLoading && <p className="field-hint">Đang tải danh sách máy...</p>}
          {!devicesLoading && devices.length === 0 && (
            <p className="field-hint">Chưa có máy xét nghiệm nào trong hệ thống.</p>
          )}
        </div>

        {selectedId !== '' && (
          <div className="toolbar-field">
            <span aria-hidden="true" style={{ fontSize: '13px', fontWeight: 600 }}>
              &nbsp;
            </span>
            <button type="button" className="btn" onClick={startCreate} disabled={form !== null}>
              <PlusIcon />
              Thêm luật mới
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

      {saveError && !form && (
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
              <strong>Không tải được danh sách luật.</strong> {loadError}
            </p>
            <button type="button" className="btn btn-sm" onClick={() => load(selectedId)}>
              Thử lại
            </button>
          </div>
        </div>
      )}

      {selectedId === '' && (
        <p className="empty">Chọn một máy để xem các luật ghi đè đang áp dụng cho máy đó.</p>
      )}

      {selectedId !== '' && commonCount > 0 && (
        <div className="notice notice-warn" role="status">
          <GlobeIcon className="notice-icon" />
          <p>
            Trong bảng dưới có <strong>{commonCount} luật chung</strong> — chúng áp cho MỌI máy chứ
            không riêng {selectedDevice?.name ?? 'máy này'}. Sửa hay xoá một luật chung sẽ đổi cách
            diễn giải kết quả của tất cả các máy trong bệnh viện.
          </p>
        </div>
      )}

      {form && (
        <div className="editor">
          <h3 className="editor-title">
            {creating ? 'Thêm luật ghi đè mới' : 'Sửa luật ghi đè'}
          </h3>

          {saveError && (
            <div className="notice notice-danger" role="alert">
              <WarningTriangleIcon className="notice-icon" />
              <p>{saveError}</p>
            </div>
          )}

          {form.scope === 'common' && (
            <div className="notice notice-warn" role="status">
              <GlobeIcon className="notice-icon" />
              <p>
                <strong>Đây là luật CHUNG.</strong> Nó áp cho mọi máy trong bệnh viện, không chỉ{' '}
                {selectedDevice?.name ?? 'máy đang chọn'}. Bạn sẽ được hỏi xác nhận một lần nữa
                trước khi lưu.
              </p>
            </div>
          )}

          <div className="editor-grid">
            <div className="editor-field">
              <label htmlFor="rule-scope">Phạm vi áp dụng</label>
              <select
                id="rule-scope"
                value={form.scope}
                onChange={(e) => setField('scope', e.target.value === 'common' ? 'common' : 'device')}
              >
                <option value="device">
                  Chỉ máy {selectedDevice?.name ?? 'đang chọn'}
                </option>
                <option value="common">Luật chung — áp cho mọi máy</option>
              </select>
            </div>

            <AnalytePicker
              id="rule-analyte"
              label="Mã xét nghiệm"
              value={form.analyteCode}
              onChange={(code) => setField('analyteCode', code)}
              onBlur={() => setFormErrors(validateForm(form))}
              error={formErrors.analyteCode}
            />

            <div className="editor-field">
              <label htmlFor="rule-in">Giá trị máy trả về</label>
              <input
                id="rule-in"
                type="text"
                value={form.resultIn}
                onChange={(e) => setField('resultIn', e.target.value)}
                onBlur={() => setFormErrors(validateForm(form))}
                aria-invalid={formErrors.resultIn ? true : undefined}
                aria-describedby={formErrors.resultIn ? 'rule-in-error' : 'rule-in-hint'}
              />
              <p className="field-hint" id="rule-in-hint">
                Đúng như máy gửi, ví dụ <code>neg</code>, <code>1:160</code>.
              </p>
              {formErrors.resultIn && (
                <p className="editor-error" id="rule-in-error">
                  {formErrors.resultIn}
                </p>
              )}
            </div>

            <div className="editor-field">
              <label htmlFor="rule-out">Kết quả hiển thị</label>
              <input
                id="rule-out"
                type="text"
                value={form.resultOut}
                onChange={(e) => setField('resultOut', e.target.value)}
                aria-describedby="rule-out-hint"
              />
              <p className="field-hint" id="rule-out-hint">
                Ví dụ <code>Âm tính</code>. Để trống thì giữ nguyên giá trị máy trả về.
              </p>
            </div>

            <div className="editor-field">
              <label htmlFor="rule-color">Màu kết quả</label>
              <select
                id="rule-color"
                value={form.color}
                onChange={(e) => setField('color', e.target.value)}
              >
                {COLOR_OPTIONS.map((option) => (
                  <option key={option.label} value={option.value === null ? '' : option.value}>
                    {option.label}
                    {option.value === null ? '' : ` (mã ${option.value})`}
                  </option>
                ))}
              </select>
            </div>

            <div className="editor-field">
              <label htmlFor="rule-comment">Biện luận</label>
              <textarea
                id="rule-comment"
                value={form.comment}
                onChange={(e) => setField('comment', e.target.value)}
              />
            </div>
          </div>

          <div className="btn-row">
            <button
              type="button"
              className="btn btn-primary"
              onClick={requestSave}
              disabled={saving}
            >
              {saving && <span className="spinner" />}
              {saving ? 'Đang lưu...' : 'Lưu luật'}
            </button>
            <button type="button" className="btn" onClick={closeEditor} disabled={saving}>
              Huỷ
            </button>
          </div>
        </div>
      )}

      {selectedId !== '' && !loadError && (
        <>
          {loading && <p className="loading-text">Đang tải danh sách luật...</p>}
          {!loading && (
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th scope="col">Phạm vi</th>
                    <th scope="col">Xét nghiệm</th>
                    <th scope="col">Máy trả về</th>
                    <th scope="col">Kết quả hiển thị</th>
                    <th scope="col">Biện luận</th>
                    <th scope="col">Màu</th>
                    <th scope="col">Thao tác</th>
                  </tr>
                </thead>
                <tbody>
                  {rules.map((rule) => (
                    <tr key={rule.id}>
                      <td>
                        <ScopeBadge rule={rule} />
                      </td>
                      <td className="cell-wrap">
                        <code>{rule.analyteCode}</code>
                        <span className="cell-sub">
                          {rule.analyteName ?? 'Không tìm thấy tên trong danh mục'}
                        </span>
                      </td>
                      <td>
                        <code>{rule.resultIn}</code>
                      </td>
                      <td className="cell-wrap">{rule.resultOut ?? 'Giữ nguyên như máy trả về'}</td>
                      <td className="cell-wrap">{rule.comment ?? '—'}</td>
                      <td>
                        <ColorBadge color={rule.color} />
                      </td>
                      <td>
                        <div className="btn-row">
                          <button
                            type="button"
                            className="btn btn-sm"
                            onClick={() => startEdit(rule)}
                          >
                            <PencilIcon />
                            Sửa
                          </button>
                          <button
                            type="button"
                            className="btn btn-sm btn-danger"
                            onClick={() => setPendingDelete(rule)}
                          >
                            <TrashIcon />
                            Xoá
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))}

                  {rules.length === 0 && (
                    <tr>
                      <td colSpan={7} className="empty-cell">
                        <p>
                          <strong>
                            Máy {selectedDevice?.name ?? 'này'} chưa có luật ghi đè nào, và cũng
                            chưa có luật chung nào áp lên nó.
                          </strong>{' '}
                          Kết quả sẽ được hiển thị đúng như máy trả về.
                        </p>
                        <p>
                          Ví dụ một luật thường dùng: xét nghiệm HPV, máy trả về <code>neg</code> →
                          hiển thị <code>Âm tính</code>, màu “Bình thường”. Bấm “Thêm luật mới” để
                          tạo.
                        </p>
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}

      {pendingSave && (
        <div className="dialog-backdrop" role="presentation">
          <div className="dialog" role="alertdialog" aria-modal="true" aria-labelledby="common-save-title">
            <h3 id="common-save-title">Xác nhận: đây là luật áp cho MỌI máy</h3>
            <p>
              Luật <span className="code-inline">{pendingSave.payload.analyteCode}</span> — máy trả
              về <span className="code-inline">{pendingSave.payload.resultIn}</span> sẽ được áp dụng
              cho <strong>tất cả các máy xét nghiệm trong bệnh viện</strong>, không chỉ{' '}
              {selectedDevice?.name ?? 'máy đang chọn'}.
            </p>
            <p>
              Nếu bạn chỉ muốn đổi cách diễn giải của riêng một máy, hãy bấm “Quay lại” và chọn
              phạm vi “Chỉ máy {selectedDevice?.name ?? 'đang chọn'}”.
            </p>
            <div className="dialog-actions">
              <button
                type="button"
                className="btn"
                onClick={() => setPendingSave(null)}
                disabled={saving}
              >
                Quay lại
              </button>
              <button
                type="button"
                className="btn btn-primary"
                onClick={() => void performSave(pendingSave.payload, pendingSave.id)}
                disabled={saving}
              >
                {saving && <span className="spinner" />}
                Tôi hiểu — lưu luật chung
              </button>
            </div>
          </div>
        </div>
      )}

      {pendingDelete && (
        <div className="dialog-backdrop" role="presentation">
          <div className="dialog" role="alertdialog" aria-modal="true" aria-labelledby="rule-delete-title">
            <h3 id="rule-delete-title">Xoá luật ghi đè?</h3>
            <p>
              Xét nghiệm <span className="code-inline">{pendingDelete.analyteCode}</span>, máy trả
              về <span className="code-inline">{pendingDelete.resultIn}</span> →{' '}
              {pendingDelete.resultOut ?? 'giữ nguyên'}.
            </p>
            {pendingDelete.scope === 'common' && (
              <div className="notice notice-warn" role="status">
                <GlobeIcon className="notice-icon" />
                <p>
                  <strong>Đây là luật CHUNG.</strong> Xoá nó sẽ đổi cách diễn giải kết quả của MỌI
                  máy trong bệnh viện, không chỉ {selectedDevice?.name ?? 'máy đang chọn'}.
                </p>
              </div>
            )}
            <p>
              Sau khi xoá, giá trị <span className="code-inline">{pendingDelete.resultIn}</span> sẽ
              hiển thị nguyên như máy trả về. Thao tác này không hoàn tác được từ màn hình.
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
                Xoá luật
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  )
}
