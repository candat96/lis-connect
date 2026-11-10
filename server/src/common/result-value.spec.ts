import { applyQualitative, computeFlag, normalizeNumber, parseValue, roundValue } from './result-value';

describe('đọc giá trị kết quả', () => {
  it('số, dấu phẩy thập phân, dấu so sánh', () => {
    expect(parseValue('7.25')).toEqual({ number: 7.25, comparator: null, isEmpty: false });
    expect(parseValue(' 4,8 ')).toEqual({ number: 4.8, comparator: null, isEmpty: false });
    expect(parseValue('>500')).toEqual({ number: 500, comparator: '>', isEmpty: false });
    expect(parseValue('<= 0.5')).toEqual({ number: 0.5, comparator: '<=', isEmpty: false });
    expect(parseValue('-3')).toEqual({ number: -3, comparator: null, isEmpty: false });
  });

  it('hiệu giá a:b và a/b', () => {
    expect(parseValue('1:4').number).toBe(0.25);
    expect(parseValue('1/160').number).toBe(1 / 160);
    expect(parseValue('1:0').isEmpty).toBe(true);
  });

  it('rỗng và ký hiệu lỗi của máy là "không có giá trị", không phải 0', () => {
    for (const raw of ['', '   ', null, '***.*', '**', 'abc', '12..5', '1e5', '1 000']) {
      expect(parseValue(raw).isEmpty).toBe(true);
    }
  });
});

describe('làm tròn', () => {
  it('giữ đúng số lẻ, kể cả số 0 ở cuối', () => {
    expect(roundValue('4.8', 2)).toBe('4.80');
    expect(roundValue('12.46', 1)).toBe('12.5');
    expect(roundValue('7', 0)).toBe('7');
    expect(roundValue('.5', 1)).toBe('0.5');
  });

  it('làm tròn nửa ra xa số 0, không sai số thực', () => {
    expect(roundValue('2.675', 2)).toBe('2.68');
    expect(roundValue('1.005', 2)).toBe('1.01');
    expect(roundValue('0.5', 0)).toBe('1');
    expect(roundValue('-2.5', 0)).toBe('-3');
    expect(roundValue('9.995', 2)).toBe('10.00');
  });

  it('giữ dấu so sánh; không phải số thì trả nguyên văn; không có số lẻ thì không đổi', () => {
    expect(roundValue('>500.26', 1)).toBe('>500.3');
    expect(roundValue('4,85', 1)).toBe('4.9');
    expect(roundValue('Âm tính', 2)).toBe('Âm tính');
    expect(roundValue('4.8', null)).toBe('4.8');
  });

  it('chuẩn hoá khi chỉ số không khai số lẻ', () => {
    expect(normalizeNumber('4,8', parseValue('4,8'), null)).toBe('4.8');
    expect(normalizeNumber('< 0,5', parseValue('< 0,5'), null)).toBe('<0.5');
    expect(normalizeNumber('1:4', parseValue('1:4'), null)).toBe('1:4');
    expect(normalizeNumber('12.46', parseValue('12.46'), 1)).toBe('12.5');
  });
});

describe('tính cờ', () => {
  const range = { low: 4, high: 10, criticalLow: 2, criticalHigh: 30 };

  it('thấp / cao / bình thường; biên không tính là bất thường', () => {
    expect(computeFlag(parseValue('3.9'), range)).toBe('Low');
    expect(computeFlag(parseValue('4'), range)).toBe('None');
    expect(computeFlag(parseValue('10'), range)).toBe('None');
    expect(computeFlag(parseValue('10.1'), range)).toBe('High');
  });

  it('nguy kịch thắng cao/thấp thường, tính cả khi bằng ngưỡng', () => {
    expect(computeFlag(parseValue('2'), range)).toBe('CriticalLow');
    expect(computeFlag(parseValue('30'), range)).toBe('CriticalHigh');
    expect(computeFlag(parseValue('>30'), range)).toBe('CriticalHigh');
  });

  it('không có khoảng hoặc không đọc được số → không cờ', () => {
    expect(computeFlag(parseValue('99'), null)).toBe('None');
    expect(computeFlag(parseValue('***'), range)).toBe('None');
    expect(computeFlag(parseValue('5'), { low: null, high: 5.2, criticalLow: null, criticalHigh: null })).toBe('None');
  });
});

describe('quy đổi định tính', () => {
  const rules = [
    { instrumentId: null, rawValue: 'pos', displayValue: 'Dương tính', flag: 'Abnormal' as const },
    { instrumentId: null, rawValue: 'neg', displayValue: 'Âm tính', flag: null },
    { instrumentId: 'm1', rawValue: 'POS', displayValue: 'Dương tính (máy 1)', flag: 'Abnormal' as const },
  ];

  it('không phân biệt hoa thường, bỏ khoảng trắng', () => {
    expect(applyQualitative(' Pos ', null, rules)).toEqual({ display: 'Dương tính', flag: 'Abnormal' });
    expect(applyQualitative('neg', null, rules)).toEqual({ display: 'Âm tính', flag: null });
  });

  it('luật đúng máy ưu tiên hơn luật chung; luật của máy khác không áp', () => {
    expect(applyQualitative('pos', 'm1', rules).display).toBe('Dương tính (máy 1)');
    expect(applyQualitative('pos', 'm2', rules).display).toBe('Dương tính');
  });

  it('không có luật khớp thì giữ nguyên', () => {
    expect(applyQualitative('Nghi ngờ', null, rules)).toEqual({ display: 'Nghi ngờ', flag: null });
  });
});
