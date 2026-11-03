# LIS Connect cho Windows — bản thiết kế

Ngày: 03/11/2026. Repo: `lis-connect` (code cũ đã xóa, còn trong lịch sử git ở commit `87792d0`).
Tài liệu driver máy: [`docs/drivers/README.md`](../../drivers/README.md).

Quy ước ghi nguồn trong tài liệu này:
- Mục **người dùng chốt** là yêu cầu của chủ dự án.
- Mục **Claude đề xuất** là lựa chọn của người viết thiết kế. Người dùng đã giao quyền quyết, nhưng vẫn có thể đổi.

## 1. Mục đích và lý do

Khoa xét nghiệm cần một phần mềm cài trên máy Windows, làm được hai việc:

1. **Nối máy xét nghiệm hai chiều.**
   - Đẩy chỉ định xuống máy: máy quét barcode, hỏi phần mềm, phần mềm trả về các xét nghiệm cần chạy.
   - Nhận kết quả từ máy về.
2. **Quản lý chỉ định và kết quả**, theo một trong hai chế độ:
   - **Có HIS:** phần mềm là cầu nối. Nhận chỉ định và barcode từ HIS, trả kết quả về HIS. Người dùng làm việc chủ yếu trên HIS.
   - **Không có HIS:** phần mềm là một LIS đầy đủ. Tự nhập chỉ định, cấp barcode, nhận kết quả, duyệt và in phiếu.

Hệ LIS cũ đang chạy ở BVĐK 10 (khảo sát ở `~/Downloads/lis-tham-khao/khao-sat/lis-hien-huu.md`) có ba lỗi gây sai kết quả mà không ai hay. Phần mềm mới phải chặn được cả ba **bằng thiết kế**:
- Một mã kênh máy map vào hai xét nghiệm, rồi gỡ trùng theo thứ tự chữ cái. Hậu quả: kết quả về nhầm xét nghiệm.
- Khớp barcode theo đuôi số trong 10 ngày. Hậu quả: gán nhầm người bệnh.
- Khoảng tham chiếu theo tuổi/giới tuy có bảng nhưng bảng rỗng. Hậu quả: mọi người bệnh dùng chung một ngưỡng.

Thêm một bài học từ code cũ: các driver trước đây chưa từng được kiểm chứng với máy thật, vì bản ghi thô trong kho đều do giả lập sinh ra. Vì vậy **ghi log dữ liệu thô là tính năng lõi**, không phải phần phụ.

## 2. Mục tiêu, ngoài phạm vi, điều tuyệt đối tránh

**Mục tiêu của bản đầu (người dùng chốt):**
- Cài trên Windows, chạy nền. Đóng cửa sổ phần mềm thì kết nối máy và kết nối HIS vẫn chạy.
- Kết nối được CSDL trên máy hoặc CSDL cloud. **Mỗi cơ sở một CSDL.**
- Màn cài đặt lần đầu: cấu hình kết nối CSDL và tạo tài khoản quản trị đầu tiên.
- Nhận chỉ định và barcode từ HIS, hoặc nhập chỉ định tay.
- Danh mục dịch vụ (có dịch vụ cha với chỉ số con), danh mục máy, map máy với chỉ số.
- Kết nối máy hai chiều.
- Trả kết quả về HIS hoặc in ngay trên phần mềm.
- **Tích hợp HIS cấu hình được:** đường API nhận chỉ định, đường trả kết quả, cách xác thực của HIS.
- Tự quản lý tài khoản đăng nhập. Có thể đồng bộ nhân sự từ HIS.
- **Ghi log dữ liệu thô để debug.**

**Ngoài phạm vi bản đầu (Claude đề xuất, người dùng đồng ý):**
- Nội kiểm QC và biểu đồ Levey-Jennings. Bản đầu chỉ tách riêng và lưu kết quả QC do máy gửi.
- Báo cáo thống kê, thu tiền, ký số.
- Đồng bộ dữ liệu giữa nhiều cơ sở.
- Driver cho các máy chưa có tài liệu (hàng ❌ trong `docs/drivers/README.md`).

**Điều tuyệt đối tránh:** dù phần mềm "chạy được", vẫn coi là thất bại nếu có một trong các điều sau.
- Kết quả gán vào sai người bệnh hoặc sai chỉ số mà không có cảnh báo.
- Mất kết quả máy đã gửi, vì không ACK kịp hoặc phần mềm đang tắt.
- Không truy được bản tin gốc của một kết quả khi cần đối chiếu.
- Lẫn mẫu QC/chuẩn máy vào kết quả người bệnh.
- Dữ liệu người bệnh thật lọt vào code, test hoặc tài liệu.

