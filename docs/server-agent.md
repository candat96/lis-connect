# Server online + agent cạnh máy xét nghiệm

Mô hình online của LIS Connect. App Windows cũ vẫn chạy được song song (cùng CSDL) cho tới khi web có đủ chức năng.

```
Máy xét nghiệm ──COM/TCP──> Lis.Agent (dịch vụ Windows) ──HTTPS──> Lis.Server ──> PostgreSQL
                                                                      │             ├─ lis_master (phòng khám, agent)
                                                              Web ────┘             ├─ CSDL phòng khám A
                                                                                    └─ CSDL phòng khám B
```

- **Một server, nhiều phòng khám:** mỗi phòng khám một CSDL riêng; CSDL `lis_master` giữ danh sách
  phòng khám và agent (chỉ lưu bản băm của khoá). Khoá agent xác định luôn phòng khám — agent không chạm được dữ liệu nơi khác.
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
| `PublicUrl` (hoặc `PUBLIC_URL`) | Địa chỉ công khai agent dùng để nối về, vd `https://lis.phongkham.vn` — được đưa vào mã ghép nối. Phải là địa chỉ tuyệt đối `http`/`https`, không có `?query` hay `#fragment`. Để trống thì lấy theo địa chỉ trình duyệt đang mở web |
| `Agent:DistDir` (hoặc `AGENT_DIST_DIR`) | Thư mục chứa tệp cài `LisAgent.exe` và `version.txt` để web cho tải. Image Docker đã đặt sẵn `/app/agent`; chạy trực tiếp thì tạo bằng `scripts/publish-agent.sh` rồi trỏ vào đó. Không đặt thì web báo chưa có tệp cài |
| `Proxy:TrustForwardedHeaders` | `true` khi đứng sau reverse proxy tin cậy: dùng `X-Forwarded-For` / `X-Forwarded-Proto` (mặc định tắt) |
| `LocalUtcOffsetMinutes` | Múi giờ phòng khám khi chia "ngày", mặc định 420 (UTC+7) |

Cần PostgreSQL 18. Đặt server sau reverse proxy có HTTPS (nginx/caddy) khi chạy trên VPS. Phòng khám đầu tiên tạo trên web
(`/thiet-lap`); các lệnh dưới là đường cứu hộ khi không dùng được web:

```
LisServer clinic add --code pkstl --name "Phòng khám STL" --db lis_pkstl   # tạo + migrate CSDL (CSDL có sẵn thì dùng lại)
LisServer clinic list
LisServer user add --clinic pkstl --username admin --name "Quản trị" --password "..."   # tài khoản Quản trị đầu tiên
LisServer agent add --clinic pkstl --name "Máy phòng XN" --workstation XN-01  # cứu hộ: in khoá agent, chỉ hiện một lần
LisServer                                                                    # chạy server
```

Agent thường tạo và ghép nối ngay trên web (mục **Agent**, xem bên dưới), không cần lệnh `agent add`. `agent add` chỉ là đường
cứu hộ khi không dùng được web: nó in khoá agent (một lần), `--workstation` phải trùng "mã máy trạm" đã gán cho máy xét nghiệm
(`instrument.workstation_code`); khoá này đặt cho agent chạy console bằng biến `LISAGENT_*` (xem dưới).

## Agent

Agent là một tệp `LisAgent.exe` (Windows x64, tự chứa, một tệp ~40 MB, không cần cài .NET). Nhân viên **không sửa tệp cấu hình**:
mọi thứ đi qua mã ghép nối.

### Luồng ghép nối

1. Web → **Agent** → *Thêm agent* (chỉ nhập tên) → hộp thoại **Ghép nối** hiện nút **Tải tệp cài** (tệp tên `LisAgent_LIS1.….exe`, mã
   nằm trong tên tệp) và ô mã kèm nút **Chép mã**. Mã **hết hạn sau 30 phút** và dùng **một lần**.
