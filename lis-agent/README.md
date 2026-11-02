# LIS Agent

Phần mềm trung gian cài trên **máy Windows cạnh máy xét nghiệm**. Một file `lis-agent.exe`, không cần cài thêm
runtime, chạy được từ **Windows 7 SP1 đến Windows 11** (32 và 64 bit).

- Nhận chỉ định từ **HIS/CIS bất kỳ** qua REST API, hoặc **nhập tay** trên giao diện.
- Trả lời máy khi máy quét SID hỏi chỉ định (ASTM host query).
- Nhận kết quả từ máy: **ASTM E1394** (cổng COM hoặc TCP), **HL7 v2** qua MLLP (TCP).
- Trả kết quả về HIS/CIS: **webhook** có ký HMAC, hoặc HIS **tự kéo** qua API. Mất mạng thì giữ lại và gửi lại.
- Giao diện web tại chỗ: chỉ định, kết quả (gán SID cho kết quả lạc), cấu hình máy và ánh xạ mã, kết nối HIS, nhật ký.

## Cài đặt trên Windows

1. Chép `lis-agent-amd64.exe` (máy 32 bit dùng `lis-agent-386.exe`) vào `C:\LisAgent\lis-agent.exe`.
2. Mở **Command Prompt bằng quyền Administrator**:
   ```
   cd C:\LisAgent
   lis-agent.exe install
   ```
   Agent chạy thành **Windows Service** "LIS Agent": tự chạy khi bật máy, **không cần ai đăng nhập**, tự khởi
   động lại khi lỗi. Dữ liệu ở `C:\LisAgent\data`.
3. Mở `http://localhost:8090` (hoặc `http://<IP máy>:8090` từ máy khác trong mạng), đặt mật khẩu quản trị.
4. Tab **Máy xét nghiệm**: thêm máy, chọn giao thức, cổng COM/TCP, khai báo ánh xạ mã máy ↔ mã HIS.
5. Tab **Kết nối HIS/CIS**: tạo khóa API, nhập địa chỉ webhook nhận kết quả, bấm **Thử gửi tới HIS**.
6. Mở cổng 8090 trên Windows Firewall cho máy chủ HIS gọi vào (chỉ trong mạng nội bộ).

Lệnh khác: `lis-agent.exe uninstall | start | stop`. Chạy thử không cài dịch vụ: `lis-agent.exe` (Ctrl+C để dừng).
Tham số: `-listen :8090`, `-data <thư mục dữ liệu>`.

Windows 7: cài bản cập nhật hỗ trợ TLS 1.2 (KB3140245) nếu webhook HIS dùng HTTPS.

## Tích hợp HIS/CIS

Tài liệu API đầy đủ (kèm ví dụ JSON) hiển thị ngay trong tab **Kết nối HIS/CIS** của Agent. Tóm tắt:

| Việc | Gọi |
| --- | --- |
| Gửi/cập nhật chỉ định theo SID | `POST /api/v1/orders` |
| Hủy chỉ định | `POST /api/v1/orders/{sid}/cancel` |
| Xem chỉ định | `GET /api/v1/orders/{sid}` |
| Kéo kết quả | `GET /api/v1/results?since=<RFC3339>` rồi `POST /api/v1/results/ack {"ids":[...]}` |
| Webhook kết quả | Agent `POST` tới địa chỉ HIS khai báo, header `X-Signature: sha256=<HMAC>` |
| Kiểm tra sống | `GET /api/v1/health` |

Mọi lời gọi kèm header `X-Api-Key`. Kết quả là số liệu máy gửi, chưa duyệt: HIS/CIS để ở trạng thái chờ duyệt.

## Phát triển

```
go install golang.org/dl/go1.20.14@latest && go1.20.14 download
go1.20.14 test ./...        # gồm test đầu–cuối giả lập máy ASTM/HL7 và webhook HIS
./build.sh                   # ra dist/lis-agent-amd64.exe, dist/lis-agent-386.exe
```

Giữ `go 1.20` trong `go.mod` và các thư viện ở phiên bản tương thích (bbolt 1.3.9, go.bug.st/serial 1.6.2,
x/sys 0.15.0) để còn chạy trên Windows 7.
