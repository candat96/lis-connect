# LIS Connect — Giai đoạn 2, khối 2A: Danh mục và di trú từ hệ cũ

**Ngày:** 2026-09-06
**Trạng thái:** đã duyệt thiết kế, chờ rà soát spec
**Tiếp nối:** `2026-09-05-lis-connect-design.md` (thiết kế tổng thể, giai đoạn 1 đã hoàn tất)
**Khảo sát nguồn:** `docs/khao-sat/lis-hien-huu.md` và `docs/khao-sat/seed/`

---

## 1. Bối cảnh

Giai đoạn 1 dựng xong xương sống một chiều: Agent nối máy xét nghiệm, đẩy dữ liệu thô
lên Core multi-tenant, Web UI hiển thị trạng thái kết nối và byte nhận được. Kết quả
hiện lên dưới dạng dữ liệu thô — chưa ai đọc được nó là xét nghiệm gì của bệnh nhân nào.

Giai đoạn 2 biến nó thành một LIS dùng được cho phòng xét nghiệm.

**Khách hàng đầu tiên là Bệnh viện Đa khoa 10**, nơi đang chạy một hệ LIS desktop
WinForms và sẽ được thay thế. Điều này định hình toàn bộ giai đoạn 2:

- Dữ liệu danh mục phải **di trú từ hệ cũ**, không nhập tay lại.
- **Mã xét nghiệm phải giữ nguyên** — HIS SmartHOS đang dùng chúng, và kỹ thuật viên
  đã thuộc.
- Hành vi phải quen thuộc với người đang dùng hệ cũ, trừ những chỗ hệ cũ làm sai.

### 1.1 Phân rã giai đoạn 2

Giai đoạn 2 quá lớn cho một spec. Chia thành năm khối, mỗi khối có spec và kế hoạch
riêng, mỗi khối ra được thứ dùng được:

| Khối | Nội dung | Ra được gì |
|---|---|---|
| **2A** | Danh mục và di trú từ hệ cũ | Nền cho mọi khối sau |
| 2B | Bệnh nhân, chỉ định, barcode/SID | Thấy được "hôm nay có ai, làm gì" |
| 2C | Diễn giải kết quả | Kết quả đọc được thay vì số thô |
| 2D | Duyệt, in phiếu, trả kết quả về HIS | Đủ thay hệ cũ |
| 2E | Gửi chỉ định xuống máy | Bỏ thao tác bấm tay trên máy |

Phụ thuộc: 2A trước tất cả; 2C cần 2A; 2D cần 2B và 2C; 2E cần 2A và 2B.

**Spec này chỉ mô tả khối 2A.**

### 1.2 Một đảo chiều so với thiết kế giai đoạn 1

Thiết kế giai đoạn 1 ghi *"HIS gọi REST API của mình"* — LIS đóng vai máy chủ. Khảo sát
cho thấy ở BVĐK 10 thì ngược lại: LIS cũ **chủ động gọi sang** HIS SmartHOS qua
`LIS_Api.dll`, đăng nhập lấy token, kéo chỉ định về và đẩy kết quả trả lại.

**Quyết định: hỗ trợ cả hai chiều, cấu hình theo từng cơ sở.** Việc này thuộc khối 2B
và 2D; ghi ở đây để spec tổng thể được cập nhật cho đúng.

---

## 2. Phạm vi khối 2A

### Làm

- Mô hình dữ liệu danh mục: xét nghiệm, nhóm cha–con, ánh xạ mã kênh máy, luật ghi đè
  kết quả, ánh xạ mã HIS, khoảng tham chiếu theo tuổi/giới.
- Công cụ di trú một lần từ các tệp JSON đã trích của hệ cũ.
- Ba màn hình quản lý: danh mục xét nghiệm, ánh xạ mã kênh máy, luật ghi đè kết quả.
- Bộ đối chiếu khẳng định dữ liệu di trú khớp nguồn.

