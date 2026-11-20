# Danh sách kiểm Windows thật: giải mã ở server, bản tin thô (phần D)

Chủ dự án làm trên máy Windows 10/11 thật, nối máy xét nghiệm thật (BC-5380, AU480, Access 2) hoặc bộ giả lập khi chưa có máy. Đánh dấu `- [x]` khi đạt; không đạt thì ghi vào ô
**Ghi chú** (kèm ảnh nếu có). Đạt đủ các mục 1–12 thì phần D mới coi là xong. Mục 1–3 cần máy thật; các mục sau dùng được bộ giả lập `tools/Lis.Simulator` nếu máy thật bận.

## Chuẩn bị

1. **Bản agent 0.4.0.** Chạy `bash scripts/publish-agent.sh dist/agent` (`<Version>` trong `src/Lis.Agent/Lis.Agent.csproj` là `0.4.0`). Ghi SHA-256 của `dist/agent/LisAgent.exe`: ______
2. **Server mới.** Chạy với `PUBLIC_URL=http://<ip-lan>:5080` và `AGENT_DIST_DIR=<đường dẫn tuyệt đối tới dist/agent>`. Migration chạy xong (bảng `raw_message`, `raw_message_decode`; bảng master `agent_reading` đã bỏ).
   Đăng nhập web bằng tài khoản **Quản trị**.
3. **Nếu có agent 0.3.0 đang chạy:** nâng cấp theo `docs/HUONG-DAN-CAI-DAT.md` mục 6.1 (mục 11 của danh sách này thử đúng việc này).
4. **Ba máy khai trên web** (**Máy & map kênh**), agent phụ trách là agent vừa cài, map kênh đủ: BC-5380 (**HL7 MLLP**), Access 2 (**ASTM E1394**), AU480 (**Beckman AU**; trên máy AU đã **bật BCC, chọn Class B**,
   cấu hình online khai trên web khớp màn Online của máy).
5. **Mẫu có barcode.** Có sẵn ít nhất ba mẫu có chỉ định đúng chỉ số của từng máy (tạo bằng `seed-demo`; phòng khám tạo từ web thì chạy câu SQL `facility` ở `docs/HUONG-DAN-CAI-DAT.md` mục 5.6 trước).
6. **Có sẵn:** đồng hồ bấm giờ, quyền quản trị Windows, bản in kết quả của từng máy (hoặc màn hình máy) để đối chiếu, một ổ đĩa phụ hoặc ổ ảo nhỏ (VHD vài chục MB) cho mục 5.

---

## 1. BC-5380 thật gửi kết quả: khớp bản in của máy

Cách làm:
1. Chạy một mẫu có barcode đã chỉ định trên BC-5380, để máy gửi kết quả về LIS.
2. Web → **Bản tin thô**: mở bản tin mới nhất của máy. Ghi trạng thái, số kết quả, phiên bản bộ giải mã.
3. Web → **Kết quả**, mở mẫu đó. So **từng chỉ số** (WBC, RBC, HGB, HCT, MCV, PLT…) với bản in của máy: giá trị, đơn vị, cờ cao/thấp.
4. Cho máy gửi lại cùng mẫu lần hai (không đổi giá trị).

Kết quả mong đợi:
- Bản tin trạng thái **Đã giải mã**; không có dòng "Dở dang 1 B" nối đuôi sau bản tin (CR đuôi khung đã gộp).
- Mọi giá trị khớp bản in; không chỉ số nào thiếu hay lệch số lẻ; kênh chưa map (nếu có) hiện ở bảng chưa map.
- Lần gửi lại giống hệt: không nhân đôi kết quả (**Giữ nguyên**), không tạo chờ thay.

- [ ] Đạt

Ghi chú: ______________________________________________

## 2. AU480 thật gửi kết quả: khớp bản in, BCC bật

Cách làm:
1. Xác nhận trên máy: **Online › Protocol** bật BCC, Class B. Chạy một mẫu có barcode đã chỉ định (nên mẫu nhiều chỉ số để bản tin nhiều khối).
2. Web → **Bản tin thô**: mở bản tin. Ghi số kết quả.
3. Web → **Kết quả**: so từng chỉ số với bản in của AU (số Online Test No. đã map hoặc đã tự dò).
4. Mở **Sửa máy** AU trên web: phải có lời nhắc "Máy Beckman AU: bật BCC trên máy trước khi chạy thật".

Kết quả mong đợi:
- Một bản tin AU là trọn một mẫu, trạng thái **Đã giải mã**, mọi chỉ số khớp bản in.
- Không có cảnh báo "BCC sai" ở **Cần chú ý** khi máy chạy bình thường. Có thì ghi lại: có thể cáp nhiễu hoặc cấu hình online lệch.
- Chỉ số chưa biết mã: tự dò map sau **2 mẫu** khớp, có dòng ghi trong Log agent.

