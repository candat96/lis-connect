# Một server .NET, agent tự cài và điều khiển hoàn toàn từ web — bản thiết kế

Ngày: 2026-11-16 · Trạng thái: chờ duyệt

Ký hiệu trong tài liệu:

- **[Đã chốt]** — chủ dự án nói ra hoặc xác nhận trong buổi trao đổi.
- **[Claude chọn]** — người viết thiết kế tự quyết; chủ dự án chưa xác nhận riêng, có thể đổi.
- **[Để người làm quyết]** — chi tiết triển khai, người làm tự chọn miễn giữ đúng ràng buộc.

## 1. Vì sao làm

Hiện triển khai LIS cho một nơi mới phải dựng **hai** server (NestJS cho web, `LisServer` .NET cho
agent), gõ lệnh dòng lệnh để tạo phòng khám / tài khoản / agent, rồi sửa tay `appsettings.json`
trên máy tính cạnh máy xét nghiệm. Người triển khai là chủ dự án hoặc kỹ thuật viên làm từ xa;
ở phòng khám chỉ có nhân viên không rành kỹ thuật. Mỗi bước thủ công là một chỗ dễ sai và phải
gọi điện hướng dẫn.

Mong muốn **[Đã chốt]**:

1. Triển khai nơi mới chỉ cần **PostgreSQL + một server**.
2. Agent **tải từ web, bấm chạy là tự cài**; không ai gõ URL server hay mã phòng khám.
3. Sau khi cài, **mọi thứ điều khiển trên web**; agent nhận lệnh và làm theo. Không ai phải ngồi
   vào máy tính đó nữa.
4. Agent **gửi dữ liệu thô về server**; server chuyển tín hiệu thành kết quả.
5. Backend viết bằng gì cũng được → **gom về một server .NET, bỏ NestJS**.

Bối cảnh nghiệp vụ **[Đã chốt]**: LIS không chạy độc lập lâu dài — luôn nối với HIS/CIS; phần
riêng của LIS là bộ danh mục xét nghiệm và bộ kết nối máy. Tiếp nhận ngay trong LIS vẫn được
phép, trước mắt để chạy thử.

## 2. Mục tiêu, ngoài phạm vi, điều phải tránh

### Mục tiêu

- Một tiến trình server .NET duy nhất phục vụ: trang web, `/api` cho web, `/agent/v1` cho agent,
  tự tạo và nâng cấp CSDL khi khởi động, phát tệp cài agent.
- Tạo phòng khám và tài khoản quản trị đầu tiên trên web, không cần dòng lệnh.
- Ghép nối agent bằng mã dùng một lần; agent tự cài thành dịch vụ Windows.
- Kênh lệnh tức thời server → agent; web điều khiển và theo dõi mọi thứ của agent.
- Giải mã bản tin máy xét nghiệm ở server, từ dữ liệu thô agent gửi lên; giải mã lại được.
- Tiếp nhận tối giản trên web, đủ để chạy thử trọn vòng mà không cần HIS/CIS **[Claude chọn]**.

### Ngoài phạm vi (làm ở bản thiết kế khác)

- Nối HIS-PROMAX / CIS (nhận chỉ định, trả kết quả; tự sinh LIS khi tạo phòng khám bên CIS/HIS).
- In tem barcode, in phiếu kết quả, cài đặt cơ sở / máy in, nhập Excel trên web.
- Ngừng hẳn app Windows `Lis.App`. Trong bản này nó phải **vẫn build và test xanh**, không sửa
  hành vi.
- Agent cho Linux/macOS (code agent vẫn chạy được dạng console để phát triển, nhưng tự cài chỉ
  làm cho Windows).

### Điều phải tránh (làm được mà vi phạm là hỏng)

- **Kết quả gán sai mẫu / sai người bệnh, hoặc kết quả đã duyệt bị ghi đè lặng lẽ.** Mọi đường
  ghi kết quả từ máy — kể cả "giải mã lại" — phải đi qua đúng `IngestService` hiện có (khớp
  barcode, map kênh, cờ theo khoảng tham chiếu, "chờ thay" khi mẫu đã duyệt).
