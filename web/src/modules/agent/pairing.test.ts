import { describe, expect, it } from 'vitest';
import type { Agent } from '@/services';
import { installerFileName, pairingState } from './pairing';

const now = new Date('2026-11-19T08:00:00.000Z');

const agent = (over: Partial<Agent>): Agent => ({
  id: 'a1', name: 'Máy XN 1', workstationCode: 'W1', lastSeenAt: null, online: false, instruments: [],
  paired: false, pairedAt: null, machineName: null, version: null, pairingExpiresAt: null, channel: false, ports: null, portsScannedAt: null,
  ...over,
});

describe('installerFileName', () => {
  it('ghép mã vào tên tệp để agent tự đọc mã khi chạy', () => {
    expect(installerFileName('LIS1.abc_-')).toBe('LisAgent_LIS1.abc_-.exe');
  });
});

describe('pairingState', () => {
  it('đã ghép nối và đang báo về → online', () => {
    expect(pairingState(agent({ paired: true, online: true, lastSeenAt: '2026-11-19T07:59:50Z' }), now)).toBe('online');
  });
  it('đã ghép nối, từng báo về nhưng im → offline', () => {
    expect(pairingState(agent({ paired: true, lastSeenAt: '2026-11-19T07:00:00Z' }), now)).toBe('offline');
  });
  it('đã ghép nối, chưa báo về lần nào → never', () => {
    expect(pairingState(agent({ paired: true }), now)).toBe('never');
  });
  it('chưa ghép nối, còn mã → pairing', () => {
    expect(pairingState(agent({ pairingExpiresAt: '2026-11-19T08:00:00.001Z' }), now)).toBe('pairing');
  });
  it('chưa ghép nối, mã hết hạn đúng 1 ms trước → unpaired', () => {
    expect(pairingState(agent({ pairingExpiresAt: '2026-11-19T07:59:59.999Z' }), now)).toBe('unpaired');
  });
  it('chưa ghép nối, không có mã → unpaired', () => {
    expect(pairingState(agent({}), now)).toBe('unpaired');
  });
  it('đã ghép nối và đang có mã ghép nối lại → vẫn theo kết nối', () => {
    expect(pairingState(agent({ paired: true, online: true, pairingExpiresAt: '2026-11-19T08:20:00Z' }), now)).toBe('online');
  });
});
