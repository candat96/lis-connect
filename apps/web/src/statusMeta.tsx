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

function CheckCircleIcon({ className }: { className?: string }): JSX.Element {
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
