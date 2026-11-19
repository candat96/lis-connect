import { describe, expect, it } from 'vitest';
import type { AgentCommand } from '@/services';
import { ALL_COMMANDS, commandLabel, isPending, portOptionLabel, summarizeResult } from './commands';

const cmd = (over: Partial<AgentCommand>): AgentCommand => ({
  id: 'c1', name: 'scan-ports', args: null, status: 'done', requestedBy: 'Quản trị', requestedAt: '2026-11-19T08:00:00.000Z',
  startedAt: null, finishedAt: null, result: null, error: null,
  ...over,
});

describe('commandLabel', () => {
  it('có nhãn tiếng Việt cho đủ 8 lệnh', () => {
    expect(ALL_COMMANDS).toHaveLength(8);
    expect(ALL_COMMANDS.map(commandLabel)).toEqual([
      'Nạp lại cấu hình', 'Quét cổng COM', 'Thử cổng', 'Nối lại máy', 'Gửi bù ngay', 'Khởi động lại', 'Cập nhật', 'Thu hồi',
    ]);
  });
});

describe('isPending', () => {
  it('queued / sent / running là chưa xong', () => {
    expect(['queued', 'sent', 'running'].every((s) => isPending(s as AgentCommand['status']))).toBe(true);
    expect(['done', 'failed', 'expired', 'unknown'].some((s) => isPending(s as AgentCommand['status']))).toBe(false);
  });
});

describe('summarizeResult', () => {
  it('quét cổng: liệt kê cổng, máy đang dùng, cổng bận', () => {
    const result = { ports: [
      { name: 'COM3', description: 'USB', usedBy: 'Máy Thử', busy: false },
      { name: 'COM5', description: null, usedBy: null, busy: true },
      { name: 'COM7', description: null, usedBy: null, busy: false },
    ] };
    expect(summarizeResult(cmd({ result }))).toBe('3 cổng: COM3 (Máy Thử), COM5 bận, COM7');
    expect(summarizeResult(cmd({ result: { ports: [] } }))).toBe('Không thấy cổng COM nào');
  });

  it('thử cổng: số byte nhận, máy đã tạm nhường cổng, hoặc lỗi', () => {
    expect(summarizeResult(cmd({ name: 'test-port', result: { opened: true, error: null, hex: '0241', byteCount: 42, borrowedFrom: [] } })))
      .toBe('Mở được, nhận 42 byte');
    expect(summarizeResult(cmd({ name: 'test-port', result: { opened: true, error: null, hex: '', byteCount: 0, borrowedFrom: ['BC-5380'] } })))
      .toBe('Mở được, không nhận byte nào · đã tạm ngắt BC-5380');
    expect(summarizeResult(cmd({ name: 'test-port', result: { opened: false, error: 'Cổng COM7 đang bận', hex: '', byteCount: 0, borrowedFrom: [] } })))
      .toBe('Cổng COM7 đang bận');
  });

  it('cập nhật: đã giao bản mới / đã ở bản này', () => {
    expect(summarizeResult(cmd({ name: 'update', status: 'running', result: { handedOver: true } }))).toBe('Đã giao bản mới, chờ agent nối lại');
    expect(summarizeResult(cmd({ name: 'update', result: { version: '0.3.0' } }))).toBe('Đã cập nhật lên 0.3.0');
    expect(summarizeResult(cmd({ name: 'update', result: { alreadyCurrent: true } }))).toBe('Agent đã ở bản này');
  });

  it('các lệnh khác', () => {
    expect(summarizeResult(cmd({ name: 'reload-config', result: { instruments: 2 } }))).toBe('Đang chạy 2 máy');
    expect(summarizeResult(cmd({ name: 'flush-pending', result: { remaining: 0 } }))).toBe('Đã gửi hết bản tin chờ');
    expect(summarizeResult(cmd({ name: 'flush-pending', result: { remaining: 3 } }))).toBe('Còn 3 bản tin chờ gửi');
    expect(summarizeResult(cmd({ name: 'revoke', result: { offline: true } }))).toBe('Đã thu hồi lúc agent không nối kênh lệnh — máy chủ đã khoá, agent không gửi được dữ liệu nữa');
    expect(summarizeResult(cmd({ name: 'revoke', result: null }))).toBe('Đã thu hồi, agent đã dừng');
  });

  it('lỗi / hết hạn / không rõ / chưa xong', () => {
    expect(summarizeResult(cmd({ status: 'failed', error: 'Tải bản mới lỗi' }))).toBe('Tải bản mới lỗi');
    expect(summarizeResult(cmd({ status: 'expired' }))).toBe('Agent không nhận lệnh trong 60 giây');
    expect(summarizeResult(cmd({ status: 'unknown' }))).toBe('Agent mất kết nối khi đang chạy lệnh');
    expect(summarizeResult(cmd({ status: 'unknown', error: 'Máy chủ khởi động lại — không rõ kết quả' }))).toBe('Máy chủ khởi động lại — không rõ kết quả');
    expect(summarizeResult(cmd({ status: 'running' }))).toBe('');
  });
});

describe('portOptionLabel', () => {
  it('ghép tên cổng, tên thiết bị và máy đang dùng', () => {
    expect(portOptionLabel({ name: 'COM3', description: 'USB-SERIAL CH340', usedBy: 'Máy Thử', busy: false }))
      .toBe('COM3 — USB-SERIAL CH340 · đang dùng bởi Máy Thử');
    expect(portOptionLabel({ name: 'COM5', description: null, usedBy: null, busy: true })).toBe('COM5 · phần mềm khác đang giữ');
    expect(portOptionLabel({ name: 'COM7', description: null, usedBy: null, busy: false })).toBe('COM7');
  });
});
