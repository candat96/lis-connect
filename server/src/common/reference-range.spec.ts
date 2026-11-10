import { ageInDays, formatRange, ReferenceRange, selectRange } from './reference-range';

const range = (over: Partial<ReferenceRange>): ReferenceRange => ({
  sex: null, ageFromDays: null, ageToDays: null, instrumentId: null, low: 1, high: 2, criticalLow: null, criticalHigh: null, textRange: null, ...over,
});

describe('chọn khoảng tham chiếu', () => {
  const man = { sex: 'M', dob: '1980-06-15', birthYear: null };

  it('tính tuổi theo ngày; chỉ có năm sinh thì lấy 01/07; không rõ thì -1', () => {
    expect(ageInDays({ sex: 'M', dob: '2026-11-01', birthYear: null }, '2026-11-08')).toBe(7);
    expect(ageInDays({ sex: 'M', dob: null, birthYear: 2026 }, '2026-07-11')).toBe(10);
    expect(ageInDays({ sex: 'M', dob: null, birthYear: null }, '2026-11-08')).toBe(-1);
  });

  it('ưu tiên khoảng theo giới hơn khoảng chung', () => {
    const general = range({ low: 10 });
    const male = range({ sex: 'M', low: 20 });
    const female = range({ sex: 'F', low: 30 });
    expect(selectRange([general, female, male], man, '2026-11-08', null)).toBe(male);
    expect(selectRange([general, female], man, '2026-11-08', null)).toBe(general);
  });

  it('ưu tiên đúng máy, rồi khoảng tuổi hẹp hơn', () => {
    const general = range({});
    const onMachine = range({ instrumentId: 'm1' });
    const adult = range({ ageFromDays: 16 * 365, ageToDays: 60 * 365 });
    expect(selectRange([general, onMachine], man, '2026-11-08', 'm1')).toBe(onMachine);
    // Khoảng của máy khác không được xét.
    expect(selectRange([onMachine], man, '2026-11-08', 'm2')).toBeNull();
    expect(selectRange([general, adult], man, '2026-11-08', null)).toBe(adult);
  });

  it('không rõ tuổi chỉ khớp khoảng không giới hạn tuổi', () => {
    const adult = range({ ageFromDays: 16 * 365 });
    expect(selectRange([adult], { sex: 'M', dob: null, birthYear: null }, '2026-11-08', null)).toBeNull();
  });

  it('định dạng khoảng để hiển thị', () => {
    expect(formatRange('Âm tính', 1, 2, 1)).toBe('Âm tính');
    expect(formatRange(null, 4, 10, 2)).toBe('4.00 – 10.00');
    expect(formatRange(null, 0.9, null, null)).toBe('≥ 0.9');
    expect(formatRange(null, null, 5.2, null)).toBe('≤ 5.2');
    expect(formatRange(null, null, null, 2)).toBeNull();
  });
});
