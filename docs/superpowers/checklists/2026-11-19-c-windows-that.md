# Danh sách kiểm Windows thật: điều khiển agent từ web (phần C)

Chủ dự án làm trên máy Windows 10/11 thật, cùng lượt với Task 9 của phần B. Đánh dấu `- [x]` khi đạt; không đạt thì ghi vào ô
**Ghi chú** (kèm ảnh nếu có). Đạt đủ 9 mục thì phần C mới coi là xong.

## Chuẩn bị

1. **Bản agent.** Chạy `bash scripts/publish-agent.sh dist/agent` trên máy phát triển. Ghi lại phiên bản (`<Version>` trong
   `src/Lis.Agent/Lis.Agent.csproj`, nay là `0.2.0`) và SHA-256 của `dist/agent/LisAgent.exe`.
   - Phiên bản đang cài trên máy Windows: ______
   - Phiên bản mới dùng cho mục 5: ______ (cao hơn, ví dụ `0.2.1`)
2. **Máy chủ.** Chạy server với `PUBLIC_URL=http://<ip-lan>:5080` (máy Windows tới được địa chỉ này) và
   `AGENT_DIST_DIR=<đường dẫn tuyệt đối tới dist/agent>`. Đăng nhập web bằng tài khoản **Quản trị**.
3. **Cài agent** lên máy Windows theo Task 9 của phần B (web → **Agent** → thêm agent → **Tải tệp cài** → chạy). Bảng điều khiển
   (nút **Điều khiển**) phải hiện **Đang kết nối** và không có lời báo "Kênh lệnh đang đứt".
4. **Cổng COM ảo cho mục 2** (nếu không có máy xét nghiệm thật): cài com0com, tạo một cặp cổng nối với nhau, ví dụ `COM5` và `COM6`.
   Agent nghe `COM5`; mở `COM6` bằng PuTTY hoặc Tera Term (cùng baud 9600, 8N1) và gõ chữ để giả lập máy gửi dữ liệu.
   Muốn thêm một máy thử nối COM: Máy & map kênh → thêm máy tên "Máy Thử COM", kết nối **Cổng COM (RS-232)**, cổng `COM5`,
   agent là agent vừa cài.
5. **Bản "cố tình hỏng" cho mục 6.** Cần một `LisAgent.exe` có phiên bản cao hơn nhưng không chạy được lên. Làm một trong hai cách:
   - **Cách A (đơn giản):** publish với `<Version>` cao hơn (ví dụ `0.2.1`), rồi **thay** `dist/agent/LisAgent.exe` bằng một tệp exe
     thoát ngay khi chạy (ví dụ `cmd.exe` đổi tên thành `LisAgent.exe`, hoặc chương trình rỗng `return 1;`). Giữ nguyên `version.txt`
     của lần publish. Bản này không chạy lên, nên không bao giờ nối lại được máy chủ.
   - **Cách B:** dùng bản build thật có `<Version>` khác, rồi làm cho nó không nối được máy chủ (ví dụ chặn riêng tệp này ở chiều ra
     bằng Windows Firewall sau khi chép sang). Bản này chạy lên nhưng không nối lại trong 2 phút.
   - Sau khi thay tệp, kiểm tra web đã hiện nút **Cập nhật lên ...** (khởi động lại server nếu nó chưa thấy tệp mới).
   - Sau mục 6, **khôi phục** `dist/agent` về bản tốt (publish lại) trước khi làm mục 7.
6. **Có sẵn:** đồng hồ bấm giờ, quyền quản trị Windows, một máy xét nghiệm thật hoặc cặp com0com ở bước 4.

---

## 1. Quét cổng thấy đúng COM và tên thiết bị; cổng do phần mềm khác giữ hiện "bận"

Cách làm:
1. Cắm thiết bị USB-COM thật (hoặc dùng cặp com0com). Ghi lại các cổng thấy trong Device Manager → Ports (COM & LPT): ______
2. Web → **Agent** → **Điều khiển** → bấm **Quét cổng COM**. Đợi trạng thái **Xong**.
3. Xem mục **Cổng COM**. So từng cổng và tên thiết bị với Device Manager.
4. Mở PuTTY, kết nối serial vào một cổng trống (ví dụ `COM6`) và để đó. Bấm **Quét cổng COM** lại.

