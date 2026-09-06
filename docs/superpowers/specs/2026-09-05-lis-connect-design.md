# LIS Connect — Thiết kế hệ thống

**Ngày:** 2026-09-05
**Trạng thái:** hoàn tất bản thiết kế, chờ rà soát

---

## 1. Bối cảnh và ràng buộc

Phần mềm trung gian (middleware) kết nối máy xét nghiệm, chạy được độc lập hoặc
tích hợp với HIS. Các ràng buộc đã chốt:

| Hạng mục | Quyết định |
|---|---|
| Mô hình sản phẩm | Đóng gói bán cho nhiều cơ sở; cài đặt phải dễ, cấu hình phải linh hoạt |
| Giao tiếp máy XN | Serial RS-232 (ASTM E1381), TCP/IP socket (ASTM/HL7 MLLP), file-based (watch folder) |
| Tích hợp HIS | **Hai chiều, cấu hình theo từng cơ sở** (cập nhật 2026-09-06). Ban đầu chốt "HIS gọi vào API của mình", nhưng khảo sát hệ đang chạy ở khách hàng đầu tiên cho thấy ngược lại: LIS **chủ động gọi sang** HIS — đăng nhập lấy token, kéo chỉ định theo barcode, đẩy kết quả trả lại. Vì vậy hỗ trợ cả hai hướng, mỗi cơ sở chọn một |
| Phạm vi standalone | Tối thiểu: nhập bệnh nhân + chỉ định, sinh barcode, gửi máy, nhận kết quả, duyệt, in phiếu. **Không** thu ngân, kho hóa chất, QC nội kiểm trong giai đoạn này |
| Mô hình triển khai | **Cloud multi-tenant là chính**: Core chạy trên hạ tầng của nhà cung cấp, khách chỉ cài Agent tại phòng xét nghiệm. On-premise là ngoại lệ cho khách bắt buộc giữ dữ liệu trong viện — cùng một codebase, chạy với đúng một tenant |
| Cô lập dữ liệu | Một Postgres host duy nhất; bên trong là một database quản trị và mỗi khách một database riêng (database-per-tenant) |
| Mạng tại cơ sở | LAN bệnh viện có internet ra ngoài, nhưng **không giả định HIS gọi ra được internet** |

**Không làm trong phạm vi này (YAGNI):** thu ngân/viện phí, quản lý kho hóa chất,
QC nội kiểm Levey-Jennings/Westgard, đọc trực tiếp DB của HIS, kết nối qua API
riêng của hãng máy.

---

## 2. Phương án kiến trúc

Ba phương án đã cân nhắc:

- **A. All-in-one trên một PC** — một installer chứa tất cả, chạy trên chính PC
  cắm dây serial. Cài rất nhanh nhưng mọi máy XN buộc phải cắm về một PC.
- **B. Core server + Instrument Agent tách rời** — **đã chọn**.
- **C. Desktop app trên từng PC dùng chung DB** — logic tán ra nhiều máy, nâng cấp
  phải chạy vòng từng PC, HIS không có điểm gọi REST cố định. Là nợ dài hạn với
  sản phẩm bán nhiều nơi.

**Lý do chọn B:** thứ duy nhất *bắt buộc* phải nằm cạnh máy xét nghiệm là phần
đọc/ghi cổng. Tách đúng phần đó ra thành Agent giúp toàn bộ phần còn lại chạy tập
trung trên cloud, trong khi khách chỉ phải cài đúng một chương trình nhỏ tại phòng
xét nghiệm.

Với khách on-premise (ngoại lệ), cùng bộ mã được cài trọn trên hạ tầng của họ và
chạy với đúng một tenant. Không có nhánh mã riêng, không có bản dựng riêng.

### Stack

| Thành phần | Công nghệ |
|---|---|
| Core | NestJS + PostgreSQL |
| Web UI | React |
| Agent | Một app Electron duy nhất: `main` (Node, `serialport`) chạy nền liên tục, `renderer` (React) là cửa sổ cấu hình mở khi cần |

Chọn Node cho Agent để đồng nhất ngôn ngữ với Core và tái dùng cùng bộ kiểu dữ liệu.

---

## 3. Kiến trúc tổng thể

