import { describe, expect, it } from 'vitest';
import type { CatalogService } from '@/services';
import { findServiceByCode, parseBirth, parseSex } from './reception';

describe('parseBirth', () => {
  it('4 số là năm sinh', () => {
    expect(parseBirth('1985')).toEqual({ birthYear: 1985 });
    expect(parseBirth(' 2023 ')).toEqual({ birthYear: 2023 });
  });

  it('ngày đủ dd/MM/yyyy, có hoặc không có dấu', () => {
    expect(parseBirth('12/03/1985')).toEqual({ dob: '1985-03-12' });
    expect(parseBirth('1-3-1985')).toEqual({ dob: '1985-03-01' });
    expect(parseBirth('12031985')).toEqual({ dob: '1985-03-12' });
  });

  it('không hợp lệ: ngày sai, tương lai, trước 1900, rỗng', () => {
    expect(parseBirth('31/02/1985')).toBeNull();
    expect(parseBirth('1899')).toBeNull();
    expect(parseBirth(`01/01/${new Date().getFullYear() + 1}`)).toBeNull();
    expect(parseBirth('')).toBeNull();
    expect(parseBirth('abc')).toBeNull();
  });
});

describe('findServiceByCode', () => {
  const svc = (over: Partial<CatalogService>): CatalogService => ({
    id: 'x', code: 'GLU', name: 'Glucose', groupName: null, specimenName: 'Huyết thanh', unit: null, valueType: 'so', decimals: 1,
    isPanel: false, autoApprove: false, active: true, rangeText: null, children: [], ...over,
  });
  const list = [svc({ id: '1', code: 'GLU' }), svc({ id: '2', code: 'CBC', isPanel: true }), svc({ id: '3', code: 'OLD', active: false })];

  it('không phân biệt hoa thường, bỏ dịch vụ ngừng dùng', () => {
    expect(findServiceByCode(list, 'glu')?.id).toBe('1');
    expect(findServiceByCode(list, ' CBC ')?.id).toBe('2');
    expect(findServiceByCode(list, 'OLD')).toBeNull();
    expect(findServiceByCode(list, 'XYZ')).toBeNull();
    expect(findServiceByCode(list, '')).toBeNull();
  });
});

describe('parseSex', () => {
  it('phím tắt N / U / K và chữ đầy đủ', () => {
    expect(parseSex('n')).toBe('M');
    expect(parseSex('U')).toBe('F');
    expect(parseSex('Nữ')).toBe('F');
    expect(parseSex('k')).toBe('O');
    expect(parseSex('x')).toBeNull();
  });
});
