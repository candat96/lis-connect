# Server online + agent cạnh máy xét nghiệm

Bước 1 của việc chuyển LIS Connect sang mô hình online. App Windows hiện tại vẫn chạy được song song
(cùng CSDL) cho tới khi giao diện web xong.

```
Máy xét nghiệm ──COM/TCP──> Lis.Agent (dịch vụ Windows) ──HTTPS──> Lis.Server ──> PostgreSQL
                                                                      │             ├─ lis_master (phòng khám, agent)
                                                         Web (sắp làm)┘             ├─ CSDL phòng khám A
                                                                                    └─ CSDL phòng khám B
```

- **Một server, nhiều phòng khám:** mỗi phòng khám một CSDL riêng; CSDL `lis_master` giữ danh sách
  phòng khám và khoá agent. Khoá agent xác định luôn phòng khám — agent không chạm được dữ liệu nơi khác.
- **Agent** nối máy, đẩy kết quả lên server, hỏi server khi máy hỏi chỉ định. Mất mạng: kết quả giữ
  trên đĩa (`%ProgramData%\LIS Agent\cho-gui`) và tự gửi lại; gửi lại không ghi trùng.
- Một máy tính chỉ chạy **hoặc** app Windows **hoặc** agent cho cùng một máy xét nghiệm (cùng cổng COM/TCP).

## Server

Cấu hình (`appsettings.json` hoặc biến môi trường `Master__ConnectionString`, `Tenants__ConnectionTemplate`, `Urls`):

```json
{
  "Urls": "http://0.0.0.0:5080",
  "Master":  { "ConnectionString": "Host=...;Database=lis_master;Username=...;Password=..." },
  "Tenants": { "ConnectionTemplate": "Host=...;Database={db};Username=...;Password=..." }
}
```

Cần PostgreSQL 18. Đặt server sau reverse proxy có HTTPS (nginx/caddy) khi chạy trên VPS.

```
LisServer clinic add --code pkstl --name "Phòng khám STL" --db lis_pkstl   # tạo + migrate CSDL (CSDL có sẵn thì dùng lại)
LisServer clinic list
LisServer agent add --clinic pkstl --name "Máy phòng XN" --workstation XN-01  # in khoá agent, chỉ hiện một lần
LisServer                                                                    # chạy server
```

`--workstation` phải trùng "mã máy trạm" đã gán cho máy xét nghiệm (`instrument.workstation_code`).

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
| `POST /status` | trạng thái kết nối từng máy (web sẽ hiển thị) |

## Chưa có

Giao diện web, đăng nhập web, in tem qua agent, đẩy log thô lên server, nhận chỉ định từ HIS.