```
        CLOUD (nhà cung cấp)                  BỆNH VIỆN A (LAN)
┌────────────────────────────┐         ┌──────────────────────────┐
│  Core (NestJS)             │         │                          │
│   ├ tenant router          │◄────────┤  Agent ──► máy XN        │
│   ├ DB quản trị            │  WSS    │   ├ SQLite cache         │
│   └ DB-A  DB-B  DB-C ...   │         │   ├ hàng đợi kết quả     │
│  Web UI (React)            │◄──HTTPS─┤   └ REST :8080 ◄── HIS-A │
└────────────────────────────┘         └──────────────────────────┘
```

### 3.1 Agent

Chạy như Windows Service trên mỗi PC có cắm máy xét nghiệm. Là thành phần duy nhất
biết tới cổng COM, socket và thư mục. Mang ba vai, tách thành ba module rõ ràng bên
trong nó:

**Vai 1 — nói chuyện với máy:**

- Giữ kết nối tới các máy được Core giao cho.
- Dịch xuôi/ngược giữa khung dữ liệu của máy và một message JSON nội bộ.
- Ghi log mọi byte vào/ra.
- Đệm ra đĩa khi mất kết nối tới Core, gửi lại khi nối lại.

**Vai 2 — cache chỉ định:** giữ bản sao SQLite các chỉ định chưa làm để trả lời
host-query tại chỗ trong 1-3 giây (mục 3.6).

**Vai 3 — cổng LAN cho HIS:** mở REST API trên `:8080` cho HIS trong mạng nội bộ
gọi vào (mục 3.6).

**Vai 4 — phục vụ Agent Console:** mở HTTP + WebSocket trên `:8081` cho ứng dụng
cấu hình tại chỗ (mục 3.8).

Agent **không** biết gì về bệnh nhân, giá dịch vụ hay HIS. Cấu hình của nó do Core
đẩy xuống chứ không sửa tay trên từng máy — đây là điều kiện để vận hành được
hàng chục cơ sở mà không phải đi tới nơi.

### 3.2 Driver

Thư viện nạp động bên trong Agent. Mỗi model máy xét nghiệm là một driver, phơi
ra một giao diện tối thiểu:

- `parse(buffer) → kết quả`
- `build(order) → buffer`
- bảng ánh xạ mã test của máy sang mã chuẩn nội bộ

Driver là thứ sẽ được viết mới liên tục theo từng khách hàng, nên phải tách hẳn
khỏi lõi Agent: thêm máy mới không được đụng vào code đã chạy ổn định.

### 3.3 Core

Nguồn sự thật duy nhất cho chỉ định, mẫu, kết quả, danh mục xét nghiệm, danh sách
máy và log. Phơi ra hai mặt:

- **API cho HIS** — nhận chỉ định, trả kết quả, webhook.
- **API cho Web UI**.

Nói chuyện với các Agent qua một kênh WebSocket duy nhất.

### 3.4 Web UI

Nhập chỉ định (chế độ standalone), worklist, duyệt kết quả, theo dõi và xem log
kết nối máy, in phiếu.

### 3.5 Multi-tenant: một Core, nhiều database

Toàn hệ thống dùng **đúng một Postgres host**. Bên trong host đó có một **database
quản trị** `lis_admin` (danh sách khách hàng, khoá Agent, license, tài khoản) và
**mỗi khách một database nghiệp vụ** riêng, đặt tên `lis_t_<mã khách>`.

Định tuyến tenant lấy từ ba nguồn:

- **Agent** — mỗi Agent cầm một `agentKey` đã gắn cứng với tenant khi cấp phát;
  Agent không tự khai mình thuộc khách nào.
- **HIS** — API key cấp riêng cho từng khách, gửi qua header `X-Api-Key`.
- **Người dùng web** — tenant nằm trong JWT sau khi đăng nhập.

Tenant được gán ở tầng ngoài cùng của request, rồi mọi truy vấn phía dưới đi qua
đúng connection của tenant đó. Đây phải là **middleware một cửa**: service tầng
dưới không được tự chọn database. Chỉ cần một chỗ quên là rò dữ liệu bệnh nhân
giữa hai bệnh viện.

**Vì sao DB riêng thay vì schema chung:**

- Rò dữ liệu giữa hai khách bị chặn ở tầng vật lý, không phụ thuộc việc lập trình
  viên có nhớ `WHERE tenant_id = ?` hay không.
- Sao lưu, khôi phục, xuất dữ liệu cho một khách là thao tác trên đúng một database.
- Khách đòi toàn bộ dữ liệu của mình, hoặc muốn chuyển về chạy tại chỗ — làm được
  ngay, không phải bóc tách.