2. Chép tệp sang máy tính cạnh máy xét nghiệm (giữ nguyên tên) và bấm đúp. Tệp tự xin quyền quản trị, cài dịch vụ Windows
   **LIS Agent** vào `%ProgramFiles%\LIS Agent`, dữ liệu ở `%ProgramData%\LIS Agent`, rồi đổi mã lấy khoá agent từ server.
   Khoá agent chỉ đi qua mạng đúng một lần (phản hồi của `/agent/v1/pair`), được mã hoá DPAPI theo máy trong
   `%ProgramData%\LIS Agent\pairing.json`; server chỉ giữ SHA-256 của khoá và của bí mật ghép nối.
3. Web tự chuyển "Agent đã kết nối ✓ <tên máy>". Bảng Agent hiện trạng thái *Đang kết nối* / *Mất kết nối* /
   *Đã ghép nối, chưa chạy* / *Chờ ghép nối* / *Chưa ghép nối*; nút **Ghép nối lại** cấp mã mới (khoá cũ dùng được cho tới khi
   mã mới được đổi, sau đó bị từ chối 401).
4. Chạy tệp lần hai → menu: Ghép nối lại / Gỡ agent / Thoát. Ghép nối lại sang **phòng khám khác** thì kết quả đang chờ gửi
   (`cho-gui`) của phòng khám cũ được chuyển sang thư mục cách ly, không bao giờ gửi lên phòng khám mới. Gỡ agent xoá dịch vụ
   và thư mục chương trình, giữ lại dữ liệu ở `%ProgramData%\LIS Agent`.
5. Tệp bị trình duyệt thêm ` (1)` vẫn đọc được mã; tệp bị đổi tên hẳn thì tệp cài hỏi mã — dán mã từ nút **Chép mã**.

### Dòng lệnh

```
LisAgent.exe run                    # chạy ngay trong cửa sổ dòng lệnh (dịch vụ Windows tự nhận ra, không cần gõ lệnh này)
LisAgent.exe pair <mã>              # đổi mã lấy khoá, lưu ghép nối, không cài dịch vụ
LisAgent.exe install [--code <mã>]  # cài dịch vụ (cần quyền quản trị; không có mã thì đọc từ tên tệp hoặc hỏi)
LisAgent.exe uninstall              # gỡ dịch vụ và thư mục chương trình
```

Bấm đúp không tham số trên Windows = cài lần đầu (lấy mã từ tên tệp) hoặc hiện menu nếu đã cài; trên hệ điều hành khác không tham số = `run`. Mã là bí mật nên `install --code` không tự nâng
quyền: phải mở cửa sổ dòng lệnh bằng quyền quản trị.

### Chạy khi phát triển

Biến môi trường `LISAGENT_Server__Url` **và** `LISAGENT_Server__AgentKey` (đủ cả hai) thắng cấu hình đã ghép nối — **chỉ dùng
khi phát triển** để chạy console (`LisAgent.exe run`) với khoá lấy từ `LisServer agent add`. Không dùng ở máy phòng khám.

### Đóng gói tệp cài

`bash scripts/publish-agent.sh [thư-mục-ra]` (mặc định `dist/agent`) publish win-x64 tự chứa một tệp nén thành `LisAgent.exe`
và ghi `version.txt` (lấy từ `<Version>` của `Lis.Agent.csproj`). Image Docker của server đã có sẵn hai tệp này ở `/app/agent`
(`AGENT_DIST_DIR=/app/agent`), nên dựng image xong là web cho tải được tệp cài.

Dữ liệu agent: `%ProgramData%\LIS Agent` (log thô `rawlog`, kết quả chờ gửi `cho-gui`, cấu hình máy lần
cuối lấy được — dùng khi bật máy mà chưa có mạng).

## Kênh lệnh (điều khiển agent từ web)

Ngoài các đường `/agent/v1` ở trên, agent giữ thêm một kênh WebSocket tới server để nhận lệnh từ web (nạp lại cấu hình, thử cổng COM,
cập nhật…). Kênh chỉ chở **lệnh và kết quả lệnh**. Kết quả xét nghiệm, trạng thái máy và log vẫn đi qua HTTP như cũ, nên kênh
đứt không làm mất kết quả và agent vẫn chạy máy xét nghiệm với cấu hình đã lưu.

