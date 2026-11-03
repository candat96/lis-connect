# DIRUI HC-300 (họ H/HC series) — nước tiểu

> Trạng thái tài liệu: ✅ đủ để viết driver nhận kết quả — có sổ tay **đúng model HC-300** (EN, 08/2023), Appendix B mô tả tham số cổng và 4 bố cục khung (SI/Conventional/Plus tắt và bật mã vạch, CT200, DR200). Còn mở: bảng mã ký tự ngoài ASCII (`µ`, `：`), vị trí thật của byte `0xAB`, khác biệt chế độ LIS "Standard"/"Simple", cờ `*` trong bản tin RS-232 (chỉ thấy trên bản in)
> Kiểm chứng: chưa đối chiếu máy thật. Bản ghi thô `PK-BINHMINH_hc300_*.jsonl` (09/09 → 05/10/2026) trong kho cũ là **dữ liệu bộ giả lập** `lis-agent/tools/simulate.ts` phát — không có giá trị kiểm chứng
> Driver: `dirui-hc300` — họ giao thức: dòng chữ cố định Dirui H (khung STX…ETX theo dòng, không checksum)

## 1. Tóm tắt

- Máy đọc que thử nước tiểu để bàn; que 11 thông số cơ bản (UBG, BIL, KET, BLD, PRO, NIT, LEU, GLU, SG, pH, VC) + tùy chọn creatinin (CRE), microalbumin (MALB/ALB), calci niệu (Ca).
- Nối RS-232, máy **đẩy một chiều** lên LIS: không ENQ, không ACK/NAK, không checksum.
- Mỗi que một bản tin văn bản: `STX CR LF` + dòng `Date:` + dòng `No.` + (dòng `ID:` nếu bật mã vạch) + mỗi chỉ số một dòng + `ETX`.
- Điểm khó nhất: (1) bố cục đổi theo cài đặt (mã vạch bật/tắt; định dạng SI/Conventional/Plus/CT200/DR200); (2) vùng giá trị 16 ký tự gộp **mức định tính + số + đơn vị** và có ký tự ngoài ASCII (`µ`); (3) chỉ số không đo thì **không xuất dòng** — không được đọc theo số dòng.

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | RS-232 ("RS-232 interface", số 8 mặt sau máy); cáp nối tiếp kèm máy | HC-300 UM §2.2, §2.3.3, App. B |
| Baud | **9600** hoặc **1200** (menu `Transmission speed`) | App. B; §3.2.5.6 (2) |
| Data bits / Parity / Stop | 8 / không / 1 | App. B |
| Bắt tay phần cứng | Không | App. B |
| TCP | Không có | — |
| Sơ đồ chân | App. B có hình "Connection between analyzer and computer" nhưng bản chữ không đọc được chân — **Tài liệu không nêu — cần đo trên máy thật** (thường cáp chéo TX/RX/GND *(suy luận)*) | App. B-1 |
| Cài đặt trên máy | `Status settings`: **LIS Port = On** (tắt thì không gửi), **ID = On** nếu dùng đầu đọc mã vạch, `Abnormal` (in `*`), `Plus Used`; `Other settings`: Test mode (Fast 30 s/que, Slow 60 s/que), **Baud rate** 9600/1200, **LIS: Standard / Simple** | §3.2.5.3, §3.2.5.6 |
| Đầu đọc mã vạch | Cổng "barcode reader interface" riêng (số 9), không dùng chung cổng LIS; tối đa 30 mã quét trước, mỗi mã **< 20 ký tự** | §2.3.4, §3.2.5.3 (1) |

## 3. Khung truyền và bắt tay

| Ký hiệu | Hex | Ý nghĩa (App. B) |
|---|---|---|
| STX | 0x02 | Ký tự mở |
| CR LF | 0x0D 0x0A | Xuống dòng |
| SP | 0x20 | Khoảng trắng ("2 bytes space 20H") |
| SP1 | 0xAB | "3 bytes space ABH" — **ký tự không phải ASCII**; tài liệu khai nhưng **không vẽ vị trí** trong bảng khung |
| `-` | 0x2D | Gạch nối trong ngày |
| ETX | 0x03 | Ký tự kết thúc |

Khung (SI / Conventional / Plus — App. B (1)(2), cột 1–24):

```
STX CR LF
SP "Date:" yyyy-mm-dd SP hh:mm CR LF
SP "No." SP nnnn CR LF
[SP "ID:" <mã vạch, tối đa 18 ký tự> CR LF]           ← chỉ khi ID = On
SP <nhãn 3 ký tự, nhãn 2 ký tự đệm SP> SP <vùng giá trị 16 ký tự, cột 6–21> CR LF   × mỗi chỉ số
ETX
```