### Không làm (YAGNI)

- Màn hình tạo xét nghiệm mới từ đầu — di trú xong là có đủ 404 mã; thêm mới là việc
  hiếm, để khối sau nếu thực sự cần.
- Lịch sử thay đổi danh mục.
- Phân quyền chi tiết theo vai trò — giai đoạn 1 đã có đăng nhập, mọi người dùng của
  một cơ sở hiện có quyền như nhau.
- Đồng bộ hai chiều liên tục với hệ cũ. **Di trú là việc một lần** lúc chuyển đổi.

---

## 3. Quyết định nền: giữ mã cũ, cấu trúc mới

Ba phương án đã cân nhắc:

- **A. Di trú thẳng** — bê nguyên cấu trúc bảng của hệ cũ. Nhanh và dễ đối chiếu, nhưng
  thừa hưởng luôn mọi khiếm khuyết mà chính tài liệu khảo sát chỉ ra.
- **B. Chuẩn hoá hoàn toàn** — đánh mã mới theo mô hình giai đoạn 1. Mô hình sạch, nhưng
  HIS đang dùng mã cũ, và mọi phiếu in ra sẽ khác mã so với hệ cũ.
- **C. Giữ mã cũ làm mã tự nhiên, dùng cấu trúc mới** — **đã chọn**.

**Lý do chọn C:** cái đắt nhất khi thay phần mềm không phải viết code mà là bắt người
dùng học lại. Giữ nguyên mã xét nghiệm giữ được trí nhớ của kỹ thuật viên, giữ được
tích hợp HIS đang chạy, và vẫn cho ta cấu trúc sạch bên dưới.

Cụ thể, mã `TestCode` của hệ cũ (`123AST`, `126ChoT`, `400TPTNT`) trở thành **mã nghiệp
vụ chính thức** của bảng `analytes` trong hệ mới. Không đổi, không thêm lớp dịch.

### 3.1 Những chỗ hệ cũ làm sai và cách sửa

Tài liệu khảo sát mục 7 liệt kê các điểm không nhất quán. Khối 2A xử lý như sau:

| Vấn đề ở hệ cũ | Cách xử lý ở hệ mới |
|---|---|
| Cặp `(InsID, TestcodeIN)` **không duy nhất**; gỡ nhập nhằng bằng `TOP 1` theo alphabet. Trên AU640, `128LDL` và `244ACo` cùng map kênh `19` | Ràng buộc **UNIQUE `(device_id, channel_code)`**. Lúc di trú, xung đột được **báo ra để người quyết**, không tự đoán |
| Cột dùng sai mục đích: `Result.UserU` lưu số hiệu máy, `ResultMoM` lưu vị trí máy | Đọc đúng ý nghĩa thật rồi ghi vào cột đúng tên |
| Truy vấn dùng `LIKE` trên mã; CSDL có mã chứa ký tự đại diện (`034NEU%`, `035LYM%`) | So khớp **chính xác** bằng `=`. Ký tự `%` trong mã được giữ nguyên như một ký tự bình thường |
| `Instrument.Active` vô dụng — cả 20 máy đều bằng 1 | Ba nhãn độc lập: *có cấu hình cổng*, *từng gửi kết quả*, *đang bật* |
| 11 mã cha không khai báo con; quan hệ chỉ tồn tại qua thụt lề tên và `PrintOrder` | Dựng lại quan hệ **tường minh**, xuất danh sách để kỹ thuật viên xác nhận |
| `PNormalRange` rỗng hoàn toàn — cơ chế cá thể hoá ngưỡng chưa bao giờ chạy | Cài đúng cơ chế (yêu cầu chuyên môn thật), nhưng **không kỳ vọng di trú được gì**; bảng trống sau di trú là đúng |

---

## 4. Mô hình dữ liệu