- **Mất kết quả khi mất mạng hoặc server khởi động lại.** Máy xét nghiệm đã được "đã nhận" thì
  dữ liệu phải nằm trên đĩa agent cho tới khi server xác nhận đã lưu.
- **Ghi trùng** một bản tin khi agent gửi lại.
- **Agent ngừng làm việc với máy xét nghiệm** chỉ vì mất kết nối tới server.
- **Agent của phòng khám này chạm được dữ liệu phòng khám khác.**
- **Web ra lệnh mà không để lại dấu vết** ai ra lệnh, lúc nào, kết quả gì.

## 3. Hiện trạng (để người làm biết bắt đầu từ đâu)

- `src/Lis.Server` (.NET minimal API, ~660 dòng): `/agent/v1/{me,instruments,readings,order-queries,logs,status}`
  xác thực bằng header `X-Agent-Key`; `MasterStore` (CSDL `lis_master`: `clinic`, `agent`,
  `agent_reading`); `TenantRegistry` (mỗi phòng khám một CSDL); CLI `clinic add|list`,
  `user add`, `agent add`; chạy `Migrator.Upgrade` (DbUp, `src/Lis.Data/Migrations/0001–0009`).
- `server/` (NestJS, ~2.700 dòng, 3 tệp test): toàn bộ `/api` cho web — `auth` (login, me),
  `users`, `catalog` (+ admin), `instruments` (+ channels, declared, unmapped), `agents`,
  `agent-logs`, `samples`, `results`. JWT; mật khẩu Argon2id cùng định dạng với .NET.
- `web/` (React + antd + Vite): gọi `/api/...` qua `web/src/services/index.ts`.
- `src/Lis.Agent` (.NET worker, dịch vụ Windows): đọc `Server:Url`, `Server:AgentKey` từ
  `appsettings.json`; 30 giây hỏi lại danh sách máy; chạy `InstrumentConnectionManager` →
  `InstrumentRunner` → driver trong `Lis.Core/Drivers`; gửi **kết quả đã giải mã** lên
  `/readings`; đẩy log thô lên `/logs` qua hàng chờ trong bộ nhớ (có thể rơi dòng khi mất mạng lâu).
- `IInstrumentDriver` gộp cả hai việc: bắt tay đường truyền (`DriverOutput.Send`) và hiểu nội
  dung (`DriverOutput.Readings`). `RawLogDecoder` đã có sẵn cách "cho driver đọc lại byte thô,
  bỏ qua phần gửi trả".
- `Lis.Data` đã có sẵn các store nghiệp vụ mà `Lis.App` dùng (người dùng, danh mục, máy, kết
  quả, duyệt…). NestJS đang viết lại một phần logic đó bằng TypeScript (`reference-range.ts`,
  `result-value.ts`, `results-write.service.ts`).
- Test: xunit v3 + Shouldly; test CSDL dùng Testcontainers (cần Docker). `tools/Lis.Simulator`
  giả lập máy HL7 / ASTM / Beckman AU và phát lại tệp `.listrace`.

## 4. Kiến trúc đích

```
Trình duyệt ──/api + trang web──┐
                                ▼
                    ┌──────────────────────────────┐
                    │ LisServer (.NET, một tiến    │
PC cạnh máy XN      │ trình)                       │
┌───────────────┐   │  · web tĩnh + /api           │
│ Lis.Agent     │──►│  · /agent/v1 (HTTP + kênh    │──► PostgreSQL
│ COM/TCP ↔ máy │◄──│    lệnh thường trực)         │    lis_master + 1 CSDL/phòng khám
└───────────────┘   │  · giải mã + IngestService   │
                    │  · tự migrate, phát tệp agent│
                    └──────────────────────────────┘
```

Chia việc agent / server **[Đã chốt]**:

| Agent — "đường truyền" | Server — "hiểu" |
|---|---|
| Mở cổng COM/TCP theo cấu hình server gửi | Giữ toàn bộ cấu hình, danh mục, map kênh |
| Trả lời bắt tay cho máy (ACK/NAK, khung, checksum) tại chỗ | Đọc bản tin thô thành kết quả |
| Lưu bản tin thô xuống đĩa, gửi lên, gửi bù khi có mạng | Ghép vào mẫu, gắn cờ, lưu, chờ duyệt |
| Chuyển câu "máy hỏi chỉ định" lên server, chuyển trả lời xuống máy | Quyết định mẫu đó chạy kênh nào |
| Thực hiện lệnh từ web, báo trạng thái | Ra lệnh, ghi vết, hiển thị |

Phần bắt tay buộc ở agent vì máy xét nghiệm chỉ chờ trả lời vài giây.

## 5. Các phần việc

Thứ tự làm **[Claude chọn]**: A → B → C → D → E. Mỗi phần xong là chạy được, test xanh, không
phụ thuộc phần sau. A đi trước vì B–E đều cần server .NET có `/api`.

### A. Một server .NET, bỏ NestJS

- `LisServer` nhận thêm toàn bộ `/api` mà NestJS đang có. **Giữ nguyên đường dẫn, phương thức,
  dạng JSON, mã lỗi và thông báo lỗi** để `web/` không phải sửa ngoài cấu hình proxy
  **[Claude chọn]**. Danh sách route lấy từ `server/src/**/**.controller.ts` — đó là đặc tả.
- Dùng lại store trong `Lis.Data` thay vì viết SQL mới ở nơi đã có sẵn. Chỗ NestJS và `Lis.Data`
  xử lý khác nhau (làm tròn, cờ, khoảng tham chiếu, quy tắc duyệt) thì **`Lis.Data`/`Lis.Core`
  thắng**, và ghi lại khác biệt trong mô tả thay đổi.
- Đăng nhập: mã phòng khám + tên đăng nhập + mật khẩu như hiện nay; JWT; 4 vai trò `quan-tri`,
  `tiep-nhan`, `ky-thuat-vien`, `truong-khoa`; quyền từng route giữ như NestJS.
- Server phục vụ luôn tệp tĩnh của web (`web/dist`) **[Claude chọn]**; lúc phát triển vẫn dùng
  Vite proxy.
- Khởi động: tạo `lis_master` nếu chưa có, nâng cấp CSDL của mọi phòng khám (đã có). Một phòng
  khám nâng cấp lỗi thì **chỉ phòng khám đó** bị từ chối phục vụ kèm thông báo rõ; các phòng
  khám khác vẫn chạy **[Claude chọn]**.
- **Tạo phòng khám trên web.** Khi `lis_master` chưa có phòng khám nào, web hiện trang "Thiết
  lập lần đầu": nhập mã thiết lập, mã + tên phòng khám, tài khoản quản trị đầu tiên. Mã thiết
  lập lấy từ biến môi trường `SETUP_KEY`; nếu không đặt, server sinh một mã và in ra log lúc
  khởi động **[Claude chọn]**. Thêm phòng khám thứ hai trở đi cũng dùng trang này với mã thiết
  lập (chưa làm màn quản trị liên phòng khám). Tên CSDL sinh từ mã phòng khám
  **[Để người làm quyết]**.
- Giữ các lệnh CLI hiện có (dùng khi cần cứu hộ).
- Đóng gói: `docker-compose` gồm PostgreSQL 18 + server, và hướng dẫn chạy không Docker
  **[Claude chọn]**. Cập nhật `docs/HUONG-DAN-CAI-DAT.md`, `docs/server-agent.md`.
- Xóa thư mục `server/` sau khi test đối chiếu xanh.

Xong khi: web hiện tại chạy trọn mọi màn hình với server .NET; bộ test API bao hết các route
đã chuyển; dựng máy chủ trắng chỉ bằng PostgreSQL + server rồi tạo phòng khám trên web được.

### B. Ghép nối và tự cài agent

Luồng **[Đã chốt]**:

