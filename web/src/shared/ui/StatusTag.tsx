import { Tag } from 'antd';
import {
  CheckCircleOutlined, ClockCircleOutlined, CloseCircleOutlined, DisconnectOutlined, ExclamationCircleOutlined,
  HourglassOutlined, LinkOutlined, LoadingOutlined, QuestionCircleOutlined,
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
    online: { label: 'Đang chạy', color: 'success', icon: <CheckCircleOutlined /> },
    offline: { label: 'Không báo về', color: 'warning', icon: <ExclamationCircleOutlined /> },
    never: { label: 'Chưa từng kết nối', color: 'default', icon: <QuestionCircleOutlined /> },
  },
};

export function StatusTag({ domain, value }: { domain: keyof typeof STATUS; value: string }) {
  const def = STATUS[domain]?.[value];
  if (!def) return <Tag icon={<QuestionCircleOutlined />}>{value}</Tag>;
  return <Tag color={def.color} icon={def.icon} style={{ marginInlineEnd: 0 }}>{def.label}</Tag>;
}
