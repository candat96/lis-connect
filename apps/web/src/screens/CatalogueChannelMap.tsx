import { useCallback, useEffect, useMemo, useState, type JSX } from 'react'
import {
  ApiError,
  catalogueApi,
  errorMessageOf,
  type ChannelMapPayload,
  type ChannelMapView,
  type DeviceSummary,
} from '../api.js'
import { AnalytePicker } from '../components/AnalytePicker.js'
import {
  CheckCircleIcon,
  PencilIcon,
  PlusIcon,
  TrashIcon,
  UnknownDashedIcon,
  UnlinkIcon,
  WarningTriangleIcon,
} from '../statusMeta.js'

/**
 * Màn hình ánh xạ mã kênh máy → xét nghiệm.
 *
 * Đây là màn hình kỹ thuật viên mở ra khi "máy trả kết quả về mà không hiện
 * lên". Mọi quyết định bố cục dưới đây phục vụ đúng câu hỏi đó:
 *
 *   1. Ngay ở danh sách chọn máy, máy CHƯA CÓ KÊNH NÀO được ánh xạ đã bị tô cả
 *      thẻ kèm nhãn chữ — nguyên nhân thô nhất phải lộ ra trước cả khi chọn.
 *   2. Chọn máy xong, dòng đầu tiên trong khung là con số "N/M mã kênh chưa gán
 *      xét nghiệm", không phải một bảng phẳng bắt người dùng tự dò ô trống.
 *   3. Hàng kênh chưa gán được tô nền cả hàng và mang nhãn chữ "CHƯA GÁN XÉT
 *      NGHIỆM", đúng quy ước "không dùng riêng màu để truyền nghĩa".
 *   4. "CHƯA GÁN" và "CHƯA QUYẾT" là HAI nhãn khác nhau, đếm riêng, và thẻ máy
 *      chỉ nói "đã gán đủ" khi cả hai con số đều bằng 0.
 *
 * Việc chặn gán trùng (Core trả 409) được xử lý bằng một hộp thoại GIẢI THÍCH
 * chứ không phải một câu báo lỗi: nói rõ mã kênh đó đang trỏ tới xét nghiệm
 * nào, vì sao hệ thống không cho ghi đè, và đưa sẵn đường thoát (xoá ánh xạ cũ
 * rồi gán lại). Chính việc hệ cũ cho ghi đè trong im lặng đã đẻ ra 7 cặp mã
 * kênh nhập nhằng phải nhờ người gỡ tay.
 */

interface ChannelForm {
  channelCode: string
  analyteCode: string
  round: string
  allowInsert: boolean
  omit: boolean
  keepHigherSign: boolean
  keepLowerSign: boolean
}

interface FormErrors {
  channelCode?: string
  analyteCode?: string
  round?: string
}

/** Thao tác bị Core chặn — giữ đủ ngữ cảnh để giải thích được cho người dùng. */
interface Conflict {
  channelCode: string
  /** Xét nghiệm mã kênh đang trỏ tới theo dữ liệu client đang có; null = chưa gán. */
  currentAnalyteCode: string | null
  currentAnalyteName: string | null
  targetAnalyteCode: string
  serverMessage: string
  payload: ChannelMapPayload
}

/** Ánh xạ vừa bị xoá, giữ lại đủ dữ liệu để hoàn tác bằng một lời gọi PUT. */
interface UndoableDelete {
  channel: ChannelMapView
}

/**
 * Trạng thái của một mã kênh, đọc từ Core và có đường lùi cho Core cũ hơn.
 *
 * BA trạng thái, không phải hai — đây chính là lỗi nặng nhất mà vòng review
 * toàn nhánh tìm ra. Mã kênh đang xung đột mà chưa ai quyết trước đây không tồn
 * tại thành dòng nào trong database, nên màn hình này không có gì để đếm và
 * báo "đã gán đủ" cho đúng những máy đang thiếu mã kênh (AU640 thiếu `01` và
 * `19` — ca `128LDL` vs `244ACo`, nồng độ cồn về nhầm ô LDL).
 */
type ChannelState = 'assigned' | 'unassigned' | 'undecided'

function stateOf(channel: ChannelMapView): ChannelState {
  if (channel.state) return channel.state
  return channel.analyteCode === null ? 'unassigned' : 'assigned'
}

function emptyForm(channelCode: string): ChannelForm {
  return {
    channelCode,
    analyteCode: '',
    round: '',
    allowInsert: false,
    omit: false,
    keepHigherSign: false,
    keepLowerSign: false,
  }
}