1. Web → mục Agent → "Thêm agent", đặt tên.
2. Web hiện nút tải tệp cài và **mã ghép nối**: dùng một lần, hết hạn sau 30 phút, chứa địa chỉ
   server + mã phòng khám + khóa tạm.
3. Nhân viên chạy tệp cài. Tải từ web thì mã đã gắn sẵn, không gõ gì. Chép tệp qua USB thì dán
   mã vào một ô duy nhất.
4. Agent đổi khóa tạm lấy khóa lâu dài, lưu trên máy, tự cài thành dịch vụ Windows và chạy.
5. Web hiện agent "Đang kết nối". Xóa agent trên web là thu hồi.

Ràng buộc:

- Server chỉ lưu **băm** của khóa tạm và khóa lâu dài. Khóa tạm dùng xong hoặc hết hạn là vô
  hiệu. Khóa lâu dài chỉ đi qua mạng một lần, lúc đổi.
- Khóa lâu dài lưu trên máy agent ở dạng chỉ máy đó giải mã được (DPAPI phạm vi máy)
  **[Claude chọn]**.
- Địa chỉ server trong mã ghép nối là địa chỉ công khai của server, lấy từ cấu hình
  `PUBLIC_URL`; thiếu thì lấy theo địa chỉ trình duyệt đang dùng **[Claude chọn]**.
- Agent phát hành dạng **một tệp `.exe` tự chứa cho win-x64** (không cần cài .NET). Server phát
  tệp này từ một thư mục cấu hình được (`AGENT_DIST_DIR`), kèm số phiên bản và SHA-256.
- Cách gắn mã vào tệp tải về **[Để người làm quyết]** — ví dụ đặt trong tên tệp, phải chịu
  được việc trình duyệt thêm " (1)". Cần thử thật trên Windows trước khi chốt.
- Cài dịch vụ cần quyền quản trị Windows: tệp cài tự xin nâng quyền (UAC). Tài khoản không có
  quyền quản trị thì báo rõ bằng tiếng Việt, không cài nửa chừng.
- Dịch vụ tự chạy khi bật máy và tự khởi động lại khi lỗi.
- Chạy tệp cài lần hai trên máy đã cài: hỏi "ghép nối lại" hay "gỡ" **[Claude chọn]**.
- "Mã máy trạm" (`workstation_code`) hiện người dùng phải tự đặt: từ nay server tự sinh, ẩn
  khỏi form; việc gán máy xét nghiệm cho agent trên web chọn theo tên agent **[Claude chọn]**.
- Bỏ cách cấu hình bằng `appsettings.json` + lệnh `agent add` cho luồng thường; giữ biến môi
  trường `LISAGENT_*` để chạy console lúc phát triển.

Hạn chế đã biết: tệp `.exe` **chưa ký số** sẽ bị Windows SmartScreen cảnh báo "phần mềm lạ" lần
đầu chạy. Hướng dẫn cài phải có ảnh bước "More info → Run anyway". Ký số cần mua chứng thư —
**việc của chủ dự án, không chặn bản này**.

Xong khi: trên một máy Windows sạch, từ lúc bấm "Thêm agent" đến lúc web hiện "Đang kết nối"
không phải gõ URL, mã phòng khám hay sửa tệp nào.

### C. Kênh lệnh và điều khiển từ web

Agent tự mở **một kết nối thường trực đi ra** tới server; server đẩy lệnh xuống qua đó. Phòng
khám không phải mở cổng. Công nghệ: WebSocket trên chính cổng HTTP(S) của server
**[Claude chọn]**; đứt thì agent tự nối lại với thời gian chờ tăng dần.

Lệnh **[Đã chốt là "điều khiển mọi thứ trên web"; danh sách cụ thể do Claude đề xuất, chủ dự án
chưa sửa]**:

| Lệnh | Agent làm gì |
|---|---|
| Nạp lại cấu hình | Lấy danh sách máy ngay, không chờ 30 giây. Server tự gửi mỗi khi web sửa máy |
| Quét cổng COM | Trả danh sách cổng, tên thiết bị, cổng nào đang bị chiếm |
| Thử cổng | Mở cổng với thông số đã chọn trong N giây, trả "mở được / không" và các byte thô nhận được |
| Nối lại một máy | Đóng rồi mở lại kết nối với máy xét nghiệm đó |
| Gửi lại dữ liệu chờ | Đẩy ngay các bản tin còn nằm trên đĩa |
| Khởi động lại agent | Dịch vụ tự thoát, Windows bật lại |
| Cập nhật agent | Tải bản mới từ server, kiểm SHA-256, thay tệp, khởi động lại |
| Thu hồi | Xóa khóa trên máy, ngừng kết nối máy xét nghiệm, dừng dịch vụ |

Ràng buộc:

- Mỗi lệnh có mã, người ra lệnh, thời điểm, trạng thái (đã gửi / đang chạy / xong / lỗi / hết
  hạn) và kết quả; lưu lại để xem trên web. Chỉ vai trò `quan-tri` ra lệnh được **[Claude chọn]**.
- Lệnh tức thời gửi cho agent đang mất kết nối thì **hết hạn sau 60 giây**, không xếp hàng chờ
  ngày hôm sau mới chạy **[Claude chọn]**. Riêng "thu hồi" có hiệu lực ngay ở server (khóa bị
  từ chối) dù agent đang tắt.
- "Thử cổng" trên cổng đang được một máy xét nghiệm dùng: agent tạm nhường cổng rồi tự nối lại;
  web phải cảnh báo trước khi làm.
- Cập nhật agent lỗi (bản mới không chạy lên được) thì tự quay về bản cũ. Có nút cập nhật từng
  agent; **không** tự động cập nhật hàng loạt trong bản này **[Claude chọn]**.
- Mất kết nối server: agent tiếp tục chạy với cấu hình gần nhất đã lưu trên đĩa (như hiện nay).

Web hiển thị: agent trực tuyến / mất kết nối, phiên bản, tên máy tính, lần cuối liên lạc; từng
máy xét nghiệm: trạng thái đường truyền, lần cuối nhận dữ liệu, số bản tin đã nhận, số đang chờ
gửi; log thô chạy gần thời gian thực; lịch sử lệnh. Ô "Cổng COM" trong form máy xét nghiệm thành
danh sách chọn từ kết quả quét, vẫn cho gõ tay khi agent đang mất kết nối. Cách web nhận cập
nhật (hỏi định kỳ hay đẩy) **[Để người làm quyết]**; trễ hiển thị không quá ~3 giây.

Giao diện do Claude tự thiết kế và kiểm bằng ảnh chụp thật (quy ước của dự án).

Xong khi: với agent đã cài, mọi thao tác trong bảng trên làm được từ web và thấy kết quả, không
đụng vào máy tính agent.

### D. Giải mã ở server

Nguyên tắc **[Đã chốt]**: agent gửi dữ liệu thô; server chuyển thành kết quả.

Cách làm **[Claude chọn]**:

- Agent vẫn chạy driver, nhưng **chỉ dùng phần bắt tay** (`Send`) và phần nhận ra "máy hỏi chỉ
  định". Nó **không** gửi kết quả đã giải mã nữa.
- Agent cắt dòng byte nhận được thành từng **bản tin thô** — một lượt truyền trọn vẹn của máy
  theo ranh giới đường truyền (ví dụ một phiên ASTM từ ENQ đến EOT, một khối MLLP). Mỗi bản tin
  thô có: máy, số thứ tự tăng dần theo máy, thời điểm nhận, phiên kết nối, byte nguyên văn.
- Bản tin thô ghi xuống đĩa **trước khi** agent trả "đã nhận" cuối cùng cho máy nếu giao thức
  cho phép, và luôn trước khi gửi lên server. Gửi lên theo thứ tự; server xác nhận đã lưu thì
  agent mới xóa khỏi hàng chờ.
