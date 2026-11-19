import { Tag } from 'antd';
import {
  CheckCircleOutlined, ClockCircleOutlined, CloseCircleOutlined, DisconnectOutlined, ExclamationCircleOutlined,
  HourglassOutlined, LinkOutlined, LoadingOutlined, MinusCircleOutlined, QuestionCircleOutlined, SaveOutlined, SendOutlined,
} from '@ant-design/icons';
import type { ReactNode } from 'react';

// Trạng thái luôn có chữ + icon, không chỉ dựa vào màu (quy ước chung với his-web).
interface StatusDef {
  label: string;
  color: string;
  icon: ReactNode;
}

const STATUS: Record<string, Record<string, StatusDef>> = {
  sample: {
    'cho-ket-qua': { label: 'Chờ kết quả', color: 'default', icon: <HourglassOutlined /> },
    'cho-duyet': { label: 'Chờ duyệt', color: 'warning', icon: <ClockCircleOutlined /> },
    'da-duyet': { label: 'Đã duyệt', color: 'success', icon: <CheckCircleOutlined /> },
  },
  link: {
    Connected: { label: 'Đã kết nối', color: 'success', icon: <LinkOutlined /> },
    Connecting: { label: 'Đang chờ máy', color: 'processing', icon: <LoadingOutlined /> },
    Error: { label: 'Lỗi kết nối', color: 'error', icon: <CloseCircleOutlined /> },
    NotRunning: { label: 'Không chạy', color: 'default', icon: <DisconnectOutlined /> },
    offline: { label: 'Agent không báo về', color: 'warning', icon: <ExclamationCircleOutlined /> },
    'no-agent': { label: 'Chưa gán agent', color: 'default', icon: <QuestionCircleOutlined /> },
  },
  agent: {
    online: { label: 'Đang kết nối', color: 'success', icon: <CheckCircleOutlined /> },
    offline: { label: 'Mất kết nối', color: 'warning', icon: <ExclamationCircleOutlined /> },
    never: { label: 'Đã ghép nối, chưa chạy', color: 'default', icon: <QuestionCircleOutlined /> },
    pairing: { label: 'Chờ ghép nối', color: 'processing', icon: <HourglassOutlined /> },
    unpaired: { label: 'Chưa ghép nối', color: 'default', icon: <DisconnectOutlined /> },
  },
  command: {
    queued: { label: 'Chờ agent', color: 'default', icon: <HourglassOutlined /> },
    sent: { label: 'Đã gửi', color: 'processing', icon: <SendOutlined /> },
    running: { label: 'Đang chạy', color: 'processing', icon: <LoadingOutlined /> },
    done: { label: 'Xong', color: 'success', icon: <CheckCircleOutlined /> },
    failed: { label: 'Lỗi', color: 'error', icon: <CloseCircleOutlined /> },
    expired: { label: 'Hết hạn', color: 'warning', icon: <ClockCircleOutlined /> },
    unknown: { label: 'Không rõ kết quả', color: 'warning', icon: <QuestionCircleOutlined /> },
  },
  decode: {
    'cho-giai-ma': { label: 'Chờ giải mã', color: 'default', icon: <HourglassOutlined /> },
    'dang-giai-ma': { label: 'Đang giải mã', color: 'processing', icon: <LoadingOutlined /> },
    'da-giai-ma': { label: 'Đã giải mã', color: 'success', icon: <CheckCircleOutlined /> },
    'khong-co-ket-qua': { label: 'Không có kết quả', color: 'default', icon: <MinusCircleOutlined /> },
    loi: { label: 'Lỗi', color: 'error', icon: <CloseCircleOutlined /> },
    'chi-luu': { label: 'Chỉ lưu', color: 'default', icon: <SaveOutlined /> },
    'can-xem-lai': { label: 'Cần xem lại', color: 'warning', icon: <ExclamationCircleOutlined /> },
  },
  user: {
    active: { label: 'Đang hoạt động', color: 'success', icon: <CheckCircleOutlined /> },
    locked: { label: 'Tạm khoá (sai mật khẩu)', color: 'warning', icon: <ExclamationCircleOutlined /> },
    disabled: { label: 'Đã ngừng', color: 'default', icon: <CloseCircleOutlined /> },
  },
};

export function StatusTag({ domain, value }: { domain: keyof typeof STATUS; value: string }) {
  const def = STATUS[domain]?.[value];
  if (!def) return <Tag icon={<QuestionCircleOutlined />}>{value}</Tag>;
  return <Tag color={def.color} icon={def.icon} style={{ marginInlineEnd: 0 }}>{def.label}</Tag>;
}
