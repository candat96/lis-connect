# Sysmex CA-500 series (CA-510/520/530/540/550/560) và CA-600 series — đông máu

> Trạng thái tài liệu: ✅ định dạng riêng Sysmex ("CA1000"/"CA500") cho CA-500 và CA-600 | ✅ định dạng **ASTM / ASTM2** theo *CA-600 ASTM Host Interface Specification* v1.03 (2014) — mục 3.1, 4.1, 5.1, 6.1, 7.1. Với **CA-500** dùng chung đặc tả ASTM này (theo người dùng cung cấp; CA-600 còn có chế độ đặt tên máy "CA-500") — cần xác nhận bằng bản ghi thô máy BVĐK 10
> Kiểm chứng: chưa đối chiếu máy thật
> Driver: `sysmex-ca` (đề xuất) — họ giao thức: fixed-field (định dạng CA1000/CA500) hoặc astm-e1394 (định dạng ASTM)

## 1. Tóm tắt

Máy đông máu bán tự động/tự động nhỏ, đo PT, APTT, Fbg, TT, AT3, D-Dimer… Nối RS-232. Trong menu *Host Computer* có mục **Format: CA1000 / CA500 / ASTM**:

- **CA1000/CA500** — khung riêng của Sysmex: `STX + text cố định vị trí + ETX`, không checksum. *Class A* = đẩy một chiều không chờ trả lời; *Class B* = chờ ACK/NAK, cho phép hỏi chỉ định (hai chiều). Tài liệu hãng mô tả đầy đủ.
- **ASTM / ASTM2** — chuẩn ASTM E1381-91 (tầng thấp) + E1394-91 (record), có đặc tả hãng cho CA-600 (v1.03, 2014). ASTM2 = ASTM + record chú thích `C` (lô thuốc thử, đường chuẩn, QC). Ở chế độ ASTM, cài đặt Class và ACK Text **bị bỏ qua**.

Hệ LIS cũ khai CA500 (InsID 6) với protocol `ASTM`, `SIDIndex = 3` (khác các máy ASTM khác = 2) và đã nhận 39 kết quả → nhiều khả năng máy ở BVĐK 10 đặt Format = ASTM. Đặc tả ASTM xác nhận máy gửi SID ở **O.4 (Instrument Specimen ID), thành phần thứ 3** — khớp `SIDIndex = 3` của hệ cũ. Điểm khó nhất: phải ra máy xem đang đặt Format nào trước khi chọn driver.

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | RS-232C, đầu **DB-9 cái** ở mặt sau thân máy (cổng ghi "HOST") | IM 14.3; SM B-7 |
| Chân | 2 RxD (vào máy), 3 TxD (ra), 4 DTR (ra), 5 SG, 6 DSR (vào), 7 RTS (ra), 8 CTS (vào) | IM 14.3 |
| Chế độ | Bất đồng bộ, **bán song công** | IM 14.3 |
| Baud | 600 / 1200 / 2400 / 4800 / 9600 | IM 10.14 |
| Data bits | 7 / 8 | |
| Stop bits | 1 / 2 | |
| Parity | None / Even / Odd | |
| Mặc định nhà máy (bản service manual) | 2400, 7 bit, 1 stop, Even, Class A, Interval 2 s, Inquiry Auto, Format CA1000, ACK/NAK | SM 5.11 (12) |
| Đang dùng ở BVĐK 10 | **9600, 8, N, 1**, COM7 trạm DK10.2 | `InterfacePara` hệ cũ |
| Bắt tay phần cứng | **Có dùng**: máy theo dõi **DSR** (tắt → lỗi "HC Off Line") và **CTS** (bật RTS rồi chờ CTS tối đa 5 s → lỗi "HC CTS Time Out") | IM 14.3 "Transmission Errors" |
| Cài đặt trên máy | *Settings → I/O Setting → Host Computer*: Status = Connected, Baud, Char Length, Stop Bit, Parity, Class, Interval, Inquiry, Format, ACK Text | IM 10.14 |

Cáp: hãng không kèm cáp. Vì máy kiểm DSR/CTS, cáp 3 dây (2-3-5) sẽ báo Off Line — cần cáp null-modem đủ dây (DTR↔DSR, RTS↔CTS) hoặc nối vòng DTR→DSR, RTS→CTS ngay ở đầu máy *(suy luận)*. IM ghi: chạy không dây điều khiển thì thời gian trả lời T1/T3/T4 phải ≥ 0,2 s.

### 2.1 CA-600 series (theo *CA-600 series User Manual*, 09/2011, mục 10.14 và 14.4)

