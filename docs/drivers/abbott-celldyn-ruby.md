# Abbott CELL-DYN Ruby — huyết học (CBC 5 thành phần bạch cầu, RETIC)

> Trạng thái tài liệu: ✅ đủ để viết driver — có đặc tả gốc *CELL-DYN Ruby System Host Interface Specification*, List No. 09H05-01, Rev. C (07/2008, phần mềm 2.0ML): tầng thấp, đủ các record, mã chỉ số, cờ, hỏi/gửi chỉ định, bản tin mẫu. Còn mở: đơn vị cụ thể từng chỉ số (tài liệu chỉ nói "đơn vị Mỹ"), giá trị timeout cụ thể (tài liệu dẫn sang chuẩn LIS1)
> Kiểm chứng: chưa đối chiếu máy thật (BVĐK 10 có 2 máy, LIS cũ nhận 294 + 67 kết quả, nhưng không lưu byte thô)
> Driver: `abbott-celldyn` — họ giao thức: astm-e1394 (LIS1-A / LIS2-A qua RS-232)

## 1. Tóm tắt

- Máy huyết học laser (MAPSS), các lựa chọn xét nghiệm CBC, CBC+NOC, CBC+RRBC, RETIC.
- Nối **RS-232 (DB-9 đực)**, giao thức CLSI LIS1-A (ASTM E1381) + LIS2-A (ASTM E1394). Không có TCP.
- **Hai chiều**: máy gửi kết quả (H-P-O-R…-[M…]-L); máy định kỳ hỏi danh sách chỉ định (`Q|1|^ALL`) hoặc hỏi theo Specimen ID; LIS gửi "Orders List Entry" (H-P-O…-L); máy trả bản tin từ chối nếu không nhận.
- **Mã chỉ số nằm ở thành phần 7 của Universal Test ID** (`^^^CBC^^^WBC`) — xác nhận đúng cấu hình LIS cũ `TestCodeIndex = 6` (đếm từ 0).
- Điểm khó nhất: record được **đóng gói liền vào frame đầy** (một record có thể vắt qua 2 frame, nối bằng ETB); LIS phải ACK **từng frame** mới coi là chuyển thành công; ảnh histogram/scattergram mã hóa Base64 làm bản tin dài tới ~31 KB.

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | RS-232 trên Data Station của máy, đầu **DB-9 đực** (khác đầu nối LIS1 chuẩn) | §2.2 |
| Chân | 2 = RxD (vào máy), 3 = TxD (ra từ máy), 5 = GND; các chân khác **không dùng** → không bắt tay phần cứng | §2.2 |
| Baud | 4800 / **9600 (mặc định)** / 19200 / 38400 / 57600 / 115200 | §2.2 |
| Data bits | 7 / **8 (mặc định)** | §2.2 |
| Stop bits | **1 (mặc định)** / 2 | §2.2 |
| Parity | Even / Odd / **None (mặc định)** | §2.2 |
| Bảng mã | 8 bit: ISO 8859(-1) (tài liệu viết "ISO8559"); ký tự hợp lệ 32–127 (7 bit) hoặc 32–255 (8 bit) | §2.2, §3.1.5 |
| TCP | Không có — tài liệu chỉ mô tả cổng nối tiếp | — |
| BVĐK 10 | Máy _1: COM4 trạm DK10.3; máy _2: COM8 trạm DK10.2; đều 9600 8N1 | `InterfacePara` |
| Cài đặt trên máy | Tên menu: **Tài liệu không nêu**. Các tùy chọn tài liệu nhắc: auto-transmit; polling (chu kỳ hỏi chỉ định, mặc định 60 s, 0 = tắt); host query theo Specimen ID; Host Query Timeout (1–20 s, mặc định 5 s); Serial Communications Test bật/tắt + chu kỳ 1–120 phút; độ dài chuỗi giá trị 4 hoặc 5 ký tự (mặc định 5); có gửi đồ thị hay không | §3.1.2, §3.2, §3.3.2.2.3, §3.4.2.4.4, App. C |

