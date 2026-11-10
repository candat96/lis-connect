// Mọi truy cập dữ liệu của màn hình đi qua đây (quy ước chung với his-web): component không gọi fetch trực tiếp.
import { QueryClient } from '@tanstack/react-query';
import { api, ApiError } from './http';
import type { SessionClinic, SessionUser } from '@/stores/sessionStore';

export { ApiError };

export const createQueryClient = () =>
  new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 5_000,
        refetchOnWindowFocus: false,
        // Lỗi quyền/không tìm thấy thì thử lại cũng vô ích; lỗi mạng thử lại một lần.
        retry: (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 1,
      },
    },
  });

// ---- Đăng nhập ----
export interface Session {
  token: string;
  clinic: SessionClinic;
  user: SessionUser;
}

export const authService = {
  login: (clinicCode: string, username: string, password: string) =>
    api<Session>('/auth/login', { method: 'POST', body: { clinicCode, username, password } }),
};

// ---- Kết quả ----
export type SampleStatus = 'cho-ket-qua' | 'cho-duyet' | 'da-duyet';
export type ResultFlag = 'None' | 'Low' | 'High' | 'CriticalLow' | 'CriticalHigh' | 'Abnormal';

export interface SampleListItem {
  sampleId: string;
  barcode: string;
  patientName: string;
  patientCode: string;
  orderedAt: string;
  specimenName: string;
  total: number;
  withValue: number;
  status: SampleStatus;
  hasAbnormal: boolean;
  hasPending: boolean;
}

export interface ResultRow {
  orderItemId: string;
  serviceCode: string;
  serviceName: string;
  isPanelHeader: boolean;
  depth: number;
  value: string | null;
  unit: string | null;
  refText: string | null;
  flag: ResultFlag;
  status: string | null;
  pendingValue: string | null;
  source: string | null;
}

export interface SampleDetail {
  sampleId: string;
  barcode: string;
  patient: { name: string; code: string; sex: string; dob: string | null; birthYear: number | null };
  order: { doctor: string | null; dept: string | null; diagnosis: string | null; orderedAt: string };
  approvedAt: string | null;
  approvedBy: string | null;
  rows: ResultRow[];
}

export const resultService = {
  list: (filter: { from: string; to: string; status?: SampleStatus; q?: string }) =>
    api<SampleListItem[]>('/samples', { query: filter }),
  detail: (sampleId: string) => api<SampleDetail>(`/samples/${sampleId}`),
};

// ---- Danh mục ----
export interface CatalogService {
  id: string;
  code: string;
  name: string;
  groupName: string | null;
  specimenName: string | null;
  unit: string | null;
  valueType: string;
  decimals: number | null;
  isPanel: boolean;
  autoApprove: boolean;
  active: boolean;
  rangeText: string | null;
  children: CatalogService[];
}

export const catalogService = {
  services: () => api<CatalogService[]>('/catalog/services'),
};

// ---- Máy xét nghiệm ----
export interface InstrumentStatus {
  state: 'NotRunning' | 'Connecting' | 'Connected' | 'Error';
  text: string;
  lastResultAt: string | null;
  samplesReceived: number;
  pendingToRetry: number;
}

export type ConnectionKind = 'none' | 'serial' | 'tcp-server' | 'tcp-client';

/** Cấu hình kết nối máy ↔ agent (cổng COM hoặc TCP). */
export interface Connection {
  kind: ConnectionKind;
  portName?: string;
  baudRate?: number;
  dataBits?: number;
  parity?: string;
  stopBits?: number;
  host?: string;
  tcpPort?: number;
}

export interface Instrument {
  id: string;
  name: string;
  model: string | null;
  protocolFamily: string | null;
  workstationCode: string | null;
  active: boolean;
  connection: Connection;
  connectionText: string | null;
  channelCount: number;
  pendingServiceCount: number;
  unmappedCount: number;
  agent: { id: string; name: string; lastSeenAt: string | null; online: boolean } | null;
  status: InstrumentStatus | null;
}

/** Dữ liệu form thêm / sửa máy. */
export interface InstrumentInput {
  name: string;
  model: string | null;
  protocolFamily: string | null;
  workstationCode: string | null;
  active: boolean;
  connection: Connection;
}

export interface Channel {
  id: string | null;
  channelCode: string | null;
  serviceCode: string;
  serviceName: string;
  decimals: number | null;
  factor: number | null;
  keepComparator: boolean;
  sendOnQuery: boolean;
}

export interface ChannelInput {
  channelCode: string;
  serviceCode: string;
  decimals: number | null;
  factor: number | null;
  keepComparator: boolean;
  sendOnQuery: boolean;
}

export interface UnmappedChannel {
  channelCode: string;
  count: number;
  lastValue: string;
  lastUnit: string | null;
  lastAt: string;
}

