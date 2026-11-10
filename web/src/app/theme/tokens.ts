// Design tokens theo spec mục 4. Component chỉ lấy màu/khoảng cách từ đây, không viết mã hex trực tiếp.
export const tokens = {
  colors: {
    primary: '#2C3782', primaryStrong: '#2C3782' /* màu chủ đạo #2C3782: chữ trắng trên nền đạt ~10:1 */, primaryHover: '#212A66' /* rê chuột trên nút: đậm hơn màu chủ đạo */, success: '#059669', danger: '#DC2626', warning: '#D97706', info: '#2563EB',
    text: '#000000', textSecondary: '#374151', textMuted: '#4B5563', bg: '#EDF1F5', surface: '#FFFFFF', border: '#DCE3EA',
    // Ô nhập: viền rõ; placeholder tối vừa đủ để không nhầm với giá trị đã nhập nhưng đạt WCAG AA
    // (#6B7480 ≈ 4.6:1 trên nền trắng — #9AA5B1 cũ chỉ 2.5:1, axe flag color-contrast)
    inputBorder: '#AEB9C6', placeholder: '#6B7480', label: '#1F2937',
    // Tiêu đề khối (Card): nền nhạt + vạch màu bên trái
    sectionHeaderBg: '#F3F7FA',
    focusRing: 'rgba(44, 55, 130, 0.18)', // quầng sáng quanh ô nhập đang focus (màu chủ đạo, nhạt)
    menuSection: '#6576FF', // tiêu đề cột trong menu lớn (Nghiệp vụ, Danh mục…)
    // Thanh đa tab kiểu Chrome: nền thanh đậm hơn nền trang, tab rê chuột sáng hơn một bậc
    tabBar: '#CFD8E3', tabHover: '#E2E8EF',
    bhyt: '#2C3782', allergy: '#B91C1C',
    // Chữ đậm cho Tag trạng thái trên nền nhạt (đạt WCAG AA 4.5:1)
    successText: '#047857', warningText: '#92400E' /* #B45309 chỉ 4.42:1 trên nền trang #EDF1F5 */, dangerText: '#B91C1C', infoText: '#1D4ED8',
    // Nền Tag thành công: bảng màu antd sinh từ success (#059669) ra nền xám đậm (#c1d6cc), chữ chỉ đạt 3.6:1
    successBg: '#ECFDF5',
    // Đợt 2: vạch màu trạng thái mẫu XN (Đã trả KQ, Tạm dừng); nền nhạt dòng chỉ số bất thường
    purple: '#7C3AED', magenta: '#C026D3', warningBg: '#FFF7E6', dangerBg: '#FEF2F2',
  },
  // Màu biểu đồ SVG thuần (Task 23): 8 màu, tương phản ≥ 3:1 trên nền trắng. Chỉ dùng qua tokens.chart[i % 8].
  // Mẫu in: MỘT chỗ đổi font và thang cỡ chữ cho mọi phiếu in (PrintEngine sinh biến CSS --print-*).
  // Times New Roman theo thể thức văn bản hành chính (NĐ 30/2020). Đổi ở đây là toàn bộ mẫu in đổi theo.
  print: {
    fontFamily: "'Times New Roman', Times, 'Liberation Serif', serif",
    size: { xs: '8pt', sm: '9pt', base: '10pt', md: '11pt', lg: '12pt', xl: '14pt', title: '16pt', huge: '28pt' },
  },
  // Mực vẽ lược đồ PT/TT (in ra giấy như bút bi): nền trắng, đen/đỏ/xanh — không đổi theo theme
  ink: { paper: '#FFFFFF', black: '#111111', red: '#C62828', blue: '#1565C0' },
  chart: ['#2C3782', '#D97706', '#7C3AED', '#059669', '#DB2777', '#2563EB', '#65A30D', '#475569'],
  space: { xs: 4, sm: 8, md: 12, lg: 16, xl: 24 },
  font: {
    family: "'Be Vietnam Pro', system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
    mono: "'Roboto Mono', ui-monospace, monospace",
    size: 13, sizeLg: 15, sizeXl: 17,
    // Inter 400 trên Windows hiển thị mảnh: chữ thường dùng 500, chữ đậm 700
    weight: 500, weightStrong: 700,
  },
  layout: { controlHeight: 28 /* chiều cao ô nhập, nút (size small) */, topBarHeight: 44, tabBarHeight: 34, actionBarHeight: 48, maxTabs: 15 },
} as const;