- Ranh giới dữ liệu rõ ràng khi cần rà soát hoặc bàn giao.

**Cái giá phải trả, phải xử lý ngay từ đầu:**

- **Migration chạy trên N database.** Cần trình chạy migration lặp qua toàn bộ
  tenant, ghi nhận tenant nào đang ở version nào, và xử lý được trường hợp chạy
  nửa chừng thì lỗi.
- **Connection pool.** Nhiều tenant × pool riêng sẽ ăn hết kết nối Postgres. Dùng
  pool nhỏ theo tenant, đóng pool của tenant không hoạt động, đặt PgBouncer phía trước.
- **Báo cáo xuyên khách hàng** trở nên phiền. Với phạm vi hiện tại thì hiếm dùng
  nên chấp nhận được.

### 3.6 Hệ quả của mô hình cloud

**Host-query phải trả lời tại chỗ.** Máy xét nghiệm cho 1-3 giây để trả lời
"SID này làm xét nghiệm gì". Đường từ phòng xét nghiệm ra cloud không đảm bảo được
con số đó. Vì vậy Agent giữ bản sao cục bộ (SQLite) các chỉ định chưa làm và trả
lời máy hoàn toàn tại chỗ — không gọi lên cloud trong luồng nóng. Core đẩy chỉ
định xuống trước. Mất internet vài giờ, phòng xét nghiệm vẫn chạy bình thường;
kết quả dồn trong hàng đợi và tự đẩy lên khi có mạng.

**HIS thường không gọi ra được internet.** HIS nằm trong mạng nội bộ bệnh viện,
nhiều nơi chặn cứng chiều ra. Nếu API chỉ tồn tại trên cloud thì tích hợp hỏng
ngay từ khách đầu tiên. Vì vậy **Agent kiêm cổng LAN cho HIS**: mở cùng bộ REST
API trên `http://<ip-agent>:8080`, HIS gọi vào đó trong mạng nội bộ, Agent chuyển
tiếp lên Core — riêng chỉ định thì ghi thẳng vào cache cục bộ, nên việc nhận chỉ
định vẫn chạy khi mất mạng. Khách nào HIS ra được internet thì trỏ thẳng lên
cloud. Cùng một đặc tả API, hai điểm vào.

**Dữ liệu bệnh nhân rời khỏi bệnh viện.** Kéo theo: TLS bắt buộc mọi chặng, mã
hóa at-rest, nhật ký truy cập (ai xem kết quả của ai, lúc nào), và chính sách lưu
trữ. Nhật ký truy cập phải có từ đầu — thêm vào khi đã có dữ liệu thật thì rất phiền.

Agent do đó mang ba vai — nói chuyện với máy, cache chỉ định, cổng LAN cho HIS —
vẫn tách thành ba module rõ ràng bên trong nó.

### 3.7 Agent Console — ứng dụng Windows cấu hình tại chỗ

Cấu hình nghiệp vụ do Core đẩy xuống, nhưng lúc cài đặt lần đầu thì chưa có gì để
đẩy: kỹ thuật viên đang đứng cạnh máy, chưa biết cổng COM nào, Agent chưa ghép với
tenant nào. Bắt họ mở Web UI trên cloud để dò cổng COM của cái máy ngay trước mặt là
vô lý. Vì vậy Agent có ứng dụng cấu hình riêng chạy tại chỗ.

#### Một app, hai tiến trình có sẵn của Electron

Electron vốn đã tách sẵn hai tiến trình, nên không cần đóng gói hai chương trình:

```
LIS Connect Agent.exe   ← một app, một installer, một icon tray
 ├─ main (Node)        chạy liên tục: COM/TCP/file, cache, hàng đợi, WSS lên Core,
 │                     REST :8080 cho HIS, HTTP/WS :8081 cho cửa sổ cấu hình
 └─ renderer (cửa sổ)  mở khi cần, đóng thì huỷ, main không đụng gì
```

Toàn bộ phần kết nối nằm ở `main`. Đóng cửa sổ chỉ huỷ `renderer`; `main` vẫn chạy
nền và RAM tụt xuống phần nhỏ. App thu về khay hệ thống chứ không thoát.

**Đánh đổi:** app kiểu này chỉ chạy khi đã có người đăng nhập Windows. Máy khởi động
lại lúc nửa đêm rồi dừng ở màn hình đăng nhập thì Agent không chạy, và máy xét nghiệm
gửi kết quả vào hư không.

