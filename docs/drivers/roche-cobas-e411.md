# Roche cobas e 411 (và Elecsys 2010) — miễn dịch

> Trạng thái tài liệu: ✅ đủ để viết driver (có Host Interface Manual gốc của hãng, bản 2.2/2013 và bản 2.3 US/2016, giao thức khớp nhau). Riêng **bảng mã xét nghiệm** phải lấy từ màn hình máy (tài liệu chỉ có ví dụ).
> Kiểm chứng: chưa đối chiếu máy thật (bản ghi trong kho LIS cũ là giả lập)
> Driver: `roche-e411` — họ giao thức: `astm-e1394` (khung ASTM E1381-91, bản tin E1394-91; dùng lớp chung trong [astm-e1394.md](astm-e1394.md))

## 1. Tóm tắt

- Máy miễn dịch điện hóa phát quang (ECL). cobas e 411 là **đời kế tiếp Elecsys 2010** và giữ nguyên giao thức
  host của Elecsys 2010; thêm một kiểu thứ hai tương thích họ cobas (cobas 6000). Trên máy chọn
  **"Elecsys® type"** hoặc **"cobas® type"** — hai kiểu khác nhau đáng kể về record và cách đóng frame.
  Các MSR (Manufacturer Specific Record) riêng của Elecsys 2010 **không** được e 411 hỗ trợ.
- Nối **RS-232C**, một cổng, DB-9 bên trái máy (nhãn HOST). Không có TCP/IP trong tài liệu.
- **Hai chiều**: máy hỏi chỉ định thời gian thực (`Q`) sau khi đọc barcode; LIS cũng có thể đẩy chỉ định theo
  lô (batch, chỉ cobas type có ví dụ). Máy gửi kết quả bệnh nhân và QC (không gửi kết quả chuẩn/calibration).
- **Điểm khó nhất:** hai kiểu giao thức khác nhau ở Universal Test ID (`^^^10^^0` so với `^^^10//not`),
  mã cờ trong record `C` (bảng số khác nhau), loại mẫu (`SAMPLE/NORMAL` so với `S1/SC`), và cobas type
  **gộp nhiều record vào một frame**; LIS phải trả lời query trong **15 giây** và **trả lại nguyên**
  SequenceNo/CarrierNo/Position, sai một trường là máy hủy và bỏ đo mẫu.

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | RS-232C, **1 cổng**, đầu **D-SUB 9** nhãn **HOST** ở cạnh trái máy | Bảng B-1; mục Communication cable (B-7) |
| Chân | Máy: 2 = RXD, 3 = TXD, 5 = SG; **chân 7–8 nối tắt (RTS–CTS) phía máy**. Host: TXD→chân 2, RXD←chân 3, SG–5 | Hình B-2 |
| Cáp | Cáp chéo 3 dây kiểu null-modem (TXD↔RXD), dài tối đa **15 m** | Bảng B-1, hình B-2 |
| Tốc độ | **19200 / 9600 / 4800**; hãng khuyên **9600** | Bảng B-1, B-3 |
| Khung ký tự | Chọn 1 trong 8: 7E2, 7O2, 7E1, 7O1, 8N2, **8N1** (khuyên dùng), 8E1, 8O1 | Bảng B-2 |
| Bắt tay phần cứng | Không (chỉ 3 dây; RTS–CTS tự nối tắt) | Hình B-2 |
| Chế độ | Bán song công (half duplex) | Bảng B-1 |
| TCP/IP | **Tài liệu không nêu** — chỉ có RS-232C. Muốn nối mạng thì dùng bộ chuyển serial↔TCP *(suy luận)* | — |
| Cài đặt trên máy | Bật **Host Communication = On** ở màn **Start Conditions**. Thông số chỉ sửa được khi Host Communication đang **Off**, tại **[Utility] → [System] → [Host Communication Setting]** | B-3, B-4 |

Các tham số truyền (Bảng B-3):

| Tham số | Giá trị | Ý nghĩa |
|---|---|---|
| Protocol type | Elecsys® / cobas® | Chọn kiểu giao thức (khuyên Elecsys) |
| Automatic result upload | Yes / No | Yes: gửi kết quả thời gian thực khi đủ kết quả của mẫu |
| Communication trace | Yes / No | Ghi vết; tệp `d:\e411\data\log\host_trace.log` — hãng yêu cầu gửi tệp này khi báo lỗi |
| STAT Sample Result Upload | By Sample / By Test | Mẫu cấp cứu: gửi khi đủ cả mẫu, hoặc gửi từng xét nghiệm ngay khi có |
| Automatic recovery of session *(chỉ cobas)* | Yes / No | Tự nối lại sau lỗi E1381; tin lỗi và tin host gửi trong lúc nối lại bị bỏ |
| QC additional information *(chỉ cobas)* | Control name / Lot No. | Specimen ID của QC là tên control, hoặc `tên^lô` |
| Send result only mode *(chỉ cobas)* | Yes / No | Chỉ gửi kết quả, **không hỏi chỉ định** (không áp dụng cho batch) |
| Host test code | Bảng Host Test Code | Ánh xạ mã máy ↔ mã host; **phải khai cho mọi xét nghiệm dùng, kể cả ứng dụng mới**. Bản 2.2 ghi: ở cả hai kiểu Elecsys và cobas đều phải nhập Host Code thì mới gửi được kết quả (B-5) |