Giai đoạn 1 đã có `devices` và `device_analyte_map` trong database nghiệp vụ của mỗi
cơ sở. Khối 2A **mở rộng** chúng và thêm các bảng danh mục. Tất cả nằm trong database
nghiệp vụ của từng cơ sở, không phải database quản trị.

### 4.1 `analytes` — danh mục xét nghiệm

Nguồn: `TestCode.json` (404 dòng).

| Cột | Nguồn | Ghi chú |
|---|---|---|
| `code` | `TestCode` | **Khoá nghiệp vụ**, giữ nguyên mã cũ. Duy nhất trong một cơ sở |
| `name` | `TestName` | |
| `name_en` | `TestNameE` | |
| `category` | `Category` | |
| `unit` | `Unit` | Đơn vị của LIS **luôn thắng** đơn vị máy gửi lên |
| `decimals` | — | Số chữ số thập phân mặc định; ánh xạ mã kênh có thể ghi đè |
| `normal_range_text` | `NormalRange` | Dạng chữ, để in trên phiếu |
| `lower_limit` / `higher_limit` | `LowerLimit` / `HigherLimit` | Dạng số, để so ngưỡng |
| `comment_lower` / `comment_higher` / `comment_gray` | `BLLower` / `BLHigher` / `BLGray` | Câu biện luận |
| `criteria` | `Criteria` | So chuỗi khi kết quả không phải số |
| `result_is_comment` | `GetComment` | `false`: cột kết quả giữ số, biện luận riêng. `true`: cột kết quả nhận luôn câu biện luận |
| `print_order` | `PrintOrder` | |
| `is_profile_head` | `Profile` / `TestHead` | Đánh dấu mã cha |
| `source` | — | `'legacy'` hoặc `'manual'` |
| `active` | — | |

### 4.2 `profiles` và `profile_members` — nhóm cha–con

Nguồn: `Profile.json`, `ProfileTest.json`, cộng phần dựng lại từ thụt lề tên.

`profile_members` giữ `(profile_code, analyte_code, order)`. **Mã cha không được nằm
trong danh sách thành viên của chính nó** — hệ cũ để nó ở đó và phải lọc bằng cờ; hệ
mới loại ngay lúc di trú.

Mỗi nhóm có cột `source`: `'legacy-declared'` (hai nhóm `TPTMau22`, `TPTNT` có khai báo
thật) hoặc `'legacy-inferred'` (11 nhóm dựng lại từ thụt lề, **cần kỹ thuật viên xác
nhận**).

### 4.3 `devices` — mở rộng

Nguồn: `Instrument.json` (20 dòng), `InterfacePara.json` (9 dòng cấu hình cổng).

Thêm vào bảng đã có ở giai đoạn 1: `legacy_ins_id`, `protocol` (`ASTM`, `AU`, `AIA360`,
`TOSHO`, `U500`), `location`, và **ba nhãn hoạt động độc lập**:

- `has_port_config` — có dòng trong `InterfacePara`
- `has_sent_results` — từng xuất hiện trong `Result_OUT`
- `active` — cờ `Instrument.Active`

Ba tập này **không trùng nhau**: BC3200 và CYBOW gửi về 319 kết quả mà không có dòng
cấu hình cổng nào (cấu hình nằm ở tệp local trên trạm). Giữ ba nhãn riêng để người vận
hành thấy đúng sự thật thay vì một cờ `active` vô nghĩa.

Cấu hình cổng serial của hệ cũ dùng chung `9600, 8, N, 1` cho mọi máy. Máy socket duy
nhất (COBAS 8000) là `TCP 192.168.1.4:50001`, hệ cũ đóng vai **client**.

### 4.4 `device_analyte_map` — mở rộng

Nguồn: `InsTest.json` / `anh-xa-ma-may.json` (292 dòng).

Thêm vào bảng đã có: `round` (số chữ số thập phân khi làm tròn), `allow_insert` (cho
phép tự thêm chỉ định khi bệnh nhân chưa được chỉ định xét nghiệm này), `omit` (bỏ qua
dòng khi tra), `keep_higher_sign` / `keep_lower_sign` (giữ dấu `>` `<` trong kết quả).

