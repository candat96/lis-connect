import { theme, type ThemeConfig } from 'antd';
import type { MapToken, SeedToken } from 'antd/es/theme/interface';
import { tokens } from './tokens';

// compactAlgorithm lấy fontSizeSM làm cỡ chữ gốc (13px → 10px), trái spec (13px, tối thiểu 12px).
// Thuật toán này giữ khoảng cách gọn của compact nhưng khôi phục cỡ chữ từ thuật toán mặc định.
const keepSpecFonts = (seed: SeedToken, map?: MapToken): MapToken => {
  const base = theme.defaultAlgorithm(seed);
  const out = { ...(map ?? base) } as MapToken & Record<string, unknown>;
  for (const k of Object.keys(base) as (keyof MapToken)[]) {
    if (/^(fontSize|lineHeight|fontHeight)/.test(k)) (out as Record<string, unknown>)[k] = base[k];
  }
  out.fontSizeSM = Math.max(12, base.fontSizeSM);
  // Ô nhập/nút: compact + componentSize small chỉ còn 21px, quá nhỏ; nâng lên cho dễ bấm và dễ đọc
  out.controlHeightSM = tokens.layout.controlHeight;
  out.controlHeight = tokens.layout.controlHeight + 4;
  out.controlHeightLG = tokens.layout.controlHeight + 10;
  return out;
};

export const antdTheme: ThemeConfig = {
  algorithm: [theme.defaultAlgorithm, theme.compactAlgorithm, keepSpecFonts],
  token: {
    colorPrimary: tokens.colors.primary, colorSuccess: tokens.colors.success, colorError: tokens.colors.danger,
    colorWarning: tokens.colors.warning, colorInfo: tokens.colors.info, colorText: tokens.colors.text,
    colorTextSecondary: tokens.colors.textSecondary, colorTextTertiary: tokens.colors.textSecondary, colorTextDescription: tokens.colors.textSecondary,
    colorTextPlaceholder: tokens.colors.placeholder, colorBgLayout: tokens.colors.bg,
    colorBorder: tokens.colors.border, colorLink: tokens.colors.primaryStrong, fontFamily: tokens.font.family, fontSize: tokens.font.size,
    fontWeightStrong: tokens.font.weightStrong,
    borderRadius: 4, motionDurationMid: '0.15s', motionDurationSlow: '0.2s',
    // Vòng focus ô chọn (Select) 1px sát viền: cùng ô nhập thành viền đặc 2px màu chủ đạo
    controlOutlineWidth: 1,
  },
  // Màu chủ đạo #2C3782 đạt ~10:1 với chữ trắng (primaryStrong cùng màu)
  components: {
    // Chữ "bảng trống" (locale.emptyText) mặc định #BFBFBF chỉ 1.8:1
    Table: { cellPaddingBlockSM: 5, headerBg: tokens.colors.sectionHeaderBg, headerColor: tokens.colors.label, colorTextDisabled: tokens.colors.textMuted },
    // Ô nhập/ô chọn: viền rõ hơn viền khung để nhìn ra ngay chỗ cần nhập
    Input: { colorBorder: tokens.colors.inputBorder, hoverBorderColor: tokens.colors.primary, activeBorderColor: tokens.colors.primary, activeShadow: `0 0 0 1px ${tokens.colors.primary}` },
    InputNumber: { colorBorder: tokens.colors.inputBorder, hoverBorderColor: tokens.colors.primary, activeBorderColor: tokens.colors.primary, activeShadow: `0 0 0 1px ${tokens.colors.primary}` },
    Select: { colorBorder: tokens.colors.inputBorder, hoverBorderColor: tokens.colors.primary, activeBorderColor: tokens.colors.primary, activeOutlineColor: tokens.colors.primary },
    DatePicker: { colorBorder: tokens.colors.inputBorder, hoverBorderColor: tokens.colors.primary, activeBorderColor: tokens.colors.primary, activeShadow: `0 0 0 1px ${tokens.colors.primary}` },
    Radio: { colorBorder: tokens.colors.inputBorder },
    Checkbox: { colorBorder: tokens.colors.inputBorder },
    // Lỗi validate cạnh trường: đỏ đậm để đạt 4.5:1 cả trên nền trang xám nhạt
    Form: { labelColor: tokens.colors.label, verticalLabelPadding: '0 0 4px', itemMarginBottom: 14, colorError: tokens.colors.dangerText },
    // Chữ Typography type warning/danger/success: dùng tông chữ đậm (màu gốc chỉ 2.8–4.3:1)
    Typography: { colorWarningText: tokens.colors.warningText, colorErrorText: tokens.colors.dangerText, colorSuccessText: tokens.colors.successText },
    Card: { headerBg: tokens.colors.sectionHeaderBg, headerFontSizeSM: tokens.font.sizeLg, colorBorderSecondary: tokens.colors.border },
    Tabs: { horizontalMargin: '0', itemSelectedColor: tokens.colors.primaryStrong, itemActiveColor: tokens.colors.primaryStrong },
    // Tag trạng thái và Tag màu preset: chữ dùng tông đậm hơn để đủ tương phản trên nền nhạt
    Tag: {
      colorSuccess: tokens.colors.successText, colorWarning: tokens.colors.warningText, colorError: tokens.colors.dangerText, colorInfo: tokens.colors.infoText,
      colorSuccessBg: tokens.colors.successBg,
      cyan7: '#006D75', gold7: '#874D00' /* #AD6800 chỉ 4.24:1 trên nền vàng nhạt */, orange7: '#AD4E00', green7: '#237804', blue7: '#003EB3', lime7: '#3F6600', volcano7: '#AD2102',
    },
    // Rê chuột: không dùng primary (giữ quy ước dùng primaryHover đậm hơn)
    // Switch có chữ (checkedChildren): chữ trắng trên xám #BFBFBF (tắt) chỉ 1.83:1
    Switch: { colorPrimary: tokens.colors.primaryStrong, colorPrimaryHover: tokens.colors.primaryHover, colorTextQuaternary: tokens.colors.textMuted, colorTextTertiary: tokens.colors.textSecondary },
    Button: { fontWeight: tokens.font.weight, colorPrimary: tokens.colors.primaryStrong, colorPrimaryHover: tokens.colors.primaryHover, colorPrimaryActive: tokens.colors.text, colorLink: tokens.colors.primaryStrong },
  },
};

// Kiosk: chữ lớn, vùng chạm lớn (xem docs/design-system/pages/kiosk.md)
export const kioskTheme: ThemeConfig = {
  algorithm: theme.defaultAlgorithm,
  token: { ...antdTheme.token, fontSize: 20, controlHeight: 56, borderRadius: 10 },
};