Kết quả mong đợi:
- Bước 3: danh sách khớp Device Manager; cột **Thiết bị** có tên (ví dụ "USB-SERIAL CH340"); cổng chưa ai dùng hiện "Trống".
- Bước 4: cổng PuTTY giữ hiện **Phần mềm khác đang giữ**. Đóng PuTTY, quét lại thì về "Trống".
- Dòng lịch sử lệnh "Quét cổng COM" ghi "N cổng: ...".

- [ ] Đạt

Ghi chú: ______________________________________________

## 2. Thử cổng: nhận byte; thử trên cổng máy đang dùng thì máy tự nối lại

Cách làm:
1. Ở mục **Cổng COM**, bấm **Thử cổng** ở một cổng **Trống** (ví dụ `COM5`). Hộp thoại **"Thử cổng COM5"** mở. Giữ baud 9600, 8, None, 1; **Nghe trong (giây)** = 15. Bấm **Thử**.
2. Trong 15 giây đó, gõ vài chữ trong PuTTY nối `COM6` (hoặc cho máy thật gửi một kết quả).
3. Đợi lệnh xong. Xem kết quả ở khung dưới nút lệnh và bấm vào dòng trong **Lịch sử lệnh**.
4. Chọn máy "Máy Thử COM" đang chạy trên `COM5`. Bấm **Thử cổng** ở dòng máy đó (hoặc ở dòng cổng `COM5`).
5. Đọc cảnh báo trong hộp thoại, bấm **Vẫn thử**, đợi lệnh xong. Xem trạng thái máy ở mục **Máy xét nghiệm**.

Kết quả mong đợi:
- Bước 3: "Mở được, nhận N byte"; chữ đã gõ hiện lại dạng chữ; ký tự điều khiển hiện `<STX>`, `<CR>`...
- Bước 5: cảnh báo "Máy Thử COM sẽ tạm ngắt trong 15 giây"; kết quả ghi "đã tạm ngắt Máy Thử COM"; sau đó máy trở lại **Đường truyền** đã nối, không cần bấm **Nối lại**. Thử gõ chữ trong PuTTY, cột **Đã nhận** tăng.

- [ ] Đạt

Ghi chú: ______________________________________________

## 3. Nối lại một máy

Cách làm:
1. Ở mục **Máy xét nghiệm**, ghi cột **Đã nhận** của "Máy Thử COM": ______
2. Bấm **Nối lại** ở dòng máy đó.
3. Đợi lệnh xong ở **Lịch sử lệnh**. Gõ chữ trong PuTTY (cổng đối diện) và xem cột **Đã nhận**.

Kết quả mong đợi:
- Lệnh **Xong**, kết quả "Đã đóng và mở lại kết nối máy".
- **Đường truyền** của máy đó về trạng thái đã nối trong vài giây; các máy khác của agent không bị ngắt.
- **Đã nhận** tiếp tục tăng khi có dữ liệu.

- [ ] Đạt

Ghi chú: ______________________________________________

## 4. Khởi động lại: dịch vụ tự bật lại trong ≤ 1 phút, web lại "Đang kết nối"

Cách làm:
1. Bấm **Khởi động lại**, xác nhận "Khởi động lại" trong hộp thoại. Bấm giờ.
2. Quan sát bảng điều khiển và `services.msc` (dịch vụ **LIS Agent**).
3. Kiểm tra tab Recovery của dịch vụ: lỗi đầu tiên là **Restart the Service**.

Kết quả mong đợi:
- Lệnh **Xong**, "Agent đang khởi động lại".
- Dịch vụ dừng rồi tự chạy lại trong **≤ 1 phút** (bình thường khoảng 5 giây); thời gian thực tế: ______ giây.
- Web: trạng thái có thể thoáng **Mất kết nối** hoặc lời báo kênh đứt, rồi trở lại **Đang kết nối** và các nút bấm được. Các máy xét nghiệm tự nối lại.
- Phụ: chạy `LisAgent.exe run` trong cửa sổ dòng lệnh thì **Khởi động lại** báo **Lỗi** "Agent không chạy như dịch vụ nên không tự khởi động lại" (tắt dịch vụ trước khi thử).

