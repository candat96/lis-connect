# Cài đặt LIS Connect Agent tại phòng xét nghiệm

## Trước khi tới cơ sở

1. Tạo cơ sở trên Core:
   `pnpm --filter @lis/core admin create-tenant bvA "Bệnh viện A"`
   Ghi lại `tenant id` in ra màn hình.
2. Cấp khoá cho từng máy tính sẽ cài Agent:
   `pnpm --filter @lis/core admin issue-key bvA "PC phòng sinh hóa"`
   **Khoá chỉ hiện một lần duy nhất, ngay lúc cấp phát.** Nó không được lưu ở
   dạng đọc lại được — database chỉ giữ hash — nên nếu không chép lại ngay,
   cách duy nhất để lấy lại là thu hồi khoá cũ và cấp khoá mới. Chép khoá vào
   nơi lưu trữ an toàn (trình quản lý mật khẩu của đội triển khai), không dán
   vào chat hay ghi ra giấy để lại tại cơ sở.

## Đóng gói bộ cài (thực hiện trên máy Windows)

`pnpm --filter @lis/agent package` sinh ra
`apps/agent/dist/LIS-Connect-Agent-<phiên bản>-setup.exe`.

Trước khi chạy `package`, nếu vừa `pnpm --filter @lis/agent build` hoặc
`pnpm test` trên cùng máy, phải chạy
`pnpm --filter @lis/agent rebuild better-sqlite3` trước — mô-đun native này bị
biên dịch chéo qua lại giữa ABI Node (dùng khi chạy test) và ABI Electron
(dùng khi Agent thật sự chạy); đóng gói với sai ABI khiến Agent crash ngay khi
khởi động trên máy khách với lỗi kiểu "was compiled against a different
Node.js version".

## Tại phòng xét nghiệm

1. Chạy bộ cài `LIS-Connect-Agent-<phiên bản>-setup.exe`.
2. Mở tab **Kết nối Core**, nhập địa chỉ Core và khoá vừa cấp. Bấm Lưu.
   Đèn ở góc phải chuyển xanh là đã thông.
3. Mở tab **Máy xét nghiệm**, thêm từng máy:
   - Máy nối dây COM: chọn cổng trong danh sách (tên thiết bị giúp phân biệt các
     bộ USB-Serial), đặt baudrate đúng như cấu hình trên máy xét nghiệm.
   - Máy nối mạng: nhập IP và cổng; chọn "Agent nối tới máy" hoặc "Máy nối vào
     Agent" theo tài liệu của hãng.
   - Máy xuất file: chọn thư mục và mẫu tên file.
   Bấm **Test kết nối** trước khi lưu.
4. Cho máy chạy một mẫu thật hoặc mẫu chuẩn. Mở tab **Terminal thô** để xem byte
   đi về. Nếu thấy dữ liệu vào nhưng Web UI không hiện, phần lớn là do sai driver
   hoặc sai mã máy.

## Bắt buộc: giữ cho Agent sống sau khi khởi động lại

Agent là ứng dụng chạy trong phiên đăng nhập của người dùng, nên máy khởi động lại
mà dừng ở màn hình đăng nhập thì Agent **không chạy** và máy xét nghiệm gửi kết quả
vào hư không.

Bộ cài đã đăng ký sẵn tác vụ Task Scheduler tên `LIS Connect Agent`, trigger
**At log on** (xem `apps/agent/build/installer.nsh` và
`apps/agent/build/install-autostart.ps1`). Tác vụ này chỉ chạy khi có người
**đăng nhập** — nó không tự chạy ở màn hình đăng nhập. Vì vậy còn phải làm
thêm hai việc, và cả hai đều **bắt buộc**, không phải tuỳ chọn:

1. Bật **tự động đăng nhập Windows** cho tài khoản dùng chung của phòng xét
   nghiệm (`netplwiz`, bỏ dấu tích "Users must enter a user name and
   password", hoặc cấu hình `AutoAdminLogon` trong Registry). Nếu bỏ qua bước
   này, máy khởi động lại (mất điện, cập nhật Windows, v.v.) sẽ dừng ở màn
   hình đăng nhập vô thời hạn, tác vụ Task Scheduler không bao giờ được kích
   hoạt, và Agent không chạy cho tới khi có người tới bấm đăng nhập bằng tay.
2. Đăng ký tác vụ Task Scheduler `LIS Connect Agent` — bộ cài đã tự làm việc
   này, nhưng phải **kiểm tra lại bằng tay** sau khi cài: mở Task Scheduler,
   xác nhận tác vụ tồn tại với trigger "At log on", rồi khởi động lại máy và
   xác nhận Agent tự chạy, đèn Core chuyển xanh trong vòng một phút.

Nếu chính sách máy trạm của bệnh viện không cho tự động đăng nhập, báo lại để cân
nhắc bản chạy dạng Windows Service.

## Khi cần hỗ trợ từ xa

Tab **Terminal thô** cho thấy chính xác máy đang nói gì. Chụp màn hình tab này,
kèm mã máy và thời điểm, là đủ để chẩn đoán phần lớn sự cố.

## Kiểm tra sau khi cài (checklist trên máy Windows sạch)

Quy trình dưới đây mô tả cách kiểm chứng bộ cài trên một máy Windows **chưa
từng cài LIS Connect Agent**. Tính tới thời điểm viết tài liệu này, quy trình
này **chưa được chạy thử thật** vì môi trường phát triển hiện tại là macOS
(xem phần "Còn nợ" trong report của Task 17).

1. Chạy bộ cài. Ứng dụng mở lên.
2. Mở Task Scheduler, xác nhận có tác vụ `LIS Connect Agent` với trigger
   **At log on**.
3. Cấu hình Core và một máy TCP trỏ tới máy giả lập (`@lis/simulator`) chạy
   trên cùng máy.
4. Đóng cửa sổ ứng dụng — icon còn ở khay hệ thống, dữ liệu vẫn chảy lên Core.
5. Khởi động lại Windows, đăng nhập — Agent tự chạy, đèn Core chuyển xanh
   trong vòng một phút.
6. Nếu có sẵn bộ `com0com`: tạo cặp cổng ảo COM10/COM11, cấu hình một máy
   serial trên COM10, dùng PuTTY gửi vài byte vào COM11, xác nhận tab
   Terminal thô hiện đúng byte đó.