| Mục | CA-600 | So với CA-500 |
|---|---|---|
| Đầu nối | DB-9 (thân cái, chân đực — "DB-9S") ở mặt sau Main Unit | giống |
| Chân | 2 RxD vào, 3 TxD ra, 4 DTR ra, 5 SG, 6 DSR vào, 7 RTS ra, 8 CTS vào; chân 1, 9 không dùng | giống |
| Chế độ | Bất đồng bộ, bán song công; mã ASCII | giống |
| Baud | 600 / 1200 / 2400 / 4800 / 9600 | giống |
| Ký tự | 7/8 bit; stop 1/2; parity None/Even/Odd | giống |
| Mặc định (bảng cài đặt ban đầu, ch. 14) | Not connected, **2400, 7 bit, 1 stop, Even**, Class A, Interval 2 s, Inquiry Auto, Format **CA1000**, ACK Text ACK/NAK, Flag format Standard | giống CA-500 + thêm Flag format |
| Format | **CA1000 / CA500 / ASTM** | giống; ASTM vẫn không được mô tả trong User Manual |
| ACK Text | "STX-ACK-ETX" hoặc "ACK/NAK" | giống |
| **Flag format** (mới) | "Standard" hoặc "CA-500": quyết định ký tự cờ "đo lại sau pha loãng" — Standard gửi `d`, CA-500 gửi `!` | CA-500 chỉ có `!` |
| Bắt tay phần cứng | Theo dõi DSR (tắt → "Off Line") và CTS (chờ ≤ 5 s sau khi bật RTS → "HC CTS Time Out") | giống |
| Timeout | Cố định 15 s (ACK/NAK, STX, ETX); gửi lại tối đa 3 lần khi NAK | giống |
| Menu | *Settings → I/O Setting → Host Computer* | giống |

Khác biệt nội dung text (mục 14.5 CA-600) — khung `D`/`R`/`S` và độ dài `54 + 9 × n` giữ nguyên:

- Sample Distinction Code có thêm `S` = **dữ liệu đường chuẩn** (CA-500 liệt kê `S` nhưng không giải thích) → driver bỏ qua hoặc ghi log, không lưu thành kết quả bệnh nhân.
- Cờ pha loãng lại: `d` (Flag format = Standard) hoặc `!` (= CA-500). Driver phải nhận **cả hai**. Thứ tự ưu tiên: `*` > `<`/`>` > `+`/`-` > `d` > trống.
- Bảng mã có thêm: `33X` BCPC, `61X` DDi (trùng nhóm với DDPl), `70X` +DDi, `65X` vWF, `66X` −vWF, `67X` +vWF, `80X` WFa, `81X` +WFa, `82X` −WFa. Không còn liệt kê yếu tố II/V/IX–XII, APL, Plg, PFDP. Chỉ số cuối X giữ nguyên nghĩa (1 thời gian, 2 %/nồng độ, 3 ratio, 4 INR, 5 dFbg).
- Rack No. trong giải thích ghi "0001"–"0099"; mẫu STAT được gán số tuần tự đếm từ lúc bật máy (vị trí ống `00`).
- Sample ID 15 ký tự gồm cả `-`; nếu đặt ít hơn 15 chữ số thì **các chữ số đầu bị bỏ** (text D) / căn phải đệm dấu cách (text R).
- Caution của hãng: nếu host nhận PT ratio/INR dạng `X.XX` thì liên hệ hãng — tức khuôn chuẩn là `XX.XX` → ` XXXX` *(hiểu là có máy cài khác — suy luận)*.

## 3. Khung truyền và bắt tay (định dạng CA1000/CA500)

| Mục | Giá trị |
|---|---|
| Khung | `<STX>(0x02)` + text + `<ETX>(0x03)` |
| Độ dài | text ≤ 255 byte; dài hơn thì chia **block** (trường "Block Number"/"Total Number of Blocks") |
| Checksum | **Không có** (chỉ kiểm parity từng ký tự) |
| Mã ký tự | ASCII |
| Class A | Gửi một chiều, không chờ ACK/NAK; chỉ dựa CTS/DSR. Giữa hai bản tin cách "Interval" |
| Class B | Máy gửi text → chờ `<ACK>(0x06)` hoặc `<NAK>(0x15)`; NAK thì gửi lại, tối đa 3 lần, sau đó dừng ("HC Reception Count Error") |
| Dạng ACK | Chọn "ACK/NAK" (một byte) hoặc "STX-ACK-ETX" (`<STX><ACK><ETX>`) |
| Interval | 0/2/3/5/7/10/15 s — tính từ lúc máy nhận ACK/NAK tới lúc gửi bản tin kế |
| Timeout | Cố định **15 s**: chờ ACK/NAK (HC ACK Time Out); sau khi nhận ACK chờ STX của text đặt chỉ định (HC STX Time Out); sau STX chờ ETX (HC ETX Time Out) |
| Contention | Không áp dụng (không có ENQ) |

### 3.1 Định dạng ASTM / ASTM2 (CA-600 ASTM Host Interface Specification v1.03, mục 6.1–6.2)