- [ ] Đạt

Ghi chú: ______________________________________________

## 5. Cập nhật lên bản mới: web hiện phiên bản mới

Cách làm:
1. Đổi `<Version>` trong `src/Lis.Agent/Lis.Agent.csproj` lên bản mới (ví dụ `0.2.1`), chạy lại `bash scripts/publish-agent.sh dist/agent` (giữ cùng `AGENT_DIST_DIR`).
2. Tải lại trang web, mở bảng điều khiển agent. Nút **Cập nhật lên 0.2.1** phải hiện.
3. Bấm nút, xác nhận "Cập nhật". Bấm giờ. Theo dõi trạng thái lệnh.
4. Sau khi xong, xem ô **Phiên bản**.

Kết quả mong đợi:
- Lệnh đi qua **Chờ agent / Đã gửi / Đang chạy**, rồi **Xong** (có thể thoáng **Mất kết nối** khi dịch vụ khởi động lại).
- Ô **Phiên bản** hiện `0.2.1`; nút **Cập nhật lên ...** biến mất. Thời gian tổng: ______ phút (tối đa dưới 10 phút).
- Máy xét nghiệm tự nối lại.

- [ ] Đạt

Ghi chú: ______________________________________________

## 6. Cập nhật với bản cố tình hỏng: sau khoảng 2 phút quay về bản cũ

Cách làm:
1. Chuẩn bị bản hỏng theo mục 5 của phần Chuẩn bị (phiên bản cao hơn bản đang cài). Ghi phiên bản agent đang chạy: ______
2. Tải lại web, bấm **Cập nhật lên ...**, xác nhận. Bấm giờ.
3. Quan sát dịch vụ **LIS Agent** trong `services.msc` và web.
4. Khi agent nối lại, mở **Lịch sử lệnh** và bấm vào dòng lệnh vừa chạy.
5. Khôi phục `dist/agent` về bản tốt.

Kết quả mong đợi:
- Sau khoảng **2 phút** dịch vụ chạy lại bản **cũ**; web trở lại **Đang kết nối**, ô **Phiên bản** vẫn là bản cũ.
- Lệnh thành **Lỗi** với lý do như "Bản mới không kết nối được máy chủ trong 2 phút — đã quay về bản cũ". Thời gian thực tế: ______ phút.
- Các máy xét nghiệm tự nối lại; `LisAgent.exe` trong `Program Files\LIS Agent` là bản cũ (kiểm tra phiên bản ở Properties → Details).

- [ ] Đạt

Ghi chú: ______________________________________________

## 7. Thu hồi: dịch vụ dừng, kiểu khởi động Disabled, `cho-gui` còn

Cách làm:
1. Khôi phục bản tốt ở `dist/agent` nếu chưa. Chép một tệp thử vào `C:\ProgramData\LIS Agent\cho-gui` nếu thư mục đang trống, để kiểm giữ nguyên (hoặc ngắt mạng ngắn rồi cho máy gửi một mẫu để có tệp chờ).
2. Bấm **Thu hồi**. Hộp thoại hỏi gõ tên agent. Gõ **sai** một chữ: nút **Thu hồi agent** phải bị tắt. Gõ đúng tên rồi bấm **Thu hồi agent**.
3. Xem `services.msc`: dịch vụ **LIS Agent**. Xem thư mục `C:\ProgramData\LIS Agent`.
4. Trên web: trạng thái agent và các nút lệnh.

Kết quả mong đợi:
- Lệnh **Xong**, "Đã thu hồi, agent đã dừng".
- Dịch vụ **Stopped**, kiểu khởi động **Disabled**; khoá agent đã xoá khỏi máy (không còn dùng được pairing cũ).
- `cho-gui` và các tệp trong đó **còn nguyên**.
- Web: agent chuyển **Mất kết nối** sau khoảng 90 giây; các nút lệnh tắt, kênh không nối lại.
- Phụ: muốn dùng lại thì **Ghép nối lại** và chạy tệp cài mới.