- **Địa chỉ:** `/agent/v1/channel`, cùng địa chỉ server trong mã ghép nối (`http` thành `ws`, `https` thành `wss`).
- **Xác thực:** khoá agent nằm ở header, không nằm trong URL. Khoá bị thu hồi thì bắt tay WebSocket bị từ chối.
- **Nối lại:** mất kênh thì agent tự nối lại sau 1, 2, 4, 8, 16, 30, 60 giây (lệch ngẫu nhiên khoảng ±20%); nối được thì về lại 1 giây.
- **Ai ra lệnh:** chỉ vai trò Quản trị. Quản trị, Kỹ thuật viên và Trưởng khoa xem được Lịch sử lệnh.
- **Sửa máy xét nghiệm:** thêm, sửa, xoá máy trên web thì server báo agent nạp lại cấu hình ngay qua kênh, không chờ 30 giây.

### Các lệnh

| Lệnh | Việc làm | Thời gian tối đa |
|---|---|---|
| `reload-config` | Nạp lại cấu hình máy từ server, chạy lại các máy theo cấu hình mới | 2 phút |
| `scan-ports` | Quét cổng COM trên máy tính agent: tên cổng, thiết bị, cổng nào máy đang dùng, cổng nào phần mềm khác giữ | 2 phút |
| `test-port` | Mở một cổng COM, chỉ nghe, không gửi gì; trả dữ liệu nhận được. Cổng đang có máy dùng thì tạm ngắt máy đó rồi tự nối lại | số giây nghe + 30 giây |
| `reconnect-instrument` | Đóng rồi mở lại kết nối của một máy xét nghiệm | 2 phút |
| `flush-pending` | Gửi ngay các bản tin đang chờ trong `cho-gui` | 2 phút |
| `restart` | Khởi động lại dịch vụ. Chỉ chạy khi agent là dịch vụ Windows (Recovery tự bật lại sau khoảng 5 giây) | 2 phút |
| `update` | Tải bản mới từ server, kiểm SHA-256, thay tệp, khởi động lại. Tải tối đa 5 phút | 10 phút |
| `revoke` | Thu hồi agent: server khoá khoá agent ngay, agent dừng dịch vụ và đặt kiểu khởi động Disabled | 2 phút |

`restart` khi agent chạy console báo lỗi "Agent không chạy như dịch vụ nên không tự khởi động lại".

### Hàng đợi và trạng thái lệnh

Agent chưa nối kênh thì lệnh nằm trong hàng đợi tối đa **60 giây**. Agent nối lại kịp thì nhận lệnh; quá 60 giây lệnh thành
**Hết hạn**, không xếp hàng lâu hơn. Riêng `revoke` có hiệu lực ngay ở server dù agent đang tắt.

| Trạng thái (trên web) | Nghĩa |
|---|---|
| Chờ agent | Agent chưa nối kênh, lệnh đang chờ (tối đa 60 giây) |
| Đã gửi | Server đã gửi lệnh cho agent, agent chưa báo bắt đầu |
| Đang chạy | Agent đang làm |
| Xong | Chạy xong, kết quả ở Lịch sử lệnh |
| Lỗi | Agent báo lỗi; lý do ghi ở Lịch sử lệnh |
| Hết hạn | Agent không nhận lệnh trong 60 giây |
| Không rõ kết quả | Agent mất kết nối khi đang chạy lệnh (hoặc server vừa khởi động lại): không biết lệnh đã làm xong chưa. Lệnh không tự chạy lại; kiểm tra rồi ra lệnh lại nếu cần |

Server khởi động lại thì lệnh đang Chờ agent / Đã gửi / Đang chạy chuyển thành "Không rõ kết quả" ("Máy chủ khởi động lại — không rõ kết quả").
Riêng `update` khi agent đứt kênh thì không báo "Không rõ kết quả" ngay: server chờ agent nối lại bằng bản mới (hoặc bản cũ nếu phải quay về) rồi mới kết luận.