## 3. Khung truyền và bắt tay

### 3.1 Ký tự điều khiển

| Ký tự | Hex | Dùng |
|---|---|---|
| ENQ | `05` | mở phiên |
| ACK | `06` | chấp nhận |
| NAK | `15` | từ chối / bận |
| STX | `02` | đầu frame |
| ETB | `17` | kết thúc frame giữa |
| ETX | `03` | kết thúc frame cuối |
| EOT | `04` | đóng phiên |
| CR / LF | `0D` / `0A` | CR kết thúc record (nằm trong text, trước ETX/ETB); `CR LF` kết thúc frame |

Ký tự **cấm** trong text: SOH, STX, ETX, EOT, ENQ, ACK, LF, DLE, DC1–DC4, NAK, SYN, ETB (mục D-3, ghi chú 2).

### 3.2 Frame

```
<STX> FN text <ETB> CS1 CS2 <CR> <LF>     (frame giữa)
<STX> FN text <ETX> CS1 CS2 <CR> <LF>     (frame cuối hoặc frame đơn)
```

- `FN`: một ký tự, **bắt đầu từ 1** đến 7, rồi quay về 0…7.
- `text`: tối đa **240 byte**; frame tối đa **247 byte**. Text > 240 byte thì tách bằng ETB.
- **Elecsys type**: mỗi frame chứa **không quá một record** (giống ASTM chuẩn).
- **cobas type**: **một frame có thể chứa nhiều record** (các record nối nhau bằng `<CR>` trong cùng một text).
  Bộ tách record phải tách theo `<CR>` sau khi ghép text, không được coi "một frame = một record".

### 3.3 Checksum

Cộng mã ASCII của mọi byte **từ FN đến hết ETX/ETB (kể cả hai byte này)**, lấy byte thấp, viết 2 ký tự hex
in hoa `0`–`9`, `A`–`F`. Ví dụ của hãng: `<STX>1Test<ETX>` → tổng `0x1D4` → `D4` → gửi `"D" "4"`.
Đã tính lại bằng script (mục 7) ra đúng `D4`.

### 3.4 Bắt tay và thời gian chờ (theo phía máy)

| Tình huống | Hành vi máy | Nguồn |
|---|---|---|
| Máy gửi ENQ, nhận NAK (hoặc ký tự khác ACK) | Chờ **10 s** rồi ENQ lại; tối đa **6 lần** | D-6 |
| Tranh chấp (cả hai cùng ENQ) | **Máy được ưu tiên**: host phải thôi ENQ và trả ACK/NAK. Máy chờ **1 s** rồi gửi lại ENQ | D-6 |
| Không có trả lời ENQ | Sau **15 s** máy gửi EOT, báo lỗi "No answer from host" | D-6, D-7 |
| Máy gửi frame, host trả khác ACK | Gửi lại frame đó, tối đa **6 lần**, rồi EOT + báo lỗi | D-8 |
| Máy gửi frame, host im lặng | Sau **15 s** máy EOT + báo lỗi | D-8 |
| Host gửi frame | Máy kiểm FN + checksum: đúng → ACK, sai → NAK (bỏ frame) | D-8 |
| Host bỏ dở giữa chừng | 15 s kể từ ACK/NAK cuối không có frame/EOT → máy xóa tin dở, về rỗi | D-8 |
| EOT | Bên nhận về rỗi, không trả lời; chỉ các frame đã nhận đủ mới có hiệu lực | D-9, D-10 |

Ở trạng thái rỗi máy bỏ qua mọi ký tự khác ENQ.

### 3.5 Mã lỗi giao tiếp trên máy (Bảng G-1)

`44-01-01` … `44-01-12`: thử lại khi gửi, hủy khi gửi/nhận, gửi lại thất bại, timeout gửi/nhận, sai định dạng,
không cập nhật được CSDL, lỗi phần cứng/phần mềm, "Upload is defined but host com. is OFF". Ghi vào host trace log.

