import { useCallback, useEffect, useState, type JSX } from 'react'
import {
  catalogueApi,
  errorMessageOf,
  type OrphanHeadView,
  type ProfileDetailView,
  type ProfileSummary,
  type ProfileView,
} from '../api.js'
import { AnalytePicker } from '../components/AnalytePicker.js'
import {
  ArrowDownIcon,
  ArrowUpIcon,
  CheckCircleIcon,
  PlusIcon,
  SearchIcon,
  TrashIcon,
  UnknownDashedIcon,
  UnlinkIcon,
  WarningTriangleIcon,
} from '../statusMeta.js'

/**
 * Màn hình nhóm xét nghiệm và các chỉ số con.
 *
 * Điều QUAN TRỌNG NHẤT ở đây: hai loại nhóm không được trông giống nhau.
 *
 *   - `legacy-declared`: hệ cũ khai báo tường minh — đáng tin.
 *   - `legacy-inferred`: phần mềm SUY RA từ thụt lề tên khi di trú. Đây chỉ là
 *     ĐỀ XUẤT. Nó có thể sai: `783PROGES` và `766HE3` trong dữ liệu thật chỉ
 *     suy ra được một phần con, và có thể lẫn xét nghiệm không thuộc nhóm.
 *
 * Vì vậy mỗi nhóm mang hai NHÃN CHỮ tách bạch (nguồn gốc, và đã xác nhận hay
 * chưa), chứ không phân biệt bằng màu. Một nhóm suy diễn chưa xác nhận còn
 * được tô cả hàng và có nút xác nhận đặt ngay tại chỗ.
 *
 * Xác nhận KHÔNG đổi nguồn gốc: nhóm suy diễn đã xác nhận vẫn là nhóm suy
 * diễn, chỉ khác là từ giờ có người đã đọc và chịu trách nhiệm cho nó.
 *
 * Màn hình còn phải cho thấy các mã cha CHƯA CÓ CON NÀO — hiện có hàng trăm mã
 * như vậy trong dữ liệu thật; `920IONDO` là ví dụ thuật toán suy diễn không
 * bắt được. Đó là việc còn phải làm, không được im lặng biến mất.
 */

const PAGE_SIZE = 50
const SEARCH_DEBOUNCE_MS = 300

type Tab = 'profiles' | 'orphans'

/** Nhãn hình dạng + chữ cho nguồn gốc nhóm. Không dùng riêng màu để truyền nghĩa. */
function SourceBadge({ profile }: { profile: ProfileView }): JSX.Element {
  const inferred = profile.source === 'legacy-inferred'
  return (
    <span
      className={`status-badge status-badge-sm ${inferred ? 'tone-warn' : 'tone-neutral'}`}
    >
      {inferred ? (
        <UnknownDashedIcon className="status-badge-icon" />
      ) : (
        <CheckCircleIcon className="status-badge-icon" />
      )}
      <span className="status-badge-label">{profile.sourceLabel}</span>
    </span>
  )
}

/** Nhãn "đã xác nhận" / "chờ xác nhận" — độc lập với nguồn gốc. */
function ConfirmBadge({ profile }: { profile: ProfileView }): JSX.Element {
  return (
    <span
      className={`status-badge status-badge-sm ${profile.confirmed ? 'tone-ok' : 'tone-danger'}`}
    >
      {profile.confirmed ? (
        <CheckCircleIcon className="status-badge-icon" />
      ) : (
        <WarningTriangleIcon className="status-badge-icon" />
      )}
      <span className="status-badge-label">{profile.confirmedLabel}</span>
    </span>
  )
}