## 3. Ràng buộc

- Chạy trên Windows 10/11 64-bit, máy trạm cấu hình phổ thông của khoa xét nghiệm.
- Máy xét nghiệm nối qua **cổng COM** (RS-232, có thể qua cáp USB-serial) hoặc **TCP**.
  - Qua TCP, phần mềm có thể phải làm server (máy là client) hoặc làm client (máy là server).
  - Chi tiết từng máy ở `docs/drivers/`.
- Một cơ sở có thể có **nhiều máy trạm** cùng trỏ vào một CSDL. Mỗi máy xét nghiệm cắm vào một máy trạm cụ thể.
- CSDL cloud có thể chậm hoặc mất kết nối. Không được vì thế mà mất kết quả máy gửi.
- Giao diện tiếng Việt có dấu, tối ưu bàn phím: nhập mã + Enter, Enter chuyển ô.
- Không đưa tài liệu PDF của hãng lên repo. Thư mục `docs/drivers/tai-lieu-hang/` đã được gitignore.
- Dữ liệu demo và test chỉ dùng dữ liệu giả: tên có chữ "Mẫu"/"Thử", số giả.

## 4. Kiến trúc (Claude đề xuất, người dùng đã đồng ý hướng chung)

```
 Máy xét nghiệm ──COM/TCP──▶ ┌──────────── Dịch vụ nền LIS (Windows Service) ────────────┐
                             │ Driver theo họ giao thức · Ghi log thô · Ghép kết quả     │
 HIS ◀──REST (cấu hình)───▶  │ Đồng bộ HIS (chỉ định vào, kết quả ra, nhân sự)           │
                             │ Hàng đợi cục bộ (SQLite) khi mất CSDL                     │
                             └───────────────┬───────────────────────────▲───────────────┘
                                             │ PostgreSQL                │ named pipe (trạng thái máy, log thô trực tiếp)
                                        ┌────▼────┐               ┌──────┴──────────────┐
                                        │  CSDL   │◀──────────────│ Ứng dụng giao diện  │
                                        │(cơ sở)  │               │ (Avalonia, icon khay)    │
                                        └─────────┘               └─────────────────────┘
```

- **Ngôn ngữ và nền tảng:** C# trên .NET 10.
  - Dịch vụ nền dùng Worker Service, chạy dạng Windows Service: tự khởi động cùng máy, Windows tự khởi động lại khi dịch vụ chết.
  - Giao diện dùng **Avalonia** (giao diện desktop kiểu WPF, chạy native trên Windows). Lúc lập kế hoạch Claude đổi từ WPF sang Avalonia vì máy phát triển là macOS: Avalonia cho phép chạy và chụp ảnh giao diện để kiểm tra ngay trên máy phát triển, còn sản phẩm cài trên Windows vẫn là ứng dụng Windows thật.
  - Lý do chọn .NET: hỗ trợ tốt nhất cho Windows Service, cổng COM (`System.IO.Ports`) và bộ cài đặt.
- **Dịch vụ nền** làm mọi việc phải chạy liên tục:
  - giữ kết nối máy;
  - ghi log thô;
  - ghép kết quả;
  - đồng bộ HIS.
  - Phải chạy được khi chưa ai đăng nhập Windows.
- **Ứng dụng giao diện** làm việc nghiệp vụ:
  - Đọc/ghi CSDL trực tiếp cho dữ liệu nghiệp vụ.
  - Hỏi dịch vụ nền trên cùng máy qua named pipe cho các thứ đang diễn ra: trạng thái máy, log thô theo thời gian thực, lệnh "kết nối lại máy".
  - Có icon dưới khay hệ thống. Đóng cửa sổ thì thu xuống khay.