| Mục | Giá trị |
|---|---|
| Chuẩn | Tầng vật lý + liên kết: ASTM E1381-91; tầng trình bày: ASTM E1394-91. Chỉ cổng nối tiếp |
| Cổng | DB-9 **đực** ở mặt sau máy (chuẩn ASTM là DB-25); cáp cần đầu DB-9 cái. Chân như mục 2 |
| Dây điều khiển | Đặc tả ASTM ghi "control signals are not used with ASTM", không nối chân không dùng — nhưng bảng cáp mẫu vẫn nối đủ RTS/CTS, DTR/DSR (DB-9↔DB-9: 3-3, 2-2, 5-5, 7-7, 8-8, 4-4, 6-6). *(suy luận)* Nên làm cáp đủ dây như bảng mẫu vì mục 14.4 User Manual vẫn kiểm DSR/CTS — đo trên máy thật để chốt |
| Tham số khuyến nghị theo ASTM | **9600, 8 bit, 1 stop, None** (dấu * trong bảng 3); 7 bit/Even/Odd/2 stop vẫn chọn được |
| Format | `CA-500`, `CA-1000`, `ASTM`, `ASTM2` (bảng 3 của đặc tả ASTM — User Manual 2011 chưa có ASTM2) |
| Thiết lập liên kết | Bên gửi gửi `<ENQ>`; bên nhận `<ACK>` (sẵn sàng) hoặc `<NAK>` (bận). Nhận NAK → máy chờ **≥ 10 s** rồi ENQ lại |
| Tranh chấp (cả hai cùng ENQ) | **Host phải nhường**: máy gửi lại ENQ sau 1 s; host chờ ≥ 20 s mới được ENQ lại |
| Frame | `<STX> F# text <ETX\|ETB> C1 C2 <CR><LF>`; F# chạy `1,2,…,7,0,1…`; text ≤ **240** ký tự, dài hơn chia frame giữa kết thúc bằng `<ETB>`. Mỗi record kết thúc bằng `<CR>` trước `<ETX>`. Trong ví dụ hãng mỗi record là một frame |
| Checksum | Cộng nhị phân mọi byte **từ F# đến hết ETX/ETB (gồm cả ETX/ETB)**, lấy 8 bit thấp, ghi 2 ký tự hex HOA (`0`–`9`, `A`–`F`) — đúng chuẩn, xem `astm-e1394.md` |
| ACK/NAK | Nhận đúng → `<ACK>`, sai → `<NAK>` gửi lại cùng F#; **tối đa 6 lần** rồi bỏ phiên (`<EOT>`) |
| `<EOT>` thay ACK | Máy coi `<EOT>` của host như `<ACK>` (không hỗ trợ yêu cầu ngắt) |
| Kết thúc | `<EOT>` → hai bên về trạng thái trung lập |
| Timeout | Chờ trả lời ENQ: 15 s; chờ ACK/NAK sau frame: 15 s; bên nhận chờ frame kế/EOT: **30 s** (quá hạn thì bỏ bản tin dở) |
| Ký tự cấm trong text | `0x00–0x06, 0x08, 0x0A, 0x0E–0x1F, 0x7F, 0xFF` |
| Dấu phân cách | trường `\|`, lặp `\`, thành phần `^`, thoát `&` (`&F&` `&R&` `&S&` `&E&`) |

Khung CA1000/CA500 ở trên **không** dùng cho ASTM; driver ASTM dùng chung bộ `astm-e1394` của dự án.

## 4. Bản tin kết quả (máy → LIS) — định dạng CA1000/CA500

Text "D" (Analysis Data). Vị trí tính từ 0, **không tính STX** (byte 0 = ký tự ngay sau STX). Độ dài = 54 + 9 × n byte **tính cả STX và ETX** khi SID 15 ký tự.

| Vị trí | Dài | Trường | Giá trị |
|---|---|---|---|
| 0 | 1 | Text Distinction Code I | `D` = dữ liệu phân tích |
| 1 | 1 | Code II | `1` đo đơn, `2` trung bình của các lần đo lặp |
| 2 | 2 | Code III | luôn `21` |
| 4 | 2 | Block number | thường `01` |
| 6 | 2 | Total blocks | thường `01` |
| 8 | 1 | Sample Distinction | `U` thường, `E` cấp cứu (STAT), `C` **QC**, khoảng trắng = không rõ (bảng định dạng còn liệt kê `S` nhưng phần giải thích không định nghĩa) |
| 9 | 6 | Ngày | theo cài đặt: yymmdd / mmddyy / ddmmyy |
| 15 | 4 | Giờ | hhmm 24h |
| 19 | 4 | Rack No | `0001`… |
| 23 | 2 | Vị trí ống | `01`–`10`; `00` = STAT |
| 25 | 15 (CA500) / 13 (CA1000) | **Sample ID** | căn phải, đệm khoảng trắng bên trái; có thể chứa `-` |
| 40 | 1 | ID Information | `M` nhập tay, `A` số tự tăng, `B` đọc barcode, `C` tải từ host |
| 41 | 11 | Patient name / dự phòng | khoảng trắng |
| 52 + 9k | 9 | Data k | xem dưới |

Vị trí trên đúng cho SID 15 ký tự (Format CA500); với CA1000 (SID 13) các trường sau SID lùi 2 byte *(suy luận từ ghi chú "13 hoặc 15")*.

**Một khối Data (9 byte):**

| Vị trí trong khối | Dài | Trường |
|---|---|---|
| 0 | 3 | Mã thông số (Parameter Code) |
| 3 | 5 | Giá trị, **không có dấu thập phân**, không bỏ số 0 đầu |
| 8 | 1 | Cờ |

**Đặt dấu thập phân** (driver phải tự làm theo chữ số cuối của mã):

| Chữ số cuối mã | Ý nghĩa | Khuôn gốc → 5 ký tự | Ví dụ |
|---|---|---|---|
| 1 | Thời gian (giây) | `XXXX.X` → `XXXXX` | `00123` = 12.3 s |
| 2 | Hoạt độ % / nồng độ | `%`: `XXX.X`; Fbg mg/dL: `XXX.X`; Fbg g/L: `X.XXX`; D-Dimer µg/L: `XXXX`; mg/L FEU: `XX.XX` (ký tự đầu là khoảng trắng) | ` 3120` = 312.0 mg/dL |
| 3 | Ratio | `XX.XX` → ` XXXX` | ` 0105` = 1.05 |
| 4 | INR | `XX.XX` → ` XXXX` | ` 0105` = 1.05 |
| 5 | dFbg | Tài liệu không nêu khuôn riêng — cần đo trên máy thật | |

Giá trị đặc biệt: `*` thay chữ số = lỗi phân tích; `/` = lỗi ở giá trị trung bình; `-` = không có đường chuẩn / dữ liệu không hợp lệ / không đông; khoảng trắng = lỗi phần cứng.

**Cờ (byte cuối):** khoảng trắng = bình thường; `+` trên giới hạn BN; `-` dưới giới hạn BN; `*` lỗi phân tích / Fbg ngoài dải / lệch lặp lớn; `!` đo lại sau pha loãng; `>` trên giới hạn báo cáo; `<` dưới giới hạn báo cáo. Ưu tiên khi trùng: `*` > `<`/`>` > `+`/`-` > `!` > trống.

**QC / chuẩn:** Sample Distinction = `C` là QC; SID dạng `QC01`…`QC06` (đệm khoảng trắng). Dữ liệu chuẩn (calibration) không thấy được xuất ra host — tài liệu không nêu.

### 4.1 Bản tin kết quả — định dạng ASTM / ASTM2 (đặc tả ASTM mục 6.3)

Thứ tự: `H → P → O → R… → L` (ASTM2: sau R của mỗi XN có thể có `C`). Mỗi lần đo một phiên `ENQ…EOT`; đo lặp thì mỗi lần một phiên và thêm phiên giá trị trung bình.

| Record | Trường dùng (máy → LIS) | Ghi chú |
|---|---|---|
| `H` | H.2 `\^&`; H.5 `CA-600^ XX-XX` (tên máy ^ phiên bản phần mềm 6 ký tự có dấu cách đầu); H.13 `1` | Tên máy thành **`CA-500`** khi Flag format = CA-500 |
| `P` | `P\|1` — không có thông tin bệnh nhân | |
| `O` | O.2 = 1; O.3 **trống**; **O.4 = `Rack^Ống^SID^Thuộc tính`**; O.6 Priority `R` thường / `S` STAT; O.12 Action code `N` thường / `Q` QC / `S` chuẩn (STD) | Rack 6 chữ số đệm 0 trái; ống `01`–`10`, STAT `00`; **SID 15 ký tự căn phải đệm dấu cách**; thuộc tính `M` tay / `A` tự tăng hoặc lỗi đọc (`ERR…`) / `B` barcode / `C` từ host |
| `R` | R.3 = `^^^Mã^Tên XN^Tỉ lệ pha loãng^Loại`; R.4 giá trị ≤ 5 ký tự căn phải; R.5 đơn vị ≤ 5 ký tự căn trái; R.7 cờ; R.13 `YYYYMMDDHHMMSS` (giây luôn `00`) | Loại: `1` đo đơn tự động, `2` trung bình tự động, `5` đo đơn xuất tay, `6` trung bình xuất tay |
| `C` (chỉ ASTM2) | C.3 `I`; C.4 `LOT^mã^lô1,lô2,…` / `CAL^mã^ngày chuẩn^1^lô chất chuẩn` / `QC^mã^ngày giờ^^lô control` (lặp `\`); C.5 `I` | Thứ tự CAL → LOT → QC; không lưu thành kết quả, chỉ log/nội kiểm |
| `L` | `L\|1\|N` | |

**Giá trị (R.4):** có dấu thập phân sẵn (khác CA1000). Ký tự che khi không có kết quả: `*` lỗi phân tích, `/` lỗi tính trung bình, `+` tràn số chữ số, `-` lỗi tính toán → lưu thành kết quả lỗi, không ép số.

**Cờ (R.7):** `N` bình thường, `L`/`H` dưới/trên giới hạn bệnh nhân, `<`/`>` dưới/trên giới hạn báo cáo, `A` lỗi phân tích. (Không có cờ "pha loãng lại" như `d`/`!` của CA1000.)

**Phân biệt mẫu:** QC có SID `QC01`–`QC06` và O.12 = `Q`; mẫu chuẩn O.12 = `S`; STAT O.6 = `S`, ống `00`.

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

Chỉ có khi **Class = B** (đặt Class A mà hỏi thì "giao tiếp sai mà không báo lỗi").

**Máy hỏi khi nào:**

| Cách | Cài đặt | Khóa hỏi |
|---|---|---|
| Thủ công: bấm [HC] ở Root Menu | Inquiry = Manual | theo Rack No + vị trí (Code II = `1`), cả rack 10 ống |
| Tự động, không có đầu đọc barcode | Inquiry = Auto | theo SID đã nhập tay (Code II = `2`) khi bấm Start |
| Tự động, có đầu đọc barcode | Inquiry = Auto | theo SID đọc được (Code II = `2`); đọc lỗi thì không hỏi |

Ống đã có cả SID lẫn chỉ định nhập tay thì máy không hỏi.

**Trình tự:** máy gửi text `R` → LIS trả `<ACK>` rồi gửi text `S` (hoặc `<NAK>` nếu text hỏng, máy gửi lại ≤ 3 lần) → máy kiểm text `S`, trả ACK/NAK (LIS gửi lại ≤ 3 lần nếu NAK). LIS phải gửi text `S` trong 15 s sau ACK.

**Text R (hỏi)** — bố cục giống text D: Code I = `R`; Code II = `1` (theo rack) / `2` (theo SID); Sample Distinction = khoảng trắng; SID rỗng (toàn khoảng trắng) khi hỏi theo rack; barcode lỗi → SID `ERR0000000001`; ID Information có thêm giá trị khoảng trắng (hỏi theo rack). Mỗi khối 9 byte = mã thông số 3 ký tự + 6 khoảng trắng. Tài liệu không nói rõ máy liệt kê những mã nào trong câu hỏi — cần đo trên máy thật.

**Text S (LIS trả lời)** — bố cục giống text D: Code I = `S`; Code II như câu hỏi; Sample Distinction `U`/`E`/`C`; SID căn phải; ID Information = `C`; mỗi khối = **mã thông số 3 ký tự kiểu `X0`** (`040` PT, `050` APTT, `060` Fbg… — hàng đơn vị là `0`) + 6 khoảng trắng.

**Không có chỉ định:** hỏi theo SID → trả một khối mã `000` ("không phân tích"); hỏi theo rack → trả `999` (máy dừng hỏi các ống còn lại của rack, báo "Instruction Not Found in HC"). Mẫu QC: SID `QC01`…`QC06`.

### 5.1 Hỏi chỉ định — định dạng ASTM

- Máy chỉ hỏi **theo SID** (không hỏi theo rack như CA1000).
- Máy → LIS (một phiên): `H`, `Q`, `L`. **Q.3 = `Rack^Ống^SID^Thuộc tính`**; Q.5 = danh sách `^^^Mã^Tên` cách nhau `\` (các XN máy đang có, mã kiểu `X0`, vd `040`); Q.6 = `0`; Q.7 = thời điểm hỏi `YYYYMMDDHHMMSS`. SID đọc lỗi → `␣␣ERR0000000001`.
- LIS → máy (phiên riêng, LIS gửi `<ENQ>`): `H` (H.5 `Tên host^^^^` ≤ 16 ký tự, H.10 = `CA-600`), `P|1`, `O`, `L|1|N`.
  - **O.3 = chép nguyên** `Rack^Ống^SID^Thuộc tính` của câu hỏi; O.5 = `^^^Mã^^100` lặp bằng `\` (tỉ lệ pha loãng **cố định `100`**, máy không cho đặt); O.6 `R`/`S`; O.7 thời điểm; O.12 `N` (hoặc `Q` cho QC; **không** chỉ định đo chuẩn từ host được).
  - Không có XN: đặc tả chỉ nói O.5 "có thể bỏ trống" — máy xử lý ra sao: Tài liệu không nêu — cần đo trên máy thật.
- Thời hạn LIS phải trả lời sau `Q`: Tài liệu không nêu — cần đo trên máy thật *(suy luận: giữ dưới 15 s như timeout chung)*. Khi gửi, LIS phải tôn trọng luật tranh chấp (máy được ưu tiên).

## 6. Bảng mã chỉ số

Mã 3 chữ số = 2 chữ số thông số + 1 chữ số loại kết quả (X: 1 thời gian, 2 %/nồng độ, 3 ratio, 4 INR, 5 dFbg).

| Nhóm mã | Thông số | | Nhóm mã | Thông số |
|---|---|---|---|---|
| 04X | PT | | 27X | LA1 |
| 05X | APTT | | 28X | LA2 |
| 06X | Fbg | | 30X | AT3 |
| 08X | TTO | | 31X | APL |
| 12X | Yếu tố II | | 32X | Plg |
| 15X | Yếu tố V | | 33X | PC Chrom |
| 17X | Yếu tố VII | | 34X | Hep |
| 18X | Yếu tố VIII | | 50X | +Fbg |
| 19X | Yếu tố IX | | 51X | TT |
| 20X | Yếu tố X | | 52X | −Fbg |
| 21X | Yếu tố XI | | 61X | DDPl / AdDD |
| 22X | Yếu tố XII | | 62X | PFDP (chỉ châu Á) |
| 25X | PCcl | | 70X | +DDP / +AdD |
| 26X | BXT | | 72X | +PFD |

Hãng dặn: máy có thể gửi mã mới không có trong bảng — driver phải bỏ qua, không báo lỗi.

**Đang map ở BVĐK 10** (InsID 6, 4 mã):

| Mã máy | Chỉ số | Đơn vị (LIS) | Ghi chú |
|---|---|---|---|
| `041` | PT (giây) | Giây | |
| `044` | PT-INR | — | 2 số lẻ |
| `051` | APTT (giây) | Giây | |
| `062` | Fibrinogen (nồng độ) | mg/dl | dấu thập phân phụ thuộc đơn vị đặt trên máy (mg/dL hay g/L) |

Các mã cùng nhóm máy có thể gửi nhưng chưa map: `042` PT%, `043` PT ratio, `061` Fbg (giây). Chưa map TT (`511`), D-Dimer.

Ở định dạng ASTM máy cũng đặt đúng các mã này (dạng 3 chữ số có loại kết quả) vào thành phần 4 của R.3 — đã xác nhận bằng đặc tả ASTM v1.03 (mục 6.1).

### 6.1 Mã ASTM (CA-600 v1.03, bảng 12/14/16)

- Trong **Q và O**: mã 3 ký tự kiểu `X0` = "Host ID" trong Test Protocol + `0`. Nhiều tên XN dùng chung một mã: `040` PT T/PT I (thromboplastin), `050` FSL/FS (APTT), `060` Fbg, `300` AT3/AT, `340` Hep/HepL/HepU, `610` DDPl/DDi/AdDD, `700` +DDP/+DDi/+AdD; ngoài ra `120`–`220` yếu tố II–XII, `250` PCcl, `260` BXT, `270`/`280` LA1/LA2, `310` APL, `320` Plg, `330` BCPC, `500`/`510`/`520` +Fbg/TT/−Fbg, `620` PFDP, `650`/`660`/`670` vWF, `720` +PFD, `800`/`810`/`820` WFa.
- Trong **R**: mã 3 chữ số = 2 chữ số nhóm + loại (`1` thời gian, `2` %/nồng độ, `3` ratio, `4` INR, `5` dFbg) — **giống hệt CA1000**, kèm tên và định dạng:

| Mã R | Tên | Đơn vị | Định dạng |
|---|---|---|---|
| `041` | PT T / PT I | sec | `###.#` |
| `042` | PT% | % | `###.#` |
| `043` | PT R. | - | `##.##` |
| `044` | PT INR | - | `##.##` |
| `045` | dFbg | mg/dL | `####` |
| `051` | FSL / FS (APTT) | sec | `###.#` |
| `061` | Fbg | sec | `###.#` |
| `062` | Fbg C. | mg/dL | `####` |
| `511` | TT | sec | `###.#` |
| `301`/`302` | AT3, AT | dOD / % | `#.###` / `###.#` |
| `612` | DD Pl / DDi. / Adv.DD | ug/L hoặc mg/L | `####` / `##.##` |
| `622` | PFDP C. | ug/mL | `###.#` |