- [ ] Đạt

Ghi chú: ______________________________________________

## 8. Rút mạng 5 phút khi máy gửi kết quả: kết quả lên đủ, kênh tự nối lại

Cách làm:
1. Agent đã ghép nối lại (nếu vừa làm mục 7). Máy "Máy Thử COM" đang nối, trạng thái **Đã nối**. Ghi **Đã nhận** và **Chờ gửi**: ______ / ______
2. Rút cáp mạng (hoặc tắt Wi-Fi) của máy Windows. Bấm giờ.
3. Trong 5 phút, cho máy gửi ít nhất 3 kết quả (máy thật chạy mẫu, hoặc gõ ba bản tin trong PuTTY). Xem thư mục `cho-gui`.
4. Cắm mạng lại sau 5 phút. Quan sát web.

Kết quả mong đợi:
- Trong lúc mất mạng: `cho-gui` có tệp chờ; máy xét nghiệm vẫn nhận bình thường (agent không dừng máy).
- Sau khi có mạng: các tệp trong `cho-gui` được gửi hết và biến mất, kết quả lên web đủ, không trùng. Cột **Chờ gửi** về 0.
- Kênh lệnh tự nối lại trong tối đa khoảng 1 phút sau khi có mạng (chờ nối lại tăng dần tới 60 giây); các nút lệnh bấm được lại. Thời gian thực tế: ______ giây.

- [ ] Đạt

Ghi chú: ______________________________________________

## 9. Server sau nginx HTTPS: kênh `wss://` nối được

Cách làm:
1. Dựng nginx trước server LIS theo mục 6 của `docs/HUONG-DAN-CAI-DAT.md`, gồm khối `location /agent/v1/channel` (có `Upgrade`, `Connection "upgrade"`, `proxy_read_timeout 120s`). Dùng chứng chỉ máy thật hoặc chứng chỉ thử mà máy Windows tin cậy.
2. Đặt `PUBLIC_URL=https://<tên-miền>`; khởi động lại server. Tạo agent mới (hoặc **Ghép nối lại**), cài lại trên máy Windows.
3. Mở bảng điều khiển agent. Bấm **Quét cổng COM**.
4. Bỏ thử khối `location /agent/v1/channel` (đối chứng), nạp lại nginx, xem web; rồi thêm lại.

Kết quả mong đợi:
- Bước 3: không có lời báo "Kênh lệnh đang đứt"; lệnh **Xong**. Kênh đi bằng `wss://`.
- Bước 4: khi bỏ khối WebSocket, web báo **"Kênh lệnh đang đứt — các lệnh tạm tắt"** trong khi agent vẫn **Đang kết nối**; thêm lại và nạp nginx thì hết báo trong tối đa khoảng 1 phút.
- Kết quả xét nghiệm vẫn lên web ở cả hai trường hợp.

- [ ] Đạt

Ghi chú: ______________________________________________

---

## Bảng tổng hợp

| # | Nội dung | Đạt | Ngày | Người làm | Ghi chú |
|---|---|---|---|---|---|
| 1 | Quét cổng: đúng COM + tên thiết bị, cổng bị giữ hiện "bận" | [ ] | | | |
| 2 | Thử cổng: nhận byte; cổng máy đang dùng thì máy tự nối lại | [ ] | | | |
| 3 | Nối lại một máy | [ ] | | | |
| 4 | Khởi động lại: dịch vụ tự bật lại ≤ 1 phút, web "Đang kết nối" | [ ] | | | |
| 5 | Cập nhật lên bản mới: web hiện phiên bản mới | [ ] | | | |
| 6 | Cập nhật bản hỏng: quay về bản cũ sau ~2 phút, lịch sử "Lỗi" kèm lý do | [ ] | | | |
| 7 | Thu hồi: dừng, Disabled, `cho-gui` còn | [ ] | | | |
| 8 | Rút mạng 5 phút: kết quả lên đủ, kênh tự nối lại | [ ] | | | |
| 9 | Sau nginx HTTPS: `wss://` nối được | [ ] | | | |

Khi đủ 9 mục đạt: ghi "Đã thử Windows thật <ngày>" vào ledger của phần C.
