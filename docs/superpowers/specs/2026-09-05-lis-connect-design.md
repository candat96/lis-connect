# LIS Connect — Thiết kế hệ thống

**Ngày:** 2026-09-05
**Trạng thái:** đang soạn — phần 1/5 đã chốt, phần 2-5 đang brainstorm

---

## 1. Bối cảnh và ràng buộc

Phần mềm trung gian (middleware) kết nối máy xét nghiệm, chạy được độc lập hoặc
tích hợp với HIS. Các ràng buộc đã chốt:

| Hạng mục | Quyết định |
|---|---|
| Mô hình sản phẩm | Đóng gói bán cho nhiều cơ sở; cài đặt phải dễ, cấu hình phải linh hoạt |
| Giao tiếp máy XN | Serial RS-232 (ASTM E1381), TCP/IP socket (ASTM/HL7 MLLP), file-based (watch folder) |
| Tích hợp HIS | REST API JSON do LIS Connect định nghĩa; HIS của khách gọi vào |
| Phạm vi standalone | Tối thiểu: nhập bệnh nhân + chỉ định, sinh barcode, gửi máy, nhận kết quả, duyệt, in phiếu. **Không** thu ngân, kho hóa chất, QC nội kiểm trong giai đoạn này |
| Mô hình triển khai | **Cloud multi-tenant là chính**: Core chạy trên hạ tầng của nhà cung cấp, khách chỉ cài Agent tại phòng xét nghiệm. On-premise là ngoại lệ cho khách bắt buộc giữ dữ liệu trong viện — cùng một codebase, chạy với đúng một tenant |
| Cô lập dữ liệu | Mỗi khách một database riêng (database-per-tenant) |
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
đọc/ghi cổng. Tách đúng phần đó ra thành Agent giúp toàn bộ phần còn lại chạy
được ở bất cứ đâu, trong khi khách nhỏ vẫn thấy sản phẩm là "cài một lần trên
một máy" — vì bản mặc định gói Core + Agent trong cùng một installer.

### Ghi chú về mô hình cloud

Phương án B được chọn từ trước khi chốt mô hình cloud, và càng đúng hơn sau đó:
phần đọc/ghi cổng bắt buộc ở tại chỗ, phần còn lại chạy tập trung trên cloud.

### Stack

| Thành phần | Công nghệ |
|---|---|
| Core | NestJS + PostgreSQL |
| Web UI | React |
| Agent | Node.js (thư viện `serialport`), đóng gói thành `.exe` chạy như Windows Service |

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

Chương trình nhẹ chạy như Windows Service trên mỗi PC có cắm máy xét nghiệm. Là
thành phần duy nhất biết tới cổng COM, socket và thư mục.

Trách nhiệm:

- Giữ kết nối tới các máy được Core giao cho.
- Dịch xuôi/ngược giữa khung dữ liệu của máy và một message JSON nội bộ.
- Ghi log mọi byte vào/ra.
- Đệm ra đĩa khi mất kết nối tới Core, gửi lại khi nối lại.

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

Có một **DB quản trị** (danh sách khách hàng, chuỗi kết nối, license, tài khoản)
và **mỗi khách một DB nghiệp vụ** riêng.

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
- Một bệnh viện lớn chạy nặng không kéo chậm phần còn lại; cần thì tách riêng nó
  sang một Postgres khác, chỉ đổi chuỗi kết nối.

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

### 3.7 Quyết định: hướng kết nối Core ↔ Agent

Kênh WebSocket do **Agent chủ động mở lên Core**, không phải Core gọi xuống Agent.

PC phòng xét nghiệm hay đổi IP, hay bị DHCP cấp lại, hay có firewall. Bắt Core tìm
đường xuống là nguồn cơn của phần lớn sự cố "tự nhiên mất kết nối". Agent gọi lên
thì chỉ cần nó thấy được Core là đủ.

---

## 4. Luồng dữ liệu

*(đang brainstorm)*

## 5. Mô hình dữ liệu

*(đang brainstorm)*

## 6. Xử lý lỗi và khả năng phục hồi

*(đang brainstorm)*

## 7. Chiến lược kiểm thử

*(đang brainstorm)*