- Chỉ số không đo (que ít thông số hơn) → **không xuất dòng** (App. B, ghi chú).
- Dòng CRE và Ca trong bảng hãng chỉ vẽ nhãn, không vẽ vùng giá trị — nhiều khả năng là sót khi vẽ, giá trị vẫn nằm ở cột 6–21 *(suy luận)*.
- Que `11MA(N)`/`11MA`: dòng VC chỉ có nhãn, không có giá trị.
- Checksum: **không có**. ACK/NAK: **không có**. Gửi lại: **không có**. Contention: không áp dụng (một chiều).
- Độ dài: ≈ 3 + 22 + 11 + 24 + 14×24 + 1 ≈ 400 byte *(suy luận)*. Timeout: tài liệu không nêu; driver cắt frame tại ETX và hủy frame dở nếu quá ~15 s không thấy ETX *(suy luận)*.
- Byte `0xAB`: tài liệu không chỉ vị trí → driver chấp nhận `0xAB` ở bất kỳ đâu trong dòng và coi như khoảng trắng.

**Định dạng CT200** (App. B (3), 11 mục, cột 1–22): không có SP đầu dòng; dòng đầu dạng `#n-nnn` + 6 SP + `yy-mm-dd` (số mẫu + ngày *(suy luận)*); các dòng chỉ số `<nhãn> SP <16 ký tự>`; thứ tự UBG, BIL, KET, BLD, PRO, NIT, LEU, GLU, SG, PH, VC, (ALB). Không có dòng giờ, không có ID.

**Định dạng DR200** (App. B (4), cột 1–21): dòng `SP "NO." nnn SP SP yy－mm－dddd` (dấu gạch trong bảng là ký tự toàn giác `－` — cần đo), dòng giờ `hh:mm:ss` đệm 11 SP, một dòng trống; chỉ số với **vùng giá trị 14 ký tự**, thứ tự: pH, NIT, GLU, VC, SG, BLD, PRO, BIL, **URO** (không phải UBG), KET, **WBC** (không phải LEU), ALB; một dòng trống trước ETX.

Hai định dạng CT200/DR200 dùng để giả lập máy đời cũ; khuyến nghị đặt máy ở SI hoặc Conventional *(suy luận)*.

### 3.1. Đối chiếu driver mã nguồn mở của DIRUI H-800

