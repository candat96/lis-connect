# Hướng dẫn sử dụng LIS Connect

LIS Connect là phần mềm quản lý xét nghiệm (LIS) cho khoa xét nghiệm, cài trên Windows. Bộ tài
liệu này hướng dẫn **cài đặt, cấu hình và sử dụng hằng ngày** ở mức "Đợt 1 — Nền tảng" (phiên bản
hiện tại, nhánh `main`). Tài liệu kỹ thuật (thiết kế, kế hoạch triển khai, driver máy xét nghiệm)
nằm ở nơi khác — xem mục "Tài liệu liên quan" cuối trang.

## Mục lục

| Tài liệu | Nội dung | Dành cho |
|---|---|---|
| [`01-cai-dat.md`](01-cai-dat.md) | Cài PostgreSQL, lấy bản build, chạy lần đầu, wizard cài đặt, máy trạm thứ 2, sao lưu/khôi phục, gỡ cài đặt | **IT / quản trị hệ thống** |
| [`02-cau-hinh.md`](02-cau-hinh.md) | Từng tab Cài đặt (cơ sở, barcode/tem, máy in, CSDL, HIS), tài khoản & vai trò, danh mục dịch vụ, khoảng tham chiếu, quy đổi định tính, nhập Excel, máy xét nghiệm & map kênh | **Quản trị / trưởng khoa xét nghiệm** |
| [`03-su-dung.md`](03-su-dung.md) | Quy trình hằng ngày: tiếp nhận, nhập/duyệt kết quả, in phiếu, xử lý "Chưa ghép", phím tắt, câu hỏi thường gặp | **Nhân viên tiếp nhận, kỹ thuật viên xét nghiệm** |
| [`04-xu-ly-su-co.md`](04-xu-ly-su-co.md) | Các lỗi thường gặp và cách xử lý | **IT / người dùng khi gặp lỗi** |

## Nên đọc gì trước

- **Người cài đặt lần đầu (IT)**: đọc `01-cai-dat.md` từ đầu đến hết, sau đó phần "Kết nối CSDL"
  và "Kết nối HIS" trong `02-cau-hinh.md`.
- **Quản trị khoa xét nghiệm** (người sẽ nhập danh mục dịch vụ, tài khoản nhân viên): đọc
  `02-cau-hinh.md` — đặc biệt mục Danh mục dịch vụ, Khoảng tham chiếu, Nhập Excel.
- **Nhân viên tiếp nhận / kỹ thuật viên** dùng hằng ngày: chỉ cần `03-su-dung.md`.
- **Khi gặp lỗi**: `04-xu-ly-su-co.md`, kèm tệp `loi.log` khi báo cho IT.

## Phạm vi phiên bản hiện tại (Đợt 1 — Nền tảng)

Bộ tài liệu này chỉ mô tả những gì **đã có** trong bản Đợt 1. Các tính năng dưới đây **chưa có**,
sẽ được bổ sung ở các đợt sau — nếu gặp tài liệu hoặc hướng dẫn nào mô tả khác, đó là tính năng
tương lai, không áp dụng cho bản đang chạy:

- **Đợt 2**: dịch vụ nền Windows (tự chạy khi khởi động máy), kết nối thật tới máy xét nghiệm
  (nhận kết quả qua LAN/USB), xem log thô trong giao diện.
- **Đợt 3**: đồng bộ thật với HIS (kéo chỉ định, đẩy kết quả, đồng bộ nhân sự) — Đợt 1 chỉ lưu
  cấu hình kết nối và cho "Thử kết nối".
- **Đợt 5**: bộ cài `.msi` chuyên nghiệp — Đợt 1 chạy bằng tệp `.exe` tự chứa, giải nén từ `.zip`.

## Ảnh minh họa

Các ảnh chụp màn hình trong các tài liệu này (thư mục [`anh/`](anh/)) dùng **dữ liệu giả lập**
(tên người bệnh, kết quả đều là dữ liệu demo), không chứa dữ liệu người bệnh thật.

## Tài liệu liên quan (không thuộc bộ hướng dẫn sử dụng)

- [`docs/CHAY-THU-WINDOWS.md`](../CHAY-THU-WINDOWS.md) — hướng dẫn chạy thử nhanh, bản tóm tắt.
- [`docs/superpowers/specs/2026-11-03-lis-windows-design.md`](../superpowers/specs/2026-11-03-lis-windows-design.md) — bản thiết kế kỹ thuật đầy đủ.
- [`docs/drivers/`](../drivers/README.md) — tài liệu giao tiếp với từng hãng máy xét nghiệm (dùng ở Đợt 2).
