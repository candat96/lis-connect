# Xử lý sự cố

Dành cho IT và người dùng khi gặp lỗi. Khi báo lỗi cho bộ phận hỗ trợ kỹ thuật, luôn kèm: thông
báo lỗi hiện trên màn hình, tệp `loi.log` (mục 8), phiên bản Windows, phiên bản PostgreSQL.

## 1. Không thể kết nối Cơ sở Dữ liệu

1. **PostgreSQL chưa chạy**: `Win+R` → `services.msc` → tìm `postgresql-x64-16` (hoặc phiên bản
   tương ứng) → đảm bảo trạng thái **Running**.
2. **Tường lửa chặn cổng 5432**: Windows Defender Firewall → Cho phép ứng dụng → kiểm tra
   `postgres.exe` đã được cho phép (cả Private và Public nếu CSDL ở máy khác trong LAN).
3. **PostgreSQL trên máy khác chưa mở cho LAN**: xem
   [`01-cai-dat.md`](01-cai-dat.md#6-khi-postgresql-nằm-trên-máy-khác-nhiều-máy-trạm-dùng-chung-một-csdl)
   — cần sửa `pg_hba.conf` và `postgresql.conf`, khởi động lại dịch vụ.
4. **CSDL cloud chưa bật SSL đúng yêu cầu**: tích **Bắt buộc TLS (SSL)** ở tab Kết nối CSDL hoặc
   wizard Bước 1 — xem [`01-cai-dat.md`](01-cai-dat.md#7-csdl-trên-cloud).

## 2. Sai mật khẩu CSDL

- Kiểm tra lại đúng mật khẩu đã đặt cho `lis_user` khi tạo CSDL (mục 5,
  [`01-cai-dat.md`](01-cai-dat.md)).
- Nếu quên, đặt lại bằng `psql` (đăng nhập bằng tài khoản `postgres`):
  ```sql
  ALTER USER lis_user WITH PASSWORD 'mat-khau-moi';
  ```
  rồi sửa lại ở **Cài đặt → Kết nối CSDL** (hoặc wizard nếu đang ở màn đó) với mật khẩu mới.

## 3. Không có quyền tạo extension citext/unaccent

Thông báo dạng `permission denied for ...` khi ứng dụng chạy migration lần đầu. Bình thường không
gặp lỗi này nếu `lis_user` là **chủ (owner)** của CSDL `lis` (xem mục 5,
[`01-cai-dat.md`](01-cai-dat.md)) — `citext`/`unaccent` là trusted extension (PostgreSQL 13+),
chủ CSDL tự tạo được, không cần `SUPERUSER`.

Nếu vẫn gặp, chạy dưới tài khoản `postgres`:

```sql
\c lis
CREATE EXTENSION IF NOT EXISTS citext;
CREATE EXTENSION IF NOT EXISTS unaccent;
\c postgres
```

Lỗi này chỉ chặn đúng bước tạo extension — các bảng migration **đã chạy trước đó trong cùng lần
đó không bị mất**. Sau khi cấp quyền, mở lại ứng dụng và thử kết nối lại — ứng dụng tự chạy tiếp
phần migration còn thiếu, **không cần xóa CSDL làm lại từ đầu**.

## 4. Tệp cấu hình cục bộ (config.json) hỏng

Nếu `config.json` (ở `C:\ProgramData\LIS Connect\config.json`) hỏng — sửa tay sai cú pháp JSON,
hoặc bị copy từ máy khác (mật khẩu CSDL mã hóa bằng DPAPI của Windows, **gắn với từng máy**, copy
sang máy khác không giải mã được) — ứng dụng **tự phát hiện khi khởi động**:

1. Đổi tên tệp cũ thành `config.json.bak` (giữ lại để chẩn đoán, không bị xóa).
2. Hiện thông báo giải thích.
3. Tự mở lại **Trình hướng dẫn cài đặt** để nhập lại thông tin kết nối (như cài mới — xem
   [`01-cai-dat.md`](01-cai-dat.md#8-chạy-lần-đầu--trình-hướng-dẫn-cài-đặt-3-bước)).

Không cần tự tay xóa tệp. Muốn làm lại từ đầu dù tệp không hỏng, có thể tự xóa (hoặc đổi tên)
`config.json` trước khi mở ứng dụng — **dữ liệu trong CSDL không bị ảnh hưởng**, chỉ mất cấu hình
kết nối cục bộ của riêng máy đó.

## 5. Wizard cài đặt bị đóng/tắt giữa chừng

Nếu đóng cửa sổ "Cài đặt lần đầu" trước khi hoàn tất Bước cuối (ví dụ tắt máy, đóng nhầm cửa sổ):
mở lại `LisConnect.exe` — vì `config.json` chưa được ghi (chỉ ghi khi hoàn tất wizard), ứng dụng
**tự mở lại wizard từ Bước 1**. Nếu Bước 1 (Kết nối CSDL) đã từng chạy thành công một phần
migration trước khi bị đóng, các bảng/migration đó vẫn còn trong CSDL (không rollback theo việc
đóng wizard) — chạy lại wizard, nhập đúng lại thông tin kết nối CSDL cũ, ứng dụng tự tiếp tục
đúng migration còn thiếu. Nếu CSDL đã có tài khoản/quản trị được tạo ở lần chạy trước (Bước 2 đã
hoàn tất), wizard lần sau sẽ **không hiện lại Bước 2** — vào thẳng màn Đăng nhập sau Bước 1.

## 6. Không in được

- **Không in tem/phiếu**: kiểm tra **Cài đặt → Máy in** đã chọn đúng tên máy in (ComboBox trên
  Windows liệt kê máy in đã cài ở hệ điều hành); dùng nút **In tem thử** (tab Máy in) để kiểm tra
  riêng khâu in tem không cần tạo mẫu mới.
- Kiểm tra máy in đang bật, có giấy/ruy băng (tem nhiệt), driver máy in đã cài đúng trên Windows
  (Devices and Printers).
- Nếu dùng máy không liệt kê được danh sách máy in (một số môi trường macOS/Linux khi phát
  triển), ô Máy in đổi thành ô nhập tên tay — gõ đúng tên máy in như hệ điều hành đặt, để trống
  nếu muốn dùng máy in mặc định.

## 7. Font không hỗ trợ tiếng Việt khi in

Phiếu kết quả dùng font nhúng sẵn trong ứng dụng (Inter) hỗ trợ tiếng Việt có dấu — nếu ký tự lạ
hoặc ô vuông xuất hiện khi in, thường là do **driver máy in** không xử lý đúng font nhúng trong
PDF, không phải do thiếu font hệ thống. Cài driver máy in mới nhất từ nhà sản xuất; với máy in
tem nhiệt, kiểm tra chế độ in (raster/graphics) được driver hỗ trợ đúng khổ tem đã khai ở **Cài
đặt → Máy in** (Chiều rộng/Chiều cao tem).

## 8. Barcode không quét được

- Kiểm tra lại **Chiều rộng tem (mm)** / **Chiều cao tem (mm)** ở **Cài đặt → Máy in** có đúng
  khổ tem thật đang dùng — tem bị cắt hoặc co giãn sai khổ làm đầu đọc không nhận.
- Dùng nút **In tem thử** để in một tem mẫu, kiểm tra bằng đầu đọc trước khi dùng cho mẫu thật.
- Kiểm tra đầu đọc đang ở đúng chế độ đọc mã **Code 128** (loại barcode LIS Connect dùng để in).
- Nếu tem in mờ/nhạt (đầu in nhiệt cũ, ruy băng gần hết), đầu đọc thường không nhận được — thử
  tem mới.

## 9. Kết quả rơi vào "Chưa ghép"

Xem bảng ba lý do ở [`03-su-dung.md`](03-su-dung.md#1-ba-lý-do-chưa-ghép) (không có barcode, chưa
có chỉ định, kênh chưa map). Cách xử lý nhanh theo lý do:

- **Chưa có chỉ định**: kiểm tra đã **Lưu và in tem** đúng barcode đó ở Tiếp nhận chưa, hoặc gõ
  nhầm/đọc nhầm barcode.
- **Kênh chưa map**: vào **Cài đặt → Máy & map**, thêm map kênh còn thiếu cho đúng máy
  (xem [`02-cau-hinh.md`](02-cau-hinh.md#8-máy-xét-nghiệm--map-kênh)).
- Dòng nào không xử lý được (dữ liệu nhiễu, trùng, không còn giá trị), dùng **Bỏ qua** kèm lý do
  ở màn Chưa ghép — không để tồn đọng danh sách.

(Luồng dữ liệu máy xét nghiệm thật đưa vào "Chưa ghép" là tính năng Đợt 2 — ở Đợt 1 màn này chủ
yếu dùng với dữ liệu demo/thử nghiệm.)

## 10. Ứng dụng khởi động chậm lần đầu

Bình thường — ứng dụng Avalonia khởi tạo UI framework lần chạy đầu tiên sau khi giải nén/cài mới.
Lần thứ 2 trở đi nhanh hơn.

## 11. Xem nhật ký lỗi (loi.log)

Khi ứng dụng gặp lỗi không mong đợi (treo, thông báo lỗi lạ, hoặc "biến mất" không rõ lý do), ứng
dụng ghi thời điểm + chi tiết lỗi (ngoại lệ .NET kèm stack trace) vào:

- Windows: `C:\ProgramData\LIS Connect\loi.log`
- macOS (máy phát triển): `~/Library/Application Support/LIS Connect/loi.log`

Tệp này **không chứa dữ liệu người bệnh** — gửi kèm tệp này khi báo lỗi với bộ phận hỗ trợ kỹ
thuật. Tệp này mở bằng Notepad hoặc trình soạn thảo văn bản. Log trao đổi với máy xét nghiệm là
thứ khác, xem trong màn **Log thô** (mục 12).

## 12. Máy gửi kết quả nhưng không thấy trên màn Kết quả

Vào **Log thô**, chọn máy và ngày:

- **Không có dòng "Máy → LIS" nào**: máy chưa gửi tới LIS. Kiểm tra cáp, cổng COM/địa chỉ IP,
  tốc độ baud ở màn **Máy & map**, và cài đặt "Host/LIS" trên máy xét nghiệm.
- **Có dòng nhưng không ra kết quả**: chọn đúng họ giao thức ở ô cạnh nút **Giải mã thử** rồi bấm
  nút đó. Bảng "Giải mã thử" cho biết driver hiểu ra barcode và các kênh nào, hoặc báo lỗi
  (checksum sai, sai số khung…). Giải mã thử không ghi gì vào CSDL.
- **Chưa biết máy nói giao thức gì**: đặt họ giao thức của máy là `chi-nghe` (Chỉ nghe), cho máy
  gửi một mẫu, rồi xem log thô. Ở chế độ này LIS không trả lời máy, nên máy có thể báo lỗi
  truyền. Chuyện đó là bình thường.
- **Cần gửi log cho người hỗ trợ**: chọn phiên rồi bấm **Xuất .listrace**. Tệp này **chứa dữ liệu
  người bệnh**, chỉ gửi qua kênh nội bộ. Người nhận mở bằng nút **Mở tệp .listrace** hoặc phát lại
  bằng bộ giả lập máy (`tools/Lis.Simulator`, xem README của repo).

Log thô lưu ở `C:\ProgramData\LIS Connect\rawlog\`, mỗi máy mỗi ngày một tệp, tự xóa sau số ngày
đặt ở **Cài đặt → Log thô** (mặc định 30 ngày). Tab đó cũng hiện dung lượng đang dùng và cảnh báo
khi vượt ngưỡng.

## 13. Agent đang mất kết nối

Trên web, bảng điều khiển agent báo **"Agent đang mất kết nối — các lệnh tạm tắt"**, trạng thái **Mất kết nối**. Nghĩa là máy
chủ không nhận được báo cáo của agent quá 90 giây. Các nút lệnh tắt, trừ **Thu hồi**.

Kiểm tra trên máy tính đặt agent:

1. Máy còn bật, còn mạng không.
2. `services.msc` → dịch vụ **LIS Agent** có đang chạy. Nếu dừng, bấm **Start**.
3. Mở `<PUBLIC_URL>/healthz` bằng trình duyệt trên máy đó. Không mở được là lỗi mạng hoặc máy chủ LIS đang tắt.

Trong lúc agent mất kết nối, máy xét nghiệm vẫn chạy và kết quả giữ trên đĩa (`C:\ProgramData\LIS Agent\cho-gui`). Có mạng lại thì agent
tự gửi, không ghi trùng. Kênh lệnh tự nối lại, không cần làm gì thêm.

## 14. Kênh lệnh đang đứt

Bảng điều khiển báo **"Kênh lệnh đang đứt — các lệnh tạm tắt"** trong khi trạng thái agent vẫn là **Đang kết nối**. Nghĩa là agent vẫn
báo về máy chủ, nhưng kênh để ra lệnh không nối được.

- **Vừa mới tắt hoặc mới khởi động agent**: đợi vài chục giây, kênh tự nối lại (thử sau 1, 2, 4... tối đa 60 giây). Sau khoảng 90 giây
  nếu agent thật sự đã tắt thì trạng thái chuyển **Mất kết nối** (mục 13).
- **Báo mãi không hết**: thường là reverse proxy chặn WebSocket. IT thêm đường `/agent/v1/channel` cho WebSocket đi qua
  ([HUONG-DAN-CAI-DAT.md](../HUONG-DAN-CAI-DAT.md), mục 6). Sửa xong, đợi tối đa 1 phút là hết báo.

Trong lúc kênh đứt, kết quả xét nghiệm vẫn lên web bình thường.

## 15. Lệnh "Hết hạn" hoặc "Không rõ kết quả"

Xem ở **Lịch sử lệnh** trong bảng điều khiển agent.

- **Hết hạn**: agent không nhận lệnh trong 60 giây. Lệnh **chưa** chạy. Chờ agent nối lại (mục 13, 14) rồi ra lệnh lại.
- **Không rõ kết quả**: agent mất kết nối khi đang chạy lệnh, hoặc máy chủ vừa khởi động lại. Không biết lệnh đã làm xong chưa
  và lệnh **không tự chạy lại**. Xem tình trạng thực tế rồi quyết định: ví dụ sau **Gửi bù ngay**, nhìn cột **Chờ gửi** ở mục
  **Máy xét nghiệm**; sau **Nối lại**, nhìn cột **Đường truyền**. Cần thì ra lệnh lại; các lệnh này làm lại đều an toàn.

## 16. Cập nhật quay về bản cũ

Lệnh **Cập nhật lên x.y.z** báo **Lỗi**, và cột **Phiên bản** vẫn là bản cũ. Agent đã tự quay về bản đang chạy trước đó nên máy
xét nghiệm không bị ảnh hưởng quá vài phút.

Bấm vào dòng lệnh ở **Lịch sử lệnh** để đọc lý do. Hai lý do thường gặp:

- **"Bản mới không kết nối được máy chủ trong 2 phút — đã quay về bản cũ"**: bản mới chạy lên nhưng không nối lại được máy chủ.
  Kiểm tra máy tính agent có chặn bản mới (phần mềm diệt vi-rút, tường lửa theo tên tệp) không, và máy chủ có `LisAgent.exe`
  đúng bản trong thư mục `AGENT_DIST_DIR`.
- **"Cài bản mới lỗi: ... — đã quay về bản cũ"**: lỗi khi thay tệp (tệp bị khoá, thiếu quyền). Chép nguyên văn lý do khi báo IT.

Lỗi xảy ra sớm hơn (tải tệp hỏng, sai SHA-256, máy chủ chưa có tệp mới) cũng hiện ở đây; khi đó tệp đang chạy chưa bị đụng tới. Chỉ cần
sửa nguyên nhân rồi bấm **Cập nhật lên x.y.z** lại.

## 17. Khởi động lại báo lỗi

Lệnh **Khởi động lại** báo **Lỗi**: "Agent không chạy như dịch vụ nên không tự khởi động lại". Agent này đang chạy trong cửa sổ dòng lệnh
(console), không phải dịch vụ Windows, nên không ai bật lại nó sau khi dừng. Lệnh không làm gì cả.

Dùng cho máy thật: cài agent bằng tệp cài để chạy dịch vụ **LIS Agent** (xem [HUONG-DAN-CAI-DAT.md](../HUONG-DAN-CAI-DAT.md), mục 5.2).
Máy phát triển đang chạy console thì tắt cửa sổ rồi chạy lại bằng tay.

## 18. Thử cổng báo cổng bận

Kết quả **Thử cổng** là "COM3 đang bị chương trình khác mở (LIS cũ, phần mềm của hãng, terminal…)", hoặc ở mục **Cổng COM** tình trạng là
**Phần mềm khác đang giữ**. Cổng đang bị một chương trình **không phải agent** mở. Cách xử lý:

1. Tắt app LIS Windows cũ, phần mềm của hãng máy, hoặc terminal (PuTTY, Tera Term...) trên máy tính đó.
2. Bấm **Quét cổng COM** lại; tình trạng về **Trống** thì bấm **Thử cổng** lại.

Cổng đang do một máy xét nghiệm đã khai trên chính agent dùng thì **không** bị báo bận: agent tạm ngắt máy đó, nghe thử, rồi tự nối lại.

## 19. Liên hệ hỗ trợ

Khi báo lỗi, chuẩn bị sẵn:

1. Thông báo lỗi chi tiết hiện trên màn hình (chụp ảnh hoặc chép nguyên văn).
2. Tệp `loi.log` (mục 11), và tệp `.listrace` nếu lỗi liên quan máy xét nghiệm (mục 12).
3. Phiên bản Windows (ví dụ: Windows 10 22H2), phiên bản PostgreSQL, bước đang làm khi gặp lỗi.
