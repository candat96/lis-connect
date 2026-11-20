# Hướng dẫn khởi tạo, cài đặt và kết nối LIS Connect (bản web)

Tài liệu này đi từ máy chủ trống tới lúc máy xét nghiệm gửi kết quả vào phiếu. Đọc theo thứ tự; mỗi bước có
cách kiểm tra đã xong chưa. Cập nhật: 16/11/2026.

## 1. Hệ thống gồm những gì

```
Máy xét nghiệm ──COM/TCP──> Agent (Windows, tại phòng khám) ──HTTP(S)──> Server LIS ──> PostgreSQL
                                                                            │
                                                 Trình duyệt (web) ─────────┘
```

| Thành phần | Thư mục | Chạy ở đâu | Cổng mặc định | Việc của nó |
|---|---|---|---|---|
| **Server LIS** (.NET) | `src/Lis.Server/` | máy chủ | 5080 | **Một** server làm tất cả: phục vụ giao diện web, API của web (`/api/...`), API của agent (`/agent/v1/...`), lệnh quản trị, nâng cấp CSDL |
| **Web** | `web/` | build ra tệp tĩnh, server LIS phục vụ | 5186 khi dev | Giao diện: kết quả, danh mục, máy & map kênh, agent, log, nhân sự |
| **Agent** (.NET, dịch vụ Windows) | `src/Lis.Agent/` | máy tính cạnh máy xét nghiệm | — | Mở cổng COM/TCP tới máy, gửi dữ liệu và log về máy chủ |
| **PostgreSQL 18** | — | máy chủ | 5432 | `lis_master` (phòng khám, agent) + mỗi phòng khám một CSDL riêng |

Web chỉ gọi `/api`; agent chỉ gọi `/agent/v1`; cả hai đi vào cùng một server nên chung một địa chỉ.

**Một máy chủ phục vụ nhiều phòng khám.** Mỗi phòng khám có một *mã* (vd `pkstl`) dùng khi đăng nhập web, một
CSDL riêng, tài khoản riêng, agent riêng. Dữ liệu các phòng khám không trộn nhau.

## 2. Cần chuẩn bị

Trên **máy chủ** (Windows hoặc Linux), chọn một trong hai cách:

- **Docker** (khuyên dùng): Docker + Docker Compose. Compose chạy hai dịch vụ: PostgreSQL 18 (image `postgres:18`) và server (image dựng từ `Dockerfile`, đã kèm giao diện web).
- **Chạy trực tiếp:** PostgreSQL **18** trở lên (bắt buộc 18: CSDL dùng hàm `uuidv7()`), .NET SDK 10 và Node.js 20 trở lên để build.

Trên **máy tính đặt agent** (Windows 10/11): không cần cài gì thêm — tệp cài agent tải thẳng từ web (mục 5).

Lấy mã nguồn: `git clone git@github.com:candat96/lis-connect.git` (nhánh `main`).

## 3. Cài server và tạo phòng khám đầu tiên

### 3.1. Cách A — Docker Compose

```bash
cp .env.example .env
```

Sửa `.env`, đổi **mọi** giá trị `CHANGE_ME`:

| Biến | Ý nghĩa |
|---|---|
| `POSTGRES_PASSWORD` | Mật khẩu CSDL; phải trùng với `Password=` trong hai chuỗi kết nối bên dưới |
| `Master__ConnectionString` | Chuỗi kết nối CSDL `lis_master` (giữ `Host=postgres`) |
| `Tenants__ConnectionTemplate` | Mẫu chuỗi kết nối CSDL từng phòng khám (giữ nguyên `{db}`) |
| `Jwt__Secret` | Khoá ký phiên đăng nhập, chuỗi ngẫu nhiên từ 24 ký tự: `openssl rand -base64 32`. Đổi thì mọi người đang đăng nhập phải đăng nhập lại |
| `SETUP_KEY` | Mã thiết lập, nhập trên trang `/thiet-lap` ở bước 3.3. Nếu đặt thì phải **từ 16 ký tự trở lên** (ngắn hơn server không khởi động). Để trống (hoặc còn `CHANGE_ME`) thì server tự sinh mỗi lần chạy và in ra log khi khởi động |
| `PUBLIC_URL` | Địa chỉ công khai agent dùng để nối về, vd `https://lis.phongkham.vn` (không có đường dẫn phía sau). Để trống thì lấy theo địa chỉ trình duyệt đang mở web — nếu mở web bằng `localhost` mà agent ở máy khác thì **phải đặt** |
| `LIS_PORT` | Cổng mở ra máy chủ (mặc định 5080) |
| `LIS_BIND` | Địa chỉ gắn cổng, mặc định `127.0.0.1` (chỉ chính máy chủ truy cập được — dùng khi có reverse proxy, mục 6). Không dùng reverse proxy mà muốn máy khác (web, agent) vào thẳng cổng này thì đặt `LIS_BIND=0.0.0.0` |

