// Trạng thái ghép nối của agent và tên tệp cài kèm mã — dùng chung cho bảng agent và hộp thoại ghép nối.
import type { Agent } from '@/services';

export type PairingState = 'online' | 'offline' | 'never' | 'pairing' | 'unpaired';

/** Tên tệp cài mang mã ghép nối: agent đọc mã từ tên tệp của chính nó khi chạy lần đầu. */
export const installerFileName = (code: string) => `LisAgent_${code}.exe`;

/**
 * Đã ghép nối → theo kết nối (đang báo về / mất kết nối / chưa chạy lần nào).
 * Chưa ghép nối → còn mã chưa hết hạn là "chờ ghép nối", không thì "chưa ghép nối".
 */
export function pairingState(agent: Agent, now: Date): PairingState {
  if (agent.paired) return agent.online ? 'online' : agent.lastSeenAt ? 'offline' : 'never';
  return agent.pairingExpiresAt && new Date(agent.pairingExpiresAt).getTime() > now.getTime() ? 'pairing' : 'unpaired';
}
