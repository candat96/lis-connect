# Server online + agent cạnh máy xét nghiệm

Mô hình online của LIS Connect. App Windows cũ vẫn chạy được song song (cùng CSDL) cho tới khi web có đủ chức năng.

```
Máy xét nghiệm ──COM/TCP──> Lis.Agent (dịch vụ Windows) ──HTTPS──> Lis.Server ──> PostgreSQL
                                                                      │             ├─ lis_master (phòng khám, agent)
                                                              Web ────┘             ├─ CSDL phòng khám A
                                                                                    └─ CSDL phòng khám B
```

- **Một server, nhiều phòng khám:** mỗi phòng khám một CSDL riêng; CSDL `lis_master` giữ danh sách
  phòng khám và khoá agent. Khoá agent xác định luôn phòng khám — agent không chạm được dữ liệu nơi khác.
- **Agent** nối máy, đẩy kết quả lên server, hỏi server khi máy hỏi chỉ định. Mất mạng: kết quả giữ
  trên đĩa (`%ProgramData%\LIS Agent\cho-gui`) và tự gửi lại; gửi lại không ghi trùng.
- Một máy tính chỉ chạy **hoặc** app Windows **hoặc** agent cho cùng một máy xét nghiệm (cùng cổng COM/TCP).

## Server

Một server .NET (`src/Lis.Server`) làm tất cả: phục vụ giao diện web, `/api` cho web, `/agent/v1` cho agent. Chạy bằng Docker
Compose hoặc trực tiếp — cài đặt từng bước ở [HUONG-DAN-CAI-DAT.md](HUONG-DAN-CAI-DAT.md).

Cấu hình (`appsettings.json` hoặc biến môi trường, dùng `__` thay cho `:`):

| Khoá | Ý nghĩa |
|---|---|
| `Urls` | Địa chỉ nghe, mặc định `http://0.0.0.0:5080` |
| `Master:ConnectionString` | CSDL `lis_master` (danh sách phòng khám, agent) |
| `Tenants:ConnectionTemplate` | Mẫu chuỗi kết nối CSDL từng phòng khám, giữ `{db}` |
| `Jwt:Secret` | Khoá ký phiên đăng nhập web, từ 24 ký tự (thiếu hoặc còn `CHANGE_ME` thì server không chạy) |
| `Setup:Key` (hoặc `SETUP_KEY`) | Mã thiết lập phòng khám mới trên trang `/thiet-lap`. Đã đặt thì phải từ 16 ký tự (ngắn hơn server không chạy); trống hoặc còn `CHANGE_ME` thì server tự sinh mỗi lần chạy và ghi vào log |
| `Web:Root` | Thư mục web đã build; không đặt thì dùng `wwwroot` cạnh tệp chạy nếu có |
| `Proxy:TrustForwardedHeaders` | `true` khi đứng sau reverse proxy tin cậy: dùng `X-Forwarded-For` / `X-Forwarded-Proto` (mặc định tắt) |
| `LocalUtcOffsetMinutes` | Múi giờ phòng khám khi chia "ngày", mặc định 420 (UTC+7) |

Cần PostgreSQL 18. Đặt server sau reverse proxy có HTTPS (nginx/caddy) khi chạy trên VPS. Phòng khám đầu tiên tạo trên web
(`/thiet-lap`); các lệnh dưới là đường cứu hộ khi không dùng được web:

```
LisServer clinic add --code pkstl --name "Phòng khám STL" --db lis_pkstl   # tạo + migrate CSDL (CSDL có sẵn thì dùng lại)
LisServer clinic list
LisServer user add --clinic pkstl --username admin --name "Quản trị" --password "..."   # tài khoản Quản trị đầu tiên
LisServer agent add --clinic pkstl --name "Máy phòng XN" --workstation XN-01  # in khoá agent, chỉ hiện một lần
LisServer                                                                    # chạy server
```

`--workstation` phải trùng "mã máy trạm" đã gán cho máy xét nghiệm (`instrument.workstation_code`). Agent thường tạo ngay trên
web (mục **Agent**) thay vì lệnh `agent add`.

## Agent

`appsettings.json` cạnh `LisAgent.exe`:

```json
{ "Server": { "Url": "https://lis.example.vn", "AgentKey": "lisa_..." } }
```

Chạy thử: `LisAgent.exe`. Cài làm dịch vụ Windows (tự chạy khi bật máy, không cần đăng nhập):

```
sc create "LIS Agent" binPath= "C:\LisAgent\LisAgent.exe" start= auto
sc start "LIS Agent"
```

Dữ liệu agent: `%ProgramData%\LIS Agent` (log thô `rawlog`, kết quả chờ gửi `cho-gui`, cấu hình máy lần
cuối lấy được — dùng khi bật máy mà chưa có mạng).

## API agent (`/agent/v1`, header `X-Agent-Key`)

| | |
|---|---|
| `GET /me` | phòng khám, tên agent, mã máy trạm |
| `GET /instruments` | các máy gán cho máy trạm này (cấu hình kết nối, driver) |
| `POST /readings` | một bản tin kết quả → ghi vào phiếu, tự dò map kênh; gửi lại trả `duplicate: true` |
| `POST /order-queries` | máy hỏi chỉ định theo barcode → các mã kênh cần chạy |
| `POST /status` | trạng thái kết nối từng máy (web hiển thị) |
| `POST /logs` | log thô: mọi byte đi/về với máy (hex) + sự kiện, gửi theo lô mỗi giây; server giữ 14 ngày |

## Cài đặt trên web

Máy xét nghiệm (giao thức, cổng COM/TCP, agent phụ trách), map kênh, và agent (tạo, lấy khoá, cấp khoá mới) đều làm trên web
(`../web`, do chính server LIS phục vụ). Agent chỉ cần `Server:Url` + `Server:AgentKey`; cấu hình máy nó tự lấy về mỗi 30 giây.
Chỉ cần một tên miền / địa chỉ cho cả web, `/api` và `/agent/v1` — cùng trỏ vào server LIS (reverse proxy xem mục 6 của hướng dẫn cài đặt).

## Chưa có

In tem qua agent, nhận chỉ định từ HIS.