(Bảng đầy đủ: đặc tả bảng 16. Hãng nhắc: mã có thể thêm sau này → **bỏ qua mã lạ**, không báo lỗi. Một mã R có thể ứng với nhiều tên — khóa map theo mã, giữ tên để log.)

Bốn mã đang map ở BVĐK 10 (`041`, `044`, `051`, `062`) có trong bảng R → hệ cũ map đúng là nhất quán với định dạng ASTM.

## 7. Bản tin mẫu

Dữ liệu giả. Khoảng trắng hiển thị bằng `␣` cho dễ đếm.

**Kết quả (Format CA500, SID 15, Class B):**

```
→ <STX>D1210101U2609150845000101␣␣␣␣␣␣260915001B␣␣␣␣␣␣␣␣␣␣␣04100123␣044␣0105␣05100305␣062␣3120␣<ETX>
← <ACK>
```

Giải nghĩa: đo đơn, mẫu thường, 15/09/26 08:45, rack 0001 ống 01, SID `260915001` đọc barcode; PT 12.3 s, INR 1.05, APTT 30.5 s, Fbg 312.0 mg/dL, không cờ. Tổng 90 byte = 54 + 9 × 4.

**Hỏi chỉ định theo SID và trả lời:**

```
→ <STX>R2210101␣2609150840000101␣␣␣␣␣␣260915001B␣␣␣␣␣␣␣␣␣␣␣040␣␣␣␣␣␣050␣␣␣␣␣␣060␣␣␣␣␣␣<ETX>
← <ACK>
← <STX>S2210101U2609150840000101␣␣␣␣␣␣260915001C␣␣␣␣␣␣␣␣␣␣␣040␣␣␣␣␣␣050␣␣␣␣␣␣060␣␣␣␣␣␣<ETX>
→ <ACK>
```