Khắc phục ở mức đủ dùng: bật **tự động đăng nhập** trên PC phòng xét nghiệm và đăng
ký app qua **Task Scheduler** với "run at logon" cộng "restart on failure". Thực tế
PC phòng xét nghiệm gần như luôn để sẵn một tài khoản dùng chung, nên thường chấp
nhận được. Việc này phải nằm trong tài liệu triển khai và trong bộ cài, không phó mặc
cho người cài nhớ.

Vì code kết nối nằm gọn trong `main`, nếu về sau gặp khách có chính sách máy trạm
siết chặt và bắt buộc phải là Windows Service thì bọc thêm chế độ headless là việc
nhỏ, không phải viết lại. Chọn gộp bây giờ không khoá đường về.

Cửa sổ cấu hình trỏ được tới Agent ở **máy khác trong LAN**
(`http://192.168.1.20:8081`), nên kỹ thuật viên ngồi một chỗ cấu hình được nhiều máy
trong phòng.

#### Bốn tab

**Kết nối Core** — URL Core, dán `agentKey` (hoặc mã ghép 6 số lấy từ Web UI), cấu
hình proxy nếu bệnh viện bắt đi qua proxy. Nút **Kiểm tra kết nối** báo rõ: thông
mạng chưa, khóa đúng chưa, đang thuộc cơ sở nào.

**Máy xét nghiệm** — danh sách máy kèm đèn trạng thái. Thêm/sửa một máy:

- *Serial*: dropdown liệt kê sẵn các cổng COM đang có trên máy tính, kèm tên thiết bị
  để phân biệt các bộ USB-Serial; baudrate/parity/databits/stopbits; chọn driver.
- *TCP*: IP, cổng, Agent làm client hay server.
- *File*: thư mục, mẫu tên file, có xóa file sau khi đọc không.

Nút **Test kết nối** mở cổng thử, báo mở được không, và nếu máy có bắt tay
(ENQ/ACK) thì báo bắt tay được không.

**Terminal thô** — xem byte vào/ra theo thời gian thực, dạng text lẫn hex, có ô gửi
chuỗi tự soạn xuống máy. Đây là công cụ khi dò một máy lạ chưa có driver: ngồi xem nó
nói gì rồi mới viết driver.

**Nhật ký** — log của Agent, hàng đợi còn tồn bao nhiêu, nút xuất gói chẩn đoán
(log + cấu hình, đã che thông tin bệnh nhân) để gửi về khi hỗ trợ từ xa.

#### Ai sở hữu cấu hình nào

Phải rõ, nếu không sẽ có hai nguồn sự thật đánh nhau.

| Loại cấu hình | Sửa ở đâu | Vì sao |
|---|---|---|
| URL Core, `agentKey`, proxy, cổng REST LAN | **Chỉ Agent Console** | Là thông tin về chính máy tính đó; Core không thể biết trước |
| Cổng COM, baudrate, IP máy, thư mục | **Agent Console**, đồng bộ ngược lên Core | Thứ vật lý tại chỗ. Core lưu bản sao để hiển thị và sao lưu, nhưng không tự ý ghi đè |
| Driver nào, chế độ broadcast/host-query | Cả hai, Core là nguồn sự thật | Là quyết định nghiệp vụ |
| Ánh xạ mã test, danh mục xét nghiệm, khoảng tham chiếu | **Chỉ Web UI** | Dùng chung toàn cơ sở, không thuộc về một máy tính nào |

Quy tắc: **thứ gì mô tả phần cứng thì thuộc về Agent; thứ gì mô tả nghiệp vụ thì
thuộc về Core.** Nhờ vậy cài đặt tại chỗ vẫn làm được khi chưa có mạng, còn quản lý
từ xa vẫn nắm được toàn cảnh.

#### Ghi chú kỹ thuật

`serialport` là native module. `electron-builder` cùng `electron-rebuild` xử lý việc
biên dịch và đóng gói native module đã chín, nên chọn Electron cũng gỡ luôn rủi ro
đóng gói mà `pkg`/Node SEA hay vướng. Cái giá là dung lượng bộ cài (~150MB) — chấp
nhận được.

### 3.8 Quyết định: hướng kết nối Core ↔ Agent

Kênh WebSocket do **Agent chủ động mở lên Core**, không phải Core gọi xuống Agent.

PC phòng xét nghiệm hay đổi IP, hay bị DHCP cấp lại, hay có firewall. Bắt Core tìm
đường xuống là nguồn cơn của phần lớn sự cố "tự nhiên mất kết nối". Agent gọi lên
thì chỉ cần nó thấy được Core là đủ.

