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

- **Docker** (khuyên dùng): Docker + Docker Compose. Image có sẵn PostgreSQL 18 và server kèm giao diện web.
- **Chạy trực tiếp:** PostgreSQL **18** trở lên (bắt buộc 18: CSDL dùng hàm `uuidv7()`), .NET SDK 10 và Node.js 20 trở lên để build.

Trên **máy tính đặt agent** (Windows 10/11): không cần cài gì thêm nếu publish agent dạng self-contained.

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
| `SETUP_KEY` | Mã thiết lập, nhập trên trang `/thiet-lap` ở bước 3.3. Để trống thì server tự sinh mỗi lần chạy và ghi vào log |
| `LIS_PORT` | Cổng mở ra máy chủ (mặc định 5080) |

```bash
docker compose up -d --build
```

Kiểm tra: mở `http://<máy chủ>:5080/healthz` thấy `{"ok":true}`, và `http://<máy chủ>:5080/` ra giao diện web.
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
- Có thể đặt mã thiết lập bằng biến môi trường `SETUP_KEY`.

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

Làm theo đúng thứ tự: tạo agent trên web → cài agent → khai máy trên web → cắm máy → xem log.

### 5.1. Tạo agent trên web (lấy khoá)

Web → **Agent** → *Thêm agent*:

- **Tên agent:** theo vị trí, vd "Máy tính phòng xét nghiệm".
- **Mã máy trạm:** mã ngắn, vd `XN-01`.

Bấm Lưu, web hiện **khoá cài đặt một lần duy nhất** kèm sẵn đoạn cấu hình — chép lại ngay. Mất khoá thì bấm
*Khoá mới* (khoá cũ hết hiệu lực).

### 5.2. Cài agent lên máy tính cạnh máy xét nghiệm

Trên máy build:

```bash
dotnet publish src/Lis.Agent -c Release -r win-x64 --self-contained -o publish/agent
```

Chép thư mục `publish/agent` sang máy tính đó, vd `C:\LisAgent`. Sửa `C:\LisAgent\appsettings.json`:

```json
{
  "Server": {
    "Url": "http://<địa chỉ máy chủ>:5080",
    "AgentKey": "lisa_..."
  }
}
```

`Url` trỏ tới **server LIS** (cổng 5080, cùng server với web), hoặc tên miền chung nếu đã đặt reverse proxy (mục 6).

Chạy thử trong cửa sổ dòng lệnh để nhìn thấy thông báo: `C:\LisAgent\LisAgent.exe`. Ổn thì cài làm dịch vụ Windows
(mở PowerShell bằng quyền Administrator) để tự chạy khi bật máy, không cần ai đăng nhập:

```powershell
sc.exe create "LIS Agent" binPath= "C:\LisAgent\LisAgent.exe" start= auto
sc.exe start "LIS Agent"
```

Kiểm tra: web → **Agent**, trong vòng một phút cột Trạng thái chuyển thành **Đang chạy**.

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

### 5.5. Map kênh (mã máy gửi → chỉ số)

Web → **Máy & map kênh** → chọn máy:

- **Bảng "mã kênh máy gửi chưa map"** (nền vàng) liệt kê các mã máy đã gửi mà chưa biết là chỉ số gì, kèm giá trị gần
  nhất. Bấm *Map* → chọn chỉ số → Lưu. Kết quả từ lần gửi sau sẽ vào phiếu.
- **Thêm kênh:** tự nhập mã kênh máy + chỉ số khi đã biết bảng mã của máy.
- **"Chưa biết mã kênh — để tự dò"** (dùng cho AU480): khai chỉ số là máy chạy được mà chưa cần mã. Khi máy gửi một
  mã lạ, hệ thống đối chiếu với chỉ định của barcode đó; mã khớp cùng một chỉ số ở **2 mẫu khác nhau trở lên** thì
  tự map và đưa kết quả vào phiếu (chờ duyệt). Mỗi lần tự map có một dòng trong Log agent.

Phòng khám `pkstl` đã có sẵn: BC-5380 17 kênh, Access 2 30 kênh, AU480 45 chỉ số chờ dò.

## 6. Triển khai thật: reverse proxy và HTTPS

Server LIS tự phục vụ cả web, `/api` và `/agent/v1` trên một cổng, nên reverse proxy chỉ cần chuyển tiếp mọi thứ về đó
và lo HTTPS. Mẫu nginx (**chưa chạy thử trên máy chủ thật** — chỉnh theo máy chủ của anh):

```nginx
server {
  listen 443 ssl;
  server_name lis.example.vn;
  # ssl_certificate ...; ssl_certificate_key ...;

  client_max_body_size 8m;
  location / {
    proxy_pass http://127.0.0.1:5080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
  }
}
```

Khi đó agent đặt `"Url": "https://lis.example.vn"`. Nên dùng HTTPS: dữ liệu người bệnh và khoá agent đi qua mạng.

