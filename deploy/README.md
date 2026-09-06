# Triển khai Core

## Lần đầu

1. Tạo `deploy/.env`:
   ```
   POSTGRES_USER=lis
   POSTGRES_PASSWORD=<mật khẩu mạnh>
   CORS_ORIGINS=https://app.example.vn
   CORE_TAG=0.1.0
   ```
2. Sinh `deploy/pgbouncer/userlist.txt` từ `userlist.txt.example` (xem hướng dẫn
   lấy hash trong file đó).
3. `docker compose -f deploy/docker-compose.prod.yml up -d postgres pgbouncer`
4. Tạo DB quản trị và migrate:
   ```bash
   docker compose -f deploy/docker-compose.prod.yml exec postgres \
     psql -U lis -c "CREATE DATABASE lis_admin"
   docker run --rm --network lis-connect_default \
     -e ADMIN_DATABASE_URL=... -e TENANT_DATABASE_URL_TEMPLATE=... \
     -e DIRECT_DATABASE_URL_TEMPLATE=postgres://lis:...@postgres:5432/{db} \
     lis-connect-core:0.1.0 node apps/core/dist/admin-cli.js migrate-admin
   ```
5. `docker compose -f deploy/docker-compose.prod.yml up -d`

## Mỗi lần lên phiên bản mới

Thứ tự quan trọng: **migrate trước, đổi image sau**.

1. Dựng và đẩy image mới với tag mới.
2. Chạy job migration một lần (KHÔNG phải trong container ứng dụng):
   ```bash
   docker run --rm --network lis-connect_default \
     -e ADMIN_DATABASE_URL=... -e DIRECT_DATABASE_URL_TEMPLATE=... \
     lis-connect-core:<tag mới> node apps/core/dist/admin-cli.js migrate-admin
   docker run --rm --network lis-connect_default \
     -e ADMIN_DATABASE_URL=... -e DIRECT_DATABASE_URL_TEMPLATE=... \
     lis-connect-core:<tag mới> node apps/core/dist/admin-cli.js migrate-all
   ```
   Lệnh `migrate-all` **không dừng ở tenant lỗi đầu tiên** và in ra danh sách
   thất bại. Xử lý từng cơ sở thất bại trước khi đổi image.
3. Đổi `CORE_TAG` trong `.env` rồi `docker compose ... up -d core`.

Vì migration chạy trước, phiên bản Core cũ phải đọc được schema mới trong khoảng
thời gian rollout. Nghĩa là: chỉ thêm cột, không xoá hay đổi tên cột trong cùng một
lần lên phiên bản. Việc xoá cột để sang lần sau, khi mọi replica đã dùng mã mới.

## Thêm một cơ sở khách hàng

```bash
docker compose -f deploy/docker-compose.prod.yml exec core \
  node apps/core/dist/admin-cli.js create-tenant bvA "Bệnh viện A"
docker compose -f deploy/docker-compose.prod.yml exec core \
  node apps/core/dist/admin-cli.js issue-key bvA "PC phòng sinh hóa"
```

Khoá Agent chỉ hiện một lần. Chép ngay và gửi cho người đi cài đặt.

## Sao lưu

Mỗi cơ sở một database, nên sao lưu và khôi phục làm được cho từng khách:

```bash
docker compose -f deploy/docker-compose.prod.yml exec postgres \
  pg_dump -U lis -Fc lis_t_bva > lis_t_bva-$(date +%F).dump
```