---

## 4. Luồng dữ liệu

### 4.1 Chiều xuôi: chỉ định → máy xét nghiệm

```
HIS ──POST /api/orders──► Core (hoặc Agent :8080 khi HIS không ra được internet)
                           │ tạo Order + Sample, cấp barcode/SID
                           │ tra Routing: test nào chạy máy nào
                           ▼
                      OrderTask (mỗi máy một task, PENDING)
                           │
        ┌──────────────────┴──── hai chế độ, cấu hình theo từng máy ────┐
        ▼                                                              ▼
  BROADCAST (đẩy trước)                              HOST-QUERY (máy hỏi)
  Core đẩy task xuống Agent ngay khi có;             KTV quét barcode, máy gửi
  Agent gửi vào máy bất kể mẫu đã tới chưa           lệnh hỏi "SID này làm gì?"
                                                     Agent trả lời từ cache cục bộ
```

Đa số máy đời mới dùng **host-query**: kỹ thuật viên đặt ống có barcode lên máy,
máy đọc barcode rồi hỏi ngược lên hệ thống. Máy đời cũ hơn phải **broadcast**
trước. Hai luồng khác hẳn nhau nên hỗ trợ cả hai ngay từ đầu, chọn bằng cấu hình
từng máy.

Với host-query, **hệ thống phải trả lời trong 1-3 giây** hoặc máy bỏ qua mẫu. Vì
Core nằm trên cloud, Agent trả lời hoàn toàn từ cache SQLite cục bộ, không gọi
lên cloud trong luồng nóng (xem mục 3.6).

### 4.2 Chiều ngược: kết quả → HIS / màn hình / máy in

```
Máy XN ──► Agent: parse → RawMessage (lưu nguyên văn) + Result đã chuẩn hóa
                    │ đệm ra đĩa, gửi lên Core, chờ ACK mới xóa đệm
                    ▼
                  Core: khớp SID → Order, map mã test, tính cờ (cao/thấp/nguy hiểm)
                    │
                    ├──► trạng thái RECEIVED — hiện ngay trên Web UI
                    │
                    ├──► KTV duyệt (hoặc auto-verify nếu được bật)
                    │
                    └──► VERIFIED ──┬──► gửi HIS (webhook POST, có retry)
                                    └──► in phiếu
```

### 4.3 Ba quyết định trong luồng dữ liệu

**Kết quả thô lưu nguyên văn, vĩnh viễn.** Mọi tin nhắn máy gửi lên được lưu dạng
byte gốc trước khi parse. Khi driver parse sai — chuyện chắc chắn xảy ra với máy
mới — sửa driver rồi *chạy lại* trên dữ liệu cũ: không mất kết quả, không phải
chạy lại mẫu.

**Kết quả chưa duyệt mặc định không ra khỏi hệ thống.** HIS chỉ nhận cái đã
`VERIFIED`. Đây là hành vi mặc định, nhưng **cấu hình được theo từng khách**: khách
nào muốn HIS nhận sớm thì bật chế độ gửi ngay kèm cờ "chưa duyệt", và gửi lại bản
cập nhật khi KTV duyệt. Ngoài ra có auto-verify theo từng xét nghiệm với điều kiện
rõ ràng (trong khoảng tham chiếu, không có cờ nguy hiểm, delta check bình thường) —
mặc định tắt.

**Trả kết quả cho HIS là webhook có hàng đợi và retry**, không phải gọi đồng bộ.
HIS của khách sẽ sập, sẽ bảo trì, sẽ chậm. Kèm theo đó luôn có
`GET /api/results?since=...` để HIS tự kéo lại khi vừa hồi phục — thiếu cái này
thì mỗi lần HIS sập là một cuộc gọi hỗ trợ.

### 4.4 In ấn

Máy in nằm ở phòng xét nghiệm, Core nằm trên cloud. Core sinh PDF, trình duyệt máy
trạm in ra máy in cục bộ của nó. Trường hợp cần in tự động không người bấm (in
ngay khi duyệt xong) thì giao cho Agent, vì Agent đã nằm sẵn trong LAN.

---

## 5. Mô hình dữ liệu

### 5.1 DB quản trị (một, dùng chung)