**Ràng buộc `UNIQUE (device_id, channel_code)`** — thay đổi quan trọng nhất của khối 2A.

### 4.5 `result_mappings` — luật ghi đè kết quả

Nguồn: `ResultMapping.json` (56 dòng).

Khoá `(device_id, analyte_code, result_in)` → `result_out`, `comment`, `color`.
Đây là nơi `neg` thành "Âm tính". Bảng này được phép ghi đè **cả kết quả, biện luận lẫn
màu**.

### 4.6 `his_analyte_map` — ánh xạ mã HIS

Nguồn: `TestCode_HIS.json` (835 dòng).

`(his_service_code, analyte_code)`. Cột `profileID` ở nguồn chỉ có giá trị thật ở 14
dòng, còn lại là chuỗi rỗng — di trú bỏ qua cột này.

### 4.7 `reference_ranges` — khoảng tham chiếu cá thể hoá

Nguồn: `PNormalRange` — **rỗng hoàn toàn ở hệ cũ**.

Cấu trúc: `(analyte_code, device_id NULL, sex NULL, age_from, age_to)` → ngưỡng dưới/trên,
vùng xám, và tuỳ chọn ghi đè tên/đơn vị.

Bảng này **trống sau di trú**, và đó là kết quả đúng. Cơ chế được cài vì khoảng tham
chiếu theo tuổi và giới là yêu cầu chuyên môn thật; việc nhập dữ liệu là công việc
chuyên môn của phòng xét nghiệm, không phải việc di trú.

Khi không tìm thấy dòng cá thể hoá, hệ thống dùng `analytes.lower_limit` /
`higher_limit` dùng chung — đúng như hệ cũ đang thực sự chạy.

---

## 5. Công cụ di trú

### 5.1 Đầu vào

Các tệp JSON đã trích sẵn trong `docs/khao-sat/seed/du-lieu/`, **không** kết nối thẳng
vào SQL Server của bệnh viện.

Lý do: dữ liệu đã được trích rồi; một công cụ đọc tệp chạy lại được bao nhiêu lần cũng
được, trên máy nào cũng được, và không cần quyền truy cập database sản xuất — thứ
thường mất hàng tuần để xin.

### 5.2 Lệnh

```
admin migrate-legacy --tenant <mã cơ sở> --source <thư mục> [--decisions <tệp>] [--apply]
```

Không có `--apply` thì chỉ chạy hai giai đoạn đầu và không ghi gì vào database.

### 5.3 Ba giai đoạn

**Giai đoạn 1 — Đọc và kiểm tra, không ghi gì.**

Nạp toàn bộ tệp, kiểm ràng buộc, in báo cáo các vấn đề **trước khi** động vào database:

- Cặp `(máy, mã kênh)` trùng nhau
- Mã xét nghiệm xuất hiện trong ánh xạ nhưng không có trong danh mục
- Mã cha không có con
- Mã chứa ký tự đại diện `%`
- Mã HIS trỏ tới xét nghiệm không tồn tại

**Giai đoạn 2 — Người quyết những chỗ nhập nhằng.**

Với mỗi xung đột, công cụ ghi ra một tệp quyết định (`quyet-dinh-di-tru.json`) để người
vận hành điền. Ví dụ: kênh `19` của AU640 thuộc về `128LDL` hay `244ACo`.

**Công cụ không tự đoán.** Hệ cũ đoán bằng `TOP 1` theo thứ tự alphabet, và đó chính là
cách kết quả nồng độ cồn có thể về nhầm ô LDL mà không ai biết. Không có tệp quyết định
cho một xung đột thì dòng đó **không được di trú**, và điều đó được ghi rõ trong báo cáo.

**Giai đoạn 3 — Ghi vào database.**

