# Hướng dẫn chạy thử LIS Connect trên Windows

Tài liệu này hướng dẫn cài đặt và chạy LIS Connect (phần mềm Hệ thống thông tin phòng xét nghiệm) trên máy tính Windows x64.

**Phiên bản**: Đợt 1 (Nền tảng)  
**Ngày cập nhật**: 2026-11-06

---

## Yêu cầu hệ thống

- **Windows**: 10 hoặc cao hơn (x64)
- **RAM**: 4 GB tối thiểu (8 GB khuyến nghị)
- **Dung lượng ổ cứng**: 2 GB trống
- **PostgreSQL**: 16.0 hoặc cao hơn
- **.NET** (tuỳ chọn): Chỉ cần nếu build từ mã nguồn

---

## Cách 1: Dùng bản phân phối (khuyến nghị) 

*Nhanh nhất — không cần cài .NET SDK.*

### Bước 1-3: Chuẩn bị

[Tiếp tục từ "Bước 1: Tải và giải nén" dưới đây]

## Cách 2: Build từ mã nguồn

*Cần cài .NET 10 SDK; tốn thêm 5-10 phút.*

### Cài đặt cần thiết

1. **Git**: Nếu chưa có, tải từ https://git-scm.com/download/win
2. **.NET 10 SDK**: Tải từ https://dotnet.microsoft.com/en-us/download/dotnet/10.0
   - Chọn **Windows x64 Installer**
   - Cài đặt bình thường (được thêm vào `PATH` tự động)
3. **PowerShell 5.1+**: Có sẵn trên Windows 10+

### Tải và build

1. Mở **Command Prompt** hoặc **PowerShell**
2. Chạy các lệnh sau:
   ```bash
   git clone https://github.com/candat96/lis-connect.git
   cd lis-connect
   git checkout dot-1-nen-tang
   powershell -ExecutionPolicy Bypass -File scripts\build-windows.ps1
   ```
3. Chờ quá trình build hoàn thành (~5 phút tuỳ kết nối mạng)
4. Tệp `dist\LisConnect-win-x64.zip` sẵn sàng, giải nén theo "Bước 1" dưới đây

---

## Cách 1: Dùng bản phân phối (khuyến nghị)

### Bước 1: Tải và giải nén

1. Tải tệp `LisConnect-win-x64.zip` từ CI hoặc bản phát hành
2. Giải nén vào thư mục bạn chọn, ví dụ: `C:\LisConnect`
3. Kết quả cấu trúc thư mục:
   ```
   C:\LisConnect\
   ├── app\
   │   ├── LisConnect.exe
   │   └── (các tệp hỗ trợ)
   ├── seedtool\
   │   └── Lis.SeedTool.exe
   ├── mau-danh-muc.xlsx
   └── CHAY-THU-WINDOWS.md
   ```

### Bước 2: Cài đặt PostgreSQL 16+

1. Tải bộ cài chính thức từ: https://www.postgresql.org/download/windows/
   - Chọn phiên bản **PostgreSQL 16.x** (hoặc cao hơn)
   - Chọn **Interactive installer**

2. Chạy trình cài đặt:
   - Chọn đường dẫn cài đặt (mặc định: `C:\Program Files\PostgreSQL\16`)
   - Nhập mật khẩu cho tài khoản `postgres` (ghi nhớ mật khẩu này)
   - Cổng mặc định: **5432**
   - Locale: **Vietnamese (Việt Nam)** hoặc **UTF-8**

3. **Rất quan trọng**: Ghi nhớ mật khẩu `postgres` — bạn sẽ cần để tạo cơ sở dữ liệu

### Bước 3: Tạo cơ sở dữ liệu và người dùng

Mở **Command Prompt** hoặc **PowerShell** (không phải Administrator):

```bash
"C:\Program Files\PostgreSQL\16\bin\psql" -U postgres -h localhost
```

Nhập mật khẩu khi được yêu cầu. Bạn sẽ thấy dấu nhắc `postgres=#`.

Chạy các lệnh SQL sau:

```sql
-- Tạo cơ sở dữ liệu
CREATE DATABASE lis;

-- Tạo người dùng LIS
CREATE USER lis_user WITH PASSWORD 'mat-khau-lis';

-- Gán quyền sở hữu cơ sở dữ liệu cho lis_user
ALTER DATABASE lis OWNER TO lis_user;

-- Thoát
\q
```