export const instrumentService = {
  list: () => api<Instrument[]>('/instruments'),
  create: (input: InstrumentInput) => api<{ id: string }>('/instruments', { method: 'POST', body: input }),
  update: (id: string, input: InstrumentInput) => api<void>(`/instruments/${id}`, { method: 'PUT', body: input }),
  remove: (id: string) => api<void>(`/instruments/${id}`, { method: 'DELETE' }),
  channels: (instrumentId: string) => api<Channel[]>(`/instruments/${instrumentId}/channels`),
  addChannel: (instrumentId: string, input: ChannelInput) => api<{ id: string }>(`/instruments/${instrumentId}/channels`, { method: 'POST', body: input }),
  updateChannel: (instrumentId: string, channelId: string, input: ChannelInput) =>
    api<void>(`/instruments/${instrumentId}/channels/${channelId}`, { method: 'PUT', body: input }),
  removeChannel: (instrumentId: string, channelId: string) => api<void>(`/instruments/${instrumentId}/channels/${channelId}`, { method: 'DELETE' }),
  declare: (instrumentId: string, serviceCode: string) => api<void>(`/instruments/${instrumentId}/declared`, { method: 'POST', body: { serviceCode } }),
  undeclare: (instrumentId: string, serviceCode: string) =>
    api<void>(`/instruments/${instrumentId}/declared/${encodeURIComponent(serviceCode)}`, { method: 'DELETE' }),
  unmapped: (instrumentId: string) => api<UnmappedChannel[]>(`/instruments/${instrumentId}/unmapped`),
};

// ---- Agent (máy tính cạnh máy xét nghiệm) ----
export interface Agent {
  id: string;
  name: string;
  workstationCode: string;
  lastSeenAt: string | null;
  online: boolean;
  instruments: { id: string; name: string; status: InstrumentStatus | null }[];
}

export const agentService = {
  list: () => api<Agent[]>('/agents'),
  /** Trả về khoá agent — chỉ hiện một lần. */
  create: (input: { name: string; workstationCode: string }) => api<{ id: string; key: string }>('/agents', { method: 'POST', body: input }),
  update: (id: string, input: { name: string; workstationCode: string }) => api<void>(`/agents/${id}`, { method: 'PATCH', body: input }),
  regenerateKey: (id: string) => api<{ key: string }>(`/agents/${id}/key`, { method: 'POST' }),
  remove: (id: string) => api<void>(`/agents/${id}`, { method: 'DELETE' }),
};

// ---- Log agent ----
export type LogDirection = 'in' | 'out' | 'event' | 'agent';

export interface AgentLog {
  id: string;
  at: string;
  agentId: string;
  instrumentId: string | null;
  instrumentName: string | null;
  direction: LogDirection;
  session: string | null;
  hex: string | null;
  note: string | null;
}

export const logService = {
  list: (filter: { agentId?: string; instrumentId?: string; direction?: string; q?: string; beforeId?: string; limit?: number }) =>
    api<AgentLog[]>('/agent-logs', { query: filter }),
};

// ---- Nhân sự (tài khoản đăng nhập) ----
export const ROLE_OPTIONS = [
  { value: 'quan-tri', label: 'Quản trị', hint: 'toàn quyền: cài đặt máy, agent, danh mục, nhân sự' },
  { value: 'truong-khoa', label: 'Trưởng khoa', hint: 'như kỹ thuật viên, thêm huỷ duyệt kết quả' },
  { value: 'ky-thuat-vien', label: 'Kỹ thuật viên', hint: 'nhập, sửa, duyệt kết quả; theo dõi máy, xem log' },
  { value: 'tiep-nhan', label: 'Tiếp nhận', hint: 'tiếp nhận người bệnh, in tem; xem kết quả' },
] as const;

export const roleLabel = (code: string) => ROLE_OPTIONS.find((r) => r.value === code)?.label ?? code;

export interface User {
  id: string;
  username: string;
  fullName: string;
  title: string | null;
  roles: string[];
  active: boolean;
  /** Đang tạm khoá do nhập sai mật khẩu nhiều lần (tới thời điểm này). */
  lockedUntil: string | null;
  lastLoginAt: string | null;
}

export const userService = {
  list: () => api<User[]>('/users'),
  create: (input: { username: string; fullName: string; title: string | null; roles: string[]; password: string }) =>
    api<{ id: string }>('/users', { method: 'POST', body: input }),
  update: (id: string, input: { fullName: string; title: string | null; roles: string[]; active: boolean; newPassword: string | null }) =>
    api<void>(`/users/${id}`, { method: 'PUT', body: input }),
  changeOwnPassword: (currentPassword: string, newPassword: string) =>
    api<void>('/users/me/password', { method: 'POST', body: { currentPassword, newPassword } }),
};

/** Khoá cache TanStack Query — một chỗ để không gõ trùng/sai. */
export const qk = {
  samples: (filter: object) => ['samples', filter] as const,
  sample: (id: string) => ['sample', id] as const,
  catalog: ['catalog', 'services'] as const,
  instruments: ['instruments'] as const,
  channels: (id: string) => ['instruments', id, 'channels'] as const,
  unmapped: (id: string) => ['instruments', id, 'unmapped'] as const,
  agents: ['agents'] as const,
  users: ['users'] as const,
  logs: (filter: object) => ['agent-logs', filter] as const,
};
