# lis-server — server LIS Connect (NestJS + TypeORM)

Một server phục vụ nhiều phòng khám: CSDL `lis_master` giữ danh sách phòng khám + agent, mỗi phòng khám
một CSDL riêng. Cấu trúc bảng do các tệp SQL ở `../src/Lis.Data/Migrations` quyết định (dùng chung với
agent .NET) — TypeORM chỉ ánh xạ, `synchronize` luôn tắt.

## Chạy

```bash
cp .env.example .env      # điền chuỗi kết nối và JWT_SECRET (chuỗi ngẫu nhiên >= 24 ký tự)
npm install
npm run dev               # http://localhost:8084
npm test && npm run typecheck
```

Cần PostgreSQL 18. Phòng khám và agent hiện tạo bằng lệnh của server .NET
(`LisServer clinic add …`, `LisServer agent add …` — xem `../docs/server-agent.md`).

## API đã có

| | |
|---|---|
| `POST /api/auth/login` | `{clinicCode, username, password}` → token (12 giờ) + tài khoản + phòng khám |
| `GET /api/auth/me` | phiên hiện tại |
| `GET /api/samples?from&to&status&q` | danh sách mẫu theo ngày chỉ định |
| `GET /api/samples/:id` | bảng chỉ số của mẫu (kèm khoảng tham chiếu, cờ, bản chờ thay thế) |
| `PUT /api/results/items/:id`, `POST /api/samples/:id/approve|unapprove`, `POST /api/results/:id/pending/accept|reject` | nhập / sửa, duyệt, huỷ duyệt, xử lý giá trị máy gửi lại |
| `GET/POST/PUT /api/users`, `POST /api/users/me/password` | nhân sự, tự đổi mật khẩu |
| `GET /api/catalog/services` | cây dịch vụ / chỉ số |
| `GET /api/instruments`, `GET /api/instruments/:id/channels` | máy, trạng thái agent báo về, map kênh |
| `POST/PUT/DELETE /api/instruments…` | thêm / sửa / xoá máy, cấu hình kết nối, gán agent (Quản trị) |
| `POST/PUT/DELETE /api/instruments/:id/channels…`, `…/declared`, `GET …/unmapped` | map kênh, khai chỉ số chờ dò, mã kênh máy gửi chưa map |
| `GET/POST/PATCH/DELETE /api/agents`, `POST /api/agents/:id/key` | quản lý agent, cấp khoá (khoá chỉ trả một lần) |
| `GET /api/agent-logs` | log agent đẩy lên (byte thô + sự kiện), lọc theo agent / máy / chiều / chữ |

Mọi API trừ đăng nhập cần `Authorization: Bearer <token>`; token xác định luôn phòng khám.
Mật khẩu dùng chung định dạng Argon2id với app .NET nên tài khoản cũ đăng nhập được.

## Chưa chuyển sang NestJS

Nhận kết quả từ agent (`/agent/v1`, hiện do `src/Lis.Server` .NET phục vụ trên cùng CSDL), tiếp nhận + cấp barcode,
sửa danh mục và khoảng tham chiếu, cài đặt cơ sở / barcode / máy in, in tem và phiếu kết quả, hồ sơ HIS.