**Múi giờ phòng khám.** Mọi logic "ngày địa phương" của server (lọc danh sách mẫu theo ngày, tính tuổi người bệnh để chọn
khoảng tham chiếu, kể cả kết quả máy gửi qua agent) dùng cấu hình `LocalUtcOffsetMinutes` (độ lệch so với UTC tính bằng phút,
mặc định **420** = UTC+7), **không** dùng múi giờ của máy chủ hay container. Phòng khám ở múi giờ khác thì đặt biến môi
trường `LocalUtcOffsetMinutes` (hoặc khoá cùng tên trong `appsettings.json`). `docker-compose.yml` đặt thêm `TZ=Asia/Ho_Chi_Minh`
cho container để giờ trong log khớp giờ phòng khám — chỉ để đọc log, không thay cho `LocalUtcOffsetMinutes`.

```bash
docker compose up -d --build
```

Kiểm tra: mở `http://<máy chủ>:5080/healthz` thấy `{"ok":true}`, và `http://<máy chủ>:5080/` ra giao diện web
(với `LIS_BIND` mặc định thì chỉ mở được ngay trên máy chủ, địa chỉ `http://127.0.0.1:5080`). `docker compose ps` báo
container `server` là `healthy` khi server đã chạy (kiểm tra bằng `LisServer --healthcheck`).
Dữ liệu nằm trong volume `lis-pgdata`; `docker compose down` giữ dữ liệu, `docker compose down -v` **xoá sạch**.

### 3.2. Cách B — chạy trực tiếp

Cần một PostgreSQL 18 có tài khoản quyền tạo database (ví dụ `lis_user`). Build và cấu hình server:

```bash
cd web && npm ci && npm run build && cd ..       # giao diện web -> web/dist
dotnet publish src/Lis.Server -c Release -o publish/server
```

Sửa `publish/server/appsettings.json` (hoặc đặt biến môi trường cùng tên, dùng `__` thay cho dấu `:`):

```json
{
  "Urls": "http://0.0.0.0:5080",
  "Master":  { "ConnectionString": "Host=localhost;Port=5432;Database=lis_master;Username=lis_user;Password=<mật khẩu CSDL>" },
  "Tenants": { "ConnectionTemplate": "Host=localhost;Port=5432;Database={db};Username=lis_user;Password=<mật khẩu CSDL>" },
  "Jwt":     { "Secret": "<chuỗi ngẫu nhiên từ 24 ký tự>" },
  "Setup":   { "Key": "<mã thiết lập>" },
  "Web":     { "Root": "/đường/dẫn/tới/web/dist" }
}
```

- `{db}` giữ nguyên — server tự thay bằng tên CSDL của từng phòng khám.
- `Web:Root` trỏ tới thư mục web đã build. Không đặt thì server tìm thư mục `wwwroot` cạnh tệp chạy; không có thì chỉ chạy API
  (không có giao diện).
- Có thể đặt mã thiết lập bằng biến môi trường `SETUP_KEY`. Mã đã đặt phải từ 16 ký tự trở lên, nếu không server từ chối khởi động.

Chạy server (Windows: `LisServer.exe`, Linux: `./LisServer`), thường trực như dịch vụ (systemd / NSSM):

```bash
LisServer          # mỗi lần khởi động tự dựng lis_master và nâng cấu trúc CSDL của mọi phòng khám
```

Kiểm tra: `http://<máy chủ>:5080/healthz` thấy `{"ok":true}`.

### 3.3. Tạo phòng khám và tài khoản Quản trị đầu tiên (trên web)

Mở `http://<máy chủ>:5080/thiet-lap` (trang đăng nhập cũng có lời nhắc khi máy chủ chưa có phòng khám nào). Điền:

- **Mã thiết lập** — đúng giá trị `SETUP_KEY` / `Setup:Key`. Nếu chưa đặt, đọc trong log của server dòng
  "mã thiết lập phòng khám lần chạy này là …" (Docker: `docker compose logs server`). Nhập sai nhiều lần thì bị làm chậm dần.
- **Mã phòng khám** (chữ thường không dấu, vd `pkstl`) — người dùng gõ mã này khi đăng nhập; **tên phòng khám**.
- **Tài khoản Quản trị đầu tiên**: tên đăng nhập, họ tên, mật khẩu (tối thiểu 8 ký tự).

