# Xử lý sự cố

Dành cho IT và người dùng khi gặp lỗi. Khi báo lỗi cho bộ phận hỗ trợ kỹ thuật, luôn kèm: thông
báo lỗi hiện trên màn hình, tệp `loi.log` (mục 8), phiên bản Windows, phiên bản PostgreSQL.

## 1. Không thể kết nối Cơ sở Dữ liệu

1. **PostgreSQL chưa chạy**: `Win+R` → `services.msc` → tìm `postgresql-x64-16` (hoặc phiên bản
   tương ứng) → đảm bảo trạng thái **Running**.
2. **Tường lửa chặn cổng 5432**: Windows Defender Firewall → Cho phép ứng dụng → kiểm tra
   `postgres.exe` đã được cho phép (cả Private và Public nếu CSDL ở máy khác trong LAN).
3. **PostgreSQL trên máy khác chưa mở cho LAN**: xem
   [`01-cai-dat.md`](01-cai-dat.md#6-khi-postgresql-nằm-trên-máy-khác-nhiều-máy-trạm-dùng-chung-một-csdl)
   — cần sửa `pg_hba.conf` và `postgresql.conf`, khởi động lại dịch vụ.
4. **CSDL cloud chưa bật SSL đúng yêu cầu**: tích **Bắt buộc TLS (SSL)** ở tab Kết nối CSDL hoặc
   wizard Bước 1 — xem [`01-cai-dat.md`](01-cai-dat.md#7-csdl-trên-cloud).

## 2. Sai mật khẩu CSDL

- Kiểm tra lại đúng mật khẩu đã đặt cho `lis_user` khi tạo CSDL (mục 5,
  [`01-cai-dat.md`](01-cai-dat.md)).
- Nếu quên, đặt lại bằng `psql` (đăng nhập bằng tài khoản `postgres`):
  ```sql
  ALTER USER lis_user WITH PASSWORD 'mat-khau-moi';
  ```
  rồi sửa lại ở **Cài đặt → Kết nối CSDL** (hoặc wizard nếu đang ở màn đó) với mật khẩu mới.

## 3. Không có quyền tạo extension citext/unaccent

Thông báo dạng `permission denied for ...` khi ứng dụng chạy migration lần đầu. Bình thường không
gặp lỗi này nếu `lis_user` là **chủ (owner)** của CSDL `lis` (xem mục 5,
[`01-cai-dat.md`](01-cai-dat.md)) — `citext`/`unaccent` là trusted extension (PostgreSQL 13+),
chủ CSDL tự tạo được, không cần `SUPERUSER`.

Nếu vẫn gặp, chạy dưới tài khoản `postgres`:

```sql
\c lis
CREATE EXTENSION IF NOT EXISTS citext;
CREATE EXTENSION IF NOT EXISTS unaccent;
\c postgres
```

Lỗi này chỉ chặn đúng bước tạo extension — các bảng migration **đã chạy trước đó trong cùng lần
đó không bị mất**. Sau khi cấp quyền, mở lại ứng dụng và thử kết nối lại — ứng dụng tự chạy tiếp
phần migration còn thiếu, **không cần xóa CSDL làm lại từ đầu**.

## 4. Tệp cấu hình cục bộ (config.json) hỏng

Nếu `config.json` (ở `C:\ProgramData\LIS Connect\config.json`) hỏng — sửa tay sai cú pháp JSON,
hoặc bị copy từ máy khác (mật khẩu CSDL mã hóa bằng DPAPI của Windows, **gắn với từng máy**, copy
sang máy khác không giải mã được) — ứng dụng **tự phát hiện khi khởi động**:

1. Đổi tên tệp cũ thành `config.json.bak` (giữ lại để chẩn đoán, không bị xóa).
2. Hiện thông báo giải thích.
3. Tự mở lại **Trình hướng dẫn cài đặt** để nhập lại thông tin kết nối (như cài mới — xem
   [`01-cai-dat.md`](01-cai-dat.md#8-chạy-lần-đầu--trình-hướng-dẫn-cài-đặt-3-bước)).

Không cần tự tay xóa tệp. Muốn làm lại từ đầu dù tệp không hỏng, có thể tự xóa (hoặc đổi tên)
`config.json` trước khi mở ứng dụng — **dữ liệu trong CSDL không bị ảnh hưởng**, chỉ mất cấu hình
kết nối cục bộ của riêng máy đó.

## 5. Wizard cài đặt bị đóng/tắt giữa chừng

Nếu đóng cửa sổ "Cài đặt lần đầu" trước khi hoàn tất Bước cuối (ví dụ tắt máy, đóng nhầm cửa sổ):
mở lại `LisConnect.exe` — vì `config.json` chưa được ghi (chỉ ghi khi hoàn tất wizard), ứng dụng
**tự mở lại wizard từ Bước 1**. Nếu Bước 1 (Kết nối CSDL) đã từng chạy thành công một phần
migration trước khi bị đóng, các bảng/migration đó vẫn còn trong CSDL (không rollback theo việc
đóng wizard) — chạy lại wizard, nhập đúng lại thông tin kết nối CSDL cũ, ứng dụng tự tiếp tục
đúng migration còn thiếu. Nếu CSDL đã có tài khoản/quản trị được tạo ở lần chạy trước (Bước 2 đã
hoàn tất), wizard lần sau sẽ **không hiện lại Bước 2** — vào thẳng màn Đăng nhập sau Bước 1.

## 6. Không in được

- **Không in tem/phiếu**: kiểm tra **Cài đặt → Máy in** đã chọn đúng tên máy in (ComboBox trên
  Windows liệt kê máy in đã cài ở hệ điều hành); dùng nút **In tem thử** (tab Máy in) để kiểm tra
  riêng khâu in tem không cần tạo mẫu mới.
- Kiểm tra máy in đang bật, có giấy/ruy băng (tem nhiệt), driver máy in đã cài đúng trên Windows
  (Devices and Printers).
- Nếu dùng máy không liệt kê được danh sách máy in (một số môi trường macOS/Linux khi phát
  triển), ô Máy in đổi thành ô nhập tên tay — gõ đúng tên máy in như hệ điều hành đặt, để trống
  nếu muốn dùng máy in mặc định.

## 7. Font không hỗ trợ tiếng Việt khi in

Phiếu kết quả dùng font nhúng sẵn trong ứng dụng (Inter) hỗ trợ tiếng Việt có dấu — nếu ký tự lạ
hoặc ô vuông xuất hiện khi in, thường là do **driver máy in** không xử lý đúng font nhúng trong
PDF, không phải do thiếu font hệ thống. Cài driver máy in mới nhất từ nhà sản xuất; với máy in
tem nhiệt, kiểm tra chế độ in (raster/graphics) được driver hỗ trợ đúng khổ tem đã khai ở **Cài
đặt → Máy in** (Chiều rộng/Chiều cao tem).

## 8. Barcode không quét được

- Kiểm tra lại **Chiều rộng tem (mm)** / **Chiều cao tem (mm)** ở **Cài đặt → Máy in** có đúng
  khổ tem thật đang dùng — tem bị cắt hoặc co giãn sai khổ làm đầu đọc không nhận.
- Dùng nút **In tem thử** để in một tem mẫu, kiểm tra bằng đầu đọc trước khi dùng cho mẫu thật.
- Kiểm tra đầu đọc đang ở đúng chế độ đọc mã **Code 128** (loại barcode LIS Connect dùng để in).
- Nếu tem in mờ/nhạt (đầu in nhiệt cũ, ruy băng gần hết), đầu đọc thường không nhận được — thử
  tem mới.

## 9. Kết quả rơi vào "Chưa ghép"

Xem bảng ba lý do ở [`03-su-dung.md`](03-su-dung.md#1-ba-lý-do-chưa-ghép) (không có barcode, chưa
có chỉ định, kênh chưa map). Cách xử lý nhanh theo lý do:

- **Chưa có chỉ định**: kiểm tra đã **Lưu và in tem** đúng barcode đó ở Tiếp nhận chưa, hoặc gõ
  nhầm/đọc nhầm barcode.
- **Kênh chưa map**: vào **Cài đặt → Máy & map**, thêm map kênh còn thiếu cho đúng máy
  (xem [`02-cau-hinh.md`](02-cau-hinh.md#8-máy-xét-nghiệm--map-kênh)).
- Dòng nào không xử lý được (dữ liệu nhiễu, trùng, không còn giá trị), dùng **Bỏ qua** kèm lý do
  ở màn Chưa ghép — không để tồn đọng danh sách.

(Luồng dữ liệu máy xét nghiệm thật đưa vào "Chưa ghép" là tính năng Đợt 2 — ở Đợt 1 màn này chủ
yếu dùng với dữ liệu demo/thử nghiệm.)

## 10. Ứng dụng khởi động chậm lần đầu

Bình thường — ứng dụng Avalonia khởi tạo UI framework lần chạy đầu tiên sau khi giải nén/cài mới.
Lần thứ 2 trở đi nhanh hơn.

## 11. Xem nhật ký lỗi (loi.log)

Khi ứng dụng gặp lỗi không mong đợi (treo, thông báo lỗi lạ, hoặc "biến mất" không rõ lý do), ứng
dụng ghi thời điểm + chi tiết lỗi (ngoại lệ .NET kèm stack trace) vào:

- Windows: `C:\ProgramData\LIS Connect\loi.log`
- macOS (máy phát triển): `~/Library/Application Support/LIS Connect/loi.log`

Tệp này **không chứa dữ liệu người bệnh** — gửi kèm tệp này khi báo lỗi với bộ phận hỗ trợ kỹ
thuật. Bản Đợt 1 **chưa có màn xem nhật ký trong giao diện ứng dụng** (sẽ có ở Đợt 2 cùng với
log thô của máy xét nghiệm) — phải mở tệp trực tiếp bằng Notepad hoặc trình soạn thảo văn bản.

## 12. Liên hệ hỗ trợ

Khi báo lỗi, chuẩn bị sẵn:

1. Thông báo lỗi chi tiết hiện trên màn hình (chụp ảnh hoặc chép nguyên văn).
2. Tệp `loi.log` (mục 11).
3. Phiên bản Windows (ví dụ: Windows 10 22H2), phiên bản PostgreSQL, bước đang làm khi gặp lỗi.
