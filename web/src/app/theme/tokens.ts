import type { ThemeConfig } from 'antd';

/**
 * Nguồn màu DUY NHẤT của giao diện, lấy từ ../qubx-color-theme.md (xanh lục y tế #0A6B45, bo góc 12px).
 * Font: Roboto (bản variable, đủ dấu tiếng Việt). Chữ thường 400–500, nhãn/menu 600, tiêu đề 700.
 * Component không viết mã hex: dùng token Ant Design (theme.useToken) hoặc biến CSS --c-* sinh từ đây.
 * Quy tắc 60-30-10: ~60% nền trung tính, ~30% chữ/viền, ~10% brand (nút chính, mục đang chọn).
 */
export const palette = {
  brand: '#0A6B45',
  brandHover: '#075437',
  brandTint: '#DCF3E7',
  ring: '#5AD49A',

  bg: '#F5F5F7',
  card: '#FFFFFF',
  fg: '#121A1A',
  ink: '#222F30',
  inkFg: '#F5F5F7',
  muted: '#EEF2F3',
  // Chữ phụ (nhãn, mô tả, ghi chú): đủ tương phản để đọc rõ (≈ 9:1 trên nền trắng)
  mutedFg: '#434A4F',
  border: '#E5E7E8',
  input: '#C8D1D3',

  // Trang đăng nhập (theo thiết kế Figma): lớp sóng xanh ngọc phủ ảnh nền
  heroDeep: '#267778',
  heroLight: '#6BBFA0',

  cream: '#F5EEE3',
  creamFg: '#1C2828',

  success: '#16A260',
  successTint: '#DCF3E7',
  warning: '#C77C0E',
  warningTint: '#FDF1DC',
  info: '#2F6FB0',
  infoTint: '#E3EEF9',
  danger: '#E40014',
  dangerTint: '#FDE6E8',

  // Biểu đồ: 8 màu phân loại, dùng theo THỨ TỰ (đã kiểm CVD/độ tách màu trên nền trắng — skill dataviz,
  // bảng tham chiếu, ô 6 thay bằng xanh brand). Không tự sinh màu thứ 9: gộp "Khác".
  chart1: '#2A78D6',
  chart2: '#EB6834',
  chart3: '#1BAF7A',
  chart4: '#EDA100',
  chart5: '#E87BA4',
  chart6: '#0A6B45',
  chart7: '#4A3AA7',
  chart8: '#E34948',
  // Thang thứ bậc một màu (nhạt → đậm), VD tuổi nợ
  seq1: '#86B6EF',
  seq2: '#3987E5',
  seq3: '#1C5CAB',
  seq4: '#0D366B',
  // Khung biểu đồ: lưới, trục, chữ phụ
  grid: '#ECEDEE',
  axis: '#C8CCCE',
} as const;

export type PaletteKey = keyof typeof palette;

export const fontFamily = '"Roboto Variable", Roboto, -apple-system, "Segoe UI", Arial, sans-serif';
export const radius = { sm: 8, md: 12, lg: 16 } as const;

export const theme: ThemeConfig = {
  token: {
    colorPrimary: palette.brand,
    colorPrimaryHover: palette.brandHover,
    colorPrimaryActive: palette.brandHover,
    colorPrimaryBg: palette.brandTint,
    colorLink: palette.brand,
    colorLinkHover: palette.brandHover,
    colorSuccess: palette.success,
    colorWarning: palette.warning,
    colorError: palette.danger,
    colorInfo: palette.info,
    colorBgLayout: palette.bg,
    colorBgContainer: palette.card,
    colorBgSpotlight: palette.ink,
    colorText: palette.fg,
    colorTextSecondary: palette.mutedFg,
    colorTextDescription: palette.mutedFg,
    colorTextTertiary: palette.mutedFg,
    colorTextPlaceholder: '#7A8287',
    colorTextDisabled: '#6B7378',
    colorBorder: palette.input,
    colorBorderSecondary: palette.border,
    colorFillAlter: palette.muted,
    controlOutline: `${palette.ring}55`,
    borderRadius: radius.md,
    borderRadiusSM: radius.sm,
    borderRadiusLG: radius.lg,
    fontFamily,
    fontSize: 14,
    fontWeightStrong: 600,
    lineHeight: 1.6,
    controlHeight: 36,
    boxShadow: '0 1px 2px rgba(18, 26, 26, 0.06), 0 4px 12px rgba(18, 26, 26, 0.04)',
  },
  components: {
    Layout: { siderBg: palette.card, headerBg: palette.card, bodyBg: palette.bg, headerPadding: '0 20px' },
    Menu: {
      itemBg: palette.card,
      itemSelectedBg: palette.brandTint,
      itemSelectedColor: palette.brandHover,
      itemHoverBg: palette.muted,
      itemBorderRadius: 10,
      subMenuItemBg: palette.card,
      groupTitleColor: palette.mutedFg,
      itemColor: palette.fg,
      fontSize: 14,
    },
    Table: {
      headerBg: palette.muted,
      headerColor: palette.fg,
      rowHoverBg: palette.brandTint,
      rowSelectedBg: palette.brandTint,
      rowSelectedHoverBg: palette.brandTint,
      headerSplitColor: 'transparent',
    },
    Button: { primaryShadow: 'none', defaultShadow: 'none', fontWeight: 600 },
    Card: { headerFontSize: 16, fontWeightStrong: 700 },
    Descriptions: { labelColor: palette.mutedFg, titleColor: palette.fg },
    // Tab / Segmented: mục đang chọn phải nổi rõ — chữ brand đậm + vạch dày (Tabs), nền brand chữ trắng (Segmented)
    Tabs: {
      titleFontSize: 14,
      itemColor: palette.mutedFg,
      itemHoverColor: palette.brand,
      itemSelectedColor: palette.brand,
      itemActiveColor: palette.brandHover,
      inkBarColor: palette.brand,
      cardBg: palette.muted,
    },
    Segmented: {
      trackBg: palette.muted,
      trackPadding: 3,
      itemColor: palette.mutedFg,
      itemHoverColor: palette.fg,
      itemHoverBg: palette.border,
      itemSelectedBg: palette.brand,
      itemSelectedColor: palette.inkFg,
    },
    Typography: { fontWeightStrong: 600, titleMarginBottom: '0.4em' },
    Form: { labelColor: palette.fg },
    Tag: { defaultBg: palette.muted },
  },
};

/** Ghi palette thành biến CSS --c-<key> trên :root để global.css và mẫu in dùng chung một nguồn. */
export function applyCssVars(root: HTMLElement = document.documentElement) {
  for (const [k, v] of Object.entries(palette)) {
    root.style.setProperty(`--c-${k.replace(/[A-Z0-9]/g, (m) => `-${m.toLowerCase()}`)}`, v);
  }
  root.style.setProperty('--font-sans', fontFamily);
  root.style.setProperty('--radius', `${radius.md}px`);
}