Bấm tạo: server tự tạo CSDL của phòng khám, dựng toàn bộ bảng và tài khoản Quản trị; trang chuyển về đăng nhập với mã
phòng khám điền sẵn. Các tài khoản sau tạo trên web (mục **Nhân sự**).

> Trang thiết lập chỉ dùng được khi có đúng mã thiết lập; sau khi cấu hình xong nên đổi `SETUP_KEY` thành giá trị
> mới hoặc không công khai địa chỉ `/thiet-lap` ra ngoài.

**Đường cứu hộ bằng dòng lệnh.** Khi cần làm không qua web (chuyển dữ liệu từ máy khác, web chưa lên được), chạy trong
thư mục chứa `LisServer` (Docker: `docker compose exec server dotnet LisServer.dll ...`):

```bash
# Tạo phòng khám: tự tạo CSDL lis_pkstl và dựng toàn bộ bảng
LisServer clinic add --code pkstl --name "Phòng khám STL" --db lis_pkstl

# Tài khoản Quản trị đầu tiên (mật khẩu tối thiểu 8 ký tự)
LisServer user add --clinic pkstl --username admin --name "Nguyễn Văn Quản Trị" --password "<mật khẩu>"

# Xem các phòng khám đã có
LisServer clinic list
```

`--db`: tên CSDL. Nếu CSDL đó **đã có dữ liệu** (chuyển từ bản cài tại chỗ, hoặc nạp từ tệp `.sql`) thì được dùng lại
nguyên vẹn, chỉ nâng cấu trúc lên bản mới. Chuyển dữ liệu từ máy khác:

```bash
createdb -h localhost -p 5432 -U lis_user lis_pkstl
psql -h localhost -p 5432 -U lis_user -d lis_pkstl -f lis-db-2026-11-10.sql
LisServer clinic add --code pkstl --name "Phòng khám STL" --db lis_pkstl
```

### 3.4. Nạp danh mục dịch vụ

Có hai cách:

- **Nhập tay trên web:** mục **Danh mục** → *Thêm dịch vụ* (kèm chỉ số con, khoảng tham chiếu).
- **Nạp hàng loạt từ bản xuất HIS** bằng công cụ dòng lệnh (như đã làm cho `pkstl`):

  ```bash
  dotnet run --project tools/Lis.SeedTool -- seed-clinic \
    --conn "Host=localhost;Port=5432;Database=lis_pkstl;Username=lis_user;Password=<mật khẩu CSDL>" \
    --his-dm DM_DICHVU_KYTHUAT.json --his-rls RLS_DV_KYTHUAT.json \
    --profile tools/clinics/pkstl/clinic.json
  ```

  Lệnh này nạp danh mục, tạo 3 máy, map kênh, và áp đơn vị + khoảng tham chiếu mặc định
  (`tools/clinics/pkstl/don-vi-tham-chieu.sql` — **bản nháp, trưởng khoa cần duyệt**). Thêm `--reset` sẽ **xoá sạch**
  người bệnh, chỉ định, kết quả cũ — chỉ dùng khi khởi tạo.

## 4. Đăng nhập web

Mở `http://<máy chủ>:5080/`. **Đăng nhập:** mã phòng khám (`pkstl`) + tên đăng nhập + mật khẩu vừa tạo ở mục 3.3. Tài khoản
tạo ở app Windows cũ dùng được luôn. Sai 5 lần liên tiếp thì khoá 15 phút; Quản trị đặt lại mật khẩu ở mục **Nhân sự**
sẽ mở khoá.