## 3. Khung truyền và bắt tay

Theo LIS1-A (xem `astm-e1394.md`) với các điểm riêng:

| Mục | Giá trị |
|---|---|
| Ký tự điều khiển | ENQ 0x05, ACK 0x06, NAK 0x15, EOT 0x04, STX 0x02, ETX 0x03, ETB 0x17, CR 0x0D, LF 0x0A |
| Frame | `<STX> FN text <ETX|ETB> C1 C2 <CR> <LF>`; FN 1–7 rồi 0, quay vòng |
| Checksum | Tổng mod 256 các byte từ FN đến hết ETX/ETB, ghi 2 ký tự hex hoa (ví dụ ở mục 7, tính bằng script) |
| Đóng gói | **Record LIS2 được nhồi liền vào frame đầy cỡ** (tương thích CELL-DYN 4000/Sapphire) — một record **không** nhất thiết bắt đầu ở frame mới; frame trung gian kết thúc bằng ETB. Máy cũng nhận frame trung gian ETB từ LIS |
| Độ dài frame | Theo LIS1: tối đa 240 ký tự text/frame *(theo chuẩn LIS1 — tài liệu Ruby không ghi số)* |
| Timeout / chờ | Mặc định theo LIS1 §6.5.2.1, 6.5.2.3–6.5.2.5 và §6.2.6, 6.2.7.1 (không sửa được). Giá trị chuẩn LIS1: thiết lập 15 s, chờ ACK 15 s, chờ frame 30 s; tranh chấp: máy chờ ≥ 1 s, LIS chờ ≥ 20 s *(theo chuẩn LIS1, tài liệu Ruby không ghi số)* |
| Gửi lại | Chỉ ở mức frame (LIS1). Máy **không** làm khôi phục lỗi logic LIS2 §5.2.2 |
| Thành công | LIS ACK **mọi** frame của bản tin kết quả = chuyển thành công; nếu không, máy giữ mẫu trong hàng đợi "to be transmitted" (không mất khi mất kết nối hay tắt máy) và gửi lại khi link lên |
| Kiểm tra link | Máy gửi `ENQ`; LIS trả `ACK` (máy gửi `EOT`), `NAK` hoặc `ENQ` đều tính là link lên; im lặng/ký tự khác = link xuống. Thực hiện khi mở cổng, định kỳ (nếu bật) và khi người dùng yêu cầu. **Link xuống thì máy không gửi gì** |
| Dấu phân cách | Trường `|`, thành phần `^`, lặp `\`, escape `&` (khai nhưng không dùng; máy không tạo/giải escape) |

## 4. Bản tin kết quả (máy → LIS)

Mỗi bản tin = **một lần hút mẫu**: `H` → `P` → `O` → `R` số (i) → `R` cờ (j) → `M` đồ thị (k, tùy cài đặt) → `L`.

**H** — `H|\^&|||<ASN>^CDRuby^<SVID>^1.0|||||||P|LIS2-A`. H.5 Sender ID = số máy (≤ 9 ký tự) ^ model `CDRuby` ^ phiên bản phần mềm (≤ 16) ^ phiên bản giao tiếp `1.0`.

**P** — chỉ có nội dung khi Specimen Type = `Patient`: P.4 Patient ID (≤ 16), P.6 tên (≤ 28, một thành phần), P.8 ngày sinh `YYYYMMDD`, P.9 giới `M/F/U`, P.14 bác sĩ (≤ 22), P.16 `UF1^UF2`. Mẫu QC/nền/chuẩn: `P|1`.

**O** (bảng 14):

| Trường | Nội dung | Ví dụ |
|---|---|---|
| O.3 Specimen ID | `Tube ID^Rack^Position` — Tube ID 1–20 ký tự (mã vạch/ID nhập trên máy); Rack và Position 2 ký tự, **chỉ có ở chế độ ống kín** (rỗng ở chế độ mở). Tube ID chỉ bắt buộc với mẫu Patient | `260915001^01^03` |
| O.4 Instrument Specimen ID | Số thứ tự Datalog 4 chữ số 0000–9999, quay vòng (không duy nhất) | `0042` |
| O.5 Universal Test ID | `^^^TS^PSS^LSS`: TS = lựa chọn xét nghiệm (`CBC`, `CBC+NOC`, `CBC+RRBC`, `RETIC`, `NON-WHOLE BLOOD`), PSS = bộ thông số 1–8, LSS = bộ giới hạn (0) | `^^^CBC^1^0` |
| O.8 | Ngày giờ lấy mẫu `YYYYMMDDHHMMSS` | |
| O.16 Specimen Descriptor | `Loại^Phân loại` (mục 4.1) | `Patient` |
| O.26 Report Type | luôn `F` | `F` |

**R** (bảng 15):

| Trường | Nội dung |
|---|---|
| R.2 | Số thứ tự 1…n |
| R.3 Universal Test ID | `^^^<TS>^^^<nhãn>` — 7 thành phần: 1–3 rỗng (LIS2 giữ chỗ), **4 = TS** (chép từ O), 5 = PSS (rỗng ở R), 6 = LSS (rỗng ở R), **7 = nhãn kết quả** (mã chỉ số hoặc tên cờ) |
| R.4 | Giá trị: số 4–5 ký tự dấu thập phân trôi (`0.000`–`9999.`), `-----` (đã đo nhưng không tính được), `>>>>>` (vượt dải báo cáo), `<<<<<` (HGB khi WBC vượt dải tuyến tính), `FLAG` (bản ghi cờ) |
| R.9 | Trạng thái (chỉ bản ghi số): `F` cuối cùng, `W` cảnh báo (có cờ thông số nghi ngờ/lỗi hệ thống hoặc ngoài dải — cần xem lại), `X` không tính được. Bản ghi cờ: rỗng |
| R.11 | Mã người vận hành (1–6 ký tự, có thể rỗng) |
| R.12 | Thời điểm hút mẫu `YYYYMMDDHHMMSS` |
| R.14 | Số máy (giống H.5 thành phần 1) |

**Đơn vị**: mọi kết quả số theo **đơn vị Mỹ** (riêng RBC của NON-WHOLE BLOOD: tế bào/µL). Đơn vị **không** được gửi trong bản tin (R.5 rỗng) — LIS tự gán.

**M** (đồ thị, tùy cài đặt):
- Histogram: `M|n|H|<WB1|WB2|RBC|PLT|NOC|RT1|RT2>||<đường phân định 0–255, phân cách \>|<Base64 của ≤ 256 giá trị 16 bit, byte thấp trước>`. CBC gửi WB1, WB2, RBC, PLT; CBC+NOC/RRBC thêm NOC; RETIC gửi RT1, RT2.
- Scattergram: `M|n|S|<WBC|RBC/PLT|RTC>|<nhãn kênh>|<đường phân định>|<Base64 của ≤ 2000 vector 4–5 byte>`.
- Base64 theo RFC 2045, có `=` đệm, không ngắt dòng 76 ký tự.

### 4.1. Phân biệt mẫu thường / QC / nền / chuẩn (O.16)

| O.16 (Loại^Phân loại) | Ý nghĩa | Xử lý |
|---|---|---|
| `Patient` (phân loại rỗng) | Mẫu người bệnh | Ghi kết quả |
| `QC^Whole Blood`, `QC^Commercial` | QC máu toàn phần / QC thương mại | Đưa vào QC |
| `QC^Background CBC`, `QC^Background Retic` | Nền | Bỏ qua |
| `Autobackground` | Nền tự động (O.3 không có Tube ID) | Bỏ qua |
| `Calibrator Whole Blood`, `Calibrator Commercial` | Chuẩn | Bỏ qua |
| `Standard Reference Particle` | Hạt chuẩn | Bỏ qua |

### 4.2. Cờ (bản ghi R với giá trị `FLAG`)

Nhãn cờ ở thành phần 7 của R.3, phân biệt hoa thường:
- Thông số nghi ngờ: `WBC`, `DFLT(N)`, `DFLT(L)`, `DFLT(M)`, `DFLT(E)`, `DFLT(B)`, `MCHC`, `LRI`, `URI`, `Fragile RBC`, `Too Few Events`, `ERL`.
- Quần thể nghi ngờ: `BAND`, `IG`, `BLAST`, `VAR LYM`, `FWBC`, `NWBC`, `NRBC`, `RRBC`, `RBC MORPH`, `ATYP DEP`.
- Lỗi hệ thống: `SAMPLING ERROR`, `WOC FLOW ERROR`, `NOC FLOW ERROR`, `RBC FLOW ERROR`, `RETC FLOW ERROR`, `WOC HEATER ERROR`, `HGB HEATER ERROR`.
- Thông tin: `(WOC)`, `(NOC)`.

**Chú ý trùng tên**: cờ `WBC`, `MCHC`, `RRBC` trùng chữ với nhãn kết quả số → phân biệt bằng **R.4 = `FLAG`**, không bằng nhãn.

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

**Máy hỏi** (H-Q-L), hai kiểu:
- Định kỳ theo polling (mặc định 60 s, khi link lên): `Q|1|^ALL` — xin mọi chỉ định đang chờ.
- Theo mẫu (bật host query by Specimen ID): `Q|1|^<Specimen ID>`. Máy chờ **Host Query Timeout** (mặc định 5 s); **LIS phải trả trong 4 s**, tính từ EOT của bản hỏi đến EOT của bản trả lời; quá hạn máy chạy theo lựa chọn mặc định. Trong lúc chờ máy không gửi kết quả mới, nhưng nếu đang gửi dở kết quả thì bản hỏi phải chờ (App. C — đặt timeout = thời gian gửi + 2,5 s).

**Không có chỉ định**: LIS **không trả gì** (chỉ ACK ở mức LIS1).

**LIS gửi chỉ định** (bất kỳ lúc nào, không cần chờ hỏi): `H` → 1–3000 cặp `P`-`O` → `L|1|N`. Mỗi O chỉ **một** xét nghiệm, O.2 luôn `1`. Tối thiểu: `H|\^&` / `P|1` / `O|1|<Specimen ID>` / `L|1|N` (máy tự điền CBC, bộ thông số, bộ giới hạn). Đầy đủ: O.5 `^^^CBC^<1–8>^0`, O.8 giờ lấy mẫu, O.16 `Patient^Human`. Cấm gửi xuống máy: dấu lặp `\` (trừ H.2), record C, Q, S, M.

**Máy từ chối** (chỉ gửi khi từ chối, nhận thì im lặng): `H` → `P` (chép lại) → `O` (chép lại, O.26 = `X`) → `C|1|I|<lý do>^<trường lỗi>|G` → `L|1|E`. Lý do: `Orders List Full`, `Order List Add Failed`, `Duplicate Order`, `RRTT Orders List` (máy cấu hình khớp theo giá/vị trí, không nhận chỉ định), `ID Conflict in the Order`, `Specimen ID in the Order is not unique`, `Parsing Error` (kèm tên trường: `SpecimenID`, `TestSelect`, `DOB`, `Sex`…). LIS phải tự giữ và gửi lại chỉ định bị từ chối.

Lưu ý: máy có thể gửi kết quả ngay sau bản hỏi mà không chờ trả lời (§2.4) — driver không được giả định thứ tự hỏi → đáp → kết quả.

## 6. Bảng mã chỉ số

Nhãn ở **thành phần 7** của R.3, phân biệt hoa thường. Đơn vị: đặc tả chỉ ghi "đơn vị Mỹ"; cột đơn vị dưới đây lấy theo danh mục LIS cũ (`anh-xa-ma-may.json`, InsID 16/17).

| Mã máy gửi | Chỉ số | Đơn vị (LIS cũ) | Ghi chú |
|---|---|---|---|
| `WBC` | Bạch cầu | K/µL | |
| `NEU` | Bạch cầu trung tính (số lượng) | K/µL | |
| `LYM` | Lympho (số lượng) | K/µL | |
| `MONO` | Mono (số lượng) | K/µL | |
| `EOS` | Ưa acid (số lượng) | K/µL | |
| `BASO` | Ưa base (số lượng) | K/µL | |
| `%N` | Trung tính % | % | |
| `%L` | Lympho % | % | |
| `%M` | Mono % | % | |
| `%E` | Ưa acid % | % | |
| `%B` | Ưa base % | % | |
| `RBC` | Hồng cầu | M/µL | |
| `HGB` | Hemoglobin | g/dL | |
| `HCT` | Hematocrit | % | |
| `MCV` | MCV | fL | |
| `MCH` | MCH | pg | |
| `MCHC` | MCHC | g/dL | trùng chữ với cờ `MCHC` — xem 4.2 |
| `RDW` | RDW | % | |
| `PLT` | Tiểu cầu | K/µL | |
| `MPV` | MPV | fL | |
| `PCT` | Thể tích khối tiểu cầu | % | hãng ghi chưa xác lập ý nghĩa lâm sàng (không báo cáo ở Mỹ) |
| `PDW` | Độ phân bố tiểu cầu | Tài liệu không nêu (LIS cũ: %) | như trên |
| `WOC`, `NOC` | Bạch cầu theo kênh quang học / kênh nhân | K/µL *(suy luận)* | LIS cũ không map; NOC chỉ có khi TS có NOC |
| `%R`, `RETC` | Hồng cầu lưới % / số lượng | % / Tài liệu không nêu | chỉ ở RETIC; LIS cũ không map |
| `RBGD` | Nền RBC (chỉ ở chạy nền) | — | bỏ qua |

22 mã LIS cũ (InsID 16 và 17) đều có trong bảng 16 của hãng, viết đúng chữ.

## 7. Bản tin mẫu

Dữ liệu giả. Checksum tính bằng script Python (tổng mod 256 từ FN đến ETX/ETB), record đóng gói liền vào frame 240 ký tự giống cách máy làm. Phiên: `Máy <ENQ>` → `LIS <ACK>` → từng frame, LIS `<ACK>` sau mỗi frame → `Máy <EOT>`.

**Kết quả CBC, mẫu người bệnh, ống kín giá 01 vị trí 03, PLT vượt dải, có cờ BAND:**

```
<STX>1H|\^&|||RUBY0001^CDRuby^2.0ML^1.0|||||||P|LIS2-A<CR>P|1<CR>O|1|260915001^01^03|0042|^^^CBC^1^0|||||||||||Patient||||||||||F<CR>R|1|^^^CBC^^^WBC|7.120|||||F|||20261103091500||RUBY0001<CR>R|2|^^^CBC^^^NEU|4.210|||||F|||20261103091500||RUBY0001<CR>R|3|^^^CBC<ETB>CD<CR><LF>
<STX>2^^^LYM|2.050|||||F|||20261103091500||RUBY0001<CR>R|4|^^^CBC^^^MONO|0.580|||||F|||20261103091500||RUBY0001<CR>R|5|^^^CBC^^^EOS|0.210|||||F|||20261103091500||RUBY0001<CR>R|6|^^^CBC^^^BASO|0.070|||||F|||20261103091500||RUBY0001<CR>R|7|^^^CBC^^^%N|59.13|||<ETB>61<CR><LF>
<STX>3||F|||20261103091500||RUBY0001<CR>R|8|^^^CBC^^^%L|28.79|||||F|||20261103091500||RUBY0001<CR>R|9|^^^CBC^^^%M|8.146|||||F|||20261103091500||RUBY0001<CR>R|10|^^^CBC^^^%E|2.949|||||F|||20261103091500||RUBY0001<CR>R|11|^^^CBC^^^%B|0.983|||||F|||202611030915<ETB>0B<CR><LF>
<STX>400||RUBY0001<CR>R|12|^^^CBC^^^RBC|4.812|||||F|||20261103091500||RUBY0001<CR>R|13|^^^CBC^^^HGB|14.12|||||F|||20261103091500||RUBY0001<CR>R|14|^^^CBC^^^MCV|88.40|||||F|||20261103091500||RUBY0001<CR>R|15|^^^CBC^^^MCH|29.34|||||F|||20261103091500||RUBY0001<ETB>4B<CR><LF>
<STX>5<CR>R|16|^^^CBC^^^MCHC|33.19|||||F|||20261103091500||RUBY0001<CR>R|17|^^^CBC^^^HCT|42.54|||||F|||20261103091500||RUBY0001<CR>R|18|^^^CBC^^^RDW|12.81|||||F|||20261103091500||RUBY0001<CR>R|19|^^^CBC^^^PLT|>>>>>|||||W|||20261103091500||RUBY0001<CR>R|20|^^^CB<ETB>11<CR><LF>
<STX>6C^^^MPV|8.120|||||W|||20261103091500||RUBY0001<CR>R|21|^^^CBC^^^PDW|-----|||||X|||20261103091500||RUBY0001<CR>R|22|^^^CBC^^^PCT|-----|||||X|||20261103091500||RUBY0001<CR>R|23|^^^CBC^^^BAND|FLAG||||||||20261103091500||RUBY0001<CR>L|1|N<CR><ETX>A2<CR><LF>
```

(Record R|3 bị cắt giữa `^^^CBC` và `^^^LYM` qua hai frame — driver phải ghép text các frame trước rồi mới tách record theo `<CR>`.)

**Máy hỏi chỉ định theo mẫu:**

```
<STX>1H|\^&|||RUBY0001^CDRuby^2.0ML^1.0|||||||P|LIS2-A<CR>Q|1|^260915001<CR>L|1|N<CR><ETX>8C<CR><LF>
```

**LIS gửi chỉ định (trong 4 s):**

```
<STX>1H|\^&||||||||||P|LIS2-A<CR>P|1||BN000123||THU MAU||19800101|M<CR>O|1|260915001||^^^CBC^1^0|||20261103083000||||||||Patient^Human<CR>L|1|N<CR><ETX>58<CR><LF>
```

**Máy từ chối (trùng chỉ định):**

```
<STX>1H|\^&|||RUBY0001^CDRuby^2.0ML^1.0|||||||P|LIS2-A<CR>P|1||BN000123||THU MAU||19800101|M<CR>O|1|260915001||^^^CBC^1^0|||20261103083000||||||||Patient^Human||||||||||X<CR>C|1|I|Duplicate Order^None|G<CR>L|1|E<CR><ETX>23<CR><LF>
```

## 8. Bẫy và lưu ý khi viết driver

1. **Mã ở thành phần 7** của R.3 (`^^^CBC^^^WBC` → tách `^` được `['','','','CBC','','','WBC']`, chỉ số 6 đếm từ 0). Dùng `generic-astm` (thành phần 4) sẽ ra `CBC` cho mọi dòng → sai hết. Cấu hình LIS cũ `TestCodeIndex = 6` là **đúng**.
2. **Record vắt qua frame** (nhồi frame đầy) — không được tách record theo frame.
3. **O.3 có 3 thành phần** khi chạy ống kín (`ID^giá^vị trí`) → SID = thành phần 1. LIS cũ đọc `SIDIndex = 2` (cả trường O.3) *(suy luận: nếu lấy cả trường thì mẫu chạy ống kín không khớp được)*.
4. **Cờ trùng chữ nhãn số** (`WBC`, `MCHC`, `RRBC`): phân biệt bằng R.4 = `FLAG`.
5. Giá trị đặc biệt `-----`, `>>>>>`, `<<<<<` không phải số; lưu nguyên kèm trạng thái `X`/`W`. **`W` = cần xem lại**, không tự duyệt.
6. Thứ tự R thay đổi theo lựa chọn xét nghiệm (CBC: WBC, NEU…; CBC+NOC: WBC, %N… rồi NEU…) → tra theo nhãn, không theo số thứ tự.
7. Độ dài giá trị 4 hay 5 ký tự là cài đặt máy (mặc định 5) — số chữ số thập phân thay đổi theo độ lớn.
8. Không có đơn vị trong bản tin; đơn vị Mỹ (K/µL, g/dL) khác máy khác ở BVĐK 10 (10^9/L, g/L) → bảng map riêng, không gộp với BC-3200/CON500.
9. **Không trả lời khi không có chỉ định** (khác Roche: Roche cần `L|1|I`). Trả trễ quá 4 s → máy chạy mặc định.
10. LIS phải đáp `ENQ` kiểm tra link bằng `ACK`; nếu LIS im, máy coi link xuống và **dừng gửi** (kết quả dồn hàng đợi).
11. Bản tin có đồ thị tới ~31 KB; ở 9600 baud mất ~32 s — nên tắt gửi đồ thị hoặc tăng baud nếu dùng host query.
12. Ký tự 8 bit (ISO 8859-1) trong tên người bệnh/bác sĩ — giải mã Latin-1, không UTF-8.

## 9. Kiểm thử

- Bản ghi thô (khử định danh) từ cả hai máy BVĐK 10: CBC ống mở và ống kín; CBC+NOC; RETIC (nếu dùng); một mẫu có cờ + giá trị `>>>>>`/`-----`; QC Whole Blood và Commercial; Autobackground; một lần hỏi `^ALL` và một lần hỏi theo ID.
- Mô phỏng: script đóng vai máy phát lại byte thô qua cặp COM ảo; script đóng vai LIS trả lời trong 4 s và sau 5 s (kiểm máy chạy mặc định).
- Lỗi phải thử: checksum sai → NAK → máy gửi lại frame; record vắt qua frame; ETB nhiều frame liên tiếp; mất kết nối giữa bản tin rồi nối lại (máy gửi lại cả bản tin); ENQ kiểm tra link định kỳ xen giữa; LIS gửi chỉ định bị từ chối (Duplicate, Parsing Error); kết quả đến ngay sau bản hỏi.

## 10. Nguồn

- *CELL-DYN Ruby System Host Interface Specification*, Abbott Diagnostics, List No. 09H05-01, **Revision C, 07/2008** (giao tiếp phiên bản 1.0, phần mềm 2.0ML), 40 trang + phụ lục: §2.1–2.4 (chuẩn, tham số cổng, chân DB-9, đóng gói frame), §3.2 (kiểm tra link), §3.3 (hỏi/gửi/từ chối chỉ định; bảng 9–13), §3.4 (bản tin kết quả; bảng 14–24), §4 (xử lý lỗi), App. A (bản tin mẫu), App. B (Base64), App. C (thời gian truyền, timeout hỏi), App. D (khác biệt 1.0ML → 2.0ML). Tệp: `tai-lieu-hang/Abbott_CELL-DYN_Ruby_Host_Interface_Specification_09H05-01C.pdf`.
- LIS cũ: `khao-sat/seed/du-lieu/Instrument.json` InsID 16, 17 (`TestCodeIndex 6`, `SIDIndex 2`); `InterfacePara.json`; `khao-sat/seed/anh-xa-ma-may.json`; `lis-agent/src/drivers.ts` (`abbott-celldyn`, `testIdComponent: 7`).
- Chuẩn: CLSI LIS1-A (ASTM E1381-02), LIS2-A (ASTM E1394-97) — xem `astm-e1394.md`.
- Còn cần hỏi hãng/nhà phân phối Abbott: tên menu cài đặt giao tiếp trên Data Station, bảng đơn vị chính xác từng chỉ số, phiên bản phần mềm máy ở BVĐK 10 (đặc tả này cho 2.0ML).