`tenants` (mã, tên, chuỗi kết nối DB, trạng thái, gói license) · `users` (đăng nhập,
thuộc tenant nào, vai trò) · `agent_keys` (khóa cấp cho từng Agent, gắn cứng tenant) ·
`api_keys` (khóa cấp cho HIS từng khách) · `migrations_state` (mỗi tenant đang ở
version nào).

### 5.2 DB nghiệp vụ (mỗi khách một)

**Danh mục** — `analytes` (xét nghiệm: mã nội bộ, tên, đơn vị, kiểu giá trị số/chữ,
số lẻ) · `reference_ranges` (khoảng tham chiếu theo tuổi/giới, ngưỡng nguy hiểm) ·
`profiles` (bộ xét nghiệm) · `specimen_types`.

**Thiết bị** — `devices` (tên, driver, kiểu kết nối, thuộc Agent nào, chế độ
broadcast hay host-query) · `device_analyte_map` (mã test trên máy ↔ analyte nội bộ) ·
`agents` (lần cuối online, version).

`device_analyte_map` là nơi trú ngụ của gần như mọi sự cố "máy trả về mà không hiện
lên", nên **phải sửa được từ Web UI**, không phải sửa file cấu hình trên máy.

**Nghiệp vụ** — `patients` · `orders` (từ HIS hay tự nhập, bác sĩ chỉ định, chẩn
đoán, ưu tiên) · `samples` (`sid`/barcode, loại bệnh phẩm, thời điểm lấy/nhận) ·
`order_tests` (từng xét nghiệm trong chỉ định, trạng thái riêng) · `order_tasks`
(việc giao cho một máy cụ thể).

**Kết quả** — `results` (giá trị, đơn vị, cờ H/L/critical, máy nào chạy, lúc nào,
ai duyệt, trạng thái) · `result_revisions` (mọi lần sửa: giá trị cũ, giá trị mới,
ai sửa, lý do). Kết quả xét nghiệm **không bao giờ bị ghi đè mất dấu** — sửa là
thêm bản ghi mới.

**Dữ liệu thô** — `raw_messages`: nguyên văn byte từ máy, đã parse hay chưa, parse
ra kết quả nào. Đây là thứ cho phép chạy lại khi driver có lỗi.

**Log kết nối** — `connection_events` (mở/đóng/timeout/lỗi cổng) · `device_status`
(trạng thái hiện tại từng máy, phục vụ màn hình theo dõi).

**Tích hợp và kiểm toán** — `outbound_deliveries` (mỗi lần gửi kết quả cho HIS:
payload, số lần thử, lỗi lần cuối) · `access_logs` (ai xem/in kết quả của bệnh nhân
nào, lúc nào).

### 5.3 Ba quyết định trong mô hình dữ liệu

**`analyte` là mã nội bộ, không phải mã của máy và cũng không phải mã của HIS.** Ở
giữa có hai bảng ánh xạ: máy→analyte, và analyte→mã HIS. Thoạt nhìn là thừa một
lớp, nhưng thiếu nó thì đổi máy sẽ phải sửa cấu hình HIS, và mỗi khách một kiểu mã
sẽ nhân bản logic khắp nơi.

**`sid` (barcode ống) là khóa nối thực sự giữa máy và hệ thống**, không phải mã bệnh
nhân — máy chỉ biết cái nó đọc được trên ống. `sid` duy nhất trong phạm vi một tenant.

**`order_tasks` tách khỏi `order_tests`** vì một chỉ định có thể chạy trên hai máy
(điện giải máy A, sinh hóa máy B) và có thể chạy lại lần hai khi kết quả bất thường.
Gộp chung thì không biểu diễn được việc chạy lại.

## 6. Xử lý lỗi và khả năng phục hồi

Với middleware xét nghiệm, phần lớn giá trị sản phẩm nằm ở chỗ nó cư xử thế nào khi
mọi thứ hỏng. Dưới đây chia theo bốn ranh giới có thể đứt.

**Nguyên tắc xuyên suốt:** hệ thống không được phép **mất** kết quả xét nghiệm, và
không được phép **bịa ra** kết quả. Khi nghi ngờ thì giữ lại, đánh dấu, và bắt con
người quyết định. Mọi lựa chọn dưới đây rút ra từ nguyên tắc này.

### 6.1 Đứt ở dây: Agent ↔ máy xét nghiệm

Mất kết nối cổng COM hay socket không phải lỗi, là trạng thái. Agent tự kết nối lại
theo backoff (1s, 2s, 4s… trần 30s) và không bao giờ bỏ cuộc. Mỗi lần đổi trạng
thái ghi một `connection_event` và đẩy lên Core để màn hình theo dõi thấy được.