**Địa chỉ của người dùng sau proxy.** Server dùng địa chỉ kết nối để làm chậm dò mã thiết lập và ghi log. Đứng sau proxy thì
địa chỉ đó luôn là của proxy. Muốn dùng địa chỉ thật từ `X-Forwarded-For` / `X-Forwarded-Proto`, đặt
`Proxy:TrustForwardedHeaders=true` (biến môi trường `Proxy__TrustForwardedHeaders=true`; mặc định tắt). **Chỉ bật khi**
server nằm sau proxy tin cậy và cổng 5080 không mở ra ngoài trực tiếp (chỉ proxy gọi được): nếu truy cập thẳng được vào server thì ai cũng gửi được
header giả.

Cần làm thêm trên máy chủ thật: chạy server như dịch vụ (Docker `restart: unless-stopped` đã có sẵn; chạy trực tiếp thì
systemd / NSSM), **sao lưu CSDL hằng ngày** (`pg_dump` từng CSDL phòng khám + `lis_master`), và chỉ mở cổng 443 ra ngoài.

## 7. Khi không chạy

| Hiện tượng | Kiểm tra |
|---|---|
| Web báo "Không kết nối được máy chủ" | Server LIS (5080) có chạy không (`/healthz`); cấu hình proxy |
| Đăng nhập báo sai dù đúng mật khẩu | Mã phòng khám; tài khoản có bị *Tạm khoá* / *Đã ngừng* ở mục Nhân sự |
| Agent hiện "Không báo về" / "Chưa từng kết nối" | Dịch vụ "LIS Agent" có chạy; `Url` và `AgentKey` trong `appsettings.json`; từ máy đó mở được `<Url>/healthz` |
| Agent chạy thử báo "khoá agent không hợp lệ" | Dán sai / thiếu khoá, hoặc đã bấm *Khoá mới* trên web |
| Máy hiện "Chưa gán agent" | *Sửa máy* → chọn Agent phụ trách |
| Máy hiện "Lỗi kết nối" (COM) | Tên cổng COM; cổng đang bị phần mềm khác giữ (kể cả app LIS Windows cũ) |
| Máy hiện "Đang chờ máy" mãi (TCP) | IP + cổng khai trên máy xét nghiệm; Windows Firewall trên máy tính agent |
| Log có "Máy → LIS" nhưng toàn ký tự lạ | Sai baud / bit dữ liệu / parity, hoặc chọn sai giao thức |
| Log có "Nhận kết quả … không ghép được" | Mẫu chưa có chỉ định trong LIS, hoặc mã kênh chưa map (mục 5.5) |
| Mất mạng | Agent vẫn nhận từ máy và giữ trên đĩa (`C:\ProgramData\LIS Agent\cho-gui`), có mạng tự gửi lại, không ghi trùng |

Một máy tính chỉ chạy **hoặc** agent **hoặc** app LIS Windows cũ cho cùng một máy xét nghiệm — cả hai cùng giữ một cổng sẽ tranh nhau.

Dữ liệu agent trên máy tính: `C:\ProgramData\LIS Agent` (`rawlog` — log đầy đủ theo ngày; `cho-gui` — bản tin chờ gửi).

## 8. Chạy lại trên máy phát triển

```bash
docker start lis-db                                            # CSDL
dotnet run --project src/Lis.Server                            # cần Master__ConnectionString, Tenants__ConnectionTemplate, Jwt__Secret (>= 24 ký tự); cổng 5080
cd web && npm run dev                                          # http://localhost:5186, proxy /api tới 5080
```

Kiểm tra tự động: `dotnet test LisConnect.slnx` (cần Docker đang chạy), `cd web && npm test`.

## 9. Hiện có gì, chưa có gì

**Đã làm trên web:** đăng nhập theo phòng khám · xem, nhập, sửa, duyệt, huỷ duyệt kết quả, xử lý giá trị máy gửi lại,
lịch sử · thêm / sửa / xoá dịch vụ, chỉ số con, khoảng tham chiếu, quy đổi định tính · máy, kết nối, map kênh ·
agent và khoá · log agent · nhân sự, đổi mật khẩu.

**Chưa có trên web (vẫn làm ở app Windows cũ, hoặc đang chờ làm):**

- Tiếp nhận: nhập người bệnh, chọn dịch vụ, cấp barcode, in tem.
- In phiếu kết quả.
- Cài đặt thông tin cơ sở, kiểu barcode, máy in. (Phòng khám tạo mới bằng `clinic add` chưa có thông tin cơ sở —
  hiện phải khai qua app Windows.)
- Nhập danh mục từ Excel; hồ sơ HIS và nhận chỉ định từ HIS.
- Agent hiện tự đọc bản tin rồi gửi kết quả đã đọc kèm dữ liệu thô; chưa chuyển sang "chỉ gửi thô, máy chủ đọc".

**Cần kiểm chứng với máy thật:** ba driver (BC-5380, Access 2, AU480) mới chạy với dữ liệu giả lập; cổng COM thật và
dịch vụ Windows của agent chưa thử; khoảng tham chiếu là bản nháp.