**Ghi chú về quyền extension**: 
- `lis_user` là chủ của database `lis` nên có thể tạo extension `citext` và `unaccent` (trusted extensions, PostgreSQL 13+)
- Nếu gặp lỗi `permission denied`, chạy các lệnh này dưới tài khoản `postgres`:
  ```sql
  \c lis
  CREATE EXTENSION IF NOT EXISTS citext;
  CREATE EXTENSION IF NOT EXISTS unaccent;
  \c postgres
  ```

### Bước 4: Chạy ứng dụng lần đầu

Mở `C:\LisConnect\app\` và nhấp đôi vào **LisConnect.exe**.

**Lần chạy đầu tiên** sẽ hiện cửa sổ "Cài đặt lần đầu", gồm 3 bước (chung một nút **Tiếp theo** ở góc dưới bên phải để qua bước sau):

1. **Bước 1 — Kết nối CSDL**:
   - Máy chủ: `localhost`
   - Cổng: `5432`
   - Tên CSDL: `lis`
   - Tài khoản: `lis_user`
   - Mật khẩu: `mat-khau-lis` (mật khẩu bạn vừa tạo)
   - Mã máy trạm (vd `XN-01`)
   - Nhấp **Thử kết nối** để kiểm tra, thấy thông báo kết nối thành công rồi mới nhấp **Tiếp theo**

2. **Bước 2 — Tài khoản quản trị đầu tiên** (chỉ hiện khi CSDL chưa có tài khoản nào):
   - Tên đăng nhập: (tên người dùng của bạn, ví dụ: `admin`)
   - Họ tên: (tên đầy đủ, ví dụ: `Quản trị viên hệ thống`)
   - Mật khẩu (tối thiểu 8 ký tự)
   - Nhập lại mật khẩu
   - Nhấp **Tiếp theo**

3. **Bước 3 — Thông tin cơ sở** (chỉ hiện khi CSDL vừa được tạo mới):
   - Tên cơ sở: (tên bệnh viện/phòng xét nghiệm)
   - Địa chỉ, Điện thoại (tuỳ chọn)
   - Nhấp **Tiếp theo** để hoàn tất

Ứng dụng sẽ tự động tạo cấu trúc bảng và chạy migrations.

---

## Bước 5: Đăng nhập và cấu hình

1. **Đăng nhập** với tài khoản quản trị vừa tạo
2. **Cài đặt** → **Kết nối CSDL**: xem lại các thiết lập kết nối, sửa khi cần
3. **Cài đặt** → **Kết nối HIS** (tuỳ chọn, chỉ hiện cho tài khoản quản trị):
   - Tích **Bật kết nối HIS**
   - Nhấp **Dùng mẫu HIS-PROMAX** để tự điền (hoặc tự nhập **Địa chỉ máy chủ (base URL)**, **Kiểu xác thực**, tài khoản/khóa API)
   - Nhấp **Thử kết nối**, rồi **Lưu**

---

## Bước 6: Nạp dữ liệu mẫu

### 6.1 Nạp danh mục

1. Mở ứng dụng, đăng nhập bằng tài khoản quản trị (chỉ quản trị mới thấy nút này)
2. Vào mục **Danh mục**, nhấp nút **📥 Nhập Excel…**
3. Chọn tệp `mau-danh-muc.xlsx` (trong thư mục `C:\LisConnect\`)
4. Ứng dụng kiểm tra trước (không ghi gì) và hiện panel kết quả:
   - Không lỗi: hiện "Sẽ tạo X, cập nhật Y" — nhấp **Nhập thật** để ghi vào CSDL
   - Có lỗi: hiện danh sách lỗi theo từng dòng (vd "Sheet DichVu dòng 5: thiếu Mã") — nút **Nhập thật** bị mờ, không ghi gì; sửa tệp Excel rồi nhập lại
5. Sau khi nhấp **Nhập thật**, panel hiện "Đã tạo X, cập nhật Y" và danh sách dịch vụ tự nạp lại; nhấp **Đóng**
   - Nhấp **Hủy** ở bất kỳ lúc nào trước khi nhập thật để đóng panel, không ghi gì
   - Sẽ nạp: chỉ số xét nghiệm, nhóm dịch vụ, thông số tham chiếu, ánh xạ máy, v.v.

### 6.2 Tạo dữ liệu demo (tuỳ chọn)

Để kiểm thử, bạn có thể tạo mẫu và kết quả giả lập:

1. Mở **Command Prompt** / **PowerShell**
2. Chuyển đến thư mục seedtool:
   ```bash
   cd C:\LisConnect\seedtool\
   ```
3. Chạy lệnh tạo dữ liệu demo (3 ngày qua):
   ```bash
   Lis.SeedTool.exe seed-demo `
     --conn "Host=localhost;Port=5432;Database=lis;Username=lis_user;Password=mat-khau-lis" `
     --days 3
   ```

Dữ liệu sẽ bao gồm:
- Bệnh nhân mẫu (tên chứa "Mẫu" hoặc "Thử")
- Phiếu xét nghiệm
- Kết quả xét nghiệm (hoàn thành hoặc chờ duyệt)

---

## Bước 7: Cấu hình máy in

### Máy in tem (Barcode)

1. **Cài đặt** → **Máy in**: chọn **Máy in tem (tên)** — máy in công suất nhỏ (A6 hoặc thermal), đặt **Chiều rộng tem (mm)**/**Chiều cao tem (mm)**, nhấp **Lưu**
   - Có thể nhấp **In tem thử** để kiểm tra
2. **Cài đặt** → **Barcode & tem**: kiểm tra định dạng barcode
   - Tiền tố: `9` (mặc định)
   - Mẫu ngày: `yyMMdd`
   - Số chữ số thứ tự: `3`
   - Xem trước ở "Xem trước barcode mẫu", nhấp **Lưu**

### Máy in phiếu (Kết quả)

1. **Cài đặt** → **Máy in**: chọn **Máy in phiếu kết quả (tên)** — máy in A4 bình thường, nhấp **Lưu**

---

## Gỡ lỗi thường gặp

### Lỗi: "Không thể kết nối Cơ sở Dữ liệu"

**Nguyên nhân và giải pháp**:

1. **PostgreSQL chưa chạy**:
   - Kiểm tra: Nhấn `Win+R`, gõ `services.msc`
   - Tìm dịch vụ `postgresql-x64-16` (hoặc phiên bản tương ứng)
   - Đảm bảo trạng thái là **Running** (xanh lá)

2. **Tường lửa chặn cổng 5432**:
   - Mở **Windows Defender Firewall** → **Cho phép ứng dụng**
   - Kiểm tra PostgreSQL có trong danh sách không
   - Nếu không, thêm: `C:\Program Files\PostgreSQL\16\bin\postgres.exe`
   - Cho phép cả mạng Private lẫn Public

3. **PostgreSQL trên máy khác**:
   - Nếu DB trên máy tính khác trong mạng LAN:
   - Sửa `pg_hba.conf` (thường ở `C:\Program Files\PostgreSQL\16\data\`):
     ```
     # Cho phép kết nối từ mạng LAN (192.168.x.x)
     host    all         all         192.168.0.0/16    md5
     ```
   - Sửa `postgresql.conf`:
     ```
     listen_addresses = '*'
     ```
   - Khởi động lại dịch vụ PostgreSQL

4. **Sai mật khẩu**:
   - Kiểm tra lại mật khẩu `lis_user` bạn vừa tạo
   - Nếu quên, đặt lại:
     ```sql
     ALTER USER lis_user WITH PASSWORD 'mat-khau-moi';
     ```

### Lỗi: "Không có quyền tạo extension citext/unaccent"

**Giải pháp**:

Bình thường không gặp lỗi này — `lis_user` là chủ CSDL `lis` (xem Bước 3) nên tự tạo được
`citext`/`unaccent` (trusted extensions, PostgreSQL 13+), không cần quyền `SUPERUSER`. Nếu vẫn
gặp lỗi, tạo extension dưới tài khoản `postgres` trước khi chạy ứng dụng:
```sql
\c lis
CREATE EXTENSION IF NOT EXISTS citext;
CREATE EXTENSION IF NOT EXISTS unaccent;
\c postgres
```

Nếu lỗi này xảy ra ngay trong Trình hướng dẫn cài đặt (bước "Kết nối CSDL"), việc tạo bảng chỉ
lỗi ở đúng phần script bị thiếu quyền — các bảng đã tạo trước đó trong cùng lần chạy **không bị
mất**. Sau khi cấp quyền (hoặc tạo extension bằng tay như trên), chỉ cần mở lại ứng dụng và thử
kết nối lại — ứng dụng tự chạy tiếp phần migration còn thiếu, không cần xóa CSDL làm lại từ đầu.

### Lỗi: "Font không hỗ trợ tiếng Việt" khi in

- Đảm bảo hệ thống đã cài đặt font hỗ trợ tiếng Việt (ví dụ: Segoe UI, Times New Roman)
- Máy in: Cài đặt driver mới nhất từ nhà sản xuất

### Ứng dụng khởi động chậm lần đầu

- Điều này bình thường — ứng dụng Avalonia khởi tạo UI framework
- Lần thứ 2 trở đi sẽ nhanh hơn

### Xem nhật ký lỗi (loi.log)

Nếu ứng dụng gặp lỗi không mong đợi (treo, thông báo lỗi lạ, hoặc "biến mất" không rõ lý do),
ứng dụng ghi lại thời điểm + chi tiết lỗi vào tệp nhật ký ở thư mục dữ liệu cục bộ:

- Windows: `C:\ProgramData\LIS Connect\loi.log`
- macOS (máy phát triển): `~/Library/Application Support/LIS Connect/loi.log`

Tệp này **không chứa dữ liệu người bệnh**, chỉ có thời điểm và nội dung ngoại lệ (.NET
exception, kèm stack trace) — gửi kèm tệp này khi báo lỗi với bộ phận hỗ trợ kỹ thuật.

### Đặt lại cấu hình cục bộ (config.json)

Cấu hình kết nối CSDL + mã máy trạm lưu ở cùng thư mục trên, tệp `config.json`:

- Windows: `C:\ProgramData\LIS Connect\config.json`
- macOS (máy phát triển): `~/Library/Application Support/LIS Connect/config.json`

Nếu tệp này hỏng (sửa tay sai cú pháp JSON, hoặc copy từ máy khác — mật khẩu CSDL mã hóa bằng
DPAPI của Windows, **gắn với từng máy**, không copy sang máy khác được), ứng dụng tự phát hiện,
đổi tên tệp cũ thành `config.json.bak` (giữ lại để chẩn đoán), hiện một thông báo giải thích,
rồi tự mở lại **Trình hướng dẫn cài đặt** để nhập lại thông tin kết nối. Không cần tự tay xóa
tệp — nhưng nếu muốn làm lại từ đầu, có thể tự xóa (hoặc đổi tên) `config.json` trước khi mở
ứng dụng.

---

## Chưa có ở bản thử

Các tính năng sau sẽ được thêm vào các phiên bản tiếp theo:

**Đợt 2** (Q1 2027):
- **Dịch vụ nền Windows**: Tự động chạy ứng dụng khi hệ thống khởi động
- **Kết nối máy xét nghiệm**: Đồng bộ kết quả từ máy xét nghiệm qua LAN/USB
- **Log thô (Raw Log)**: Ghi nhật ký chi tiết cho mục đích gỡ lỗi

**Đợt 3** (Q2 2027):
- **Đồng bộ HIS thực**: Kết nối và đồng bộ dữ liệu với HIS chính thức

**Đợt 5** (Q3 2027):
- **Bộ cài MSI**: Tệp cài đặt chuyên nghiệp (Windows Installer)

---

## Hỗ trợ thêm

Nếu gặp vấn đề:

1. Ghi lại **thông báo lỗi chi tiết** hiện trên màn hình, kèm tệp `loi.log` (xem mục "Xem nhật ký lỗi (loi.log)" ở trên) — bản đợt 1 chưa có màn xem nhật ký trong giao diện ứng dụng (xem mục "Chưa có ở bản thử")
2. Liên hệ bộ phận hỗ trợ kỹ thuật cùng:
   - Phiên bản Windows (ví dụ: Windows 10 22H2)
   - Phiên bản PostgreSQL
   - Thông báo lỗi từ ứng dụng

---

## Các lệnh hữu ích

### Kết nối trực tiếp đến PostgreSQL

```bash
"C:\Program Files\PostgreSQL\16\bin\psql" -U lis_user -h localhost -d lis
```

### Sao lưu cơ sở dữ liệu

```bash
"C:\Program Files\PostgreSQL\16\bin\pg_dump" -U lis_user -h localhost lis > lis_backup.sql
```

### Khôi phục từ sao lưu

```bash
"C:\Program Files\PostgreSQL\16\bin\psql" -U lis_user -h localhost lis < lis_backup.sql
```

---

**LIS Connect — Hệ thống Quản lý Phòng Xét Nghiệm**  
Phát triển dành cho cơ sở y tế Việt Nam