- Server lưu bản tin thô vào CSDL của phòng khám (khóa chống trùng: máy + số thứ tự), rồi cho
  **cùng bộ driver trong `Lis.Core`** đọc lại bản tin đó theo kiểu `RawLogDecoder` (bỏ qua phần
  gửi trả), lấy ra `Readings` và đưa vào `IngestService` + tự học map kênh như hiện nay. Lưu kèm
  kết quả giải mã (bao nhiêu kết quả đã ghi / chờ thay / chưa khớp / QC, ghi chú, lỗi) và phiên
  bản bộ giải mã.
- Bản tin server **không giải mã được** vẫn được lưu, đánh dấu "chưa giải mã", hiện trên web;
  không trả lỗi làm agent gửi lại vô hạn.
- **Giải mã lại** từ web: chọn một bản tin (hoặc một khoảng thời gian của một máy) → server chạy
  lại bộ giải mã hiện tại. Đi qua đúng `IngestService`, nên mẫu đã duyệt chỉ nhận "chờ thay",
  không bị ghi đè. Chỉ `quan-tri` / `truong-khoa`; có ghi vết.
- Máy hỏi chỉ định: agent tự lấy barcode từ câu hỏi (buộc phải làm tại chỗ để tiếp tục cuộc
  bắt tay), hỏi server như hiện nay (`/order-queries`), server trả danh sách kênh. Mất mạng thì
  trả lời máy "không có chỉ định" và ghi log — kết quả không mất, chỉ là máy không tự biết chạy gì.
- Họ giao thức "Chỉ nghe": agent không trả lời máy; cắt bản tin theo khoảng lặng
  **[Để người làm quyết]**; server chỉ lưu.
- Luồng log thô từng byte lên `/logs` (để xem trực tiếp trên web) giữ nguyên, tách biệt với
  luồng bản tin thô. Log có thể rơi dòng khi mất mạng lâu; **bản tin thô thì không được rơi**.
- `/agent/v1/readings` cũ bỏ hẳn. Chưa có nơi nào đang chạy agent bản online, nên **không cần
  tương thích ngược** với agent cũ **[Claude chọn — cần chủ dự án xác nhận]**.
- `Lis.App` vẫn dùng `InstrumentRunner` ghi thẳng CSDL như cũ; thay đổi ở `Lis.Connections` phải
  giữ được cả hai kiểu chạy.

Kiểm chứng (phần an toàn người bệnh — bắt buộc):

- Với mỗi tệp `.listrace` và mỗi ca test driver hiện có: kết quả đi theo đường mới (agent cắt
  bản tin → server giải mã → ingest) phải **giống hệt** đường cũ (driver giải mã tại chỗ →
  ingest): cùng barcode, kênh, giá trị, cờ, QC.
- Test mất mạng giữa chừng, server khởi động lại giữa chừng, agent gửi lại: không mất, không trùng.
- Test bản tin bị cắt sai ranh giới / hỏng checksum: không sinh kết quả sai; hiện "chưa giải mã".
- Chạy trọn vòng bằng `tools/Lis.Simulator` cho đủ ba họ HL7, ASTM, Beckman AU.
- Phần này do người rà soát mức cao nhất xem lại trước khi nhận.

Xong khi: sửa một lỗi đọc kết quả chỉ cần cập nhật server; bản tin cũ giải mã lại được; các
test đối chiếu trên xanh.

### E. Tiếp nhận tối giản trên web **[Claude chọn — để chạy thử trọn vòng]**

- Một màn hình: nhập người bệnh (họ tên, ngày/năm sinh, giới), chọn dịch vụ xét nghiệm, lưu →
  sinh mẫu và barcode theo đúng quy tắc `Lis.Data` đang dùng cho `Lis.App`. Hiện barcode trên
  màn hình để gõ vào bộ giả lập hoặc máy.
- Không in tem, không thu phí, không nhập Excel. Vai trò `tiep-nhan`, `quan-tri`.
- Thao tác bằng bàn phím: Enter chuyển ô, chọn dịch vụ bằng mã (quy ước của dự án).