## 4. Bản tin kết quả (máy → LIS)

### 4.1 Thứ tự record

| Bản tin | Cấu trúc | Header field 11 (chỉ cobas) |
|---|---|---|
| Hỏi chỉ định (máy → LIS, thời gian thực) | `H Q L` | `TSREQ^REAL` |
| Trả lời chỉ định (LIS → máy) | `H P O L` | `TSDWN^REPLY` |
| Gửi chỉ định theo lô (LIS → máy) | `H P O … L` | `TSDWN^BATCH` |
| Kết quả thời gian thực | `H P O {R [C]} L` | `RSUPL^REAL` |
| Kết quả theo lô (màn DataReview, Send to Host) | `H P O {R [C]} L` | `RSUPL^BATCH` |

Không có "host hỏi kết quả" (Inquiry of result) ở cả hai kiểu. Kết quả chuẩn **không** gửi. Máy **không tự chạy
lại** (rerun phải làm tay). QC **không** hỏi chỉ định.

Dấu phân cách cố định `|\^&`; escape `&F&`, `&S&`, `&R&`, `&E&` (escape khác bị coi là rỗng).

### 4.2 H — Message header

| Vị trí | Trường | Elecsys | cobas |
|---|---|---|---|
| H.1 | Record type | `H` | `H` |
| H.2 | Delimiters | `\^&` | `\^&` |
| H.5 | Sender | để trống khi máy gửi | `cobas-e411^1` (máy gửi); host gửi tên tùy ý (chữ, số, `-`) + `^1` |
| H.10 | Receiver | — | `host` (máy gửi); host gửi `cobas-e411` |
| H.11 | Comment/Special instr. | — | `<ý nghĩa>^<nguyên nhân>`: `TSREQ`/`RSUPL`/`TSDWN` + `REAL`/`BATCH`/`REPLY` |
| H.12 | Processing ID | `P` | `P` |
| H.13 | Version | — | `1` |

Ví dụ: Elecsys `H|\^&||||||||||P||` ; cobas `H|\^&|||cobas-e411^1|||||host|RSUPL^REAL|P|1`.

### 4.3 P — Patient

Rỗng. Elecsys (máy gửi): `P|1` kèm 33 dấu `|` đệm; Elecsys (host gửi) và cobas: `P|1`.
Tài liệu **không** dùng thông tin hành chính bệnh nhân; comment record sau O (thông tin nhân khẩu) không dùng.

### 4.4 O — Test order

