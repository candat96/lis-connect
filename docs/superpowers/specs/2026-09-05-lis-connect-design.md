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
| Hạ tầng khách hàng | On-premise trong LAN, có internet ra ngoài (cho phép tự cập nhật, remote support, kiểm tra license) |

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
   HIS của khách                     Phòng xét nghiệm
        │                        ┌──────────────────────┐
        │ REST (JSON)            │  PC-1   PC-2         │
        ▼                        │   │      │           │
┌───────────────────┐  WebSocket │ Agent  Agent         │
│      CORE         │◄───────────┼───┤      ├──── COM1 ─┼─► Máy sinh hóa
│  NestJS + Postgres│            │   │      └──── LAN ──┼─► Máy huyết học
│                   │            │   └──────  folder ───┼─► Máy nước tiểu
│  ├ HIS API        │            └──────────────────────┘
│  ├ Order/Result   │
│  ├ Device Registry│      ┌──────────────┐
│  ├ Log & Monitor  │◄─────┤  WEB UI      │  (trình duyệt máy trạm)
│  └ Print service  │      │  React       │
└───────────────────┘      └──────────────┘
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

### 3.5 Quyết định: hướng kết nối Core ↔ Agent

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