Xong khi: trên máy của chủ dự án, từ web tạo một mẫu → bộ giả lập gửi kết quả qua agent → kết
quả hiện ở màn Kết quả → duyệt được.

## 6. Dữ liệu

Bổ sung **[Để người làm quyết chi tiết cột]**, phải qua migration đánh số tiếp theo:

- `lis_master`: mã ghép nối (băm, hạn, đã dùng), thông tin agent (phiên bản, tên máy tính, lần
  cuối kết nối, danh sách cổng COM lần quét cuối), lệnh gửi agent (ai, lúc nào, trạng thái, kết
  quả). `lis_master` hiện tạo bằng `CREATE TABLE IF NOT EXISTS` trong code — nên chuyển sang
  migration đánh số như CSDL phòng khám.
- CSDL phòng khám: bảng bản tin thô (máy, số thứ tự, thời điểm, phiên, byte, trạng thái giải
  mã, phiên bản bộ giải mã, kết quả giải mã). Thay cho `agent_reading` ở master.
- Thời hạn giữ bản tin thô: 90 ngày **[Claude chọn]**; log thô từng byte giữ 14 ngày như hiện nay.

## 7. Bảo mật

- Agent ↔ server và web ↔ server phải chạy HTTPS khi ra khỏi mạng nội bộ; server tự nó chạy
  HTTP sau một reverse proxy. Hướng dẫn cài nêu rõ; agent cảnh báo trong log khi ghép nối qua
  HTTP thường **[Claude chọn]**.
- Khóa agent xác định luôn phòng khám; mọi route `/agent/v1` chỉ chạm CSDL của phòng khám đó và
  chỉ các máy gán cho agent đó (giữ như hiện nay, có test).
- Tệp cập nhật agent chỉ tải từ chính server đã ghép nối và phải khớp SHA-256 server công bố.
- Trang "Thiết lập lần đầu" không dùng được nếu thiếu mã thiết lập; sai mã nhiều lần thì chậm dần.

## 8. Việc sau bản này

1. Nối HIS-PROMAX / CIS (bản thiết kế riêng — là phần lõi nghiệp vụ, làm ngay sau).
2. In tem, in phiếu, cài đặt cơ sở trên web; sau đó ngừng `Lis.App`.
3. Ký số tệp cài agent; tự động cập nhật agent hàng loạt.

## 9. Làm rõ sau lượt rà soát (đều là **[Claude chọn]**)

**Ranh giới bản tin thô (phần D).** Driver được bổ sung một tín hiệu mức đường truyền "vừa kết
thúc một lượt truyền" trong `DriverOutput`, phát ra ở ranh giới của giao thức **dù có giải mã ra
kết quả hay không**: ASTM — EOT, hoặc phiên bị bỏ / hết giờ chờ; HL7 MLLP — hết một khối; Beckman
AU — do người làm xác định theo tài liệu giao thức. Lượt truyền bị bỏ dở, sai checksum, hoặc chỉ
có câu hỏi chỉ định **cũng là một bản tin thô**, được lưu và gửi lên (đánh dấu "dở dang" nếu
vậy) — không byte nào máy gửi bị bỏ rơi hay dính sang bản tin sau. Agent không dùng `Readings`
để cắt. `Lis.App` không dùng tín hiệu mới nên hành vi không đổi.

**Định nghĩa "giống hệt".** Server đọc mỗi bản tin thô bằng một driver mới tinh (`Reset`, đưa
byte vào, rồi một lần `Tick` cuối để xả trạng thái chờ). Đơn vị bản tin phải **tự đủ để giải mã
một mình**. Phép thử: trên mọi tệp `.listrace` và mọi ca test driver hiện có, tập `Readings` của
đường mới bằng đúng đường cũ. Họ giao thức nào không đạt với đơn vị nhỏ thì nâng đơn vị lên cả
phiên kết nối, không nới phép thử.

**Nguồn dữ liệu chuẩn.** Bảng bản tin thô là nguồn chuẩn cho kết quả và cho "giải mã lại" /
"giải mã thử" trên web. Log thô từng byte chỉ để xem và chẩn đoán, được phép rơi dòng.