Máy im lặng quá lâu (ví dụ 15 phút không có byte nào trong khi lịch sử cho thấy giờ
này thường có) thì sinh cảnh báo "nghi mất kết nối" — vì cổng COM có thể vẫn mở
trong khi dây đã tuột.

### 6.2 Đứt ở mạng: Agent ↔ Core

Ranh giới quan trọng nhất, vì Core nằm trên cloud.

**Mất Core không được làm phòng xét nghiệm dừng.** Agent trả lời host-query từ
cache, nhận chỉ định từ HIS qua cổng LAN, thu kết quả và xếp vào hàng đợi trên đĩa.
Khi mạng về thì đẩy lên theo thứ tự. Hàng đợi giữ tối thiểu 7 ngày và có trần dung
lượng — đầy thì cảnh báo lớn, không âm thầm xóa.

Mọi message từ Agent lên Core mang một `messageId` do Agent sinh, và Core xử lý
**idempotent**: nhận lại message cũ thì bỏ qua, không tạo kết quả trùng. Thiếu cái
này thì mỗi lần mạng chập chờn sẽ sinh kết quả nhân đôi — lỗi kinh điển của
middleware xét nghiệm.

### 6.3 Đứt ở ngữ nghĩa: dữ liệu về nhưng không hiểu được

- **Parse lỗi** → `raw_message` vẫn lưu, đánh dấu `PARSE_ERROR`, sinh cảnh báo. Sửa
  driver rồi chạy lại trên dữ liệu cũ.
- **SID không khớp chỉ định nào** → không vứt đi. Vào hàng **"kết quả mồ côi"**, KTV
  gán tay vào đúng bệnh nhân trên Web UI. Chuyện này xảy ra hằng ngày ở phòng xét
  nghiệm thật: quét nhầm, hoặc ống chạy trước khi chỉ định kịp vào.
- **Mã test máy trả về chưa có trong `device_analyte_map`** → lưu kết quả nhưng để
  trạng thái "chưa ánh xạ", hiện trên màn hình cấu hình kèm nút "ánh xạ ngay". Đây
  là thứ biến một cuộc gọi hỗ trợ thành một cú bấm chuột.

### 6.4 Đứt ở đầu ra: Core ↔ HIS

Webhook thất bại thì retry với backoff, tối đa khoảng 24 giờ, mỗi lần thử ghi vào
`outbound_deliveries`. Quá hạn thì chuyển trạng thái "cần can thiệp" và hiện trên
bảng điều khiển. Cộng với `GET /api/results?since=...` để HIS tự kéo lại. Không bao
giờ im lặng bỏ cuộc.

### 6.5 Màn hình theo dõi

Mặt trước của tất cả những cái trên: danh sách máy với đèn xanh/vàng/đỏ, lần cuối
nhận dữ liệu, số kết quả hôm nay, hàng đợi đang tồn, và nút xem log thô theo thời
gian thực của từng máy — có bộ lọc, xem được byte gốc dạng hex khi cần dò lỗi driver.

## 7. Chiến lược kiểm thử

Vấn đề trung tâm: **không có máy xét nghiệm để chạy thử trong lúc phát triển.** Máy
nằm ở bệnh viện, đang chạy mẫu bệnh nhân thật. Mọi thứ dưới đây xoay quanh ràng
buộc đó.

### 7.1 Máy giả lập (Instrument Simulator)

Công cụ quan trọng nhất của dự án. Một chương trình giả làm máy xét nghiệm: mở cổng
serial ảo (`com0com`) hoặc socket TCP, nói đúng giao thức của một model cụ thể, và
làm được cả những hành vi xấu — trả về giữa chừng rồi ngắt, sai checksum, gửi ký tự
lạ, im lặng không ACK, trả kết quả cho SID không tồn tại.

Không có nó thì toàn bộ mục 6 chỉ là lý thuyết. Nó còn có giá trị thứ hai: **demo
bán hàng và huấn luyện** mà không cần máy thật. Đáng đầu tư sớm.

### 7.2 Driver test bằng kho tin nhắn thật

Mỗi driver có một thư mục chứa bản ghi thô lấy từ máy thật, kèm kết quả kỳ vọng đã
parse. Test là chạy parse trên toàn bộ kho và so khớp.

