# CYBOW R-50S (DFI) — nước tiểu (máy đọc que)

> Trạng thái tài liệu: ❌ chưa có đặc tả giao thức — sổ tay chỉ nêu cổng USB và tốc độ baud
> Kiểm chứng: chưa đối chiếu máy thật
> Driver: `cybow` (đề xuất; LIS cũ dùng driver ASTM chung) — họ giao thức: chưa xác định (LIS cũ khai `ASTM`)

## 1. Tóm tắt

Máy đọc que thử nước tiểu để bàn, quang phổ phản xạ, bộ nhớ 2000 kết quả. Nối máy tính qua **cổng USB** (USB-serial ảo, hiện lên Windows thành cổng COM). Sổ tay hãng không mô tả khung truyền hay bố cục bản tin. Gửi dữ liệu sang PC theo thao tác *Memory → Send Data* (khoảng số thứ tự) và *(suy luận)* sau mỗi lần đo. Có vẻ **chỉ một chiều** — sổ tay không nhắc đến nhận chỉ định từ host.

Ở BVĐK 10, CYBOW (InsID 4) là máy **đang chạy thật**: đã gửi 265 kết quả về LIS cũ với protocol `ASTM`, nhưng CSDL **không có dòng cấu hình cổng** (cấu hình nằm ở file cục bộ trên trạm). Điểm khó nhất: chưa biết chắc máy ở BVĐK 10 có đúng model R-50S không (xem mục 8).

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | USB (đầu B trên máy, cáp A-B), là "serial interface port" | UM 2.2, 2.4, mục 9 |
| Baud | 38400 (mục 2.4); chọn được 9600 / 19200 / 38400 tại *System Setting → Baudrate* | UM 2.4, 4.6.4.2 |
| Data bits / parity | 8, none | UM 2.4 |
| Stop bits | Tài liệu không nêu — cần đo trên máy thật (thường 1 *(suy luận)*) | |
| Bắt tay phần cứng | Tài liệu không nêu | |
| Driver Windows | Phải cài USB driver; lỗi "không nối được LIS" → kiểm số cổng COM của USB, cáp A-B, driver | UM mục 9 (Troubleshooting) |
| Đang dùng ở BVĐK 10 | Không có trong `InterfacePara`; seed tạm đặt `COM?`, 9600 8N1 — **phải khảo sát tại chỗ** | `khao-sat/seed/may-chua-ro-cong.json` |

## 3. Khung truyền và bắt tay

Tài liệu không nêu — cần đo trên máy thật. Bao gồm: ký tự đầu/cuối khung, checksum, ACK/NAK, timeout.

Gợi ý: LIS cũ xử lý được máy này bằng driver tên `ASTM` → có thể máy dùng khung ASTM E1381 (xem `astm-e1394.md`) *(suy luận)*; cũng có thể driver ASTM của hệ cũ đủ dễ dãi để đọc một khung văn bản khác. Chỉ bản ghi byte thô mới trả lời được.

## 4. Bản tin kết quả (máy → LIS)

Tài liệu không nêu bố cục. Những gì biết được:

- **Các chỉ số trên que** (UM mục 6): URO, GLU, BIL, KET, SG, BLD, pH, PRO, NIT, LEU, AsA (vitamin C), cùng ALB, CRE, A:C, P:C với que microalbumin.
- **Ba kiểu kết quả** chọn trên máy (*Result Format → Unit*): Conv. (mg/dL), SI (mmol/L), Arb (ký hiệu `neg`, `±`, `+1`…`+4`); tùy chọn *Mark Plus* in `+` thay cho đơn vị. Kiểu đang đặt sẽ quyết định chuỗi gửi lên LIS *(suy luận)*.
- Cách LIS cũ đọc (theo cột vị trí trong `Instrument.json`, InsID 4 — giống mọi máy "ASTM" khác): `SIDIndex=2`, `TestCodeIndex=3`, `ValueIndex=3`, `UnitIndex=4` → SID ở O.3, mã chỉ số ở thành phần 4 của R.3, giá trị R.4, đơn vị R.5 *(suy luận về ý nghĩa các cột)*.
- QC/chuẩn: tài liệu không nêu cách đánh dấu mẫu QC trong bản tin.

## 5. Hỏi chỉ định / gửi worklist

Chỉ một chiều (theo những gì sổ tay mô tả: máy đẩy kết quả sang PC; không có chức năng nhận chỉ định). Cần xác nhận với hãng.

## 6. Bảng mã chỉ số

**Đang map ở BVĐK 10** (InsID 4, 11 mã — mã do máy gửi, lấy từ LIS cũ):

| Mã máy | Chỉ số | Đơn vị (LIS) | Ghi chú |
|---|---|---|---|
| `URO` | Urobilinogen | mg/dL | |
| `GLU` | Glucose niệu | mg/dL | |
| `BIL` | Bilirubin | mg/dL | |
| `KET` | Ketone | mg/dL | |
| `S.G` | Tỷ trọng | — | sổ tay ghi `SG` — máy thật gửi `S.G` |
| `BLD` | Hồng cầu (Blood) | Ery/µL | sổ tay ghi đơn vị RBC/µl |
| `PH` | pH | — | sổ tay ghi `pH` |
| `PRO` | Protein | mg/dL | |
| `NIT` | Nitrite | — | chỉ `neg`/`pos` |
| `LEU` | Bạch cầu | Leu/µL | sổ tay ghi WBC/µl |
| `VTC` | Vitamin C (ascorbic acid) | mg/dL | sổ tay ghi `AsA` |

