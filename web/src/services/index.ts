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

export interface Instrument {
  id: string;
  name: string;
  model: string | null;
  protocolFamily: string | null;
  workstationCode: string | null;
  active: boolean;
  connectionText: string | null;
  channelCount: number;
  pendingServiceCount: number;
  agent: { name: string; lastSeenAt: string | null; online: boolean } | null;
  status: InstrumentStatus | null;
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

export const instrumentService = {
  list: () => api<Instrument[]>('/instruments'),
  channels: (instrumentId: string) => api<Channel[]>(`/instruments/${instrumentId}/channels`),
};

/** Khoá cache TanStack Query — một chỗ để không gõ trùng/sai. */
export const qk = {
  samples: (filter: object) => ['samples', filter] as const,
  sample: (id: string) => ['sample', id] as const,
  catalog: ['catalog', 'services'] as const,
  instruments: ['instruments'] as const,
  channels: (id: string) => ['instruments', id, 'channels'] as const,
};