export function CatalogueProfiles(): JSX.Element {
  const [tab, setTab] = useState<Tab>('profiles')

  const [queryText, setQueryText] = useState('')
  const [appliedQuery, setAppliedQuery] = useState('')
  const [sourceFilter, setSourceFilter] = useState('')
  const [confirmedFilter, setConfirmedFilter] = useState('')
  const [offset, setOffset] = useState(0)

  const [items, setItems] = useState<ProfileView[]>([])
  const [orphans, setOrphans] = useState<OrphanHeadView[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)

  const [summary, setSummary] = useState<ProfileSummary | null>(null)
  const [summaryError, setSummaryError] = useState<string | null>(null)

  const [selected, setSelected] = useState<ProfileDetailView | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState<string | null>(null)

  const [newMemberCode, setNewMemberCode] = useState('')
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [pendingRemove, setPendingRemove] = useState<{ code: string; name: string | null } | null>(
    null,
  )

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
      .profileSummary()
      .then((value) => {
        if (cancelled) return
        setSummary(value)
        setSummaryError(null)
      })
      .catch((err: unknown) => {
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
    const promise =
      tab === 'profiles'
        ? catalogueApi
            .searchProfiles({
              q: appliedQuery,
              source: sourceFilter,
              confirmed: confirmedFilter,
              limit: PAGE_SIZE,
              offset,
            })
            .then((result) => {
              if (cancelled) return
              setItems(result.items)
              setOrphans([])
              setTotal(result.total)
            })
        : catalogueApi
            .orphanHeads({ q: appliedQuery, limit: PAGE_SIZE, offset })
            .then((result) => {
              if (cancelled) return
              setOrphans(result.items)
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
        // Không giữ danh sách cũ dưới bộ lọc mới: đó là nói dối người dùng.
        setItems([])
        setOrphans([])
        setTotal(0)
        setLoadError(errorMessageOf(err))
        setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [tab, appliedQuery, sourceFilter, confirmedFilter, offset])

  useEffect(() => reload(), [reload])
  useEffect(() => loadSummary(), [loadSummary])

  const openProfile = async (code: string): Promise<void> => {
    setDetailLoading(true)
    setDetailError(null)
    setSaveError(null)
    setNotice(null)
    setNewMemberCode('')
    try {
      setSelected(await catalogueApi.profile(code))
    } catch (err: unknown) {
      setSelected(null)
      setDetailError(errorMessageOf(err))
    } finally {
      setDetailLoading(false)
    }
  }

  /** Gói mọi thao tác ghi: cùng một cách báo lỗi "CHƯA LƯU" và cùng một cách làm mới. */
  const runWrite = async (
    action: () => Promise<ProfileDetailView>,
    successMessage: string,
  ): Promise<void> => {
    setSaving(true)
    setSaveError(null)
    try {
      const updated = await action()
      setSelected(updated)
      setNotice(successMessage)
      reload()
      loadSummary()
    } catch (err: unknown) {
      setSaveError(`CHƯA LƯU — ${errorMessageOf(err)}`)
    } finally {
      setSaving(false)
    }
  }

  const confirmProfile = (profile: ProfileView): Promise<void> =>
    runWrite(
      () => catalogueApi.confirmProfile(profile.code),
      `Đã xác nhận nhóm ${profile.code} — ${profile.name}. Nguồn gốc của nhóm giữ nguyên; từ giờ nhóm này không còn nằm trong danh sách chờ xác nhận.`,
    )

  const addMember = (): Promise<void> => {
    if (!selected) return Promise.resolve()
    const code = newMemberCode.trim()
    if (code === '') {
      setSaveError('CHƯA LƯU — chưa chọn xét nghiệm nào để thêm vào nhóm.')
      return Promise.resolve()
    }
    return runWrite(async () => {
      const updated = await catalogueApi.addProfileMember(selected.code, code)
      setNewMemberCode('')
      return updated
    }, `Đã thêm ${code} vào cuối danh sách chỉ số con của nhóm ${selected.code}.`)
  }

  const move = (analyteCode: string, delta: number): Promise<void> => {
    if (!selected) return Promise.resolve()
    const codes = selected.members.map((m) => m.analyteCode)
    const index = codes.indexOf(analyteCode)
    const target = index + delta
    if (index < 0 || target < 0 || target >= codes.length) return Promise.resolve()
    const next = [...codes]
    const moved = next[index] as string
    next[index] = next[target] as string
    next[target] = moved
    return runWrite(
      () => catalogueApi.reorderProfileMembers(selected.code, next),
      `Đã đổi thứ tự chỉ số con của nhóm ${selected.code}. Thứ tự này là thứ tự in trên phiếu kết quả.`,
    )
  }

  const removeMember = async (): Promise<void> => {
    if (!selected || !pendingRemove) return
    const target = pendingRemove
    setPendingRemove(null)
    await runWrite(
      () => catalogueApi.removeProfileMember(selected.code, target.code),
      `Đã bỏ ${target.code} khỏi nhóm ${selected.code}.`,
    )
  }

  const createProfileFor = (head: OrphanHeadView): Promise<void> =>
    runWrite(
      () => catalogueApi.createProfile(head.code),
      `Đã tạo nhóm ${head.code} — ${head.name}. Nhóm còn rỗng: hãy thêm các chỉ số con bên dưới.`,
    )

  const switchTab = (next: Tab): void => {
    setTab(next)
    setOffset(0)
    setQueryText('')
    setAppliedQuery('')
    setNotice(null)
    setSaveError(null)
  }

  const from = total === 0 ? 0 : offset + 1
  const to = Math.min(offset + PAGE_SIZE, total)

  return (
    <section className="panel">
      <h2>Nhóm xét nghiệm và chỉ số con</h2>

      <p className="field-hint">
        Một xét nghiệm như Tổng phân tích tế bào máu ngoại vi (<code>002HD</code>) gồm 20 chỉ số
        con. Chỉ định nhóm là chỉ định cả các chỉ số bên trong, và thứ tự ở đây là thứ tự in trên
        phiếu kết quả.
      </p>

      {summaryError && (
        <div className="notice notice-warn" role="status">
          <WarningTriangleIcon className="notice-icon" />
          <p>Chưa đếm được số nhóm chờ xác nhận: {summaryError} Danh sách bên dưới vẫn dùng được.</p>
        </div>
      )}

      {summary && summary.pendingCount > 0 && (
        <div className="notice notice-danger" role="alert">
          <WarningTriangleIcon className="notice-icon" />
          <div>
            <p>
              <strong>
                {summary.pendingCount}/{summary.profileCount} nhóm ĐANG CHỜ KỸ THUẬT VIÊN XÁC NHẬN.
              </strong>{' '}
              Các nhóm này do phần mềm SUY RA từ thụt lề tên của hệ cũ khi di trú — chúng là ĐỀ
              XUẤT, không phải khai báo. Một nhóm suy diễn có thể thiếu chỉ số con, hoặc lẫn xét
              nghiệm không thuộc nhóm. Hãy mở từng nhóm, đối chiếu rồi bấm “Xác nhận nhóm này”.
            </p>
            <p>
              Hiện có {summary.declaredCount} nhóm do hệ cũ khai báo tường minh và{' '}
              {summary.inferredCount} nhóm suy diễn, tổng cộng {summary.memberCount} chỉ số con.
            </p>
          </div>
        </div>
      )}

      {summary && summary.pendingCount === 0 && (
        <div className="notice notice-ok" role="status">
          <CheckCircleIcon className="notice-icon" />
          <p>
            Cả {summary.profileCount} nhóm đều đã được xác nhận ({summary.memberCount} chỉ số con).
          </p>
        </div>
      )}

      {summary && summary.orphanHeadCount > 0 && (
        <div className="notice notice-warn" role="status">
          <UnlinkIcon className="notice-icon" />
          <div>
            <p>
              <strong>{summary.orphanHeadCount} mã cha CHƯA CÓ CHỈ SỐ CON NÀO.</strong> Hệ cũ đánh
              dấu chúng là đầu nhóm, nhưng thuật toán suy diễn theo thụt lề không dựng lại được
              nhóm cho chúng (ví dụ <code>920IONDO</code> — Điện giải đồ).
            </p>
            {tab === 'profiles' && (
              <button type="button" className="btn btn-sm" onClick={() => switchTab('orphans')}>
                Xem danh sách mã cha chưa có con
              </button>
            )}
          </div>
        </div>
      )}

      <div className="btn-row" style={{ marginBottom: 'var(--space-4)' }}>
        <button
          type="button"
          className={tab === 'profiles' ? 'btn btn-primary' : 'btn'}
          aria-pressed={tab === 'profiles'}
          onClick={() => switchTab('profiles')}
        >
          Danh sách nhóm{summary ? ` (${summary.profileCount})` : ''}
        </button>
        <button
          type="button"
          className={tab === 'orphans' ? 'btn btn-primary' : 'btn'}
          aria-pressed={tab === 'orphans'}
          onClick={() => switchTab('orphans')}
        >
          Mã cha chưa có con{summary ? ` (${summary.orphanHeadCount})` : ''}
        </button>
      </div>

      <div className="toolbar">
        <div className="toolbar-field toolbar-grow">
          <label htmlFor="profile-search">Tìm theo mã hoặc tên</label>
          <div className="search-box">
            <SearchIcon className="search-icon" />
            <input
              id="profile-search"
              type="search"
              value={queryText}
              onChange={(e) => setQueryText(e.target.value)}
              placeholder="Ví dụ: 002HD, tế bào máu"
              autoComplete="off"
            />
          </div>
        </div>
        {tab === 'profiles' && (
          <>
            <div className="toolbar-field">
              <label htmlFor="profile-source">Nguồn gốc</label>
              <select
                id="profile-source"
                value={sourceFilter}
                onChange={(e) => {
                  setSourceFilter(e.target.value)
                  setOffset(0)
                }}
              >
                <option value="">Tất cả nguồn gốc</option>
                <option value="legacy-declared">Hệ cũ khai báo tường minh</option>
                <option value="legacy-inferred">Phần mềm suy ra từ thụt lề tên</option>
                <option value="manual">Tạo tay trên phần mềm này</option>
              </select>
            </div>
            <div className="toolbar-field">
              <label htmlFor="profile-confirmed">Tình trạng xác nhận</label>
              <select
                id="profile-confirmed"
                value={confirmedFilter}
                onChange={(e) => {
                  setConfirmedFilter(e.target.value)
                  setOffset(0)
                }}
              >
                <option value="">Tất cả</option>
                <option value="false">Chờ kỹ thuật viên xác nhận</option>
                <option value="true">Đã xác nhận</option>
              </select>
            </div>
          </>
        )}
      </div>

      {notice && (
        <div className="notice notice-ok" role="status" aria-live="polite">
          <CheckCircleIcon className="notice-icon" />
          <p>{notice}</p>
        </div>
      )}

      {saveError && (
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
              <strong>Không tải được danh sách.</strong> {loadError}
            </p>
            <button type="button" className="btn btn-sm" onClick={() => reload()}>
              Thử lại
            </button>
          </div>
        </div>
      )}

      {loading && <p className="loading-text">Đang tải...</p>}

      {!loading && tab === 'profiles' && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Mã nhóm</th>
                <th scope="col">Tên nhóm</th>
                <th scope="col">Nguồn gốc</th>
                <th scope="col">Tình trạng xác nhận</th>
                <th scope="col" className="cell-num">
                  Số chỉ số con
                </th>
                <th scope="col">Thao tác</th>
              </tr>
            </thead>
            <tbody>
              {items.map((profile) => (
                <tr key={profile.code} className={profile.confirmed ? undefined : 'row-warn'}>
                  <td>
                    <code>{profile.code}</code>
                  </td>
                  <td className="cell-wrap">{profile.name}</td>
                  <td className="cell-wrap">
                    <SourceBadge profile={profile} />
                  </td>
                  <td className="cell-wrap">
                    <ConfirmBadge profile={profile} />
                  </td>
                  <td className="cell-num">
                    {profile.memberCount === 0 ? 'CHƯA CÓ CON NÀO' : profile.memberCount}
                  </td>
                  <td>
                    <div className="btn-row">
                      <button
                        type="button"
                        className="btn btn-sm"
                        onClick={() => void openProfile(profile.code)}
                      >
                        Mở nhóm
                      </button>
                      {!profile.confirmed && (
                        <button
                          type="button"
                          className="btn btn-sm btn-primary"
                          onClick={() => void confirmProfile(profile)}
                          disabled={saving}
                        >
                          <CheckCircleIcon />
                          Xác nhận nhóm này
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}

              {items.length === 0 && (
                <tr>
                  <td colSpan={6} className="empty-cell">
                    {appliedQuery.trim() !== '' || sourceFilter !== '' || confirmedFilter !== '' ? (
                      <p>
                        Không có nhóm nào khớp bộ lọc hiện tại. Thử rút ngắn từ khoá, hoặc đưa hai
                        bộ lọc “Nguồn gốc” và “Tình trạng xác nhận” về “Tất cả”.
                      </p>
                    ) : (
                      <p>
                        Chưa có nhóm xét nghiệm nào. Nếu bệnh viện đã có nhóm ở phần mềm LIS cũ,
                        cần chạy công cụ di trú danh mục trước khi dùng màn hình này.
                      </p>
                    )}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {!loading && tab === 'orphans' && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Mã cha</th>
                <th scope="col">Tên</th>
                <th scope="col">Tình trạng</th>
                <th scope="col">Thao tác</th>
              </tr>
            </thead>
            <tbody>
              {orphans.map((head) => (
                <tr key={head.code} className="row-warn">
                  <td>
                    <code>{head.code}</code>
                  </td>
                  <td className="cell-wrap">{head.name}</td>
                  <td className="cell-wrap">
                    {/* Hai tình huống KHÁC NHAU: chưa có nhóm nào, và có nhóm
                        nhưng rỗng. Việc phải làm ở hai ca là khác nhau. */}
                    <span className="status-badge status-badge-sm tone-warn">
                      <UnlinkIcon className="status-badge-icon" />
                      <span className="status-badge-label">
                        {head.hasProfileRow
                          ? 'ĐÃ CÓ NHÓM NHƯNG RỖNG — chưa có chỉ số con nào'
                          : 'CHƯA CÓ NHÓM NÀO — thuật toán suy diễn không bắt được mã này'}
                      </span>
                    </span>
                  </td>
                  <td>
                    {head.hasProfileRow ? (
                      <button
                        type="button"
                        className="btn btn-sm"
                        onClick={() => void openProfile(head.code)}
                      >
                        Mở nhóm để thêm chỉ số con
                      </button>
                    ) : (
                      <button
                        type="button"
                        className="btn btn-sm"
                        onClick={() => void createProfileFor(head)}
                        disabled={saving}
                      >
                        <PlusIcon />
                        Tạo nhóm cho mã này
                      </button>
                    )}
                  </td>
                </tr>
              ))}

              {orphans.length === 0 && (
                <tr>
                  <td colSpan={4} className="empty-cell">
                    <p>
                      {appliedQuery.trim() !== ''
                        ? `Không có mã cha chưa có con nào khớp “${appliedQuery.trim()}”.`
                        : 'Mọi mã cha trong danh mục đều đã có ít nhất một chỉ số con.'}
                    </p>
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
              : tab === 'profiles'
                ? `Đang hiện ${from}–${to} trong ${total} nhóm khớp bộ lọc.`
                : `Đang hiện ${from}–${to} trong ${total} mã cha chưa có chỉ số con.`}
          </p>
          <div className="btn-row">
            <button
              type="button"
              className="btn btn-sm"
              onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}
              disabled={offset === 0}
            >
              Trang trước
            </button>
            <button
              type="button"
              className="btn btn-sm"
              onClick={() => setOffset(offset + PAGE_SIZE)}
              disabled={to >= total}
            >
              Trang sau
            </button>
          </div>
        </div>
      )}

      {detailLoading && <p className="loading-text">Đang mở nhóm...</p>}

      {detailError && (
        <div className="notice notice-danger" role="alert">
          <WarningTriangleIcon className="notice-icon" />
          <p>Không mở được nhóm: {detailError}</p>
        </div>
      )}

      {selected && (
        <div className="editor">
          <div className="detail-header">
            <div className="detail-title">
              <h3 className="detail-name">{selected.name}</h3>
              <code>{selected.code}</code>
            </div>
            <button type="button" className="btn btn-sm" onClick={() => setSelected(null)}>
              Đóng nhóm
            </button>
          </div>

          <div className="badge-list" style={{ marginBottom: 'var(--space-4)' }}>
            <SourceBadge profile={selected} />
            <ConfirmBadge profile={selected} />
          </div>

          {!selected.confirmed && (
            <div className="notice notice-warn" role="status">
              <WarningTriangleIcon className="notice-icon" />
              <div>
                <p>
                  <strong>Nhóm này đang chờ xác nhận.</strong>{' '}
                  {selected.source === 'legacy-inferred'
                    ? 'Danh sách chỉ số con dưới đây do phần mềm SUY RA từ thụt lề tên của hệ cũ — nó có thể thiếu chỉ số con, hoặc lẫn xét nghiệm không thuộc nhóm. Hãy đối chiếu với phiếu kết quả thật rồi sửa cho đúng trước khi xác nhận.'
                    : 'Hãy đối chiếu danh sách chỉ số con dưới đây rồi xác nhận.'}
                </p>
                <button
                  type="button"
                  className="btn btn-sm btn-primary"
                  onClick={() => void confirmProfile(selected)}
                  disabled={saving}
                >
                  {saving && <span className="spinner" />}
                  <CheckCircleIcon />
                  Xác nhận nhóm này
                </button>
              </div>
            </div>
          )}

          <h4 className="editor-title">
            Chỉ số con ({selected.members.length}) — theo thứ tự in trên phiếu
          </h4>

          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th scope="col" className="cell-num">
                    Thứ tự
                  </th>
                  <th scope="col">Mã xét nghiệm</th>
                  <th scope="col">Tên</th>
                  <th scope="col">Đơn vị</th>
                  <th scope="col">Thao tác</th>
                </tr>
              </thead>
              <tbody>
                {selected.members.map((member, index) => (
                  <tr key={member.analyteCode}>
                    <td className="cell-num">{index + 1}</td>
                    <td>
                      <code>{member.analyteCode}</code>
                    </td>
                    <td className="cell-wrap">
                      {member.analyteName ?? 'KHÔNG CÒN TRONG DANH MỤC'}
                    </td>
                    <td>{member.unit ?? '—'}</td>
                    <td>
                      <div className="btn-row">
                        <button
                          type="button"
                          className="btn btn-sm"
                          onClick={() => void move(member.analyteCode, -1)}
                          disabled={saving || index === 0}
                          aria-label={`Đưa ${member.analyteCode} lên trên`}
                        >
                          <ArrowUpIcon />
                          Lên
                        </button>
                        <button
                          type="button"
                          className="btn btn-sm"
                          onClick={() => void move(member.analyteCode, 1)}
                          disabled={saving || index === selected.members.length - 1}
                          aria-label={`Đưa ${member.analyteCode} xuống dưới`}
                        >
                          <ArrowDownIcon />
                          Xuống
                        </button>
                        <button
                          type="button"
                          className="btn btn-sm btn-danger"
                          onClick={() =>
                            setPendingRemove({
                              code: member.analyteCode,
                              name: member.analyteName,
                            })
                          }
                          disabled={saving}
                        >
                          <TrashIcon />
                          Bỏ khỏi nhóm
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}

                {selected.members.length === 0 && (
                  <tr>
                    <td colSpan={5} className="empty-cell">
                      <p>
                        <strong>Nhóm này chưa có chỉ số con nào.</strong> Chỉ định nhóm sẽ không
                        sinh ra chỉ số nào để nhập kết quả cho tới khi được điền.
                      </p>
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>

          <div className="editor-grid">
            <AnalytePicker
              id="profile-new-member"
              label="Thêm một chỉ số con vào nhóm"
              value={newMemberCode}
              onChange={setNewMemberCode}
              hint="Gõ mã hoặc tên để tìm. Chỉ số con mới được xếp vào CUỐI danh sách."
              disabled={saving}
            />
          </div>
          <div className="btn-row">
            <button
              type="button"
              className="btn btn-primary"
              onClick={() => void addMember()}
              disabled={saving}
            >
              {saving && <span className="spinner" />}
              <PlusIcon />
              Thêm vào nhóm
            </button>
          </div>
        </div>
      )}

      {pendingRemove && selected && (
        <div className="dialog-backdrop" role="presentation">
          <div
            className="dialog"
            role="alertdialog"
            aria-modal="true"
            aria-labelledby="member-remove-title"
          >
            <h3 id="member-remove-title">Bỏ chỉ số con khỏi nhóm?</h3>
            <p>
              Xét nghiệm <span className="code-inline">{pendingRemove.code}</span>
              {pendingRemove.name ? ` — ${pendingRemove.name}` : ''} sẽ không còn thuộc nhóm{' '}
              <span className="code-inline">{selected.code}</span>.
            </p>
            <p>
              Xét nghiệm vẫn còn trong danh mục; chỉ riêng việc chỉ định nhóm này sẽ không sinh ra
              chỉ số đó nữa.
            </p>
            <div className="dialog-actions">
              <button
                type="button"
                className="btn"
                onClick={() => setPendingRemove(null)}
                disabled={saving}
              >
                Không bỏ
              </button>
              <button
                type="button"
                className="btn btn-danger"
                onClick={() => void removeMember()}
                disabled={saving}
              >
                {saving && <span className="spinner" />}
                Bỏ khỏi nhóm
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  )
}