Mã nguồn mở `DiruiH800Conn` (https://github.com/vilake/DiruiH800Conn) viết cho **H-800**, không phải tài liệu hãng. Điểm khớp với HC-300: tách dòng theo LF, kết thúc ETX, mã mẫu lấy từ dòng `ID`, ký tự 0 của dòng chỉ số là cờ, mã ở ký tự 1–4, giá trị từ ký tự 5. Điểm khác: H-800 chạy 19200 8N1 (HC-300 chỉ có 9600/1200). Điểm bổ sung: khi bất thường, H-800 gửi `*` **thay cho dấu cách đầu dòng** — khớp với bản in HC-300 (`*` đứng trước tên chỉ số, Fig. 3-2-13), nên HC-300 nhiều khả năng làm giống *(suy luận)*.

## 4. Bản tin kết quả (máy → LIS)

| Dòng | Tiền tố | Nội dung | Ví dụ (giả) |
|---|---|---|---|
| 1 | `Date:` | Ngày giờ đo `yyyy-mm-dd hh:mm` | `Date:2026-11-03 09:15` |
| 2 | `No.` | Số thứ tự máy tự đánh, 4 chữ số, quay vòng | `No. 0042` |
| 3 (tùy chọn) | `ID:` | Mã vạch ống (< 20 ký tự; khung chừa 18) | `ID:9900000123` |
| còn lại | nhãn | Một chỉ số mỗi dòng, thứ tự cố định (mục 6) | ` LEU Ca70 Leu/µL` |

- **Mã mẫu**: có dòng `ID:` → dùng mã vạch. Không có → chỉ có `No.` (số máy 0001–9999, quay vòng, **không** phải mã của LIS — không dùng làm khóa ghép tự động).
- **Dấu hai chấm sau `ID`**: bảng khung ghi `:` ASCII; bản in mẫu dùng `：` toàn giác. Driver nhận diện dòng bằng tiền tố `ID` + 1–3 byte bất kỳ.
- **QC / que chuẩn**: tài liệu không mô tả bản tin QC riêng (QC chỉ xem trên máy, `QC record query`) — **Tài liệu không nêu — cần đo trên máy thật** xem kết quả QC có ra cổng LIS không.
- **Cờ bất thường**: `Abnormal = On` → `*` trước tên chỉ số khi giá trị ≥ ngưỡng cutoff người dùng đặt (§3.2.5.3 (2), §3.2.5.5). Trong bản tin RS-232: xem mục 3.1.
- **Vùng giá trị** phụ thuộc cài đặt: định dạng đơn vị (SI / Conventional / Plus / CT200 / DR200) và `Plus Used` (On: `2+  14 mmol/L`; Off: `14 mmol/L`). Bảng quy đổi: Appendix C.

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

**Chỉ một chiều.** Máy không hỏi chỉ định, không nhận worklist. Mã vạch đọc bằng đầu đọc riêng của máy chỉ được chèn vào dòng `ID:`.

## 6. Bảng mã chỉ số

Thứ tự trong khung SI/Conventional/Plus. Mức → giá trị SI theo Appendix C (HC-300).

| Mã máy gửi | Tên chỉ số | Mức → SI (Appendix C) | Ghi chú map (`lis-server/seed/dirui-hc300.sql`) |
|---|---|---|---|
| `UBG` | Urobilinogen | Normal 3,4 / Normal 17 · 1+ 34 · 2+ 68 · 3+ ≥135 µmol/L | → `409URO` (DR200 gửi `URO`) |
| `BIL` | Bilirubin | Neg · 1+ 17 · 2+ 51 · 3+ ≥103 µmol/L | → `408BIL` |
| `KET` | Ketone | Neg · ± 0,5 · 1+ 1,5 · 2+ 3,9 · 3+ ≥7,8 mmol/L | → `410KET` |
| `CRE` | Creatinin niệu (tùy chọn) | Tài liệu bản chữ không đọc được bảng quy đổi | Cố ý không map — không phải creatinin huyết thanh |
| `BLD` | Hồng cầu / máu ẩn | Neg · ± Ca10 · 1+ Ca25 · 2+ Ca80 · 3+ ≥Ca200 Ery/µL | → `406BLD` |
| `PRO` | Protein niệu | Neg · Trace · 1+ 0,3 · 2+ 1,0 · 3+ ≥3,0 g/L | → `407PRO` |
| `NIT` | Nitrit | Neg / Pos | → `402NIT` |
| `LEU` | Bạch cầu niệu | Neg · ± Ca15 · 1+ Ca70 · 2+ Ca125 · 3+ ≥Ca500 Leu/µL | → `411WBC` (DR200 gửi `WBC`) |
| `GLU` | Glucose niệu | Neg · 1+ 5,6 · 2+ 14 · 3+ 28 · 4+ ≥56 mmol/L | → `403GLU` (không phải glucose máu) |
| `SG` | Tỷ trọng | ≤1,005 … ≥1,030 (bước 0,005) | → `405SG` |
| `PH` | pH | ≤5,0 … ≥9,0 (bước 0,5) | → `401pH`. Khung ghi `PH`, bản in/DR200 ghi `pH` — so khớp không phân biệt hoa thường |
| `VC` | Vitamin C | 0 / 0,6 / 1,4 / 2,8 / ≥5,7 mmol/L | → `404VC` |
| `ALB` | Microalbumin (tùy chọn) | Neg / 0,15 g/L / >0,15 g/L | Cố ý không map. Bản in/màn hình ghi `MALB`, khung ghi `ALB` |
| `Ca` | Calci niệu (tùy chọn) | Tài liệu bản chữ không đọc được bảng quy đổi | Chưa có mã trong danh mục |

So với H-100: HC-300 **không có** `A:C` và `RT`; thang MALB khác (H-100: 10/30/80/150 mg/L).

Driver tách vùng giá trị thành `mức` (Neg/±/Trace/1+…/Normal/Pos), `số` (kèm `>=`/`<=`) và `đơn vị` *(suy luận — để LIS lưu cả hai dạng)*.

## 7. Bản tin mẫu

Dữ liệu giả, dựng theo App. B (2) (bật mã vạch, SI, Plus Used = Off, Abnormal = On; cờ `*` đặt theo mục 3.1 — chưa đo trên máy thật):

```
<STX><CR><LF>
 Date:2026-11-03 09:15<CR><LF>
 No. 0042<CR><LF>
 ID:9900000123<CR><LF>
 UBG Normal 3.4µmol/L<CR><LF>
 BIL Neg<CR><LF>
 KET Neg<CR><LF>
*BLD Ca25 Ery/µL<CR><LF>
 PRO Neg<CR><LF>
 NIT Neg<CR><LF>
 LEU Neg<CR><LF>
 GLU Neg<CR><LF>
 SG  1.020<CR><LF>
 PH  6.0<CR><LF>
 VC  0 mmol/L<CR><LF>
<ETX>
```

Biến thể tắt mã vạch: bỏ nguyên dòng `ID:…`.

## 8. Bẫy và lưu ý khi viết driver

1. **Byte 0xAB**: xử lý ở mức byte trước khi giải mã chuỗi; coi như khoảng trắng. Giải mã cả frame bằng UTF-8 thì `0xAB` đứng lẻ là byte hỏng.
2. **Mã hóa ký tự ngoài ASCII** (`µ`, `：`, `－`): máy Trung Quốc thường dùng GB2312/GBK *(suy luận)*. Bộ giả lập cũ phát UTF-8 nên test cũ không chứng minh được gì. Giải mã phần đơn vị theo bảng mã cấu hình được (GBK/UTF-8/Latin-1).
3. **Bố cục thay đổi theo cài đặt**: nhận diện dòng theo tiền tố/nhãn, không theo số dòng; dòng chỉ số không đo bị bỏ hẳn.
4. **CT200/DR200**: nhãn khác (`URO`, `WBC`), độ rộng giá trị khác (14), không có SP đầu dòng (CT200), có dòng trống — cấu hình driver phải chọn đúng định dạng hoặc tự nhận diện bằng dòng đầu (`Date:` / `#` / `NO.`).
5. **Không có mã vạch** → `No.` quay vòng, không ghép tự động.
6. **Cờ `*`/`＊`** không được lọt vào mã hoặc giá trị; tách thành cờ.
7. **Nhãn**: `PH`/`pH`, `ALB`/`MALB`, `Ca` chữ thường — so khớp không phân biệt hoa thường, chấp nhận cả `MALB`.
8. **Chế độ LIS "Standard"/"Simple"**: tài liệu chỉ nêu tên, không mô tả khác biệt — **Tài liệu không nêu — cần đo trên máy thật**; chụp bản ghi thô ở cả hai chế độ.
9. **Code cũ** (`lis-agent/src/protocol/codec/fixed-field.ts`): giữ nguyên cả vùng giá trị, không tách mức/số/đơn vị, không tách cờ `*`, không thử GBK.

## 9. Kiểm thử

- Bản ghi thô từ máy thật (cổng nghe thụ động): ID On/Off; Plus Used On/Off; Abnormal On có chỉ số vượt ngưỡng; LIS Standard và Simple; mỗi định dạng đơn vị đang dùng; que 11 và que có CRE/MALB/Ca; một lần đo que chuẩn/QC (xem có ra cổng không).
- Mô phỏng: phát lại byte thô qua cổng COM ảo (com0com); **không dùng bộ giả lập cũ làm chuẩn**.
- Lỗi phải thử: mất ETX; hai bản tin dính liền; nhiễu trước STX; baud lệch 1200/9600; nhãn lạ → "kênh chưa biết"; mã vạch 19 ký tự; byte `0xAB` giữa dòng.

## 10. Nguồn

| Tài liệu | Mục dùng | Tệp |
|---|---|---|
| *Urine Analyzer (Model: HC-300) User Manual*, DIRUI, bản EN 08/2023 | §2.2–2.3 (cổng, đầu đọc mã vạch), §3.2.5.3 (Status: ID, Abnormal, Plus Used, LIS Port), §3.2.5.5 (Cutoff), §3.2.5.6 (Baud, LIS Standard/Simple), **Appendix B** (tham số cổng, 4 bố cục khung), **Appendix C** (quy đổi) | `tai-lieu-hang/Dirui_HC-300_User_Manual_EN_2023-08.pdf` |
| *H Series Urine Analyzer (Model: H-100) User Manual*, REV. 06-2017 | So sánh (A:C, RT, thang MALB của H-100) | `tai-lieu-hang/Dirui_H-Series_H-100_User_Manual_REV06-2017.pdf` |
| Driver mã nguồn mở H-800 | Cờ `*` thay SP đầu dòng, cách tách trường — **của H-800** | https://github.com/vilake/DiruiH800Conn |
| Code bản cũ | `lis-agent/src/drivers.ts` (`dirui-hc300`), `protocol/layouts.ts` (`DIRUI_H_SERIES`), `codec/fixed-field.ts`, `tools/simulate.ts` | `~/Downloads/lis-tham-khao/lis-agent/` |
| Bảng map kênh | `lis-server/seed/dirui-hc300.sql` | `~/Downloads/lis-tham-khao/lis-server/seed/` |
| Bản ghi thô | `lis-agent/.demo-data/rawlog/PK-BINHMINH_hc300_*.jsonl` — dữ liệu giả lập | như trên |

**Còn cần hỏi hãng DIRUI / nhà phân phối:** bảng mã ký tự của cổng LIS (GBK hay ASCII mở rộng), vị trí byte `0xAB`, khác biệt chế độ LIS Standard/Simple, kết quả QC có gửi qua cổng không.