| Vị trí | Trường | Elecsys | cobas |
|---|---|---|---|
| O.3 | Specimen ID (≤ 22) | Sample ID; QC = **tên control** | Sample ID; lỗi đọc barcode → `@<SequenceNo>`; QC = tên control hoặc `tên^lô` (theo cài đặt) |
| O.4 | Instrument Specimen ID | `<SeqNo>^<CarrierNo>^<Pos>^^<SampleType>^<Container>` | như Elecsys |
| O.4.1 | SequenceNo (≤ 4) | số thứ tự nội bộ máy | như trái |
| O.4.2 | CarrierNo (≤ 5) | số đĩa (disk) hoặc rack; rack chưa rõ số → `@` + số | như trái |
| O.4.3 | PositionNo (≤ 2) | vị trí trong đĩa/rack | như trái |
| O.4.5 | SampleType | `SAMPLE` (bệnh nhân) / `CONTROL` (QC) | `S1` huyết thanh, `S2` nước tiểu, `S5` khác, `QC` |
| O.4.6 | ContainerType | `NORMAL` (ống/cốc) / `REDUCED` (cốc, thể tích giảm) | `SC` (ống/cốc) / `MC` (thể tích giảm, **không phải micro cup**) |
| O.5 | Universal Test ID | máy gửi: `ALL` cố định | danh sách xét nghiệm `^^^<mã>^<pha loãng>` lặp bằng `\` |
| O.6 | Priority | `R` thường + QC, `S` cấp cứu | `R` / `S`; QC để trống |
| O.8 | Specimen collection date/time | `YYYYMMDDHHMMSS` (tùy chọn) | như trái |
| O.12 | Action code | máy gửi: `X` (đã đo), `X\Q` (QC), `A` (chỉ định thêm); host gửi: `N` (mới), `Q` (QC) | máy gửi: `N` (kết quả bệnh nhân), `Q` (kết quả QC); host gửi: `A` |
| O.16 | Specimen descriptor | — | `1` huyết thanh, `2` nước tiểu, `5` khác (ưu tiên hơn SampleType nếu lệch, trừ QC) |
| O.23 | Ngày giờ đủ kết quả | — | `YYYYMMDDHHMMSS` (host không đặt) |
| O.26 | Report type | máy gửi `O`; host gửi `Q` (trả lời query) / `Z` (không có chỉ định) | máy gửi `F`; host gửi `O` |

**Phân biệt mẫu thường / QC:** Elecsys — O.4.5 = `CONTROL` và O.12 = `X\Q`; cobas — O.4.5 = `QC` và O.12 = `Q`.
QC lấy O.3 là tên control (không phải barcode).

### 4.5 R — Result

| Vị trí | Trường | Elecsys | cobas |
|---|---|---|---|
| R.3 | Universal Test ID | `^^^<mã>^<hệ số pha loãng>^<tiền pha loãng 0/1>` | `^^^<mã>/<tỉ lệ pha loãng>/<not \| pre-diluted>` (thành phần 4 tách bằng `/`) |
| R.4 | Giá trị | định lượng: số ≤ 7 ký tự, có thể đầu `<` / `>` khi ngoài khoảng đo; **7 dấu cách** nếu không có KQ. Định tính: `<-1/0/1>^<COI>` | định lượng: `<giá trị>^<message value>` (message value 0–31, thường rỗng → `2.15^`); định tính: `<-1/0/1>^<COI>` |
| R.5 | Đơn vị (≤ 6) | chuỗi đơn vị trên máy | như trái |
| R.6 | Khoảng tham chiếu | `<thấp>^<cao>`; với QC là khoảng QC | — (để trống) |
| R.7 | Cờ bất thường | `L`, `H`, `<`, `>`, `N`, `A` | `L`, `H`, `LL`, `HH`, `N`, `A` |
| R.9 | Trạng thái | `F` cuối, `X` không làm được, `R` đã gửi, `V` người dùng duyệt, `Y` hệ thống chặn, `+` người dùng chặn | `F` lần đầu, `C` chạy lại |
| R.11 | Operator ID | — | người đo (vd `admin`) |
| R.12 / R.13 | Bắt đầu / kết thúc đo | `YYYYMMDDHHMMSS` | — |
| R.14 | Instrument ID | — | `E1` |

Định tính: `1` dương tính (reac.), `0` vùng xám (border), `-1` âm tính (n-reac.); thành phần 2 là COI
(cut-off index, đơn vị thường là `COI`).

cobas type gửi thêm **xét nghiệm tính toán** (tối đa 5, khai ở màn "Calc. Test") khi mọi xét nghiệm thành phần
đã đo xong; Elecsys type không gửi. cobas type **không gửi** xét nghiệm bị cờ "Review by exception" trừ khi
chọn "with Review by Exception Results" khi gửi theo lô.

### 4.6 C — Comment sau R (cờ dữ liệu)

Chỉ có khi kết quả có data alarm. Elecsys: `C|1|I|<số cờ>^<thông điệp>|I`; cobas: `C|1|I|<số cờ>|I`.
Bảng cờ đầy đủ ở Bảng E-5 (tên cờ trên màn hình, số Elecsys, số cobas). Các cờ hay gặp:

| Ý nghĩa | Hiển thị | Số Elecsys | Số cobas |
|---|---|---|---|
| Thiếu mẫu | Samp.S | 7 | 3 |
| Thiếu thuốc thử / dung dịch pha | Reag.S | 8, 9, 10 | 4 |
| Nhiệt độ bất thường (đĩa thuốc thử / ủ / buồng đo / thuốc thử hệ thống) | Reag.T / Inc.T / Cell.T / SysR.T | 12 / 13 / 14 / 15 | 74 / 75 / 77 / 76 |
| ADC bất thường | ADC.E | 17 | 1 |
| Lỗi tính toán | Calc.? | 24 | 39 |
| Không có / dùng chuẩn cũ | Cal.E | 25 / 26 | 43 |
| Mẫu có cục đông | Samp.C | 45 | 72 |
| Thấp hơn khoảng tham chiếu | L | 48 | 41 |
| Cao hơn khoảng tham chiếu | H | 49 | 40 |
| Dưới khoảng đo | <Test | 50 | 27 |
| Trên khoảng đo | >Test | 51 | 26 |
| RackPack hết hạn | ReagEx | 52 | 101 |
| Không có mẫu | Samp.S | 53 | 3 |
| Tín hiệu dưới ngưỡng | <SigL | 58 | 100 |
| Lỗi xét nghiệm tính toán | ClcT.E | — | 37 |

Cờ 1–6 (Elecsys) là các kiểu hủy (mất điện, E.STOP, STOP…) — cobas không có số tương ứng. Thông điệp cờ
**không phải UTF-8**.

### 4.7 L — Termination

`L|1|N` (cobas, bình thường) / `L|1|E` (lỗi). Elecsys: khi máy gửi query thì `L|1|I` (trong ví dụ của hãng);
trả lời/kết quả bình thường thì **bỏ trống** mã kết thúc (`L|1|`); `E` = lỗi; `Q` = record không hiểu được;
`F` = query có dữ liệu trả lời (Bảng E-2).

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

**Khi nào hỏi:** sau khi đọc Sample ID của mẫu bệnh nhân **chưa có chỉ định** trên máy. Mẫu không barcode
hoặc đọc barcode lỗi (rack): máy hỏi sau khi đọc **số rack**, khóa là **rack + vị trí** (cobas: Sample ID sinh
dạng `@<SeqNo>`). Nếu đọc barcode lỗi mà số rack cũng là số sinh (`@…`), host **không thể** xác định mẫu.
cobas type với "Send result only mode = Yes" thì **không hỏi**.

**Q — Request information:**

| Vị trí | Elecsys | cobas |
|---|---|---|
| Q.3 | `^<SampleID>^<SeqNo>^<CarrierNo>^<Pos>^^<SampleType>^<Container>` | `^^<SampleID>^<SeqNo>^<CarrierNo>^<Pos>^^<SampleType>^<Container>` (**thêm một `^` ở đầu**) |
| Q.5 | `ALL` | `ALL` |
| Q.13 | `O` = hỏi chỉ định; `A` = hủy câu hỏi trước | như trái |

**LIS trả lời** (phiên mới do LIS mở bằng ENQ): `H P O L`.
- O.3 = Sample ID; **O.4 phải trả nguyên văn** SequenceNo, CarrierNo, Position như trong Q. Bản US 2016 nói
  thêm: nếu đổi bất kỳ trường nào, máy gửi hủy và **bỏ đo** mẫu.
- Có chỉ định: O.5 = `^^^10^\^^^30^2\^^^40^` …; Elecsys O.12 = `N`, O.26 = `Q`; cobas O.12 = `A`, O.16 = `1`, O.26 = `O`.
- Không có chỉ định: O.5 rỗng; Elecsys O.26 = `Z`; cobas vẫn `O` với O.5 rỗng.
- Tối đa 60 lặp nhưng **chỉ 18 xét nghiệm** có hiệu lực cho một mẫu; **không được** gửi trùng một xét nghiệm
  trong cùng mẫu (từ phần mềm 02-04).
- Gửi chỉ định mới cho mẫu đã có chỉ định → máy **xóa chỉ định cũ** và thay bằng chỉ định mới.

**Pha loãng tự động** (thành phần 5 của mã xét nghiệm):

| Elecsys (hệ số) | cobas (tỉ lệ) | Pha loãng |
|---|---|---|
| `0` | trống hoặc `1` | không pha |
| `1` | `2` | 1:2 |
| `2` | `5` | 1:5 |
| `3` | `10` | 1:10 |
| `5` | `20` | 1:20 |
| `7` | `50` | 1:50 |
| `9` | `100` | 1:100 |

cobas type: không pha loãng vẫn phải có `^` sau mã (`^^^10^`).

**Timeout:** máy chờ **15 s**; quá hạn gửi lại Q với Q.13 = `A` (hủy). Rack: nếu cả rack không xong trong
khoảng **40 s** máy gửi hủy.

**Batch (LIS đẩy trước):** cobas `TSDWN^BATCH`, O.4 = `^^^^S1^SC` (máy tự đánh SeqNo), chỉ nhận **chỉ định mới**.
Máy lưu tối đa **2000 xét nghiệm** (vd 1000 mẫu × 2), quá thì ghi đè cũ. Phải nạp trước khi máy đọc Sample ID.

## 6. Bảng mã chỉ số

Mã gửi qua đường truyền là **Application Code (Elecsys type, số ≤ 3 chữ số)** hoặc **Host Code (cobas type,
do người dùng khai trong bảng Host Test Code)**. Chữ số cuối của Application Code là **"thế hệ"** thuốc thử:
TSH thế hệ 0 = `10`, thế hệ 1 = `11`. Elecsys type: host đặt `10` mà trên máy chỉ có `11` thì máy chạy `11` và
**trả kết quả với mã `11`**. cobas type: một Host Code có thể gán cho nhiều thế hệ (máy ưu tiên thế hệ mới nhất).

Tài liệu **không có bảng mã đầy đủ**; chỉ có các mã sau (ví dụ và ảnh màn hình B-5):

| Mã máy (Application Code) | Tên trên máy | Chỉ số | Đơn vị (theo ví dụ) | Ghi chú |
|---|---|---|---|---|
| 10 / 11 | TSH 0 / TSH 1 | TSH | µIU/mL (hãng in `ulU/ml`) | Tài liệu nêu rõ |
| 30 | — | *(tên không nêu)* | ng/dl trong ví dụ | Chỉ là ví dụ trao đổi |
| 40 | — | *(tên không nêu)* | ulU/ml / ng/ml trong ví dụ | Chỉ là ví dụ |
| 400 | — | xét nghiệm định tính *(tên không nêu)* | COI | Chỉ là ví dụ |
| 90 | TNT-HSST 0 | Troponin T hs STAT | Tài liệu không nêu | Ảnh màn Host Test Code |
| 101 | E2 1 | Estradiol | Tài liệu không nêu | Ảnh màn hình; ví dụ Host Code gán `102` |
| 111 | TESTO 1 | Testosterone | Tài liệu không nêu | Ảnh màn hình |
| 121 | PROG 1 | Progesterone | Tài liệu không nêu | Ảnh màn hình |
| 131 | PRL 1 | Prolactin | Tài liệu không nêu | Ảnh màn hình |
| 171 | HCGSTAT1 | hCG STAT | Tài liệu không nêu | Ảnh màn hình |
| 180 | TNT-HS 0 | Troponin T hs | Tài liệu không nêu | Ảnh màn hình |
| 190 | TP1NP 0 | Total P1NP | Tài liệu không nêu | Ảnh màn hình |
| 211 | CKMBSTAT1 | CK-MB STAT | Tài liệu không nêu | Ảnh màn hình |
| 221 | TN-T 1 | Troponin T | Tài liệu không nêu | Ảnh màn hình |

Bản US 2016 cảnh báo: **bỏ qua mọi số Application Code trong sách**, lấy mã từ tờ hướng dẫn (method sheet /
package insert) hoặc tài liệu "application code numbers" của hãng; LIS phải dùng **mã duy nhất** cho từng xét
nghiệm và luôn ghi nhận **máy nguồn** của kết quả. Do đó bảng ánh xạ phải **chụp từ màn [Utility] → Host Test
Code của chính máy BVĐK 10**. Đơn vị: Tài liệu không nêu — cần đo trên máy thật (lấy theo R.5 thực tế).

## 7. Bản tin mẫu

Dữ liệu giả (Sample ID `TEST0001`/`TEST0002`, ngày 2026-11-03). Checksum được tính bằng script Python
(cộng byte từ FN đến ETX, `& 0xFF`, hex in hoa) và đã kiểm lại ví dụ của hãng `1Test<ETX>` → `D4`.

### 7.1 Kết quả — Elecsys type (mỗi record một frame)

```
<ENQ>
<STX>1H|\^&||||||||||P||<CR><ETX>05<CR><LF>
<STX>2P|1||||||||||||||||||||||||||||||||||<CR><ETX>B7<CR><LF>
<STX>3O|1|TEST0001|51^0^7^^SAMPLE^NORMAL|ALL|R|20261103083000|||||X||||||||||||||O<CR><ETX>9A<CR><LF>
<STX>4R|1|^^^10^^0|2.15|uIU/ml|0.270^4.20|N||F|||20261103083210|20261103085010|<CR><ETX>82<CR><LF>
<STX>5R|2|^^^30^^0|0.62|ng/dl|0.93^1.70|L||F|||20261103083240|20261103085040|<CR><ETX>18<CR><LF>
<STX>6C|1|I|48^Below normal(expected)range|I<CR><ETX>58<CR><LF>
<STX>7L|1|<CR><ETX>BC<CR><LF>
<EOT>
```
(LIS trả `<ACK>` sau ENQ và sau từng frame.)

### 7.2 Kết quả — cobas type (cả bản tin trong một frame, 237 byte text)

```
<ENQ>
<STX>1H|\^&|||cobas-e411^1|||||host|RSUPL^REAL|P|1<CR>P|1<CR>O|1|TEST0001|51^0^7^^S1^SC|^^^10^\^^^30^|R||||||N||||1|||||||20261103085040|||F<CR>R|1|^^^10//not|2.15^|uIU/ml||N||F||admin|||E1<CR>R|2|^^^30//not|0.62^|ng/dl||L||F||admin|||E1<CR>C|1|I|41|I<CR>L|1|N<CR><ETX>49<CR><LF>
<EOT>
```

### 7.3 Hỏi / đáp chỉ định — cobas type

Máy hỏi:
```
<STX>1H|\^&|||cobas-e411^1|||||host|TSREQ^REAL|P|1<CR>Q|1|^^TEST0002^52^0^8^^S1^SC||ALL||||||||O<CR>L|1|N<CR><ETX>7A<CR><LF>
```
LIS trả lời có chỉ định (mã 10 và 30):
```
<STX>1H|\^&|||host^1|||||cobas-e411|TSDWN^REPLY|P|1<CR>P|1<CR>O|1|TEST0002|52^0^8^^S1^SC|^^^10^\^^^30^|R||||||A||||1||||||||||O<CR>L|1|N<CR><ETX>18<CR><LF>
```
LIS trả lời không có chỉ định:
```
<STX>1H|\^&|||host^1|||||cobas-e411|TSDWN^REPLY|P|1<CR>P|1<CR>O|1|TEST0002|52^0^8^^S1^SC||R||||||A||||1||||||||||O<CR>L|1|N<CR><ETX>08<CR><LF>
```

### 7.4 Hỏi / đáp chỉ định — Elecsys type

Máy hỏi:
```
<STX>1H|\^&||||||||||P||<CR><ETX>05<CR><LF>
<STX>2Q|1|^TEST0002^52^0^8^^SAMPLE^NORMAL||ALL||||||||O<CR><ETX>AA<CR><LF>
<STX>3L|1|I<CR><ETX>01<CR><LF>
```
LIS trả lời có chỉ định:
```
<STX>1H|\^&||||||||||P||<CR><ETX>05<CR><LF>
<STX>2P|1<CR><ETX>3F<CR><LF>
<STX>3O|1|TEST0002|52^0^8^^SAMPLE^NORMAL|^^^10^\^^^30^|R||||||N||||||||||||||Q<CR><ETX>12<CR><LF>
<STX>4L|1|<CR><ETX>B9<CR><LF>
```
LIS trả lời không có chỉ định (O.26 = `Z`):
```
<STX>3O|1|TEST0002|52^0^8^^SAMPLE^NORMAL||R||||||N||||||||||||||Z<CR><ETX>0B<CR><LF>
```
(frame 1, 2, 4 như trên.)

Script tính checksum (chạy lại khi sửa ví dụ):

```python
def cs(fn, text, end="\x03"):
    return f"{sum((str(fn) + text + end).encode('ascii')) & 0xFF:02X}"