**Quy đổi định tính** (bảng `ResultMapping` LIS cũ, InstrumentID 4): `neg` → "Âm tính"; `pos` → "Dương tính" (tô màu bất thường). Áp cho mọi mã và riêng các mã `XNNT21_04`…`XNNT21_10`.

**Mức đọc theo sổ tay (đơn vị Conv.)** — dùng để kiểm tra giá trị nhận về:

| Chỉ số | neg | ± | +1 | +2 | +3 | +4 |
|---|---|---|---|---|---|---|
| URO (mg/dL) | norm | 1 | 2 | 4 | 8 | |
| GLU (mg/dL) | neg | 100 | 250 | 500 | 1000 | |
| KET (mg/dL) | neg | 5 | 15 | 40 | 100 | |
| BLD (RBC/µL) | neg | trace | 10 | 50 | 250 | |
| PRO (mg/dL) | neg | trace | 30 | 100 | 300 | 1000 |
| LEU (WBC/µL) | neg | | 25 | 75 | 500 | |
| AsA (mg/dL) | neg | | 20 | 40 | | |
| BIL | neg | | Small | Moderate | Large | |
| NIT | neg | pos | | | | |
| SG | 1.000 – 1.030 (bước 0.005) | | | | | |
| pH | 5 – 9 | | | | | |

## 7. Bản tin mẫu

Chưa có bản tin mẫu của hãng. Không dựng bản tin giả vì chưa biết khung — tránh để người viết driver tưởng đó là thật. Việc đầu tiên: bật `raw-listen` trên cổng COM của CYBOW ở BVĐK 10, đo một que nước tiểu giả, lưu byte thô.

## 8. Bẫy và lưu ý khi viết driver

1. **Có thể không phải R-50S.** Mã máy gửi ở BVĐK 10 (`S.G`, `VTC`, `BLD` đơn vị Ery/µL) khác ký hiệu trong sổ tay R-50S (`SG`, `AsA`, RBC/µl). Rất có thể máy BVĐK 10 là model CYBOW khác hoặc phiên bản phần mềm khác *(suy luận)* — ghi model + phiên bản phần mềm trên tem máy trước khi viết driver.
2. **Cổng USB ảo đổi số COM** khi cắm sang cổng USB khác hoặc cài lại driver — đúng lỗi sổ tay liệt kê. Agent phải cho chọn lại cổng dễ dàng và cảnh báo khi cổng biến mất.
3. **Baud chọn được trên máy** (9600/19200/38400) — sổ tay ghi 38400 là thông số giao tiếp nhưng seed hệ cũ đặt 9600. Phải đọc trên máy.
4. **Kết quả định tính** (`neg`, `pos`, `+1`, `norm`, `trace`, `Small`…) phải qua bảng quy đổi theo máy, không ép về số. Kiểu kết quả (Conv./SI/Arb, Mark Plus) đổi trên máy là đổi luôn chuỗi gửi lên.
5. Tên mã có dấu chấm (`S.G`) — đừng coi `.` là phân cách.
6. Hệ cũ nhận kết quả CYBOW nhưng CSDL không có cấu hình cổng — dấu hiệu cấu hình nằm ở file cục bộ trên trạm; nên chép file cấu hình LISInterface của trạm đó khi khảo sát.

## 9. Kiểm thử

- Bản ghi thô thật (đã khử định danh): một que âm tính toàn bộ, một que dương tính nhiều chỉ số, một lần *Send Data* nhiều mẫu, một mẫu QC.
- Thử cả ba kiểu kết quả Conv./SI/Arb và Mark Plus bật/tắt.
- Thử rút/cắm cáp USB khi đang truyền, đổi số COM.

## 10. Nguồn

- `tai-lieu-hang/CYBOW_R-50S_User_Manual.pdf` (User Manual OM(V2) 2014.01) — mục 2.2 (cổng USB), **2.4 Interfacing System** (USB, 38400, 8 bit, no parity), 4.6.3.2 Send Data, 4.6.4.2 System Setting (Baudrate), 6 Table of Results, 9 Troubleshooting. Tải từ medm.com.
- `khao-sat/lis-hien-huu.md` (mục 3, 7), `khao-sat/seed/du-lieu/Instrument.json` (InsID 4), `ResultMapping.json` (InstrumentID 4), `khao-sat/seed/anh-xa-ma-may.json`.
- **Cần xin hãng (DFI Co., Ltd. — nhà sản xuất CYBOW) hoặc nhà phân phối:** đặc tả giao tiếp LIS/host (LIS interface / communication protocol) cho **đúng model và phiên bản phần mềm** đang đặt ở BVĐK 10, gồm khung truyền, bố cục bản tin, có ACK hay không.
