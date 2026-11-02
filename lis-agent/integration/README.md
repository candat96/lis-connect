# Bộ tích hợp LIS Agent cho HIS/CIS

Dành cho đơn vị phát triển HIS/CIS muốn nối với LIS Agent. Không cần biết giao thức máy xét nghiệm (ASTM/HL7):
HIS chỉ gửi chỉ định và nhận kết quả bằng JSON qua HTTP.

| Tệp | Dùng để |
| --- | --- |
| `openapi.yaml` | Đặc tả API đầy đủ (OpenAPI 3) — mở bằng Swagger Editor, sinh client cho mọi ngôn ngữ |
| `postman_collection.json` | Gọi thử từng API (sửa biến `agent`, `apiKey`, `sid`) |
| `samples/send-order.sh` | Gửi thử một chỉ định bằng curl |
| `samples/webhook-node.cjs` | Mẫu nhận kết quả — Node.js, không thư viện ngoài |
| `samples/WebhookController.cs` | Mẫu nhận kết quả — ASP.NET Core |
| `samples/LisWebhookController.java` | Mẫu nhận kết quả — Spring Boot |
| `samples/webhook.php` | Mẫu nhận kết quả — PHP |
| `lis-sim` (`cmd/lis-sim`) | Máy xét nghiệm giả: hỏi chỉ định và gửi kết quả qua TCP — thử khi chưa có máy thật |

## Luồng

1. Lấy mẫu, dán mã vạch **SID** → HIS `POST /api/v1/orders` (SID + mã xét nghiệm của HIS).
2. Máy quét SID hỏi Agent → Agent trả danh sách xét nghiệm (đổi sang mã máy theo bảng ánh xạ cấu hình tại Agent).
3. Máy gửi kết quả → Agent đổi về mã HIS, `POST` tới địa chỉ webhook của HIS (hoặc HIS kéo `GET /results`).
4. HIS lưu ở trạng thái **chờ duyệt**; bác sĩ xét nghiệm duyệt mới trả người bệnh.

Quy ước bên HIS khi nhận webhook:
- Kiểm `X-Signature` trên **nguyên văn body** trước khi parse (mẫu đã làm sẵn).
- Trả **HTTP 2xx** khi đã lưu; mọi mã khác Agent sẽ gửi lại — nên **chống trùng theo `resultId`**.
- Cùng SID có thể về nhiều lần (máy chạy lại): ghi đè theo `testCode`.
- `testCode` trống = máy gửi chỉ số chưa khai báo ánh xạ ở Agent → bổ sung ánh xạ ở tab "Máy xét nghiệm".
- Bản tin `{"type":"ping"}` là nút "Thử gửi tới HIS" — chỉ cần trả 2xx.

## Thử cả vòng trên một máy

```
# 1. Chạy Agent, vào http://localhost:8090: đặt mật khẩu; tab Kết nối HIS/CIS: tạo khóa API, webhook
#    http://127.0.0.1:3000/lis/results, khóa ký; tab Máy xét nghiệm: thêm máy ASTM, "TCP – Agent chờ máy nối vào",
#    cổng 5000, ánh xạ GLU-P→GLU, UREA→URE.
lis-agent
# 2. HIS giả nhận kết quả
SECRET=<khóa ký> node integration/samples/webhook-node.cjs
# 3. HIS gửi chỉ định
AGENT=http://localhost:8090 KEY=<khóa API> integration/samples/send-order.sh 2611020055
# 4. Máy giả hỏi chỉ định rồi gửi kết quả
lis-sim -addr 127.0.0.1:5000 -sid 2611020055 -query -results "GLU-P=5.6,UREA=9.8:H"
# → webhook in: Kết quả SID 2611020055 GLU=5.6 N, URE=9.8 H
```

Máy HL7: `lis-sim -addr 127.0.0.1:5001 -proto hl7 -sid 2611020055 -results "WBC=12.3:H,RBC=4.5"`.

## Đăng nhập dùng chung tài khoản HIS/CIS

Người dùng LIS đăng nhập bằng tài khoản HIS: Agent gửi tên đăng nhập + mật khẩu tới API đăng nhập của HIS, HIS trả
đúng/sai. Agent không lưu mật khẩu HIS.

HIS-PROMAX có sẵn: `POST /api/v1/integration/auth/verify`, header `X-Integration-Key` (bật ở Cài đặt kết nối →
`SSO_TICH_HOP`). HIS khác chỉ cần một API tương tự:

```
POST <địa chỉ>            header <tên header khóa>: <khóa>
{"username":"...","password":"..."}          ← mẫu nội dung đổi được trên Agent

200 {"ok":true,"username":"...","fullName":"...","roles":["..."]}   → đúng
401 {"ok":false,"message":"..."}                                    → sai / khóa tài khoản
```

Tên trường họ tên và vai trò khai báo được (vd `data.user.fullName`). Vai trò HIS nằm trong danh sách "Vai trò HIS
được quản trị LIS" thì làm quản trị LIS, còn lại là kỹ thuật viên; quản trị LIS đổi được vai trò hoặc khóa trên Agent.