(Danh sách mã trong câu hỏi của máy ở trên là minh họa — tài liệu không nói máy liệt kê mã gì.)

**Không có chỉ định cho SID này:**

```
← <STX>S2210101U2609150840000101␣␣␣␣␣␣260915001C␣␣␣␣␣␣␣␣␣␣␣000␣␣␣␣␣␣<ETX>
```

### 7.1 Bản tin mẫu ASTM (dữ liệu giả; checksum tính lại bằng script, tổng byte từ F# đến hết ETX, mod 256)

Kết quả một mẫu (rack 1, ống 01, SID barcode giả `260915001`):

```
→ <ENQ>                                                       ← <ACK>
→ <STX>1H|\^&|||CA-600^ 00-02||||||||1<CR><ETX>1E<CR><LF>      ← <ACK>
→ <STX>2P|1<CR><ETX>3F<CR><LF>                                 ← <ACK>
→ <STX>3O|1||000001^01^      260915001^B^||R||||||N<CR><ETX>7B<CR><LF>
→ <STX>4R|1|^^^041^PT T   ^100^1| 12.3|sec  ||N||||||20260915084500<CR><ETX>11<CR><LF>
→ <STX>5R|2|^^^044^PT INR ^100^1| 1.05|-    ||N||||||20260915084500<CR><ETX>9D<CR><LF>
→ <STX>6R|3|^^^051^FSL    ^100^1| 30.5|sec  ||N||||||20260915084500<CR><ETX>05<CR><LF>
→ <STX>7R|4|^^^062^Fbg C. ^100^1|  312|mg/dL||H||||||20260915084500<CR><ETX>86<CR><LF>
→ <STX>0L|1|N<CR><ETX>03<CR><LF>                               ← <ACK> (sau mỗi frame)
→ <EOT>
```

