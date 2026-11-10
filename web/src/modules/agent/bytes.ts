// Hiển thị byte thô của máy xét nghiệm: ký tự in được giữ nguyên, ký tự điều khiển thành tên trong ngoặc nhọn.

const CONTROL: Record<number, string> = {
  0x00: 'NUL', 0x01: 'SOH', 0x02: 'STX', 0x03: 'ETX', 0x04: 'EOT', 0x05: 'ENQ', 0x06: 'ACK', 0x07: 'BEL', 0x08: 'BS', 0x09: 'TAB',
  0x0a: 'LF', 0x0b: 'VT', 0x0c: 'FF', 0x0d: 'CR', 0x0e: 'SO', 0x0f: 'SI', 0x10: 'DLE', 0x11: 'DC1', 0x12: 'DC2', 0x13: 'DC3', 0x14: 'DC4',
  0x15: 'NAK', 0x16: 'SYN', 0x17: 'ETB', 0x18: 'CAN', 0x19: 'EM', 0x1a: 'SUB', 0x1b: 'ESC', 0x1c: 'FS', 0x1d: 'GS', 0x1e: 'RS', 0x1f: 'US',
  0x7f: 'DEL',
};

function toBytes(hex: string): Uint8Array {
  const bytes = new Uint8Array(Math.floor(hex.length / 2));
  for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  return bytes;
}

/**
 * Hex → chữ đọc được. Chữ tiếng Việt (UTF-8) hiện đúng; byte điều khiển thành <STX>, <CR>…;
 * xuống dòng sau <CR>/<LF> để mỗi bản ghi của máy nằm một dòng.
 */
export function decodeBytes(hex: string): string {
  const bytes = toBytes(hex);
  const text = new TextDecoder('utf-8', { fatal: false }).decode(bytes);
  let out = '';
  for (const char of text) {
    const code = char.codePointAt(0)!;
    if (code in CONTROL) {
      out += `<${CONTROL[code]}>`;
      if (code === 0x0d || code === 0x0a) out += '\n';
    } else if (code === 0xfffd) {
      out += '<?>'; // byte không phải chữ hợp lệ
    } else {
      out += char;
    }
  }
  // <CR><LF> liền nhau chỉ xuống một dòng
  return out.replace(/<CR>\n<LF>\n/g, '<CR><LF>\n').trimEnd();
}

/** "0241" → "02 41" để dễ dò từng byte. */
export function spacedHex(hex: string): string {
  return hex.replace(/(..)/g, '$1 ').trim();
}