function formOf(channel: ChannelMapView): ChannelForm {
  return {
    channelCode: channel.channelCode,
    analyteCode: channel.analyteCode ?? '',
    round: channel.round === null ? '' : String(channel.round),
    allowInsert: channel.allowInsert,
    omit: channel.omit,
    keepHigherSign: channel.keepHigherSign,
    keepLowerSign: channel.keepLowerSign,
  }
}

function validateForm(form: ChannelForm): FormErrors {
  const errors: FormErrors = {}
  if (form.channelCode.trim() === '') {
    errors.channelCode = 'Bắt buộc — đây là mã máy gửi kèm mỗi kết quả.'
  }
  if (form.analyteCode.trim() === '') {
    errors.analyteCode = 'Bắt buộc — nhập đúng mã xét nghiệm có trong danh mục.'
  }
  const round = form.round.trim()
  if (round !== '') {
    const value = Number(round)
    if (!Number.isInteger(value) || value < 0 || value > 6) {
      errors.round = 'Phải là số nguyên từ 0 đến 6, hoặc để trống.'
    }
  }
  return errors
}

function payloadOf(form: ChannelForm): ChannelMapPayload {
  return {
    analyteCode: form.analyteCode.trim(),
    round: form.round.trim() === '' ? null : Number(form.round.trim()),
    allowInsert: form.allowInsert,
    omit: form.omit,
    keepHigherSign: form.keepHigherSign,
    keepLowerSign: form.keepLowerSign,
  }
}

/**
 * Một nhãn tình trạng của máy. Bốn nhãn hiện dùng (có cấu hình cổng / từng gửi
 * kết quả / đang bật / hệ cũ còn dùng) ĐỘC LẬP với nhau, không suy ra được từ
 * nhau. Mỗi giá trị một HÌNH DẠNG icon riêng
 * (tròn có tick / tam giác / vòng nét đứt) đi kèm nhãn chữ đầy đủ — nhìn từ xa
 * hay bị mù màu vẫn phân biệt được, và "chưa rõ" không bị nhầm thành "không".
 */
function ActivityFlag({
  value,
  labelYes,
  labelNo,
  labelUnknown,
}: {
  value: boolean | null | undefined
  labelYes: string
  labelNo: string
  labelUnknown: string
}): JSX.Element {
  if (value === undefined || value === null) {
    return (
      <span className="status-badge status-badge-sm tone-warn">
        <UnknownDashedIcon className="status-badge-icon" />
        <span className="status-badge-label">{labelUnknown}</span>
      </span>
    )
  }
  if (value) {
    return (
      <span className="status-badge status-badge-sm tone-ok">
        <CheckCircleIcon className="status-badge-icon" />
        <span className="status-badge-label">{labelYes}</span>
      </span>
    )
  }
  return (
    <span className="status-badge status-badge-sm tone-danger">
      <WarningTriangleIcon className="status-badge-icon" />
      <span className="status-badge-label">{labelNo}</span>
    </span>
  )
}

function FlagList({ channel }: { channel: ChannelMapView }): JSX.Element {
  const flags: string[] = []
  if (channel.allowInsert) flags.push('Cho phép tự thêm chỉ định')
  if (channel.omit) flags.push('Bỏ qua kênh này')
  if (channel.keepHigherSign) flags.push('Giữ dấu lớn hơn')
  if (channel.keepLowerSign) flags.push('Giữ dấu nhỏ hơn')
  if (flags.length === 0) return <span className="cell-sub">Không bật cờ nào</span>
  return (
    <span className="badge-list">
      {flags.map((flag) => (
        <span key={flag} className="status-badge status-badge-sm tone-neutral">
          <span className="status-badge-label">{flag}</span>
        </span>
      ))}
    </span>
  )
}

