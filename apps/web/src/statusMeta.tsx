import type { JSX } from 'react'

/**
 * Bốn trạng thái người dùng có thể thấy trên màn hình theo dõi:
 * - ba trạng thái do Core trả về ("connected" | "disconnected" | "error")
 * - một trạng thái do chính client suy ra khi vòng làm mới tự động gọi
 *   API thất bại ("unknown") — KHÔNG được lẫn với "connected", vì hiển thị
 *   dữ liệu cũ như thể máy vẫn ổn là nguy hiểm cho một màn hình treo cả ngày.
 *
 * Mỗi trạng thái có: màu + NHÃN CHỮ + HÌNH DẠNG icon riêng biệt (tròn/tam
 * giác/bát giác/nét đứt) — không dựa vào màu sắc một mình, để người mù màu
 * và người nhìn từ xa vài mét vẫn phân biệt được ngay.
 */
export type DeviceUiStatus = 'connected' | 'disconnected' | 'error' | 'unknown'

export interface StatusMeta {
  label: string
  toneClass: string
  Icon: (props: { className?: string }) => JSX.Element
}

export function CheckCircleIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
      <circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" strokeWidth="2" />
      <path
        d="M8 12.5l2.5 2.5 5-5.5"
        fill="none"
        stroke="currentColor"
        strokeWidth="2.2"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  )
}

/** Tam giác cảnh báo — hình dạng "báo động" ai cũng hiểu, không cần đọc màu. */
export function WarningTriangleIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
      <path
        d="M12 3.5 21.5 20h-19L12 3.5z"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinejoin="round"
      />
      <line x1="12" y1="9.5" x2="12" y2="14" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />
      <circle cx="12" cy="17" r="1.15" fill="currentColor" stroke="none" />
    </svg>
  )
}

/** Bát giác dừng — hình dạng khác hẳn tam giác, dành riêng cho lỗi driver/thiết bị. */
function StopOctagonIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
      <path
        d="M8 3h8l5 5v8l-5 5H8l-5-5V8l5-5z"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinejoin="round"
      />
      <line x1="8.5" y1="8.5" x2="15.5" y2="15.5" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />
      <line x1="15.5" y1="8.5" x2="8.5" y2="15.5" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />
    </svg>
  )
}

/** Vòng nét đứt + dấu hỏi — "không biết", tách bạch khỏi cả ba trạng thái trên. */
export function UnknownDashedIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
      <circle
        cx="12"
        cy="12"
        r="9"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeDasharray="3.2 3.2"
      />
      <text x="12" y="16.5" textAnchor="middle" fontSize="11" fontWeight="700" fill="currentColor" stroke="none">
        ?
      </text>
    </svg>
  )
}

/** Mắt mở — icon nút "hiện mật khẩu" ở màn hình đăng nhập. */
export function EyeIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
      <path
        d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7-10-7-10-7z"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinejoin="round"
      />
      <circle cx="12" cy="12" r="3" fill="none" stroke="currentColor" strokeWidth="2" />
    </svg>
  )
}

/** Mắt gạch chéo — icon nút "ẩn mật khẩu", hình dạng khác hẳn EyeIcon để không cần đọc màu. */
export function EyeOffIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
      <path
        d="M3.5 3.5l17 17M6.4 6.7C4 8.3 2 12 2 12s3.6 7 10 7c1.9 0 3.5-.6 4.9-1.4M9.9 9.9a3 3 0 0 0 4.2 4.2M9.6 5.3C10.4 5.1 11.2 5 12 5c6.4 0 10 7 10 7-.5.9-1.5 2.4-3 3.7"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  )
}

export const STATUS_META: Record<DeviceUiStatus, StatusMeta> = {
  connected: { label: 'Đang kết nối', toneClass: 'tone-ok', Icon: CheckCircleIcon },
  disconnected: { label: 'Mất kết nối', toneClass: 'tone-danger', Icon: WarningTriangleIcon },
  error: { label: 'Lỗi thiết bị', toneClass: 'tone-danger', Icon: StopOctagonIcon },
  unknown: { label: 'Không rõ trạng thái', toneClass: 'tone-warn', Icon: UnknownDashedIcon },
}

/** Core hiện chỉ trả về ba giá trị đã biết; mọi giá trị lạ đều coi là "không rõ" thay vì đoán mò. */
export function toUiStatus(status: string): DeviceUiStatus {
  if (status === 'connected' || status === 'disconnected' || status === 'error') return status
  return 'unknown'
}

