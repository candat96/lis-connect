# lis-web — giao diện web LIS Connect

React + Vite + TypeScript + Ant Design, cùng bộ thư viện và cùng theme với `his-web`
(`src/app/theme` chép từ his-web: tokens, antdTheme, global.css — không viết mã màu trong component).

```bash
npm install
npm run dev        # http://localhost:5186 — /api proxy tới server ở http://localhost:8084 (đổi bằng LIS_BACKEND_URL)
npm run typecheck && npm run build
```

Đăng nhập bằng mã phòng khám + tài khoản của phòng khám đó.

- Mọi truy cập dữ liệu đi qua `src/services`. Mỗi màn có `data-screen="<mã>"` ở phần tử gốc.
- Đã có (chỉ xem): Kết quả, Danh mục, Máy & map kênh. Các thao tác ghi và màn Tiếp nhận làm ở đợt sau.
- Cổng 5186 (không dùng 5173–5185 của các dự án khác).