Hỏi chỉ định và trả lời:

```
→ <ENQ> ← <ACK>
→ <STX>1H|\^&|||CA-600^ 00-02||||||||1<CR><ETX>1E<CR><LF>
→ <STX>2Q|1|000001^01^      260915001^B||^^^040^PT T   \^^^050^FSL    \^^^060^Fbg    |0|20260915084000<CR><ETX>52<CR><LF>
→ <STX>3L|1|N<CR><ETX>06<CR><LF>
→ <EOT>
← <ENQ> → <ACK>
← <STX>1H|\^&|||LIS^^^^|||||CA-600<CR><ETX>6C<CR><LF>
← <STX>2P|1<CR><ETX>3F<CR><LF>
← <STX>3O|1|000001^01^      260915001^B||^^^040^^100\^^^050^^100\^^^060^^100|R|20260915084000|||||N<CR><ETX>8E<CR><LF>
← <STX>4L|1|N<CR><ETX>07<CR><LF>
← <EOT>
```

Độ rộng đệm của tên XN (≈7 ký tự) và đơn vị (≈4–5 ký tự) lấy theo ví dụ hãng; trích PDF làm mất dấu cách nên **driver phải trim**, không dựa vào độ rộng.

## 8. Bẫy và lưu ý khi viết driver