export function StatusBadge({ status, size }: { status: string; size?: 'sm' }): JSX.Element {
  const meta = STATUS_META[toUiStatus(status)]
  return (
    <span className={`status-badge ${meta.toneClass}${size === 'sm' ? ' status-badge-sm' : ''}`}>
      <meta.Icon className="status-badge-icon" />
      <span className="status-badge-label">{meta.label}</span>
    </span>
  )
}

/* ---------------------------------------------------------------------------
 * Icon cho ba màn hình quản lý danh mục.
 *
 * Cùng một quy ước với bốn icon trạng thái máy ở trên: mỗi ý nghĩa một HÌNH
 * DẠNG riêng, và luôn đi kèm nhãn chữ — màu chỉ là lớp phụ. Tất cả là SVG, dự
 * án không dùng emoji làm icon.
 * ------------------------------------------------------------------------ */

/** Kính lúp — chỉ dùng trang trí cho ô tìm kiếm, đã có nhãn chữ đi kèm. */
export function SearchIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
      <circle cx="10.5" cy="10.5" r="6.5" fill="none" stroke="currentColor" strokeWidth="2" />
      <line x1="15.4" y1="15.4" x2="20.5" y2="20.5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
    </svg>
  )
}

/** Bút chì — hành động sửa. */
export function PencilIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
      <path
        d="M4 20h4l10-10a2.5 2.5 0 0 0-4-4L4 16v4z"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinejoin="round"
      />
    </svg>
  )
}

/** Thùng rác — hành động xoá. */
export function TrashIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
      <path d="M4 6.5h16M9.5 6.5V4h5v2.5" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
      <path d="M6.5 6.5 7.5 20h9l1-13.5" fill="none" stroke="currentColor" strokeWidth="2" strokeLinejoin="round" />
    </svg>
  )
}

/** Dấu cộng — hành động thêm mới. */
export function PlusIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
      <line x1="12" y1="5" x2="12" y2="19" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />
      <line x1="5" y1="12" x2="19" y2="12" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />
    </svg>
  )
}

/** Mũi tên lên — "cao" trong quy ước màu 0/1/2 của hệ cũ. */
export function ArrowUpIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
      <line x1="12" y1="19" x2="12" y2="6" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />
      <path d="M6.5 11.5 12 5.5l5.5 6" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

/** Mũi tên xuống — "thấp". */
export function ArrowDownIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
      <line x1="12" y1="5" x2="12" y2="18" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />
      <path d="M6.5 12.5 12 18.5l5.5-6" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

/** Gạch ngang trong vòng tròn — "bình thường", hình dạng trung tính. */
export function LevelNormalIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
      <circle cx="12" cy="12" r="8.5" fill="none" stroke="currentColor" strokeWidth="2" />
      <line x1="7.5" y1="12" x2="16.5" y2="12" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />
    </svg>
  )
}

/** Mắt xích đứt — mã kênh CHƯA gán xét nghiệm nào. */
export function UnlinkIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
      <path
        d="M9.5 14.5 7 17a3.5 3.5 0 0 1-5-5l2.5-2.5M14.5 9.5 17 7a3.5 3.5 0 0 1 5 5l-2.5 2.5"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
      />
      <line x1="3.5" y1="3.5" x2="20.5" y2="20.5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
    </svg>
  )
}

/** Quả cầu — luật CHUNG, áp cho mọi máy. */
export function GlobeIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
      <circle cx="12" cy="12" r="8.5" fill="none" stroke="currentColor" strokeWidth="2" />
      <ellipse cx="12" cy="12" rx="4" ry="8.5" fill="none" stroke="currentColor" strokeWidth="1.6" />
      <line x1="3.5" y1="12" x2="20.5" y2="12" stroke="currentColor" strokeWidth="1.6" />
    </svg>
  )
}

/** Hộp máy — luật RIÊNG của một máy. */
export function DeviceChipIcon({ className }: { className?: string }): JSX.Element {
  return (
    <svg className={className} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
      <rect x="5" y="5" width="14" height="14" rx="2" fill="none" stroke="currentColor" strokeWidth="2" />
      <rect x="9" y="9" width="6" height="6" fill="none" stroke="currentColor" strokeWidth="1.6" />
      <path d="M9 2.5v2.5M15 2.5v2.5M9 19v2.5M15 19v2.5M2.5 9H5M2.5 15H5M19 9h2.5M19 15h2.5" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
    </svg>
  )
}