Khi phát triển giao diện: `cd web && npm ci && npm run dev` (http://localhost:5186) — tự chuyển `/api` sang server ở
`http://localhost:5080` (đổi bằng biến `LIS_BACKEND_URL`).

## 5. Cài agent và kết nối máy xét nghiệm

Làm theo đúng thứ tự: thêm agent trên web → cài agent bằng tệp tải về → khai máy trên web → cắm máy → xem log.
Không phải sửa tệp cấu hình nào trên máy tính cạnh máy xét nghiệm.

### 5.1. Thêm agent trên web

Web → **Agent** → *Thêm agent*, nhập **tên agent** theo vị trí, vd "Máy tính phòng xét nghiệm". Bấm Lưu: web hiện hộp thoại
**Ghép nối** với nút **Tải tệp cài** (tệp tên dạng `LisAgent_LIS1.….exe`), ô mã và nút **Chép mã**. Mã ghép nối
**hết hạn sau 30 phút** và chỉ dùng được **một lần**; hết hạn thì bấm **Ghép nối lại** ở dòng agent để lấy mã mới.

### 5.2. Cài agent lên máy tính cạnh máy xét nghiệm

1. Bấm **Tải tệp cài**. Máy tính cạnh máy xét nghiệm không mở được web thì chép tệp vừa tải sang đó (USB, mạng nội bộ) và
   **giữ nguyên tên tệp** — mã ghép nối nằm trong tên tệp. Tải hai lần, trình duyệt thêm " (1)" vào tên thì vẫn dùng được.
2. Bấm đúp tệp. Nếu Windows hiện cảnh báo **SmartScreen** ("Windows protected your PC"), bấm **More info** rồi **Run anyway**
   (tệp chưa ký số nên Windows chưa biết nguồn gốc).
   *(Ảnh bổ sung sau lần thử Windows thật — task 9.)*
3. Hộp thoại quyền quản trị hiện ra: bấm **Yes** / **Có**. Bấm No / Không thì cửa sổ báo cần quyền quản trị và không cài gì;
   nhờ người có tài khoản quản trị bấm chạy lại.
   *(Ảnh bổ sung sau lần thử Windows thật — task 9.)*
4. Chờ cửa sổ hiện chữ **Xong** ("Xong. Agent đang chạy và đã kết nối tới …"), rồi đóng cửa sổ. Không phải gõ gì.
5. Quay lại web: hộp thoại tự chuyển **Agent đã kết nối ✓ <tên máy>**, cột Trạng thái của bảng hiện **Đang kết nối**
   (thường trong vài giây).

Agent được cài thành dịch vụ Windows **LIS Agent** (tự chạy khi bật máy, không cần ai đăng nhập) trong
`C:\Program Files\LIS Agent`; dữ liệu (kết quả chờ gửi, log thô) ở `C:\ProgramData\LIS Agent`.

**Tệp bị đổi tên** (vd còn `LisAgent.exe`): tệp cài hỏi "Dán mã ghép nối". Quay lại web, bấm **Chép mã** trong hộp thoại Ghép nối
(hoặc **Ghép nối lại** để lấy mã mới), dán vào cửa sổ rồi Enter.

**Mã hết hạn hoặc đã dùng:** tệp cài báo rõ lý do và không cài gì (bản cài cũ, nếu có, vẫn chạy như trước). Lấy mã mới bằng
**Ghép nối lại** trên web.

**Ghép nối lại / gỡ agent:** bấm đúp lại tệp cài (tải tệp mới nếu cần) → menu:

- **1. Ghép nối lại** — dán mã mới lấy từ nút **Ghép nối lại** trên web (dùng khi đổi phòng khám, hoặc nghi lộ khoá). Khoá cũ
  dùng được cho tới khi mã mới được đổi. Đổi sang phòng khám khác thì kết quả đang chờ gửi của phòng khám cũ được cách ly,
  không gửi lên phòng khám mới.
- **2. Gỡ agent** — xoá dịch vụ và thư mục chương trình; dữ liệu ở `ProgramData\LIS Agent` được giữ lại (xoá tay nếu không cần).

**Mạng:** agent nối về địa chỉ `PUBLIC_URL` của server. Máy tính agent và server cùng mạng nội bộ thì `http://<ip>:5080` dùng được;
**ra khỏi mạng nội bộ phải dùng HTTPS** (mục 6) vì mã ghép nối, khoá agent và dữ liệu người bệnh đi qua mạng.

Kiểm tra: web → **Agent**, trong vài giây cột Trạng thái chuyển thành **Đang kết nối**.

### 5.3. Khai máy xét nghiệm trên web

Web → **Máy & map kênh** → *Thêm máy* (hoặc *Sửa máy* nếu máy đã có sẵn từ bước nạp danh mục):

| Ô | Điền gì |
|---|---|
| Giao thức | BC-5380 → **HL7 MLLP**; Access 2 → **ASTM E1394**; AU480 → **Beckman AU** |
| Agent phụ trách | agent vừa tạo |
| Cách nối với máy | xem bảng dưới |
| Đang dùng | bật |

| Cách nối | Khi nào dùng | Điền |
|---|---|---|
| **Cổng COM (RS-232)** | máy nối bằng cáp nối tiếp / USB-COM | tên cổng trên máy tính agent (xem trong Device Manager, vd `COM3`), baud, bit dữ liệu, parity, stop bit — **phải khớp cài đặt trên máy xét nghiệm** |
| **TCP — máy nối vào agent** | máy xét nghiệm là bên gọi (thường gặp ở BC-5380) | cổng agent mở, vd `5100`. Trên máy xét nghiệm khai *IP máy tính agent* + cổng này. Mở cổng đó trong Windows Firewall |
| **TCP — agent nối tới máy** | máy xét nghiệm mở cổng chờ | IP của máy xét nghiệm + cổng |

Lưu xong, agent tự nhận cấu hình **trong vòng 30 giây** và mở kết nối. Không phải ra máy tính agent chỉnh gì.

Riêng **AU480**: trước khi nối, đọc màn hình cài đặt Online trên máy (baud, số bit dữ liệu — AU hay dùng 7 bit,
độ dài mã mẫu, số chữ số của số test) và khai cho khớp.

### 5.4. Kiểm tra bằng Log agent

Web → **Log agent**, chọn máy. Cho máy chạy / gửi lại một mẫu:

- Dòng **Sự kiện** "Đã kết nối …": cổng đã thông.
- Dòng **Máy → LIS**: máy đã gửi dữ liệu (đọc được nội dung bản tin; bật *Hiện hex* để xem từng byte).
- Dòng **LIS → Máy**: agent đã trả lời máy.
- Dòng **Sự kiện** "Nhận kết quả mẫu …: N kênh → …": bản tin đã được xử lý, cho biết bao nhiêu giá trị vào phiếu.

Không thấy dòng nào: xem mục 7.

Từ agent 0.4.0, bản tin của máy còn hiện ở web → **Bản tin thô** (nguyên văn, trạng thái giải mã, kết quả ra được, nút **Giải mã thử** / **Giải mã lại**).
Màn này là nơi xem khi kết quả không lên phiếu; xem [HUONG-DAN/04-xu-ly-su-co.md](HUONG-DAN/04-xu-ly-su-co.md), mục 20.

### 5.5. Map kênh (mã máy gửi → chỉ số)

Web → **Máy & map kênh** → chọn máy:

- **Bảng "mã kênh máy gửi chưa map"** (nền vàng) liệt kê các mã máy đã gửi mà chưa biết là chỉ số gì, kèm giá trị gần
  nhất. Bấm *Map* → chọn chỉ số → Lưu. Kết quả từ lần gửi sau sẽ vào phiếu.
- **Thêm kênh:** tự nhập mã kênh máy + chỉ số khi đã biết bảng mã của máy.
- **"Chưa biết mã kênh — để tự dò"** (dùng cho AU480): khai chỉ số là máy chạy được mà chưa cần mã. Khi máy gửi một
  mã lạ, hệ thống đối chiếu với chỉ định của barcode đó; mã khớp cùng một chỉ số ở **2 mẫu khác nhau trở lên** thì
  tự map và đưa kết quả vào phiếu (chờ duyệt). Mỗi lần tự map có một dòng trong Log agent.

Phòng khám `pkstl` đã có sẵn: BC-5380 17 kênh, Access 2 30 kênh, AU480 45 chỉ số chờ dò.

### 5.6. Chạy thử trọn vòng

Thử một vòng đủ từ máy giả lập tới phiếu kết quả, trước khi cắm máy thật. Cần: agent đã đang kết nối, một máy xét nghiệm đã khai
(mục 5.3), map kênh có sẵn (phòng khám `pkstl`: BC-5380 17 kênh).

1. **Tạo mẫu có barcode.** Tiếp nhận chưa có trên web, nên tạo mẫu giả bằng công cụ dòng lệnh (dữ liệu giả, tên có chữ "Mẫu"/"Thử"):

   ```bash
   dotnet run --project tools/Lis.SeedTool -- seed-demo --conn "Host=localhost;Port=5432;Database=lis_pkstl;Username=lis_user;Password=<mật khẩu CSDL>"
   ```

   Lệnh báo "Chưa cài đặt thông tin cơ sở" khi phòng khám tạo từ web (`/thiet-lap`) vì CSDL chưa có dòng `facility`. Tạm thời thêm bằng `psql`
   rồi chạy lại (việc này sẽ được sửa ở plan E, task E1):

   ```sql
   INSERT INTO facility (id, name) VALUES (1, 'Phòng khám Thử') ON CONFLICT DO NOTHING;
   ```

   Mở web → **Kết quả**, chọn một mẫu chưa có kết quả có chỉ số của máy thử (ví dụ WBC, HGB), ghi lại **barcode** của mẫu.

2. **Cho máy giả lập gửi.** Máy khai kiểu **TCP — máy nối vào agent** cổng `5100` thì:

   ```bash
   dotnet run --project tools/Lis.Simulator -- send --family hl7-mllp --barcode <barcode> --result WBC=7.5 --result HGB=135 --connect 127.0.0.1:5100
   ```

   `--family` là `hl7-mllp` (BC-5380), `astm-e1394` (Access 2) hoặc `beckman-au` (AU480; kênh là số Online Test No., vd `--result 01=5.42`).

   **Chờ 6 giây giữa hai lần `send`** với kiểu TCP máy nối vào agent. Sau khi máy ngắt phiên, agent ghi "Mất kết nối — thử lại sau 5 giây"
   rồi mới mở lại cổng nghe; trong 5 giây đó máy nối vào bị từ chối ("Connection refused"). Máy thật thường giữ kết nối nên ít gặp;
   bộ giả lập mở phiên mới mỗi lần nên gặp. Bị từ chối thì chờ 6 giây rồi chạy lại.

3. **Xem Bản tin thô.** Web → **Bản tin thô**. Trong vài giây có một dòng mới của máy đó, trạng thái **Đã giải mã**, cột **Kết quả** ghi số mẫu / kết quả
   đã ghi. Bấm dòng (hoặc ↑/↓ rồi Enter) để xem nguyên văn bản tin, bảng giải mã và lịch sử giải mã.

4. **Xem Kết quả.** Web → **Kết quả**, mở mẫu đó: WBC, HGB hiện giá trị vừa gửi, trạng thái nhập từ máy.

5. **Duyệt.** Duyệt mẫu (cần quyền duyệt kết quả).

6. **Máy gửi lại thành "chờ thay".** Chạy lại bước 2 với **giá trị khác** (vd `--result WBC=9.1`). Mẫu đã duyệt không bị ghi đè: màn **Kết quả** hiện biểu tượng
   chờ thay ở mẫu đó, thẻ chỉ số có dòng "Máy gửi lại … Bấm Dùng để thay giá trị hiện tại, hoặc ✕ để bỏ" và mẫu **chưa duyệt lại được** ("Mẫu còn giá trị chờ thay") cho tới khi chọn.
   Bấm **Dùng** hoặc ✕, rồi duyệt lại.

7. **(Tuỳ chọn) Giải mã lại.** Ở **Bản tin thô**, mở bản tin, bấm **Giải mã thử** (không ghi gì) rồi **Giải mã lại**. Mẫu đã duyệt chỉ nhận giá trị chờ thay.

Đạt khi cả 7 bước đúng. Không đạt: xem [HUONG-DAN/04-xu-ly-su-co.md](HUONG-DAN/04-xu-ly-su-co.md), mục 20–22.

## 6. Triển khai thật: reverse proxy và HTTPS

Server LIS tự phục vụ cả web, `/api` và `/agent/v1` trên một cổng, nên reverse proxy chỉ cần chuyển tiếp mọi thứ về đó
và lo HTTPS. Mẫu nginx (**chưa chạy thử trên máy chủ thật** — chỉnh theo máy chủ của anh):

```nginx
server {
  listen 443 ssl;
  server_name lis.example.vn;
  # ssl_certificate ...; ssl_certificate_key ...;

  client_max_body_size 8m;

  # Bản tin thô của agent (agent 0.4.0 trở lên): lô tối đa 16 MB byte gốc, base64 phình thêm 1/3, cộng JSON bao quanh.
  # Không đặt (hoặc để 8m) thì lô lớn bị nginx trả 413 trước khi tới server, agent lùi nhịp và bản tin kẹt trên đĩa.
  location /agent/v1/raw-messages {
    client_max_body_size 24m;
    proxy_pass http://127.0.0.1:5080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
  }

  # Giải mã lại tới 500 bản tin chạy trong một yêu cầu: cho chờ lâu hơn mặc định 60 giây kẻo proxy trả 504
  # (việc vẫn chạy tiếp ở server, nhưng người dùng không thấy kết quả).
  location /api/raw-messages/redecode {
    proxy_read_timeout 300s;
    proxy_pass http://127.0.0.1:5080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
  }

  location / {
    proxy_pass http://127.0.0.1:5080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
  }
}
```

`client_max_body_size` của `location /agent/v1/raw-messages` phải **từ 24m trở lên**. Caddy không giới hạn thân yêu cầu theo mặc định; IIS/ARR thì
nâng `maxAllowedContentLength` (tương đương 24 MB trở lên) cho đường này.

Khi đó đặt `PUBLIC_URL=https://lis.example.vn` trong `.env` để mã ghép nối trỏ đúng địa chỉ này. Nên dùng HTTPS: dữ liệu người bệnh,
mã ghép nối và khoá agent đi qua mạng.

**WebSocket cho kênh lệnh.** Web điều khiển agent qua một kênh WebSocket tại `/agent/v1/channel`. Proxy phải cho kênh này đi qua,
nếu không web vẫn chạy bình thường nhưng các nút lệnh của agent bị tắt. Thêm vào khối `server` ở trên, đặt trước `location /`:

```nginx
  location /agent/v1/channel {
    proxy_pass http://127.0.0.1:5080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_read_timeout 120s;
  }
```

Caddy: `reverse_proxy` tự lo WebSocket. IIS: bật tính năng WebSocket Protocol và dùng ARR. Chi tiết ở
[server-agent.md](server-agent.md) (mục "Reverse proxy cho WebSocket").

**Cách kiểm tra:** đăng nhập web bằng tài khoản Quản trị → **Agent** → bấm **Điều khiển** ở một agent đang chạy. Kênh thông thì
**không** có lời báo "Kênh lệnh đang đứt" và các nút **Nạp lại cấu hình**, **Quét cổng COM** bấm được. Bấm **Quét cổng COM** thấy trạng
thái chạy tới **Xong** là kênh dùng được cả hai chiều. Nếu có lời báo "Kênh lệnh đang đứt — các lệnh tạm tắt" trong khi agent
vẫn báo về, nguyên nhân gần như chắc chắn là proxy chưa cho WebSocket qua.

**Địa chỉ của người dùng sau proxy.** Server dùng địa chỉ kết nối để làm chậm dò mã thiết lập và ghi log. Đứng sau proxy thì
địa chỉ đó luôn là của proxy. Muốn dùng địa chỉ thật từ `X-Forwarded-For` / `X-Forwarded-Proto`, đặt
`Proxy:TrustForwardedHeaders=true` (biến môi trường `Proxy__TrustForwardedHeaders=true`; mặc định tắt). **Chỉ bật khi**
server nằm sau proxy tin cậy và cổng 5080 không mở ra ngoài trực tiếp (chỉ proxy gọi được): nếu truy cập thẳng được vào server thì ai cũng gửi được
header giả.

Cần làm thêm trên máy chủ thật: chạy server như dịch vụ (Docker `restart: unless-stopped` đã có sẵn; chạy trực tiếp thì
systemd / NSSM), **sao lưu CSDL hằng ngày** (`pg_dump` từng CSDL phòng khám + `lis_master`), và chỉ mở cổng 443 ra ngoài.

### 6.1. Nâng cấp từ agent 0.3.0 lên 0.4.0 (bắt buộc)

Server mới **bỏ** `POST /agent/v1/readings` và bảng `agent_reading`: agent 0.3.0 trở về trước gửi kết quả đã giải mã vào đường đó nên
sẽ **không gửi được gì** sau khi server nâng cấp. Làm theo thứ tự:

1. Dựng bản agent mới (`bash scripts/publish-agent.sh`, phiên bản `0.4.0` trong `Lis.Agent.csproj`) và đặt vào `AGENT_DIST_DIR` của server.
2. Nâng server lên bản mới (migration tự chạy: bảng `raw_message`, `raw_message_decode`; bỏ `agent_reading` ở CSDL master).
3. Ngay sau đó, từng agent: web → **Agent** → **Điều khiển** → **Cập nhật lên 0.4.0**. Hoặc cài lại bằng tệp cài mới.
4. Trong khoảng giữa bước 2 và 3, máy xét nghiệm vẫn gửi vào agent cũ; agent cũ giữ kết quả đã giải mã trong `cho-gui\<mã máy>\*.json` và
   **không gửi được**. Agent 0.4.0 **không gửi lại** các tệp dạng cũ này, mỗi lần khởi động ghi cảnh báo "Có kết quả chờ gửi dạng cũ". Cách dọn xem
   [HUONG-DAN/04-xu-ly-su-co.md](HUONG-DAN/04-xu-ly-su-co.md), mục 22; kết quả trong các tệp đó phải nhập lại từ máy hoặc bản in.
   Vì vậy nên nâng cấp lúc vắng mẫu và cập nhật agent ngay sau server.
5. Kiểm tra: **Bản tin thô** có dòng mới sau một lần máy gửi; cột **Phiên bản** ở bảng agent là `0.4.0`.

## 7. Khi không chạy

| Hiện tượng | Kiểm tra |
|---|---|
| Web báo "Không kết nối được máy chủ" | Server LIS (5080) có chạy không (`/healthz`); cấu hình proxy |
| Đăng nhập báo sai dù đúng mật khẩu | Mã phòng khám; tài khoản có bị *Tạm khoá* / *Đã ngừng* ở mục Nhân sự |
| Agent hiện "Mất kết nối" / "Đã ghép nối, chưa chạy" | Dịch vụ "LIS Agent" có chạy (`services.msc`); từ máy đó mở được `<PUBLIC_URL>/healthz` |
| Bảng điều khiển agent báo "Kênh lệnh đang đứt" | Reverse proxy chưa cho WebSocket qua `/agent/v1/channel` (mục 6); hoặc agent vừa tắt |
| Agent hiện "Chờ ghép nối" / "Chưa ghép nối" | Chưa chạy tệp cài, hoặc mã hết hạn: bấm **Ghép nối lại** lấy mã mới rồi chạy lại tệp cài |
| Tệp cài báo mã hết hạn / đã được dùng | Mã chỉ sống 30 phút và dùng một lần — bấm **Ghép nối lại** trên web, tải tệp mới |
| Web báo chưa có tệp cài agent | Server chưa có `AGENT_DIST_DIR` (image Docker đã có sẵn; chạy trực tiếp thì `bash scripts/publish-agent.sh` rồi đặt biến này) |
| Máy hiện "Chưa gán agent" | *Sửa máy* → chọn Agent phụ trách |
| Máy hiện "Lỗi kết nối" (COM) | Tên cổng COM; cổng đang bị phần mềm khác giữ (kể cả app LIS Windows cũ) |
| Máy hiện "Đang chờ máy" mãi (TCP) | IP + cổng khai trên máy xét nghiệm; Windows Firewall trên máy tính agent |
| Log có "Máy → LIS" nhưng toàn ký tự lạ | Sai baud / bit dữ liệu / parity, hoặc chọn sai giao thức |
| Log có "Nhận kết quả … không ghép được" | Mẫu chưa có chỉ định trong LIS, hoặc mã kênh chưa map (mục 5.5) |
| Máy gửi mà kết quả không lên phiếu | Web → **Bản tin thô**, lọc **Cần chú ý** (mục 20 của HUONG-DAN/04) |
| Mất mạng | Agent vẫn nhận từ máy và giữ bản tin trên đĩa (`C:\ProgramData\LIS Agent\cho-gui\ban-tin`), có mạng tự gửi lại, không ghi trùng |
| Bản tin kẹt trên đĩa, log agent báo 413 | `client_max_body_size` của `location /agent/v1/raw-messages` dưới 24m (mục 6) |
| Sau khi ghép nối lại bằng khoá mới agent vẫn không gửi | Khởi động lại dịch vụ **LIS Agent** (mục 21 của HUONG-DAN/04) |

Một máy tính chỉ chạy **hoặc** agent **hoặc** app LIS Windows cũ cho cùng một máy xét nghiệm — cả hai cùng giữ một cổng sẽ tranh nhau.

Dữ liệu agent trên máy tính: `C:\ProgramData\LIS Agent` (`rawlog` — log đầy đủ theo ngày; `cho-gui\ban-tin` — bản tin thô chờ gửi, tệp lỗi đổi thành `.json.loi`).

## 8. Chạy lại trên máy phát triển

```bash
docker run -d --name lis-db -p 5432:5432 -e POSTGRES_USER=lis_user -e POSTGRES_PASSWORD=<mật khẩu CSDL> -v lis-data:/var/lib/postgresql postgres:18   # CSDL (lần đầu; lần sau: docker start lis-db)
dotnet run --project src/Lis.Server                            # cần Master__ConnectionString, Tenants__ConnectionTemplate, Jwt__Secret (>= 24 ký tự); cổng 5080
cd web && npm run dev                                          # http://localhost:5186, proxy /api tới 5080
```

Kiểm tra tự động: `dotnet test LisConnect.slnx` (cần Docker đang chạy), `cd web && npm test`.

## 9. Hiện có gì, chưa có gì

**Đã làm trên web:** đăng nhập theo phòng khám · xem, nhập, sửa, duyệt, huỷ duyệt kết quả, xử lý giá trị máy gửi lại,
lịch sử · thêm / sửa / xoá dịch vụ, chỉ số con, khoảng tham chiếu, quy đổi định tính · máy, kết nối, map kênh ·
agent và khoá · log agent · **bản tin thô** (xem nguyên văn, giải mã thử, giải mã lại; máy chủ giải mã, giữ 90 ngày) · nhân sự, đổi mật khẩu.

**Chưa có trên web (vẫn làm ở app Windows cũ, hoặc đang chờ làm):**

- Tiếp nhận: nhập người bệnh, chọn dịch vụ, cấp barcode, in tem.
- In phiếu kết quả.
- Cài đặt thông tin cơ sở, kiểu barcode, máy in. (Phòng khám tạo mới bằng `clinic add` hoặc từ web chưa có dòng `facility` —
  tạm thêm bằng SQL ở mục 5.6, hoặc khai qua app Windows.)
- Nhập danh mục từ Excel; hồ sơ HIS và nhận chỉ định từ HIS.

**Cần kiểm chứng với máy thật:** ba driver (BC-5380, Access 2, AU480) mới chạy với dữ liệu giả lập; cổng COM thật và
dịch vụ Windows và tệp cài agent chưa thử trên Windows thật (chờ task 9); khoảng tham chiếu là bản nháp.