assert cs(1, "Test") == "D4"   # ví dụ của hãng, mục D-4
```

## 8. Bẫy và lưu ý khi viết driver

- **Phải biết máy đang ở kiểu nào** (Elecsys/cobas) — cấu hình driver có tham số `protocolType`. Có thể tự nhận
  diện: cobas type có `cobas-e411` ở H.5 và `RSUPL/TSREQ` ở H.11 *(suy luận)*.
- **cobas type nhiều record trong một frame**: ghép text các frame (ETB…ETX) rồi tách theo `<CR>`.
- **Universal Test ID khác cú pháp**: Elecsys dùng `^` cho cả mã, pha loãng, tiền pha loãng (`^^^10^^0`);
  cobas dùng `/` trong thành phần 4 (`^^^10//not`, `^^^30/2/pre-diluted`). Khi gửi chỉ định cobas vẫn dùng
  `^^^10^2`. Lấy mã = phần trước `/` hoặc `^` đầu tiên của thành phần 4.
- **Ví dụ batch của hãng** viết `^^^10^/^^^30^2/^^^40^` (dùng `/` làm dấu lặp) trong khi bảng record quy định `\`.
  Driver gửi theo `\`; nếu máy từ chối thì thử dạng của ví dụ *(cần đo trên máy thật)*.
- **Q.3 khác nhau một `^` đầu** giữa hai kiểu (`^ID…` so với `^^ID…`).
- Trả lời query **trong 15 s**, chép nguyên O.4 từ Q; không đổi SeqNo/Carrier/Pos.
- **Sample ID `@<số>`** là ID máy tự sinh khi đọc barcode lỗi — không được ghép với phiếu chỉ định nào;
  CarrierNo đầu `@` là số rack máy tự sinh.
- **Mã xét nghiệm đổi theo thế hệ thuốc thử** (10 → 11): bảng ánh xạ phải cho nhiều mã máy → một chỉ số; khi
  đổi lô thuốc thử thế hệ mới, kết quả có thể về mã mới.
- **Giá trị rỗng = 7 dấu cách**, không phải chuỗi rỗng; cắt khoảng trắng trước khi đọc số. Giá trị có `<`/`>`
  đầu khi ngoài khoảng đo — giữ nguyên dạng chuỗi, không ép số.
- **Cờ có hai nơi**: R.7 (L/H/N/A, `<`/`>` hoặc `LL`/`HH`) và record C (số cờ, bảng số khác nhau theo kiểu).
- **Đơn vị** máy gửi theo chuỗi cài trên máy (sách in `ulU/ml` là lỗi chữ của µIU/mL) — map đơn vị theo chuỗi thô.
- Elecsys `L` khi máy gửi query là `L|1|I`, khi trả lời thì `L|1|` — không bắt buộc mã kết thúc.
- Chỉ ASCII; thông điệp cờ không UTF-8. Không gửi tiếng Việt có dấu xuống máy.
- Host Code chưa khai trên máy → kết quả **không được gửi** (B-5). Khi lắp đặt phải khai đủ.
- Không có kết quả chuẩn qua LIS; QC đi chung kênh — phải tách theo O.4.5/O.12 trước khi ghép phiếu bệnh nhân.

## 9. Kiểm thử

- **Bộ bản tin**: mục 7 cho cả hai kiểu; thêm kết quả định tính (`-1^0.303`, đơn vị `COI`), kết quả rỗng
  (7 dấu cách), giá trị `>`/`<`, QC (Elecsys `CONTROL`/`X\Q`; cobas `QC`/`Q`, O.3 = `tên^lô`), Sample ID `@3`,
  rack `@95`, xét nghiệm tính toán (cobas), kết quả chạy lại (cobas R.9 = `C`), record > 240 byte (tách ETB),
  FN quay vòng 7 → 0.
- **Mô phỏng máy**: cặp COM ảo + kịch bản E1381 (ENQ/ACK, checksum, NAK, timeout); dùng hàm `cs` ở mục 7 để
  sinh frame.
- **Tình huống lỗi**: NAK 6 lần → máy EOT; LIS im 15 s; tranh chấp ENQ–ENQ (LIS phải nhường); LIS trả lời query
  quá 15 s (máy gửi Q…`A`); LIS đổi O.4 trong trả lời (máy hủy); gửi trùng xét nghiệm; > 18 xét nghiệm; frame
  host sai checksum (máy NAK).
- **Máy thật**: ghi lại cài đặt [Host Communication Setting] (kiểu giao thức, baud, frame, upload tự động,
  STAT By Sample/By Test), chụp bảng Host Test Code, bật Communication trace và lấy `host_trace.log` ≥ 1 ngày
  làm bộ bản ghi thô chuẩn.

## 10. Nguồn

| Tài liệu | Phiên bản | Dùng cho | Nơi lấy |
|---|---|---|---|
| **cobas e 411 analyzer — Host Interface Manual**, Roche Diagnostics | **Version 2.2, 11/2013** | Nguồn chính: B (giao diện, cài đặt, cáp), C (loại bản tin), D (E1381), E (record, Bảng E-2, E-3, E-5), F (vết giao tiếp), G (mã lỗi) | [`tai-lieu-hang/Roche_cobas_e411_Host_Interface_Manual_v2.2.pdf`](tai-lieu-hang/Roche_cobas_e411_Host_Interface_Manual_v2.2.pdf) |
| cobas e 411 analyzer — Host Interface Manual for use in the US | **Document Version 2.3, 08/2016** (1907-05-0816) | Đối chiếu | [`tai-lieu-hang/Roche_cobas_e411_Host_Interface_Manual_US_2016.pdf`](tai-lieu-hang/Roche_cobas_e411_Host_Interface_Manual_US_2016.pdf) |

**Khác biệt bản 2.2 và bản US 2.3 (2016):** giao thức, record, bảng cờ, ví dụ giống nhau. Bản 2.3 chỉ thêm:
(1) mục "Application Code Number disclaimer" — bỏ qua số mã trong sách, dùng mã từ method sheet, LIS dùng mã
duy nhất và ghi máy nguồn; (2) ghi chú ở phần vết giao tiếp: đổi SeqNo/Carrier/Position khi trả lời thì máy hủy
và bỏ đo mẫu; (3) sửa chính tả, thương hiệu. Bản 2.2 có câu "ở cả Elecsys và cobas mode phải nhập Host Code mới
gửi được kết quả" (B-5).

**Còn thiếu — cần xin Roche / nhà phân phối:**
- Danh sách **Application Code** của các xét nghiệm Elecsys đang dùng tại BVĐK 10 (tài liệu "Application Code
  Numbers" hoặc method sheet), kèm đơn vị cài trên máy.
- Phiên bản phần mềm máy (để biết ràng buộc "không gửi trùng xét nghiệm" từ 02-04 có áp dụng).
- Elecsys 2010 (bản gốc): nếu BVĐK 10 thực sự là máy 2010 chứ không phải e 411 thì cần **Elecsys 2010 Host
  Interface Manual** — máy 2010 có MSR riêng mà tài liệu e 411 không mô tả.
