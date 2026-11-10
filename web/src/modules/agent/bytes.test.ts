import { decodeBytes, spacedHex } from './bytes';

const hex = (text: string) => Array.from(new TextEncoder().encode(text), (b) => b.toString(16).padStart(2, '0').toUpperCase()).join('');

describe('hiển thị byte thô của máy', () => {
  it('ký tự điều khiển thành tên, mỗi bản ghi một dòng', () => {
    expect(decodeBytes(hex('\x0bMSH|^~\&|A\rOBX|1\r\x1c\r'))).toBe('<VT>MSH|^~\&|A<CR>\nOBX|1<CR>\n<FS><CR>');
    expect(decodeBytes('06')).toBe('<ACK>');
    expect(decodeBytes(hex('1H|\^&\r\n'))).toBe('1H|\^&<CR><LF>');
  });

  it('giữ chữ tiếng Việt, byte hỏng thành <?>', () => {
    expect(decodeBytes(hex('Nguyễn Văn Mẫu'))).toBe('Nguyễn Văn Mẫu');
    expect(decodeBytes('41FF42')).toBe('A<?>B');
  });

  it('tách hex theo từng byte', () => {
    expect(spacedHex('024142')).toBe('02 41 42');
  });
});