Trong **một giao dịch duy nhất**. Hỏng giữa chừng thì không để lại trạng thái nửa vời.

### 5.4 Báo cáo đối chiếu

Kết thúc, công cụ in một bảng: mỗi bảng nguồn đọc bao nhiêu dòng, ghi bao nhiêu, bỏ qua
bao nhiêu, và **lý do từng dòng bị bỏ**. Đây là tài liệu người vận hành cầm đi đối chiếu
với hệ cũ trước khi cắt chuyển.

---

## 6. Màn hình quản lý

Ba màn hình, thêm vào Web UI đã có.

**Danh mục xét nghiệm.** Tìm theo mã hoặc tên, lọc theo nhóm. Sửa được: đơn vị, số chữ
số thập phân, ngưỡng dưới/trên, ba câu biện luận, thứ tự in. 404 dòng nên tìm kiếm phải
nhanh.

**Ánh xạ mã kênh máy.** Chọn máy, thấy các kênh và mỗi kênh trỏ tới xét nghiệm nào.
**Đây là màn hình quan trọng nhất của khối 2A** — như tài liệu khảo sát viết, đây là nơi
trú ngụ của gần như mọi sự cố "máy trả về mà không hiện lên". Kỹ thuật viên phải sửa
được ngay tại đây, không phải gọi điện cho nhà cung cấp.

**Luật ghi đè kết quả.** Bảng `result_mappings`, sửa tại chỗ.

Cả ba màn hình theo đúng ràng buộc giao diện đã lập ở giai đoạn 1: gọi skill
`ui-ux-pro-max` trước khi viết, tiếng Việt có dấu đầy đủ, mọi lời gọi API có xử lý lỗi
hiển thị được, không dùng riêng màu để truyền đạt trạng thái.

---

## 7. Kiểm thử

Bài kiểm quan trọng nhất **không phải** unit test mà là **đối chiếu với hệ cũ trên dữ
liệu thật**.

**Đối chiếu số lượng và nội dung.** Sau di trú, một bộ so sánh khẳng định 404 xét
nghiệm, 292 ánh xạ, 56 luật ghi đè, 835 mã HIS đều có mặt và khớp nguồn — **trừ đúng
những dòng đã được người quyết loại bỏ**, và mỗi dòng bị loại phải truy được lý do.

**Ràng buộc duy nhất thật sự chặn được.** Cố ghi hai kênh trùng trên cùng một máy phải
bị từ chối ở tầng database, không phải im lặng lấy một cái.

**Cây cha–con dựng lại đúng.** Hai nhóm có khai báo thật (`TPTMau22` 23 chỉ số, `TPTNT`
10 chỉ số) phải khớp chính xác. 11 nhóm dựng từ thụt lề phải khớp danh sách kỹ thuật
viên xác nhận.

**Công cụ di trú chạy hai lần cho kết quả giống hệt nhau** trên database trống.

**Ký tự đại diện trong mã không gây so khớp nhầm.** Tra mã `034NEU%` không được khớp
với mã khác cùng tiền tố.

---

## 8. Rủi ro đã biết

**Xung đột mã kênh cần chuyên môn để quyết.** Giai đoạn 2 của công cụ di trú chặn được
việc đoán bừa, nhưng người quyết phải là người hiểu phòng xét nghiệm. Cần lên lịch buổi
làm việc với kỹ thuật viên trưởng, không phải việc làm một mình.

**11 nhóm cha dựng từ thụt lề có thể sai.** Đây là suy luận từ cách trình bày, không
phải dữ liệu khai báo. Danh sách bắt buộc phải được xác nhận trước khi dùng thật.

**Dữ liệu nguồn là bản chụp.** Hệ cũ vẫn chạy và danh mục còn có thể đổi cho tới ngày
cắt chuyển. Vì đã chốt di trú một lần, cần chốt thời điểm trích dữ liệu cuối cùng và
kiểm lại chênh lệch trước khi chạy thật.