- **CSDL:** PostgreSQL. Cài trên máy (bộ cài kèm tùy chọn cài PostgreSQL) hoặc dùng PostgreSQL trên cloud. Mỗi cơ sở một CSDL, nên không có cột "mã cơ sở" trong bảng.
- **Hàng đợi cục bộ:** mỗi dịch vụ nền có một tệp SQLite trên đĩa (`%ProgramData%\LIS Connect\`).
  - Bản tin từ máy được ghi vào đây **trước khi ACK máy**, rồi mới đẩy lên CSDL.
  - CSDL mất kết nối thì kết quả nằm chờ trong hàng đợi, không mất.
- **Nhiều máy trạm:**
  - Mỗi máy trạm có một "mã máy trạm" khai báo lúc cài đặt.
  - Mỗi máy xét nghiệm được gán cho một máy trạm. Chỉ dịch vụ nền của máy trạm đó mở cổng tới máy xét nghiệm.
  - Việc đồng bộ HIS chỉ một dịch vụ nền làm tại một thời điểm, giữ bằng khóa trong CSDL (`pg_advisory_lock`). Máy trạm đang giữ khóa tắt thì máy khác tự nhận việc.
- **Cấu hình trên máy:**
  - Chuỗi kết nối CSDL và mã máy trạm nằm trong `%ProgramData%\LIS Connect\config.json`. Mật khẩu được mã hóa bằng DPAPI (phạm vi máy).
  - Mọi cấu hình khác (danh mục, máy, map, tích hợp HIS) nằm trong CSDL, sửa từ giao diện.

## 5. Cài đặt lần đầu (người dùng chốt)

Khi chưa có cấu hình, ứng dụng mở trình hướng dẫn gồm 3 bước:

1. **Kết nối CSDL**
   - Chọn "CSDL trên máy này" hoặc "CSDL cloud".
   - Nhập máy chủ, cổng, tên CSDL, tài khoản, mật khẩu, tùy chọn SSL.
   - Nút **Thử kết nối**. Chưa thử được thì không cho đi tiếp.
   - CSDL trống thì phần mềm tự tạo bảng (chạy migration).
   - Đặt mã máy trạm (vd `XN-01`).
2. **Tài khoản quản trị đầu tiên.** Chỉ hiện khi CSDL chưa có tài khoản nào.
   - Nhập tên đăng nhập, họ tên, mật khẩu (nhập hai lần, tối thiểu 8 ký tự).
3. **Thông tin cơ sở**, dùng cho phiếu in: tên, địa chỉ, điện thoại, logo.
   - Bước này bỏ qua nếu CSDL đã có dữ liệu.

Máy trạm thứ hai trở đi chỉ làm bước 1 rồi đăng nhập. Sau khi cài, quản trị sửa được kết nối CSDL trong mục Cài đặt (phải đăng nhập lại).

## 6. Nghiệp vụ

### 6.1. Luồng khi không có HIS (người dùng chốt)

1. **Nhận chỉ định**
   - Nhập thông tin cơ bản của người bệnh: họ tên, ngày sinh hoặc năm sinh, giới, địa chỉ, SĐT.
   - Gõ tên hoặc mã thì tìm được người bệnh cũ.
   - Chọn dịch vụ bằng mã + Enter.
   - Chọn dịch vụ cha thì tự kéo theo các chỉ số con.
2. **Cấp barcode và in tem** (Claude đề xuất, người dùng đồng ý)
   - Gom dịch vụ theo **loại mẫu** (EDTA, serum, citrat, nước tiểu…). Mỗi loại mẫu một barcode, in một tem riêng.
   - Có tùy chọn cấu hình "một barcode chung cho cả phiếu".
   - Barcode chỉ gồm chữ số, là duy nhất trong CSDL. Dạng mặc định: `yyMMdd` + số thứ tự trong ngày + hậu tố loại mẫu, độ dài cấu hình được.
   - Lý do: nhiều máy chỉ nhận ID 8–10 chữ số. BC-3200 nhận 8 hoặc 10, HLC-723 G11 nhận 8.
3. **Chạy máy**
   - Máy hai chiều quét barcode thì hỏi phần mềm, phần mềm trả về các xét nghiệm của ống đó (theo map).
   - Máy một chiều thì kỹ thuật viên nhập mã mẫu trên máy.
4. **Kết quả về:** ghép theo barcode **so khớp chính xác** và theo map máy ↔ chỉ số (mục 7).
5. **Duyệt** (mục 6.3).
6. **In phiếu kết quả.**

### 6.2. Luồng khi có HIS

Chỉ định, người bệnh và barcode đến từ HIS. Từ bước chạy máy trở đi giống luồng không có HIS. Sau khi duyệt, phần mềm trả kết quả về HIS. Vẫn in được phiếu trên phần mềm nếu cơ sở muốn.

### 6.3. Duyệt kết quả (Claude đề xuất, người dùng đồng ý)

- Kết quả từ máy về ở trạng thái **Chờ duyệt**.
- Người có quyền duyệt xem kết quả, sửa tay khi cần, rồi bấm **Duyệt**. Sửa tay bắt buộc ghi lý do và lưu cả giá trị cũ.
- Chỉ kết quả **đã duyệt** mới in được (bản chính thức) hoặc trả HIS.
  - Bản in thử khi chưa duyệt in chữ "CHƯA DUYỆT".
- **Tự duyệt** (tùy chọn theo từng dịch vụ): mọi chỉ số trong khoảng tham chiếu và máy không gắn cờ thì tự chuyển sang Đã duyệt.
- **Hủy duyệt:** bắt buộc nhập lý do. Nếu đã trả HIS thì gửi lại bản sửa. Lịch sử duyệt/hủy duyệt được giữ.

### 6.4. Kết quả không ghép được (Claude đề xuất)

Các trường hợp sau đưa vào hàng **Chưa ghép**, có lý do cụ thể. **Không bao giờ tự đoán.**
- Barcode không có trong CSDL.
- Barcode có nhưng không có chỉ định dịch vụ đó.
- Mã chỉ số máy gửi chưa map.

Người dùng gán tay vào đúng mẫu/chỉ số và hệ thống ghi lại ai gán.

Kết quả của ô đã duyệt sẽ không ghi đè. Bản mới được giữ ở dạng "kết quả chờ thay thế", người dùng chọn bản nào.

Mẫu QC/chuẩn máy (nhận biết theo từng driver) được tách sang bảng QC, không vào kết quả người bệnh.

### 6.5. Khoảng tham chiếu và cờ

- Khoảng tham chiếu khai theo: chỉ số, giới (nam/nữ/chung), tuổi từ–đến tính bằng **ngày** (để biểu diễn được trẻ sơ sinh), và tùy chọn theo máy.
- Phần mềm tự chọn khoảng phù hợp nhất theo người bệnh rồi tính cờ **thấp/cao**. Có thêm ngưỡng **nguy hiểm** (critical) để cảnh báo đỏ.
- Kết quả định tính dùng bảng quy đổi theo máy + chỉ số, ví dụ `neg` → "Âm tính", `1+` → "Dương tính 1+".
  - Quy đổi này có thể đổi cả cờ: "Âm tính" không bị tô bất thường.
- Kết quả số lưu dạng **chuỗi thập phân giữ nguyên số chữ số**: `4.80` khác `4.8`. Không dùng số thực dấu phẩy động.
- Làm tròn theo số chữ số thập phân khai ở map.
- Dạng hiệu giá (`1:160`) được giữ nguyên để hiển thị, quy đổi ra số để so ngưỡng.

### 6.6. Phiếu in (Claude đề xuất)

- **Phiếu kết quả:**
  - Phần đầu: logo, tên cơ sở.
  - Thông tin người bệnh.
  - Bảng chỉ số nhóm theo dịch vụ cha, chỉ số con thụt vào. Có đơn vị, khoảng tham chiếu; giá trị bất thường in đậm kèm dấu ↑ hoặc ↓.
  - Người duyệt, thời điểm duyệt.
  - Mẫu chỉnh được: logo, tên, chữ ký.
- **Tem barcode:**
  - Mã vạch Code 128, họ tên, năm sinh, loại mẫu, ngày.
  - Khổ tem cấu hình được, in ra máy in tem (in nhiệt).

### 6.7. Vai trò (Claude đề xuất)

| Vai trò | Được làm |
|---|---|
| Quản trị | Mọi thứ, gồm danh mục, máy, map, tài khoản, kết nối HIS/CSDL, cài đặt |
| Tiếp nhận | Nhập/sửa chỉ định, in tem |
| Kỹ thuật viên | Xem kết quả, nhập tay, sửa có lý do, duyệt, ghép tay kết quả chưa ghép, theo dõi máy, xem log thô |
| Trưởng khoa | Như kỹ thuật viên, thêm hủy duyệt |

Một người có thể có nhiều vai trò.

Tài khoản:
- Mật khẩu băm Argon2id.
- Sai 5 lần thì khóa 15 phút.
- Nhân sự đồng bộ từ HIS (mục 8) được tạo tài khoản ở trạng thái chưa kích hoạt, quản trị gán vai trò rồi mới dùng được.

## 7. Danh mục và map (người dùng chốt phạm vi, Claude đề xuất chi tiết)

- **Dịch vụ xét nghiệm**
  - Gồm: mã, tên, nhóm (huyết học, sinh hóa, miễn dịch, nước tiểu…), loại mẫu, đơn vị, thứ tự in, kiểu kết quả (số/định tính/chữ), số chữ số thập phân, tự duyệt có/không.
  - Dịch vụ **cha** có danh sách **chỉ số con**, có thứ tự. Chỉ số con cũng là một dòng dịch vụ (có mã, đơn vị, khoảng tham chiếu).
  - Có mã dịch vụ bên HIS (map mã HIS ↔ mã LIS), để nhận chỉ định từ HIS bằng mã của HIS.
- **Máy xét nghiệm**
  - Gồm: tên, hãng/model, **họ giao thức** + **cấu hình driver**, kiểu kết nối, máy trạm gắn với, bật/tắt.
  - Kiểu kết nối:
    - COM: cổng, baud, data bits, parity, stop bits, bắt tay.
    - TCP: vai trò server/client, IP, cổng.
- **Map máy ↔ chỉ số**
  - Mỗi dòng: máy + mã kênh máy gửi → chỉ số LIS. Kèm theo:
    - số chữ số làm tròn;
    - hệ số quy đổi đơn vị (vd % gửi dạng phân số thì nhân 100);
    - có giữ dấu `<` / `>` không;
    - có gửi xuống máy khi máy hỏi chỉ định không.
  - **Ràng buộc duy nhất `(máy, mã kênh)`** ở CSDL. Một mã kênh không thể trỏ vào hai chỉ số.
  - Bảng quy đổi định tính gắn vào map.
- **Nhập danh mục từ Excel:** dịch vụ và map, để khởi tạo nhanh. Có sẵn bộ mẫu từ khảo sát BVĐK 10, đã làm sạch các dòng map trùng hoặc sai.

## 8. Tích hợp HIS (người dùng chốt: cấu hình được)

Phần mềm không gắn cứng vào HIS nào. Quản trị khai báo **một hồ sơ tích hợp**. HIS-PROMAX là hồ sơ dựng sẵn đầu tiên.

- **Xác thực ra HIS:**
  - không xác thực;
  - khóa API ở header (tên header tự đặt);
  - Basic;
  - Bearer token lấy từ API đăng nhập (khai đường dẫn, thân bản tin, đường JSON lấy token, thời hạn), tự lấy lại khi hết hạn.
- **Nhận chỉ định**, hỗ trợ cả hai cách:
  - **LIS hỏi HIS định kỳ (pull).** Mặc định, vì LIS thường nằm trong mạng nội bộ còn HIS có thể ở cloud. Khai báo URL, tham số thời gian/con trỏ, chu kỳ.
  - **HIS đẩy vào LIS (push).** Dịch vụ nền mở một API REST nhỏ (cổng cấu hình được) cho HIS gọi, xác thực bằng khóa API do LIS cấp. Dùng khi HIS gọi tới được máy trạm.
- **Ánh xạ trường:**
  - Dữ liệu HIS (JSON) được ánh xạ vào mô hình chỉ định của LIS (người bệnh, barcode, danh sách mã dịch vụ HIS) bằng bảng ánh xạ đường JSON → trường.
  - Hồ sơ HIS-PROMAX khai sẵn bảng này.
- **Trả kết quả:**
  - Sau khi duyệt, đẩy kết quả lên URL của HIS: theo barcode/mã chỉ định, từng chỉ số có giá trị, đơn vị, khoảng tham chiếu, cờ, người duyệt.
  - Hủy duyệt rồi duyệt lại thì gửi bản sửa.
  - Gửi qua **hàng chờ có thử lại**, gửi lỗi thì lùi dần thời gian thử. Màn quản trị xem và bấm gửi lại được.
- **Đồng bộ nhân sự:** tùy chọn. Định kỳ lấy danh sách nhân sự từ HIS (mã, họ tên, chức danh) để tạo/cập nhật tài khoản (mục 6.7).
- **Đăng nhập bằng tài khoản HIS:** tùy chọn. Dùng API kiểm mật khẩu của HIS, như `POST /api/v1/integration/auth/verify` của HIS-PROMAX đã có.
- Mọi lần gọi HIS đều ghi nhật ký tích hợp: thời điểm, đường dẫn, mã HTTP, thời gian, lỗi. Thân bản tin chỉ lưu khi bật chế độ gỡ lỗi, vì có dữ liệu người bệnh.

## 9. Driver máy xét nghiệm (Claude đề xuất)

- Mỗi **họ giao thức** là một bộ "khung truyền + bộ phân tích + bộ dựng câu trả lời" viết một lần. Phần riêng từng máy chỉ là **cấu hình**: tham số cổng, vị trí trường, bảng mã, timeout.
- Các họ giao thức lấy theo `docs/drivers/README.md` mục 2:
  - `hl7-mllp`
  - `astm-e1394` qua COM và qua TCP
  - `beckman-au`
  - `sysmex-ca`
  - `tosoh-g8`
  - `dirui-h` (dòng chữ cố định)
  - `fixed-offset` (Mindray BC-3200)
  - văn bản bản in
- Có thêm driver đặc biệt **"Chỉ nghe"**: mở cổng, ghi byte thô, không giải mã, không ACK. Dùng khi chưa biết máy nói gì.
- **Ưu tiên làm trước:**
  1. Ba máy của người dùng: Mindray BC-5380 (`hl7-mllp`), Beckman AU480 (`beckman-au`), Beckman Access 2 (`astm-e1394`).
  2. Các máy ✅ còn lại trong `docs/drivers/README.md`.
- **ACK trước, xử lý sau.** Driver ghi bản tin vào hàng đợi cục bộ rồi ACK ngay. Ví dụ BC-5380 chỉ chờ ACK 10 giây và không gửi lại.
- **Trả lời khi máy hỏi chỉ định** trong thời hạn máy chờ. Không có chỉ định thì trả lời "không có" theo đúng giao thức từng máy.
- Driver có dấu **"chưa kiểm chứng"** cho đến khi được đối chiếu với bản ghi thô của máy thật. Giao diện hiển thị dấu này.
- Mỗi driver có bộ test bằng bản tin mẫu lấy từ tài liệu hãng (đã thay dữ liệu giả), gồm cả các tình huống lỗi đã liệt kê ở mục "Kiểm thử" của từng tài liệu driver.
- Có **bộ giả lập máy** (công cụ dòng lệnh) đóng vai máy xét nghiệm: phát lại tệp log thô qua COM ảo hoặc TCP. Dùng để test và demo khi chưa có máy thật.

## 10. Log dữ liệu thô (người dùng chốt: bắt buộc)

- **Ghi gì:** mọi byte đi và về giữa phần mềm và từng máy, ghi nguyên văn. Mỗi bản ghi gồm:
  - thời điểm đến mili-giây;
  - chiều (máy → LIS / LIS → máy);
  - máy;
  - phiên kết nối;
  - các sự kiện kết nối (mở cổng, máy nối vào, mất kết nối, lỗi).
- **Bật/tắt theo từng máy.** Mặc định **bật**.
- **Ghi ở đâu:** trên đĩa máy trạm, trong `%ProgramData%\LIS Connect\rawlog\`.
  - Mỗi máy mỗi ngày một tệp, ghi nối tiếp.
  - Ghi trên đĩa thay vì CSDL để không phụ thuộc mạng/CSDL và không mất khi CSDL chậm.
- **Lưu giữ:** số ngày cấu hình được (mặc định 30), tự xóa tệp cũ. Có cảnh báo khi dung lượng vượt ngưỡng.
- **Xem trong giao diện:**
  - lọc theo máy, khoảng thời gian, phiên;
  - hiển thị ký tự điều khiển dạng `<STX>`, `<ETX>`, `<ACK>`, xem được dạng hex;
  - xem trực tiếp theo thời gian thực (qua named pipe);
  - ẩn được các ACK lẻ.
  - Máy trạm khác xem được log của máy trạm này khi cả hai cùng mạng, qua dịch vụ nền. Nếu không xem được thì dùng chức năng xuất tệp.
- **Giải mã thử:** chọn một đoạn log, chạy qua driver ở chế độ thử (không ghi CSDL) để xem driver hiểu ra kết quả gì.
- **Xuất/nhập:**
  - Xuất một khoảng log ra tệp `.listrace` (JSON lines, có hex) hoặc `.txt` dễ đọc, để gửi đi debug.
  - Nhập tệp `.listrace` từ nơi khác vào để xem, giải mã thử hoặc phát lại qua bộ giả lập.
- **Truy vết từ kết quả:** mỗi kết quả lưu tham chiếu tới bản tin thô sinh ra nó (máy, phiên, vị trí). Từ màn kết quả mở được đúng bản tin gốc khi log còn lưu.
- Log thô chứa dữ liệu người bệnh, nên:
  - chỉ vai trò Kỹ thuật viên trở lên xem được;
  - tệp xuất có cảnh báo "chứa dữ liệu người bệnh";
  - không đưa log thật vào repo.

## 11. Mô hình dữ liệu chính (Claude đề xuất, builder tinh chỉnh)

| Bảng | Nội dung |
|---|---|
| `patient` | Người bệnh: mã (tự sinh hoặc mã HIS), họ tên, ngày sinh/năm sinh, giới, địa chỉ, SĐT, nguồn (`lis`/`his`) |
| `order` | Phiếu chỉ định: người bệnh, nguồn, mã chỉ định HIS, bác sĩ/khoa chỉ định, chẩn đoán, thời điểm, trạng thái |
| `sample` | Mẫu/ống: **barcode (duy nhất)**, phiếu, loại mẫu, thời điểm lấy/nhận |
| `order_item` | Dòng chỉ định: phiếu, mẫu, dịch vụ (cha hoặc lẻ) |
| `result` | Kết quả từng chỉ số: dòng chỉ định, chỉ số, giá trị (chuỗi), đơn vị, khoảng tham chiếu đã áp, cờ, nguồn (máy/tay), máy, tham chiếu bản tin thô, trạng thái (`cho-duyet`, `da-duyet`, `huy-duyet`) |
| `result_history` | Lịch sử sửa/duyệt/hủy duyệt: giá trị cũ → mới, người, lý do |
| `unmatched_result` | Kết quả chưa ghép: máy, barcode, mã kênh, giá trị, lý do, trạng thái xử lý |
| `qc_result` | Kết quả QC/chuẩn máy (lưu thô, chưa phân tích) |
| `service` + `service_child` + `reference_range` + `qualitative_map` | Danh mục dịch vụ, cây cha–con, khoảng tham chiếu, quy đổi định tính |
| `instrument` + `instrument_channel` | Máy, cấu hình driver; map `(máy, mã kênh)` **duy nhất** → chỉ số |
| `workstation` | Máy trạm: mã, tên, lần cuối hoạt động |
| `app_user` + `user_role` | Tài khoản, vai trò |
| `his_profile` + `his_outbox` + `integration_log` | Hồ sơ tích hợp, hàng chờ gửi kết quả, nhật ký gọi HIS |
| `setting` | Cài đặt chung: thông tin cơ sở, định dạng barcode, mẫu in… |

Toàn bộ thay đổi CSDL đi qua migration có đánh phiên bản. Không sửa migration đã phát hành.

## 12. Giao diện (Claude đề xuất)

Các màn chính:
- **Tiếp nhận chỉ định:** form người bệnh + ô nhập dịch vụ bằng mã, rồi in tem.
- **Danh sách mẫu/kết quả:** lọc theo ngày, trạng thái, máy, nhóm dịch vụ.
- **Nhập/duyệt kết quả:** bảng chỉ số của một mẫu, cờ màu kèm chữ/ký hiệu (không chỉ dựa vào màu), duyệt bằng phím tắt.
- **Kết quả chưa ghép.**
- **Theo dõi máy:**
  - trạng thái kết nối từng máy (đèn + chữ);
  - bản tin gần nhất;
  - nút kết nối lại;
  - mở log thô.
- **Log dữ liệu thô** (mục 10).
- **Danh mục:** dịch vụ, máy, map, khoảng tham chiếu, nhập Excel.
- **Tích hợp HIS:** hồ sơ, thử kết nối, hàng chờ gửi, nhật ký.
- **Tài khoản và vai trò.**
- **Cài đặt:** thông tin cơ sở, barcode, mẫu in, CSDL, máy trạm.

Mọi màn hình đều phải dùng được bằng bàn phím.

## 13. Cài đặt và cập nhật (Claude đề xuất)

- **Bộ cài `.msi`** gồm dịch vụ nền, ứng dụng giao diện, bộ giả lập máy. Tùy chọn kèm PostgreSQL cho chế độ CSDL trên máy.
- **Cập nhật:** cài đè bản mới. Dịch vụ nền tự chạy migration khi khởi động. Không cài được nếu migration lỗi.

## 14. Phần để builder tự quyết

- Thư viện cụ thể: ORM/Dapper, thư viện in, thư viện barcode, thư viện UI WPF.
- Cấu trúc project/solution, đặt tên.
- Định dạng giao tiếp qua named pipe.
- Chi tiết UI ngoài các nguyên tắc ở mục 12.
- Thứ tự làm các driver ngoài 3 máy ưu tiên.

## 15. Quyết định bổ sung sau vòng rà soát (Claude đề xuất — người dùng kiểm khi đọc)

1. **Phía HIS-PROMAX.**
   - HIS-PROMAX (`his-be`) hiện chưa có API cho LIS kéo chỉ định hay nhận kết quả. API này do dự án mình làm, thành **một giai đoạn riêng** sau khi lõi LIS chạy được.
   - Hợp đồng JSON được định nghĩa trong kế hoạch, dựa trên mục 8.
   - Hồ sơ tích hợp HIS-PROMAX dựng sẵn theo đúng hợp đồng đó.
   - Trước khi có API thật, test bằng một HIS giả lập (stub) đi kèm repo.
2. **Barcode khi có HIS.**
   - Barcode do HIS cấp được dùng nguyên.
   - Barcode LIS tự cấp (chỉ định nhập tay) có **tiền tố riêng cấu hình được**, mặc định chữ số đầu là `9`, để không bao giờ trùng SID của HIS.
   - Độ dài barcode cấu hình được. Khi một máy chỉ nhận ID ngắn hơn (G11: 8 chữ số), quản trị đặt độ dài 8 cho loại mẫu chạy trên máy đó. Phần mềm cảnh báo khi barcode dài hơn giới hạn của máy được map.
   - Khi HIS cấp một barcode cho cả lượt khám, LIS chấp nhận: các ống dùng chung barcode, máy nào hỏi thì chỉ trả các chỉ số được map cho máy đó.
3. **Máy hỏi chỉ định khi mất CSDL.**
   - Mỗi dịch vụ nền giữ **bản sao chỉ định** trong SQLite cục bộ cho các máy gắn với máy trạm đó, gồm các barcode chưa có kết quả trong 7 ngày gần nhất. Bản sao làm mới mỗi khi có chỉ định mới hoặc định kỳ.
   - Máy hỏi thì đọc từ bản sao, không chờ CSDL. Bản sao chỉ để đọc nên không có xung đột.
   - Không có trong bản sao thì mới hỏi CSDL. CSDL không trả lời kịp thì trả "không có chỉ định" và ghi cảnh báo.
4. **Giao diện nối thẳng PostgreSQL** (đúng yêu cầu "kết nối đến CSDL trên máy hoặc cloud"). Giảm rủi ro bằng các biện pháp sau:
   - Tài khoản CSDL của ứng dụng chỉ có quyền đọc/ghi dữ liệu. Tạo bảng và migration dùng một tài khoản quản trị riêng, chỉ nhập lúc cài đặt hoặc nâng cấp.
   - CSDL cloud **bắt buộc SSL**.
   - Mật khẩu CSDL được mã hóa bằng DPAPI trên máy.
   - Phân quyền theo vai trò được kiểm trong ứng dụng.
   - Rủi ro còn lại: ai có quyền quản trị Windows trên máy trạm thì lấy được tài khoản CSDL. Rủi ro này được chấp nhận ở bản đầu và ghi vào tài liệu triển khai.
   - Hướng nâng cấp sau này: thêm lớp API ở giữa mà không đổi mô hình dữ liệu.
5. **API cho HIS đẩy vào (push)** chỉ mở trên **một máy trạm được chỉ định** trong hồ sơ tích hợp (địa chỉ cố định). Mặc định dùng pull. Chống nhận trùng bằng mã chỉ định HIS + phiên bản.
6. **Danh mục mẫu từ khảo sát BVĐK 10.**
   - Builder tự làm sạch từ `~/Downloads/lis-tham-khao/khao-sat/seed/` và `lis-server/seed/`. Đây là dữ liệu danh mục, không có người bệnh.
   - Bỏ các dòng map trùng hoặc sai đã liệt kê trong `docs/drivers/cac-may-astm-bvdk10.md`.
   - Không đưa bất kỳ dữ liệu kết quả hay người bệnh nào vào repo.
7. **Duyệt khi có HIS (người dùng chốt: duyệt ở LIS, đẩy về HIS).**
   - Kỹ thuật viên/trưởng khoa duyệt trên LIS. LIS chỉ gửi HIS kết quả **đã duyệt**.
   - HIS lưu và hiển thị/in, không duyệt lại.
   - Muốn sửa thì hủy duyệt trên LIS (có lý do) rồi duyệt lại. LIS gửi bản sửa, HIS ghi đè và giữ lịch sử.
   - Phía HIS-PROMAX (giai đoạn API HIS): mẫu nhận từ LIS được đánh dấu "kết quả từ LIS". Màn nhập/duyệt xét nghiệm của HIS khóa sửa/duyệt với các mẫu này, chỉ cho xem và in.
