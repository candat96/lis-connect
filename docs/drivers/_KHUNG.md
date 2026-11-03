# Khung tài liệu driver (mọi file `docs/drivers/*.md` theo đúng thứ tự mục này)

Tài liệu viết tiếng Việt có dấu, giữ nguyên thuật ngữ giao thức (ASTM, HL7, ENQ, ACK, MLLP…).
Mục nào tài liệu hãng không nói thì ghi rõ **"Tài liệu không nêu — cần đo trên máy thật"**,
không tự bịa. Mọi điều suy luận (không có trong tài liệu hãng) đánh dấu *(suy luận)*.

```
# <Hãng> <Model> — <loại máy: sinh hóa / huyết học / miễn dịch / nước tiểu / đông máu / HbA1c>

> Trạng thái tài liệu: ✅ đủ để viết driver | ⚠️ thiếu phần … | ❌ chưa có đặc tả giao thức
> Kiểm chứng: chưa đối chiếu máy thật | đã đối chiếu bản ghi thô ngày …
> Driver: `<mã-driver>` — họ giao thức: <hl7-mllp | astm-e1394 | beckman-au | fixed-field | fixed-offset | …>

## 1. Tóm tắt
Ba đến năm dòng: máy làm gì, nối kiểu gì, một chiều hay hai chiều, điểm khó nhất.

## 2. Kết nối vật lý
Bảng: cổng (RS-232/TCP), tham số cổng (baud, data bits, parity, stop bits, bắt tay phần cứng),
vai trò TCP (máy là client hay server, cổng mặc định), cáp/chân nối nếu tài liệu có, cài đặt cần
bật trên máy (tên menu).

## 3. Khung truyền và bắt tay
Ký tự điều khiển (dạng hex), cấu trúc một frame, checksum (cách tính + ví dụ), độ dài tối đa,
ACK/NAK, timeout, số lần gửi lại, xử lý tranh chấp đường truyền (contention).

## 4. Bản tin kết quả (máy → LIS)
Cấu trúc bản tin (thứ tự record/segment), bảng các trường dùng: vị trí, ý nghĩa, ví dụ.
Mã mẫu (sample ID/barcode) nằm ở đâu; phân biệt mẫu thường / QC / chuẩn; cờ bất thường.

## 5. Hỏi chỉ định / gửi worklist (hai chiều)
Máy hỏi khi nào (quét barcode / bấm chạy), bản tin hỏi, bản tin LIS trả lời (có chỉ định / không
có), timeout. Nếu máy chỉ một chiều thì ghi rõ "Chỉ một chiều".

## 6. Bảng mã chỉ số
Bảng: mã máy gửi | tên chỉ số | đơn vị | ghi chú. Đây là đầu vào cho màn "map máy ↔ chỉ số".

## 7. Bản tin mẫu
Một bản tin kết quả đầy đủ và (nếu có) một cặp hỏi/đáp chỉ định, ký tự điều khiển viết dạng
`<STX>`, `<ETX>`, `<CR>`… Chỉ dùng dữ liệu mẫu của tài liệu hãng hoặc dữ liệu giả
(tên chứa "Mẫu/Thử", mã số giả) — KHÔNG chép dữ liệu người bệnh thật.

## 8. Bẫy và lưu ý khi viết driver
Những chỗ dễ sai: mã hóa ký tự, ký tự đệm lạ, trường trùng, biến thể theo phiên bản phần mềm máy,
cài đặt máy làm đổi bố cục…

## 9. Kiểm thử
Cần bộ bản tin mẫu nào để test, cách mô phỏng máy, các tình huống lỗi phải thử.

## 10. Nguồn
Tên tài liệu, phiên bản, trang/mục, đường dẫn tệp tham khảo (tệp PDF hãng KHÔNG đưa vào repo —
chỉ ghi tên và nơi lấy). Phần còn thiếu: tên đúng của tài liệu cần xin hãng.
```