export function CatalogueChannelMap({
  devices,
  devicesLoading,
}: {
  devices: DeviceSummary[]
  devicesLoading: boolean
}): JSX.Element {
  // Danh sách máy được App làm mới mỗi 5 giây, nên mảng đổi tham chiếu liên
  // tục. Khoá theo chuỗi id để không nạp lại toàn bộ kênh mỗi vòng polling.
  const deviceIdKey = devices.map((d) => d.id).join(',')

  const [channelsByDevice, setChannelsByDevice] = useState<Record<string, ChannelMapView[]>>({})
  const [overviewLoading, setOverviewLoading] = useState(true)
  const [overviewError, setOverviewError] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [showOnlyUnassigned, setShowOnlyUnassigned] = useState(false)

  const [editing, setEditing] = useState<{ channelCode: string | null } | null>(null)
  const [form, setForm] = useState<ChannelForm | null>(null)
  const [formErrors, setFormErrors] = useState<FormErrors>({})
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const [conflict, setConflict] = useState<Conflict | null>(null)
  const [pendingDelete, setPendingDelete] = useState<ChannelMapView | null>(null)
  const [undoable, setUndoable] = useState<UndoableDelete | null>(null)

  const loadOverview = useCallback(() => {
    let cancelled = false
    if (devices.length === 0) {
      setChannelsByDevice({})
      setOverviewLoading(false)
      setOverviewError(null)
      return () => {
        cancelled = true
      }
    }
    setOverviewLoading(true)
    Promise.all(
      devices.map(async (device) => [device.id, await catalogueApi.channels(device.id)] as const),
    )
      .then((entries) => {
        if (cancelled) return
        setChannelsByDevice(Object.fromEntries(entries))
        setOverviewError(null)
        setOverviewLoading(false)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        // Không giữ số liệu cũ: một bảng đếm "0 kênh chưa gán" đã lỗi thời còn
        // nguy hiểm hơn một thông báo lỗi, vì nó nói máy đang ổn.
        setChannelsByDevice({})
        setOverviewError(errorMessageOf(err))
        setOverviewLoading(false)
      })
    return () => {
      cancelled = true
    }
    // `devices` cố tình KHÔNG nằm trong dependency: chỉ danh sách ID mới đáng
    // để nạp lại toàn bộ mã kênh, còn trạng thái kết nối thì đổi mỗi 5 giây —
    // để `devices` vào đây sẽ bắn lại N request mỗi vòng polling.
  }, [deviceIdKey])

  useEffect(() => loadOverview(), [loadOverview])

  const reloadSelected = useCallback(async (deviceId: string): Promise<void> => {
    const channels = await catalogueApi.channels(deviceId)
    setChannelsByDevice((prev) => ({ ...prev, [deviceId]: channels }))
  }, [])

  const selectedDevice = useMemo(
    () => devices.find((d) => d.id === selectedId) ?? null,
    [devices, selectedId],
  )
  const selectedChannels = selectedId ? (channelsByDevice[selectedId] ?? []) : []
  const unassignedCount = selectedChannels.filter((c) => stateOf(c) === 'unassigned').length
  const undecidedCount = selectedChannels.filter((c) => stateOf(c) === 'undecided').length
  const needsAttentionCount = unassignedCount + undecidedCount
  const visibleChannels = showOnlyUnassigned
    ? selectedChannels.filter((c) => stateOf(c) !== 'assigned')
    : selectedChannels

  const closeEditor = (): void => {
    setEditing(null)
    setForm(null)
    setFormErrors({})
    setSaveError(null)
  }

  const selectDevice = (id: string): void => {
    setSelectedId(id)
    setShowOnlyUnassigned(false)
    setNotice(null)
    setUndoable(null)
    closeEditor()
  }

  const startEdit = (channel: ChannelMapView): void => {
    setEditing({ channelCode: channel.channelCode })
    setForm(formOf(channel))
    setFormErrors({})
    setSaveError(null)
    setNotice(null)
  }

  const startCreate = (): void => {
    setEditing({ channelCode: null })
    setForm(emptyForm(''))
    setFormErrors({})
    setSaveError(null)
    setNotice(null)
  }

  const submit = async (): Promise<void> => {
    if (!form || !selectedId) return
    const errors = validateForm(form)
    setFormErrors(errors)
    if (Object.keys(errors).length > 0) {
      setSaveError('CHƯA LƯU — còn ô nhập chưa hợp lệ. Sửa các ô được đánh dấu rồi bấm Lưu lại.')
      return
    }

    const channelCode = form.channelCode.trim()
    const payload = payloadOf(form)
    setSaving(true)
    setSaveError(null)
    try {
      await catalogueApi.putChannel(selectedId, channelCode, payload)
      await reloadSelected(selectedId)
      setNotice(`Đã lưu ánh xạ: mã kênh ${channelCode} → xét nghiệm ${payload.analyteCode}.`)
      closeEditor()
    } catch (err: unknown) {
      if (err instanceof ApiError && err.status === 409) {
        // 409 ở đây KHÔNG phải sự cố kỹ thuật mà là quy tắc nghiệp vụ trung tâm
        // của khối 2A. Chuyển sang hộp thoại giải thích, không hiện "Lỗi 409".
        const existing = selectedChannels.find((c) => c.channelCode === channelCode) ?? null
        setConflict({
          channelCode,
          currentAnalyteCode: existing?.analyteCode ?? null,
          currentAnalyteName: existing?.analyteName ?? null,
          targetAnalyteCode: payload.analyteCode,
          serverMessage: err.message,
          payload,
        })
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

  /** Đường thoát của hộp thoại xung đột: xoá ánh xạ cũ rồi gán lại, có chủ ý. */
  const resolveConflict = async (): Promise<void> => {
    if (!conflict || !selectedId) return
    setSaving(true)
    try {
      await catalogueApi.deleteChannel(selectedId, conflict.channelCode)
      await catalogueApi.putChannel(selectedId, conflict.channelCode, conflict.payload)
      await reloadSelected(selectedId)
      setNotice(
        `Đã xoá ánh xạ cũ của mã kênh ${conflict.channelCode} và gán lại cho xét nghiệm ${conflict.targetAnalyteCode}.`,
      )
      setConflict(null)
      closeEditor()
    } catch (err: unknown) {
      setSaveError(
        `CHƯA LƯU — không đổi được ánh xạ: ${errorMessageOf(err)} Hãy kiểm tra lại danh sách kênh bên dưới trước khi thử tiếp.`,
      )
      setConflict(null)
      if (selectedId) await reloadSelected(selectedId).catch(() => undefined)
    } finally {
      setSaving(false)
    }
  }

  const confirmDelete = async (): Promise<void> => {
    if (!pendingDelete || !selectedId) return
    const target = pendingDelete
    setSaving(true)
    try {
      await catalogueApi.deleteChannel(selectedId, target.channelCode)
      await reloadSelected(selectedId)
      setPendingDelete(null)
      setUndoable({ channel: target })
      setNotice(
        `Đã xoá ánh xạ mã kênh ${target.channelCode}. Từ giờ kết quả máy gửi ở kênh này sẽ không vào xét nghiệm nào.`,
      )
    } catch (err: unknown) {
      setPendingDelete(null)
      setSaveError(`CHƯA XOÁ — ${errorMessageOf(err)}`)
    } finally {
      setSaving(false)
    }
  }

  const undoDelete = async (): Promise<void> => {
    if (!undoable || !selectedId) return
    const { channel } = undoable
    if (channel.analyteCode === null) return
    setSaving(true)
    try {
      await catalogueApi.putChannel(selectedId, channel.channelCode, {
        analyteCode: channel.analyteCode,
        round: channel.round,
        allowInsert: channel.allowInsert,
        omit: channel.omit,
        keepHigherSign: channel.keepHigherSign,
        keepLowerSign: channel.keepLowerSign,
      })
      await reloadSelected(selectedId)
      setUndoable(null)
      setNotice(`Đã khôi phục ánh xạ mã kênh ${channel.channelCode} → ${channel.analyteCode}.`)
    } catch (err: unknown) {
      setSaveError(`KHÔNG HOÀN TÁC ĐƯỢC — ${errorMessageOf(err)}`)
    } finally {
      setSaving(false)
    }
  }

  const setFormField = <K extends keyof ChannelForm>(key: K, value: ChannelForm[K]): void => {
    setForm((prev) => (prev ? { ...prev, [key]: value } : prev))
  }

  return (
    <section className="panel">
      <h2>Ánh xạ mã kênh máy → xét nghiệm</h2>

      <p className="field-hint">
        Máy xét nghiệm gửi kết quả kèm một mã kênh (ví dụ <code>01</code>, <code>GLU</code>). Nếu mã
        kênh chưa được gán cho xét nghiệm nào thì kết quả về tới hệ thống nhưng KHÔNG hiện lên ở đâu
        cả — đó là nguyên nhân thường gặp nhất.
      </p>

      {overviewError && (
        <div className="notice notice-danger" role="alert">
          <WarningTriangleIcon className="notice-icon" />
          <div>
            <p>
              <strong>Không tải được danh sách mã kênh.</strong> {overviewError}
            </p>
            <button type="button" className="btn btn-sm" onClick={() => loadOverview()}>
              Thử lại
            </button>
          </div>
        </div>
      )}

      <div className="split">
        <div className="split-side">
          <h3 className="section-title section-title-inline">Chọn máy</h3>
          {devicesLoading && <p className="loading-text">Đang tải danh sách máy...</p>}
          {!devicesLoading && devices.length === 0 && (
            <p className="empty">
              Chưa có máy xét nghiệm nào trong hệ thống. Cần khai báo máy ở Agent trước.
            </p>
          )}
          <div className="device-list">
            {devices.map((device) => {
              const channels = channelsByDevice[device.id]
              const known = channels !== undefined
              const unassigned = channels?.filter((c) => stateOf(c) === 'unassigned').length ?? 0
              const undecided = channels?.filter((c) => stateOf(c) === 'undecided').length ?? 0
              const noChannels = known && channels.length === 0
              // "Đã gán đủ" chỉ được nói khi CẢ HAI con số bằng 0. Một mã kênh
              // chưa quyết vẫn là một mã kênh sẽ không bao giờ trả kết quả lên.
              const summary =
                unassigned === 0 && undecided === 0
                  ? ' — đã gán đủ'
                  : [
                      unassigned > 0 ? `${unassigned} chưa gán` : null,
                      undecided > 0 ? `${undecided} chưa quyết` : null,
                    ]
                      .filter(Boolean)
                      .join(', ')
                      .replace(/^/, ' — ')
              return (
                <button
                  key={device.id}
                  type="button"
                  className={`device-item${noChannels ? ' device-item-empty' : ''}`}
                  aria-pressed={device.id === selectedId}
                  onClick={() => selectDevice(device.id)}
                >
                  <span className="device-item-name">{device.name}</span>
                  <span className="device-item-meta">
                    <code>{device.deviceKey}</code>
                  </span>
                  {!known && (
                    <span className="status-badge status-badge-sm tone-warn">
                      <UnknownDashedIcon className="status-badge-icon" />
                      <span className="status-badge-label">
                        {overviewLoading ? 'Đang đếm mã kênh...' : 'Chưa rõ số mã kênh'}
                      </span>
                    </span>
                  )}
                  {known && noChannels && (
                    <span className="status-badge status-badge-sm tone-danger">
                      <UnlinkIcon className="status-badge-icon" />
                      <span className="status-badge-label">CHƯA CÓ MÃ KÊNH NÀO</span>
                    </span>
                  )}
                  {known && !noChannels && (
                    <span className="device-item-meta">
                      {channels.length} mã kênh{summary}
                    </span>
                  )}
                  {known && undecided > 0 && (
                    <span className="status-badge status-badge-sm tone-danger">
                      <WarningTriangleIcon className="status-badge-icon" />
                      <span className="status-badge-label">CÓ MÃ KÊNH CHƯA QUYẾT</span>
                    </span>
                  )}
                  {known && unassigned > 0 && (
                    <span className="status-badge status-badge-sm tone-warn">
                      <UnlinkIcon className="status-badge-icon" />
                      <span className="status-badge-label">CÓ MÃ KÊNH CHƯA GÁN</span>
                    </span>
                  )}
                </button>
              )
            })}
          </div>
        </div>

        <div className="split-main">
          {!selectedDevice && (
            <p className="empty">
              Chọn một máy ở cột bên trái để xem và sửa bảng ánh xạ mã kênh của máy đó.
            </p>
          )}

          {selectedDevice && (
            <>
              <div className="detail-header">
                <div className="detail-title">
                  <h3 className="detail-name">{selectedDevice.name}</h3>
                  <code>{selectedDevice.deviceKey}</code>
                </div>
                <button
                  type="button"
                  className="btn"
                  onClick={startCreate}
                  disabled={editing !== null}
                >
                  <PlusIcon />
                  Thêm mã kênh
                </button>
              </div>

              <div className="badge-list" style={{ marginBottom: 'var(--space-4)' }}>
                <ActivityFlag
                  value={selectedDevice.hasPortConfig}
                  labelYes="Có cấu hình cổng"
                  labelNo="Chưa cấu hình cổng"
                  labelUnknown="Chưa rõ có cấu hình cổng hay không"
                />
                <ActivityFlag
                  value={selectedDevice.hasSentResults}
                  labelYes="Đã từng gửi kết quả"
                  labelNo="Chưa từng gửi kết quả"
                  labelUnknown="Chưa rõ đã từng gửi kết quả hay chưa"
                />
                <ActivityFlag
                  value={selectedDevice.enabled}
                  labelYes="Đang bật (cho Agent kết nối)"
                  labelNo="Đang tắt (không cho Agent kết nối)"
                  labelUnknown="Chưa rõ đang bật hay tắt"
                />
                <ActivityFlag
                  value={selectedDevice.legacyActive}
                  labelYes="Hệ cũ: còn dùng"
                  labelNo="Hệ cũ: đã ngừng dùng"
                  labelUnknown="Hệ cũ không có thông tin về máy này"
                />
              </div>

              {notice && (
                <div className="notice notice-ok" role="status" aria-live="polite">
                  <CheckCircleIcon className="notice-icon" />
                  <div>
                    <p>{notice}</p>
                    {undoable && undoable.channel.analyteCode !== null && (
                      <button
                        type="button"
                        className="btn btn-sm"
                        onClick={() => void undoDelete()}
                        disabled={saving}
                      >
                        Hoàn tác: gán lại {undoable.channel.channelCode} →{' '}
                        {undoable.channel.analyteCode}
                      </button>
                    )}
                  </div>
                </div>
              )}

              {saveError && !editing && (
                <div className="notice notice-danger" role="alert">
                  <WarningTriangleIcon className="notice-icon" />
                  <p>{saveError}</p>
                </div>
              )}

              {/* Câu trả lời cho "vì sao kết quả không hiện lên" phải nằm ở
                  dòng đầu tiên, trước bảng. */}
              {/* Hai khối riêng: "chưa quyết" và "chưa gán" là HAI VIỆC KHÁC
                  NHAU. Chưa gán là chỗ bị quên, chỉ cần khai. Chưa quyết là hệ
                  cũ khai hai xét nghiệm cho cùng một mã kênh và phần mềm KHÔNG
                  ĐƯỢC chọn hộ — phải có người đọc rồi quyết. */}
              {undecidedCount > 0 && (
                <div className="notice notice-danger" role="alert">
                  <WarningTriangleIcon className="notice-icon" />
                  <div>
                    <p>
                      <strong>
                        {undecidedCount}/{selectedChannels.length} mã kênh của máy này CHƯA ĐƯỢC
                        QUYẾT.
                      </strong>{' '}
                      Phần mềm LIS cũ khai nhiều xét nghiệm cho cùng một mã kênh rồi tự chọn bừa
                      một cái — chính chỗ đó làm kết quả về nhầm ô. Hệ thống KHÔNG chọn hộ: mỗi mã
                      kênh dưới đây đang chờ người đọc rồi quyết, và tới lúc đó kết quả máy gửi ở
                      các kênh đó sẽ không vào xét nghiệm nào.
                    </p>
                  </div>
                </div>
              )}

              {unassignedCount > 0 && (
                <div className="notice notice-warn" role="status">
                  <UnlinkIcon className="notice-icon" />
                  <div>
                    <p>
                      <strong>
                        {unassignedCount}/{selectedChannels.length} mã kênh của máy này CHƯA GÁN xét
                        nghiệm.
                      </strong>{' '}
                      Kết quả máy gửi ở các kênh đó về tới hệ thống nhưng không vào ô nào — đây là
                      nguyên nhân thường gặp nhất của “máy trả kết quả mà không hiện lên”.
                    </p>
                  </div>
                </div>
              )}

              {needsAttentionCount > 0 && (
                <label className="checkbox" style={{ marginBottom: 'var(--space-4)' }}>
                  <input
                    type="checkbox"
                    checked={showOnlyUnassigned}
                    onChange={(e) => setShowOnlyUnassigned(e.target.checked)}
                  />
                  Chỉ hiện {needsAttentionCount} mã kênh cần xử lý (chưa gán hoặc chưa quyết)
                </label>
              )}

              {selectedChannels.length > 0 && needsAttentionCount === 0 && (
                <div className="notice notice-ok" role="status">
                  <CheckCircleIcon className="notice-icon" />
                  <p>
                    Cả {selectedChannels.length} mã kênh của máy này đều đã gán xét nghiệm, và không
                    còn mã kênh nào chờ quyết. Nếu kết quả vẫn không hiện lên, nguyên nhân nằm ở chỗ
                    khác (cấu hình cổng, driver, hoặc máy chưa gửi).
                  </p>
                </div>
              )}

              {editing && form && (
                <div className="editor" style={{ borderTop: 'none' }}>
                  <h4 className="editor-title">
                    {editing.channelCode === null
                      ? 'Thêm ánh xạ mã kênh mới'
                      : `Sửa ánh xạ mã kênh ${editing.channelCode}`}
                  </h4>

                  {saveError && (
                    <div className="notice notice-danger" role="alert">
                      <WarningTriangleIcon className="notice-icon" />
                      <p>{saveError}</p>
                    </div>
                  )}

                  <div className="editor-grid">
                    <div className="editor-field">
                      <label htmlFor="channel-code">Mã kênh</label>
                      <input
                        id="channel-code"
                        type="text"
                        value={form.channelCode}
                        disabled={editing.channelCode !== null}
                        onChange={(e) => setFormField('channelCode', e.target.value)}
                        onBlur={() => setFormErrors(validateForm(form))}
                        aria-invalid={formErrors.channelCode ? true : undefined}
                        aria-describedby={
                          formErrors.channelCode ? 'channel-code-error' : 'channel-code-hint'
                        }
                      />
                      <p className="field-hint" id="channel-code-hint">
                        Đúng như máy gửi lên. Không sửa được sau khi tạo — muốn đổi thì xoá rồi tạo
                        lại.
                      </p>
                      {formErrors.channelCode && (
                        <p className="editor-error" id="channel-code-error">
                          {formErrors.channelCode}
                        </p>
                      )}
                    </div>

                    <AnalytePicker
                      id="channel-analyte"
                      label="Mã xét nghiệm được gán"
                      value={form.analyteCode}
                      onChange={(code) => setFormField('analyteCode', code)}
                      onBlur={() => setFormErrors(validateForm(form))}
                      error={formErrors.analyteCode}
                      hint="Gõ mã hoặc tên để tìm. Mã không có trong danh mục sẽ bị máy chủ từ chối."
                    />

                    <div className="editor-field">
                      <label htmlFor="channel-round">Số chữ số thập phân</label>
                      <input
                        id="channel-round"
                        type="text"
                        inputMode="numeric"
                        className="input-num"
                        value={form.round}
                        onChange={(e) => setFormField('round', e.target.value)}
                        onBlur={() => setFormErrors(validateForm(form))}
                        aria-invalid={formErrors.round ? true : undefined}
                        aria-describedby={formErrors.round ? 'channel-round-error' : undefined}
                      />
                      {formErrors.round && (
                        <p className="editor-error" id="channel-round-error">
                          {formErrors.round}
                        </p>
                      )}
                    </div>
                  </div>

                  <div className="editor-checks">
                    <label className="checkbox">
                      <input
                        type="checkbox"
                        checked={form.allowInsert}
                        onChange={(e) => setFormField('allowInsert', e.target.checked)}
                      />
                      Cho phép tự thêm chỉ định
                    </label>
                    <label className="checkbox">
                      <input
                        type="checkbox"
                        checked={form.omit}
                        onChange={(e) => setFormField('omit', e.target.checked)}
                      />
                      Bỏ qua kênh này
                    </label>
                    <label className="checkbox">
                      <input
                        type="checkbox"
                        checked={form.keepHigherSign}
                        onChange={(e) => setFormField('keepHigherSign', e.target.checked)}
                      />
                      Giữ dấu lớn hơn
                    </label>
                    <label className="checkbox">
                      <input
                        type="checkbox"
                        checked={form.keepLowerSign}
                        onChange={(e) => setFormField('keepLowerSign', e.target.checked)}
                      />
                      Giữ dấu nhỏ hơn
                    </label>
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

              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th scope="col">Mã kênh</th>
                      <th scope="col">Xét nghiệm được gán</th>
                      <th scope="col" className="cell-num">
                        Số lẻ
                      </th>
                      <th scope="col">Cờ</th>
                      <th scope="col">Thao tác</th>
                    </tr>
                  </thead>
                  <tbody>
                    {visibleChannels.map((channel) => {
                      const state = stateOf(channel)
                      const unassigned = state !== 'assigned'
                      const candidates = channel.decisionCandidates ?? []
                      return (
                        <tr key={channel.id} className={unassigned ? 'row-warn' : undefined}>
                          <td>
                            <code>{channel.channelCode}</code>
                          </td>
                          <td className="cell-wrap">
                            {state === 'undecided' ? (
                              <>
                                <span className="status-badge status-badge-sm tone-danger">
                                  <WarningTriangleIcon className="status-badge-icon" />
                                  <span className="status-badge-label">
                                    CHƯA QUYẾT — HỆ CŨ KHAI NHIỀU XÉT NGHIỆM
                                  </span>
                                </span>
                                <span className="cell-sub">
                                  {candidates.length > 0
                                    ? `Ứng viên trong hệ cũ: ${candidates.join(' hoặc ')}. Bấm “Quyết mã kênh này” để chọn một.`
                                    : 'Bấm “Quyết mã kênh này” để chọn xét nghiệm đúng.'}
                                </span>
                              </>
                            ) : state === 'unassigned' ? (
                              <span className="status-badge status-badge-sm tone-warn">
                                <UnlinkIcon className="status-badge-icon" />
                                <span className="status-badge-label">CHƯA GÁN XÉT NGHIỆM</span>
                              </span>
                            ) : (
                              <>
                                <code>{channel.analyteCode}</code>
                                <span className="cell-sub">
                                  {channel.analyteName ??
                                    'Không tìm thấy tên — mã này có thể đã bị xoá khỏi danh mục'}
                                </span>
                              </>
                            )}
                          </td>
                          <td className="cell-num">{channel.round ?? '—'}</td>
                          <td className="cell-wrap">
                            <FlagList channel={channel} />
                          </td>
                          <td>
                            <div className="btn-row">
                              <button
                                type="button"
                                className="btn btn-sm"
                                onClick={() => startEdit(channel)}
                              >
                                <PencilIcon />
                                {state === 'undecided'
                                  ? 'Quyết mã kênh này'
                                  : state === 'unassigned'
                                    ? 'Gán xét nghiệm'
                                    : 'Sửa'}
                              </button>
                              <button
                                type="button"
                                className="btn btn-sm btn-danger"
                                onClick={() => setPendingDelete(channel)}
                              >
                                <TrashIcon />
                                Xoá
                              </button>
                            </div>
                          </td>
                        </tr>
                      )
                    })}

                    {visibleChannels.length === 0 && (
                      <tr>
                        <td colSpan={5} className="empty-cell">
                          {overviewLoading ? (
                            <p>Đang tải bảng mã kênh...</p>
                          ) : selectedChannels.length === 0 ? (
                            <>
                              <p>
                                <strong>
                                  Máy này chưa có mã kênh nào được ánh xạ. Mọi kết quả máy gửi lên
                                  sẽ không vào xét nghiệm nào.
                                </strong>
                              </p>
                              <p>
                                Bấm “Thêm mã kênh” và nhập đúng mã máy gửi kèm kết quả (xem màn hình
                                Theo dõi máy, phần dữ liệu thô, để biết máy đang gửi mã nào).
                              </p>
                            </>
                          ) : (
                            <p>
                              Không còn mã kênh nào chưa gán hay chưa quyết — bỏ lọc để xem toàn bộ
                              bảng.
                            </p>
                          )}
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </div>
      </div>

      {conflict && (
        <div className="dialog-backdrop" role="presentation">
          <div className="dialog" role="alertdialog" aria-modal="true" aria-labelledby="conflict-title">
            <h3 id="conflict-title">Không gán được — mã kênh này đã có ánh xạ</h3>
            {conflict.currentAnalyteCode !== null ? (
              <p>
                Mã kênh <span className="code-inline">{conflict.channelCode}</span> của máy này{' '}
                <strong>đang trỏ tới xét nghiệm</strong>{' '}
                <span className="code-inline">{conflict.currentAnalyteCode}</span>
                {conflict.currentAnalyteName ? ` — ${conflict.currentAnalyteName}` : ''}.
              </p>
            ) : (
              <p>
                Mã kênh <span className="code-inline">{conflict.channelCode}</span> của máy này đã
                có sẵn một dòng ánh xạ và máy chủ không cho ghi đè trực tiếp lên dòng đó.
              </p>
            )}
            <p>
              Hệ thống <strong>cố tình không cho ghi đè trong im lặng</strong>: phần mềm LIS cũ cho
              phép hai xét nghiệm dùng chung một mã kênh rồi tự chọn bừa một cái, và chính chỗ đó
              làm kết quả về nhầm ô — dữ liệu thật của bệnh viện có 7 cặp như vậy phải nhờ người gỡ
              tay.
            </p>
            <p>
              Muốn chuyển mã kênh này sang xét nghiệm{' '}
              <span className="code-inline">{conflict.targetAnalyteCode}</span>, phải xoá ánh xạ cũ
              trước — đây là một hành động có chủ ý và được ghi vào nhật ký sửa đổi.
            </p>
            <p className="field-hint">Máy chủ trả về: {conflict.serverMessage}</p>
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
                onClick={() => void resolveConflict()}
                disabled={saving}
              >
                {saving && <span className="spinner" />}
                Xoá ánh xạ cũ rồi gán {conflict.targetAnalyteCode}
              </button>
            </div>
          </div>
        </div>
      )}

      {pendingDelete && (
        <div className="dialog-backdrop" role="presentation">
          <div className="dialog" role="alertdialog" aria-modal="true" aria-labelledby="delete-title">
            <h3 id="delete-title">Xoá ánh xạ mã kênh?</h3>
            <p>
              Mã kênh <span className="code-inline">{pendingDelete.channelCode}</span>
              {pendingDelete.analyteCode !== null && (
                <>
                  {' '}
                  đang gán cho xét nghiệm{' '}
                  <span className="code-inline">{pendingDelete.analyteCode}</span>
                  {pendingDelete.analyteName ? ` — ${pendingDelete.analyteName}` : ''}
                </>
              )}
              .
            </p>
            <p>
              Sau khi xoá, kết quả máy gửi ở mã kênh này sẽ <strong>không vào xét nghiệm nào</strong>{' '}
              cho tới khi được gán lại. Bạn có thể hoàn tác ngay sau đó.
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