1. **Xác định Format trước tiên.** Cùng một máy có 3–4 định dạng (CA1000, CA500, ASTM, ASTM2 trên CA-600); LIS cũ khai "ASTM". Ra máy xem *Settings → I/O Setting → Host Computer*, hoặc bật `raw-listen` để nhìn byte: bắt đầu `<ENQ>` → ASTM; bắt đầu `<STX>D1` → CA1000/CA500.
2. **CA1000 vs CA500 lệch 2 byte** (SID 13 vs 15). Với CA1000, SID 15 ký tự sẽ bị cắt mất 2 chữ số đầu — SID BVĐK 10 dạng 9 chữ số thì không ảnh hưởng.
3. **SID đệm khoảng trắng bên trái**, phải trim. Hệ cũ chỉ khớp đuôi SID (`LIKE '%-' + sid`) — đừng lặp lại cách khớp mờ đó.
4. **Không có dấu thập phân**; số lẻ phụ thuộc mã và **đơn vị đặt trên máy** (Fbg mg/dL ↔ g/L). Lưu "số chữ số thập phân" theo từng mã trong cấu hình máy, không cố định trong code.
5. **Ký hiệu thay chữ số** (`*****`, `-----`, khoảng trắng) không phải số — phải giữ thành kết quả lỗi, không ép về 0.
6. **Cờ `-` trùng ký tự với giá trị `-----`** — đọc cờ theo vị trí byte 8 của khối, không tìm theo ký tự.
7. **DSR/CTS**: không cấp tín hiệu thì máy không gửi gì và chỉ báo "HC Off Line". Driver nên bật DTR/RTS khi mở cổng.
8. **Class A không có ACK** — nếu LIS lỡ gửi ACK cũng không sao, nhưng không được trông chờ gửi lại khi mất dữ liệu.
9. **Mã mới từ máy** → bỏ qua, ghi nhật ký (hãng yêu cầu).
10. **ASTM: SID ở O.4 thành phần 3** (O.3 trống khi máy gửi), còn trong câu trả lời của LIS phải đặt ở **O.3**. SID có dấu cách đầu → trim. Rack 6 chữ số (máy chỉ hiện 2 số cuối).
11. **ASTM: giá trị đã có dấu thập phân**, khác CA1000 — không áp quy tắc chèn dấu phẩy của mục 4 cho định dạng ASTM.
12. **ASTM2 chèn record `C`** giữa các `R` — parser phải bỏ qua/ghi log, không coi là lỗi thứ tự record. Số thứ tự R có thể nhảy (ví dụ hãng: `R|3` rồi `R|4` ở lần đo lại) — không dùng sequence làm khóa.
13. **Tranh chấp ENQ**: LIS phải nhường máy, chờ ≥ 20 s mới gửi lại ENQ.
14. Ngày 6 chữ số, thứ tự tùy cài đặt máy (`Date Format`) — cấu hình theo máy.