Khi ra hiện trường gặp tin nhắn lạ, `raw_messages` cho phép lấy nguyên văn về, thêm
vào kho, viết test, sửa driver. Kho này lớn dần theo số khách hàng và trở thành lưới
an toàn thật sự — đây là lý do việc lưu byte gốc (mục 4.3) quan trọng đến vậy.

### 7.3 Test cô lập tenant

Không thể thương lượng. Một bộ test riêng khẳng định dữ liệu tenant A không bao giờ
lộ sang tenant B: gọi mọi endpoint bằng khóa của A trong khi DB của B có dữ liệu, và
ngược lại. Chạy trên mọi commit.

Rò dữ liệu bệnh nhân giữa hai bệnh viện là loại lỗi kết thúc sản phẩm, không phải
loại lỗi vá sau.

### 7.4 Test khả năng phục hồi

Chạy Agent và Core thật, cắt mạng giữa chừng, kiểm tra: hàng đợi giữ đủ, gửi lại
đúng thứ tự, gửi lại hai lần không sinh kết quả trùng, host-query vẫn trả lời khi
Core tắt hoàn toàn.

### 7.5 Test tính toán lâm sàng

Cờ cao/thấp/nguy hiểm theo tuổi và giới, làm tròn số lẻ, kết quả dạng `<0.05` hay
`âm tính`. Sai thầm lặng ở đây nguy hiểm hơn crash: một kết quả sai không có cờ cảnh
báo thì không ai biết cho tới khi muộn.

### 7.6 Cố tình không làm

Không E2E toàn bộ giao diện. Đắt, giòn, và không bắt được loại lỗi thật sự nguy hiểm
ở đây. Ưu tiên test ở tầng driver, tầng Core, và ranh giới Agent ↔ Core.

---

## 8. Phân giai đoạn triển khai

Bản thiết kế này quá lớn cho một kế hoạch thực thi duy nhất. Đề xuất tách thành ba
giai đoạn, mỗi giai đoạn có spec và kế hoạch riêng, và mỗi giai đoạn đều cho ra thứ
dùng được.

**Giai đoạn 1 — Xương sống một chiều.** Core với multi-tenant và định tuyến DB, Agent
Service, **Agent Console với tab Kết nối Core, Máy xét nghiệm và Terminal thô**, một
driver duy nhất (chọn máy phổ biến nhất trong tệp khách đầu tiên), máy giả lập, kênh
WebSocket có hàng đợi và idempotency, màn hình theo dõi kết nối, màn hình xem kết quả
thô. Chưa có chỉ định, chưa có HIS: chỉ hứng kết quả từ máy và hiện
lên. Đây là phần rủi ro kỹ thuật cao nhất nên phải làm trước.

**Giai đoạn 2 — Hai chiều và chế độ độc lập.** Danh mục xét nghiệm, bệnh nhân, chỉ
định nhập tay, barcode/SID, `order_tasks`, broadcast và host-query, cache cục bộ ở
Agent, duyệt kết quả, khoảng tham chiếu và cờ, in phiếu. Kết thúc giai đoạn này là
một LIS độc lập bán được.

**Giai đoạn 3 — Tích hợp HIS.** REST API cho HIS ở cả hai điểm vào (cloud và cổng
LAN của Agent), ánh xạ analyte ↔ mã HIS, webhook trả kết quả có retry, API kéo lại
theo `since`, cấu hình gửi sớm/gửi sau khi duyệt theo từng khách, nhật ký truy cập.

Việc brainstorm và lập kế hoạch chi tiết đi theo từng giai đoạn.

**Cập nhật 2026-09-06:** giai đoạn 1 đã hoàn tất. Giai đoạn 2 được phân rã tiếp thành
năm khối vì quá lớn cho một spec — xem
`2026-09-06-giai-doan-2a-danh-muc-di-tru-design.md`:

| Khối | Nội dung |
|---|---|
| 2A | Danh mục và di trú từ hệ cũ |
| 2B | Bệnh nhân, chỉ định, barcode/SID |
| 2C | Diễn giải kết quả (làm tròn, cờ cao/thấp, biện luận) |
| 2D | Duyệt, in phiếu, trả kết quả về HIS |
| 2E | Gửi chỉ định xuống máy (host-query và broadcast) |

Khách hàng đầu tiên là Bệnh viện Đa khoa 10, nơi sẽ thay thế một hệ LIS đang chạy —
nên giai đoạn 2 bắt buộc phải di trú dữ liệu thật và giữ nguyên mã xét nghiệm cũ.