- [ ] Đạt

Ghi chú: ______________________________________________

## 3. Access 2 thật gửi kết quả: khớp bản in, ACK trước EOT

Cách làm:
1. Chạy một mẫu có barcode đã chỉ định trên Access 2, để máy gửi kết quả.
2. Web → **Bản tin thô** và **Kết quả**: so từng chỉ số với bản in của máy.
3. Mở **Log agent**, tìm lượt này: ENQ, các khung, EOT, và ACK agent trả.
4. **Đo thời gian ghi đĩa trên Windows.** Trong `C:\ProgramData\LIS Agent\cho-gui\ban-tin\<mã máy>` quan sát tệp `.json` xuất hiện trước khi agent gửi lô lên server. Với phần mềm diệt vi-rút bật, gửi
   khoảng 20 mẫu liên tiếp và ghi thời gian từ lúc máy gửi khung cuối tới lúc agent ACK nếu đọc được từ Log agent (mốc cùng giây trở xuống là đủ): ______ ms.
   (Trên macOS `SaveAsync` trung bình 5–7 ms. Windows chưa đo; nếu thấy máy báo hết giờ chờ ACK hoặc gửi lại khung thì ghi vào đây.)

Kết quả mong đợi:
- Mọi giá trị khớp bản in; bản tin **Đã giải mã**.
- Máy không gửi lại khung do chờ ACK quá lâu; thời gian ghi đĩa mỗi bản tin trong khoảng chục mili giây tới vài chục mili giây, không tới hàng giây.

- [ ] Đạt

Ghi chú: ______________________________________________

## 4. Rút mạng khi máy đang gửi: bản tin giữ trên đĩa, lên đủ khi có mạng

Cách làm:
1. Ghi cột **Chờ gửi** của máy: ______. Rút cáp mạng của máy Windows agent (hoặc tắt Wi-Fi). Bấm giờ.
2. Trong 5 phút cho máy gửi ít nhất 3 mẫu. Xem thư mục `cho-gui\ban-tin\<mã máy>` và Log agent.
3. Cắm mạng lại. Theo dõi **Bản tin thô** và cột **Chờ gửi**.

Kết quả mong đợi:
- Máy xét nghiệm **không** báo lỗi truyền; agent vẫn ACK vì bản tin đã ở trên đĩa. `cho-gui\ban-tin\<mã máy>` có ≥ 3 tệp `.json`.
- Sau khi có mạng, các tệp được gửi hết (agent lùi nhịp tối đa 60 giây rồi gửi) và biến mất; **Bản tin thô** có đủ bản tin, **đúng thứ tự**, không trùng. **Chờ gửi** về 0. Thời gian thực tế: ______ giây.
- Bấm **Gửi bù ngay** có thể rút ngắn chờ.

- [ ] Đạt

Ghi chú: ______________________________________________

## 5. Đĩa đầy: agent ngắt máy, không ACK; máy gửi lại khi hết đầy

Cách làm:
1. Đặt `C:\ProgramData\LIS Agent` (hoặc thư mục `cho-gui`) trên ổ có thể làm đầy (ổ VHD nhỏ, junction), rồi lấp gần đầy bằng tệp rác. Làm cho ghi tệp mới thất bại.
2. Cho máy (hoặc bộ giả lập) gửi một mẫu. Xem Log agent và trạng thái máy ở web.
3. Giải phóng đĩa. Chờ agent nối lại máy. Cho máy gửi lại mẫu đó (hoặc để máy tự gửi lại).
4. Kiểm tra **Bản tin thô**.

Kết quả mong đợi:
- Log agent ghi "Không ghi được đĩa — đã ngắt máy để máy tự giữ kết quả"; kết nối máy bị ngắt, **không ACK**. Máy xét nghiệm có thể báo lỗi truyền: bình thường.
- Sau khi giải phóng đĩa agent nối lại; mẫu về đủ, **một** bản tin cho mỗi lần máy gửi thành công; không bản tin dở dang giả.
- Không có kết quả nào lên web mà bản tin chưa nằm trên đĩa.

- [ ] Đạt

Ghi chú: ______________________________________________

## 6. Tắt server khi đang gửi: không mất, không trùng

Cách làm:
1. Cho máy (hoặc bộ giả lập) gửi liên tiếp nhiều mẫu (≥ 10). Giữa chừng, dừng server LIS đột ngột (kill tiến trình hoặc dừng container). Giữ 2 phút.
2. Trong lúc đó, máy tiếp tục gửi thêm vài mẫu. Xem `cho-gui\ban-tin`.
3. Bật lại server. Đợi tối đa 2 phút. Xem **Bản tin thô**, màn **Kết quả**.
4. (Làm một lần nữa) tắt server **đúng lúc** đang giải mã (gửi một lô nhiều mẫu rồi tắt ngay), bật lại.

