// Lệnh điều khiển agent từ web: nhãn, trạng thái chưa xong, và một dòng tóm tắt kết quả cho bảng lịch sử.
import type { AgentCommand, AgentCommandName, AgentCommandStatus, AgentPort } from '@/services';

const LABELS: Record<AgentCommandName, string> = {
  'reload-config': 'Nạp lại cấu hình',
  'scan-ports': 'Quét cổng COM',
  'test-port': 'Thử cổng',
  'reconnect-instrument': 'Nối lại máy',
  'flush-pending': 'Gửi bù ngay',
  restart: 'Khởi động lại',
  update: 'Cập nhật',
  revoke: 'Thu hồi',
};

export const ALL_COMMANDS = Object.keys(LABELS) as AgentCommandName[];

export const commandLabel = (name: AgentCommandName): string => LABELS[name] ?? name;

/** Lệnh chưa có kết quả cuối — web hỏi lại mỗi giây. */
export const isPending = (status: AgentCommandStatus): boolean => status === 'queued' || status === 'sent' || status === 'running';

type Json = Record<string, unknown> | null;

const num = (r: Json, key: string): number => (typeof r?.[key] === 'number' ? (r[key] as number) : 0);

function summarizeDone(name: AgentCommandName, r: Json, args: Json): string {
  switch (name) {
    case 'scan-ports': {
      const ports = Array.isArray(r?.ports) ? (r.ports as AgentPort[]) : [];
      if (ports.length === 0) return 'Không thấy cổng COM nào';
      const parts = ports.map((p) => (p.usedBy ? `${p.name} (${p.usedBy})` : p.busy ? `${p.name} bận` : p.name));
      return `${ports.length} cổng: ${parts.join(', ')}`;
    }
    case 'test-port': {
      if (r?.opened !== true) return typeof r?.error === 'string' && r.error ? r.error : 'Không mở được cổng';
      const count = num(r, 'byteCount');
      const borrowed = Array.isArray(r.borrowedFrom) ? (r.borrowedFrom as string[]) : [];
      const head = count > 0 ? `Mở được, nhận ${count.toLocaleString('en-US')} byte` : 'Mở được, không nhận byte nào';
      return borrowed.length > 0 ? `${head} · đã tạm ngắt ${borrowed.join(', ')}` : head;
    }
    case 'update':
      if (r?.alreadyCurrent === true) return 'Agent đã ở bản này';
      return 'Đã giao bản mới, chờ agent nối lại';
    case 'reload-config':
      return `Đang chạy ${num(r, 'instruments')} máy`;
    case 'flush-pending': {
      const remaining = num(r, 'remaining');
      return remaining === 0 ? 'Đã gửi hết bản tin chờ' : `Còn ${remaining} bản tin chờ gửi`;
    }
    case 'reconnect-instrument':
      return args?.instrumentId ? 'Đã đóng và mở lại kết nối máy' : 'Đã nối lại';
    case 'restart':
      return 'Agent đang khởi động lại';
    case 'revoke':
      return r?.offline === true ? 'Đã thu hồi khi agent đang tắt — agent sẽ không nối lại được' : 'Đã thu hồi, agent đã dừng';
    default:
      return 'Xong';
  }
}

/** Một dòng tiếng Việt mô tả kết quả lệnh; lệnh chưa xong trả chuỗi rỗng. */
export function summarizeResult(cmd: AgentCommand): string {
  switch (cmd.status) {
    case 'done':
      return summarizeDone(cmd.name, cmd.result, cmd.args);
    case 'failed':
      return cmd.error || 'Lỗi không rõ';
    case 'expired':
      return 'Agent không nhận lệnh trong 60 giây';
    case 'unknown':
      return 'Agent mất kết nối khi đang chạy lệnh';
    default:
      return '';
  }
}

/** Nhãn gợi ý cổng COM ở form máy: "COM3 — USB-SERIAL CH340 · đang dùng bởi Máy Thử". */
export function portOptionLabel(port: AgentPort): string {
  let label = port.description ? `${port.name} — ${port.description}` : port.name;
  if (port.usedBy) label += ` · đang dùng bởi ${port.usedBy}`;
  else if (port.busy) label += ' · phần mềm khác đang giữ';
  return label;
}