## 9. Kiểm thử

- Bản ghi thô từ máy BVĐK 10: một mẫu thường, một mẫu QC, một mẫu có cờ `*`/`>`, một mẫu đo lặp (Code II = `2`).
- Mô phỏng: cặp COM ảo, cấu hình bắt buộc DSR/CTS để thử trường hợp mất tín hiệu.
- Tình huống lỗi: LIS trả NAK 3 lần → máy dừng; LIS không trả lời > 15 s; text vượt 255 byte chia 2 block; hỏi theo rack trả `999`; hỏi theo SID trả `000`; Fbg đổi đơn vị g/L (3 số lẻ).
- Nếu máy đặt ASTM/ASTM2: chạy bộ kiểm thử trong `astm-e1394.md` với bản tin mục 7.1; thêm ca: record `C` xen giữa (ASTM2), đo lặp 3 phiên (đơn, đơn, trung bình — loại `1`/`2`), giá trị che `*`/`/`/`+`/`-`, ENQ đồng thời (LIS phải nhường), NAK 6 lần, timeout 30 s phía nhận, câu hỏi với SID `ERR0000000001`.

## 10. Nguồn

- *Sysmex CA-500 series Instructions for Use* (rev. 2003, 2.0_en) — chương 14 *Technical Information*, mục **14.3 Serial Interface for Host Computer** (tr. 14-16 → 14-23) và **14.4 Text Format** (tr. 14-23 → 14-32); mục 5.12–5.13 (hỏi chỉ định); 10.14 (cài đặt Host Computer). Bản công khai: http://www.frankshospitalworkshop.com/equipment/documents/automated_analyzer/user_manuals/Sysmex%20CA-500%20Blood%20Coadulation%20Analyzer%20-%20Instruction%20manual.pdf
- `tai-lieu-hang/Sysmex_CA-500_Service_Manual.pdf` — mục 5.11 (12) *Host Computer* (giá trị mặc định), 6.9.3–6.9.13 (lỗi HC), B-7 (cổng HOST). Service manual **không** có mục giao thức chi tiết.
- `khao-sat/lis-hien-huu.md`, `khao-sat/seed/du-lieu/Instrument.json` (InsID 6), `InterfacePara.json`, `khao-sat/seed/anh-xa-ma-may.json`.
- *Sysmex CA-600 series Instructions for Use / User Manual* (09/2011, sửa 05/2012 và 04/2014) — 10.14 *Host Computer* (tr. 10-24–10-25), **14.4 Serial Interface for Host Computer** (tr. 14-18–14-24), 14.5 *Text Format* (tr. 14-24–14-32), bảng cài đặt ban đầu (tr. 14-10). Tệp: `tai-lieu-hang/Sysmex_CA-600_User_Manual.pdf`.
- **CA-600 ASTM Host Interface Specification**, Sysmex, Ver. 1.03 (16/7/2014), 48 trang — 6.1 tầng vật lý (chân, cáp, bảng 3 tham số, ASTM2), 6.2 tầng liên kết (ENQ/ACK, tranh chấp, frame, checksum, timeout), 6.3 record H/P/Q/O/R/C/L, bảng mã 12/14/16/18, ví dụ mục 7. Tệp: `tai-lieu-hang/Sysmex_CA-600_ASTM_Host_Interface_Specification_v1.03_2014.pdf`.
- **Còn nên xin hãng (Sysmex):** xác nhận CA-500 (đúng phiên bản phần mềm ở BVĐK 10) dùng y hệt đặc tả ASTM v1.03 của CA-600 — đặc biệt độ rộng SID, mã XN nhóm `X0`, có/không ASTM2. Cách chắc nhất: ghi bản thô từ máy.