Kết quả mong đợi:
- Trong lúc server tắt: bản tin chờ trên đĩa; máy không báo lỗi.
- Sau khi bật: mọi bản tin có trên web, mỗi bản tin một dòng; **không** có kết quả nào ghi hai lần (so số kết quả với số mẫu máy đã gửi).
- Bản tin đang giải mã khi server tắt: sau tối đa khoảng 2 phút về **Chờ giải mã** rồi **Đã giải mã** (hoặc **Cần xem lại** nếu là lượt giải mã lại). Không có bản tin kẹt **Đang giải mã**.
- Log server có dòng "phục hồi … bản tin" nếu có bản tin dở.

- [ ] Đạt

Ghi chú: ______________________________________________

## 7. Sửa map kênh rồi giải mã lại

Cách làm:
1. Chọn một máy có kênh chưa map (hoặc xoá tạm map của một kênh, ví dụ HGB của BC-5380). Cho máy gửi một mẫu: kết quả kênh đó vào **Chưa ghép**/thiếu.
2. **Máy & map kênh** → thêm lại map cho kênh đó.
3. **Bản tin thô** → mở bản tin → **Giải mã thử**: bảng có kênh vừa map. Rồi **Giải mã lại**.
4. Làm lại với **Giải mã lại theo khoảng** cho máy đó trong hôm nay.
5. Với một mẫu **đã duyệt** có giá trị khác, giải mã lại.

Kết quả mong đợi:
- Giải mã thử không ghi gì (kiểm tra **Kết quả** chưa đổi trước khi bấm Giải mã lại).
- Giải mã lại: kênh vừa map vào phiếu; dòng **Chưa ghép** chưa xử lý của bản tin đó được thay, không nhân đôi.
- Mẫu đã duyệt: giá trị khác chỉ vào **chờ thay** (biểu tượng ở **Kết quả**, "Mẫu còn giá trị chờ thay" chặn duyệt lại); không bị ghi đè.
- Ngăn chi tiết có lịch sử giải mã: ai, lúc nào, phiên bản bộ giải mã.
- Tài khoản không phải Quản trị / Trưởng khoa **không thấy** nút **Giải mã thử** / **Giải mã lại**.

- [ ] Đạt

Ghi chú: ______________________________________________

## 8. Máy gửi lại mẫu đã duyệt: chờ thay

Cách làm:
1. Duyệt một mẫu đã có kết quả từ máy.
2. Cho máy gửi lại mẫu đó với **giá trị khác** (chạy lại mẫu, hoặc bộ giả lập `send` giá trị khác). Máy kiểu TCP nối vào agent: chờ 6 giây giữa hai lần gửi.
3. Xem **Kết quả**.

Kết quả mong đợi:
- Giá trị đã duyệt giữ nguyên. Thẻ chỉ số có dòng nguồn "Máy gửi lại …" kèm giờ và cờ máy; mẫu không duyệt lại được tới khi bấm **Dùng** hoặc ✕.
- Lịch sử mẫu có dòng "Máy gửi lại / dùng bản máy".

- [ ] Đạt

Ghi chú: ______________________________________________

## 9. Cổng nghe TCP: mở lại sau 5 giây

Cách làm:
1. Máy kiểu **TCP — máy nối vào agent**. Ngắt phiên máy (tắt/bật kết nối trên máy hoặc đóng bộ giả lập). Xem Log agent: "Mất kết nối — thử lại sau 5 giây".
2. Thử nối lại trong 2 giây đầu rồi sau 6 giây.

Kết quả mong đợi:
- Trong 5 giây đầu máy nối bị từ chối; sau 6 giây nối được. Ghi nhận để chủ dự án quyết định có sửa (backlog mục 8) hay chấp nhận.

- [ ] Đạt (hành vi đúng như mô tả)

Ghi chú: ______________________________________________

## 10. Tệp `.loi` và bản tin hỏng

Cách làm:
1. Dừng dịch vụ **LIS Agent**. Chép một tệp `.json` hỏng (nội dung bậy) vào `cho-gui\ban-tin\<mã máy>` với tên đúng dạng `<20 chữ số>-<32 chữ số hex>.json`. Chạy lại dịch vụ.
2. Cho máy gửi một mẫu thật.
3. Xem thư mục và Log agent.

Kết quả mong đợi:
- Tệp hỏng đổi thành `….json.loi`, **không** chặn hàng; mẫu thật vẫn gửi và lên web.
- Agent không gửi lại tệp `.loi`, không xoá nó.