**Mức "giữ nguyên hợp đồng /api" (phần A).** Giữ đường dẫn, phương thức, dạng JSON, mã trạng
thái HTTP và các thông báo lỗi nghiệp vụ tiếng Việt. Câu chữ lỗi kiểm tra đầu vào do framework
sinh ra không cần giống từng chữ. Chuẩn nghiệm thu: test của `web/` xanh và đi hết các màn hình
bằng tay không lỗi. Phòng khám của một request xác định từ JWT như NestJS đang làm.

**Tệp cài agent.** Một tệp `.exe` duy nhất, có các chế độ: chạy dịch vụ, cài, gỡ, áp bản cập
nhật. Cập nhật: agent tải bản mới về cạnh bản cũ, gọi bản mới ở chế độ "áp cập nhật" như một
tiến trình tách rời để dừng dịch vụ → đổi tệp → bật lại. "Bản mới không chạy được" = dịch vụ
không nối lại được server trong 2 phút → trả tệp cũ về và bật lại; kết quả báo lên web.

**Một bản server.** Server chỉ chạy **một tiến trình**; danh sách kết nối agent giữ trong bộ
nhớ. Server khởi động lại: lệnh đang "đã gửi / đang chạy" chuyển thành "không rõ kết quả", không
tự chạy lại. Thu hồi agent cắt ngay kết nối đang mở.

**Khóa chống trùng bản tin thô.** Mỗi bản tin thô mang một mã do agent sinh lúc ghi xuống đĩa
(UUID), đó là khóa chống trùng; số thứ tự chỉ để sắp xếp. Chuyển máy xét nghiệm sang agent khác
không gây đụng khóa. Ghép nối lại **cùng phòng khám** thì giữ hàng chờ trên đĩa; ghép nối sang
**phòng khám khác** thì hàng chờ cũ bị cách ly, **không bao giờ gửi lên phòng khám mới**.

**Tạo phòng khám.** Tài khoản PostgreSQL của server cần quyền `CREATEDB` (như hiện nay). Việc
tạo phòng khám được khóa để hai người bấm cùng lúc không tạo trùng. Mã thiết lập luôn bắt buộc,
kể cả khi đã có phòng khám.

**Phần E** dùng lại đúng các store người bệnh / chỉ định / mẫu trong `Lis.Data` mà `Lis.App`
đang dùng; không viết quy tắc sinh barcode mới.

**Cấu hình agent.** Biến môi trường `LISAGENT_*` thắng cấu hình đã ghép nối; chỉ dùng khi phát
triển.

**Thử trên Windows thật.** Phần B và C chỉ được coi là xong sau khi thử trên một máy Windows
thật (cài dịch vụ, UAC, cổng COM). Trên máy phát triển macOS chỉ kiểm được phần logic.

## 10. Những chỗ Claude tự quyết — chủ dự án xem khi duyệt

1. Giữ nguyên hợp đồng `/api` của NestJS khi chuyển sang .NET, để web không phải sửa.
2. Mã thiết lập (`SETUP_KEY`) bảo vệ trang tạo phòng khám.
3. Kênh lệnh bằng WebSocket; lệnh tức thời hết hạn sau 60 giây nếu agent đang mất kết nối.
4. Chỉ `quan-tri` ra lệnh cho agent; cập nhật agent bấm tay từng cái.
5. Agent vẫn giữ driver để bắt tay và để lấy barcode khi máy hỏi chỉ định; server giải mã lại
   từ bản tin thô bằng cùng bộ driver.
6. Không tương thích ngược với agent bản cũ (coi như chưa nơi nào chạy bản online).
7. Thêm phần E (tiếp nhận tối giản) vào bản này để chạy thử được.
8. Giữ bản tin thô 90 ngày.
9. Tệp cài chưa ký số, chấp nhận cảnh báo SmartScreen.
10. Thứ tự làm A → B → C → D → E.
11. Các điểm ở mục 9.