### Cập nhật agent

Server cần có tệp mới trong `AGENT_DIST_DIR` (`LisAgent.exe` và `version.txt`, tạo bằng `scripts/publish-agent.sh`). Nút
**Cập nhật lên x.y.z** chỉ hiện khi phiên bản đó khác phiên bản agent đang chạy. Agent chỉ tải tệp từ chính server đã ghép nối và
phải khớp SHA-256 server công bố. Agent chép tệp mới cạnh tệp cũ rồi mới dừng dịch vụ, đổi tệp, bật lại. Bản mới **không nối lại được
server trong 2 phút** thì agent trả tệp cũ, bật lại bản cũ, và lệnh thành "Lỗi" kèm lý do. Không có cập nhật hàng loạt: mỗi agent một lệnh.

### Reverse proxy cho WebSocket

Mục 6 của [HUONG-DAN-CAI-DAT.md](HUONG-DAN-CAI-DAT.md) có mẫu nginx cho cả web, `/api` và `/agent/v1`. Riêng đường
`/agent/v1/channel` là WebSocket, nên proxy phải cho nâng cấp kết nối (`Upgrade`). Proxy chặn WebSocket thì mọi thứ khác vẫn chạy,
chỉ riêng kênh lệnh đứt: web hiện "Kênh lệnh đang đứt — các lệnh tạm tắt" trong khi agent vẫn báo về qua HTTP.

nginx (thêm vào khối `server` có sẵn, đặt trước `location /`):

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

- **Caddy:** `reverse_proxy 127.0.0.1:5080` tự lo WebSocket, không cần thêm gì.
- **IIS:** bật tính năng **WebSocket Protocol** (Server Manager → Add Roles and Features → Web Server → Application Development)
  và dùng URL Rewrite + Application Request Routing (ARR) để chuyển tiếp về server LIS.
- **Thời gian chờ:** proxy cắt kết nối im lặng quá lâu, nên `proxy_read_timeout` đặt từ 120 giây trở lên. Kênh bị cắt thì agent tự nối lại.
- Mẫu nginx và hướng dẫn IIS ở đây **chưa chạy thử trên máy chủ thật**; xem mục 9 của danh sách kiểm
  [2026-11-19-c-windows-that.md](superpowers/checklists/2026-11-19-c-windows-that.md).

## API agent (`/agent/v1`, header `X-Agent-Key`)

| | |
|---|---|
| `GET /me` | phòng khám, tên agent, mã máy trạm |
| `GET /instruments` | các máy gán cho máy trạm này (cấu hình kết nối, driver) |
| `POST /readings` | một bản tin kết quả → ghi vào phiếu, tự dò map kênh; gửi lại trả `duplicate: true` |
| `POST /order-queries` | máy hỏi chỉ định theo barcode → các mã kênh cần chạy |
| `POST /status` | trạng thái kết nối từng máy (web hiển thị) |
| `POST /logs` | log thô: mọi byte đi/về với máy (hex) + sự kiện, gửi theo lô mỗi giây; server giữ 14 ngày |
| `GET /channel` (WebSocket) | kênh lệnh, xem mục trên |

## Cài đặt trên web

Máy xét nghiệm (giao thức, cổng COM/TCP, agent phụ trách), map kênh, và agent (thêm, ghép nối, ghép nối lại) đều làm trên web
(`../web`, do chính server LIS phục vụ). Agent sau khi ghép nối tự lấy cấu hình máy về mỗi 30 giây.
Chỉ cần một tên miền / địa chỉ cho cả web, `/api` và `/agent/v1` — cùng trỏ vào server LIS (reverse proxy xem mục 6 của hướng dẫn
cài đặt; riêng `/agent/v1/channel` phải cho WebSocket qua, xem mục "Reverse proxy cho WebSocket" ở trên). Ra khỏi mạng nội bộ thì dùng HTTPS: mã ghép nối và khoá agent đi qua mạng.

## Chưa có

In tem qua agent, nhận chỉ định từ HIS.