- [ ] Đạt

Ghi chú: ______________________________________________

## 11. Nâng cấp agent 0.3.0 lên 0.4.0 và dọn `cho-gui` dạng cũ

Cách làm (chỉ khi đang có agent 0.3.0; không có thì cài 0.3.0 từ bản trước để thử):
1. Với agent 0.3.0, cho máy gửi một mẫu rồi **ngắt mạng** để có tệp kết quả đã giải mã trong `cho-gui\<mã máy>\*.json`.
2. Nâng server lên bản mới. Cho thấy agent 0.3.0 không gửi được (không có kết quả mới).
3. Web → **Agent** → **Điều khiển** → **Cập nhật lên 0.4.0**. Xem Log agent sau khi khởi động.
4. Dọn theo `docs/HUONG-DAN/04-xu-ly-su-co.md` mục 22 (dừng dịch vụ, xoá **chỉ** thư mục mã máy, **giữ** `ban-tin`, chạy lại).
5. Thử **Ghép nối lại** bằng khoá mới (sau khi thu hồi một agent thử): agent vẫn không gửi cho tới khi **Restart** dịch vụ.

Kết quả mong đợi:
- Sau khi nâng cấp, log có cảnh báo "Có kết quả chờ gửi dạng cũ — bản này không gửi được nữa" mỗi lần khởi động, tới khi dọn.
- Sau khi dọn: hết cảnh báo; thư mục `ban-tin` còn nguyên; bản tin mới gửi bình thường.
- Sau ghép nối lại bằng khoá mới: bản tin chờ chỉ gửi sau khi khởi động lại dịch vụ (ghi lại thời gian thực tế: ______).

- [ ] Đạt

Ghi chú: ______________________________________________

## 12. nginx: bản tin lớn và giải mã lại khoảng lớn

Cách làm:
1. Dựng nginx theo `docs/HUONG-DAN-CAI-DAT.md` mục 6, có `location /agent/v1/raw-messages` với `client_max_body_size 24m` và `location /api/raw-messages/redecode` với `proxy_read_timeout 300s`.
2. Tạo một hàng chờ lớn: tắt mạng agent, cho máy (hoặc bộ giả lập) gửi nhiều mẫu tới vài MB, bật mạng.
3. Giải mã lại một khoảng có vài trăm bản tin.
4. Đối chứng: đặt `client_max_body_size 8m` cho `location /agent/v1/raw-messages`, lặp bước 2.

Kết quả mong đợi:
- Bước 2: lô lớn lên được, không 413. Bước 4: agent báo 413/lùi nhịp, bản tin kẹt trên đĩa (xác nhận ý nghĩa cấu hình `24m`).
- Bước 3: giải mã lại xong, không 504 với `proxy_read_timeout 300s`. Khi bỏ cấu hình đó và khoảng đủ lớn, nếu proxy trả 504 thì việc vẫn chạy tiếp ở server (kiểm tra danh sách sau vài phút).

- [ ] Đạt

Ghi chú: ______________________________________________

---

## Bảng tổng hợp

| # | Nội dung | Đạt | Ngày | Người làm | Ghi chú |
|---|---|---|---|---|---|
| 1 | BC-5380 thật: khớp bản in, gửi lại không nhân đôi | [ ] | | | |
| 2 | AU480 thật (BCC bật): khớp bản in | [ ] | | | |
| 3 | Access 2 thật: khớp bản in, đo thời gian ghi đĩa trên Windows | [ ] | | | |
| 4 | Rút mạng khi máy gửi: giữ trên đĩa, lên đủ, đúng thứ tự | [ ] | | | |
| 5 | Đĩa đầy: ngắt máy không ACK, máy gửi lại | [ ] | | | |
| 6 | Tắt server khi đang gửi / đang giải mã: không mất, không trùng | [ ] | | | |
| 7 | Sửa map kênh rồi giải mã lại (thử, lại, theo khoảng, mẫu đã duyệt) | [ ] | | | |
| 8 | Máy gửi lại mẫu đã duyệt: chờ thay | [ ] | | | |
| 9 | Cổng nghe TCP mở lại sau 5 giây | [ ] | | | |
| 10 | Tệp `.loi` không chặn hàng | [ ] | | | |
| 11 | Nâng cấp 0.3.0 → 0.4.0, dọn `cho-gui` cũ, khởi động lại sau ghép nối lại | [ ] | | | |
| 12 | nginx: 24m cho bản tin, giải mã lại khoảng lớn | [ ] | | | |

Khi đủ các mục 1–12 đạt: ghi "Đã thử Windows thật <ngày>" vào ledger của phần D. Các điểm chưa làm gom ở
[2026-11-19-d-backlog.md](../plans/2026-11-19-d-backlog.md).
