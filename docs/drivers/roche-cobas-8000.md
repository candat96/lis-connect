# Roche cobas 8000 (qua cobas 8000 data manager) — sinh hóa + miễn dịch (hệ mô-đun c/e/ISE)

> Trạng thái tài liệu: ✅ đủ để viết driver — có *cobas 8000 data manager Host Interface Manual* bản 16 (phần mềm 1.06.01, 12/2016): tầng thấp ASTM, đủ bảng trường H/P/O/C/R/Q/M/L, hỏi chỉ định, truy vấn kết quả/chỉ định, QC, nhiều ví dụ. Còn thiếu: timeout/số lần gửi lại tầng thấp, cách cấu hình data manager làm TCP server (tài liệu chỉ trỏ sang Service Manual), bảng mã ứng dụng (lấy từ máy)
> Kiểm chứng: chưa đối chiếu máy thật (LIS cũ chỉ nhận **1** kết quả từ máy này)
> Driver: `roche-c8000` — họ giao thức: astm-e1394 qua TCP (E1381 giữ nguyên ENQ/ACK/frame/checksum trên socket)

## 1. Tóm tắt

- **LIS không nói chuyện với từng mô-đun c 502/c 701/c 702/e 602/e 801/ISE, mà với cobas 8000 *data manager*** — một PC/máy chủ trung gian giữa control unit của máy và LIS. Data manager gom chỉ định, kết quả, QC, chuẩn của mọi mô-đun và là đầu mối duy nhất.
- Kết nối chọn trên data manager: **ASTM Network** (TCP/IP), **ASTM Serial** (RS-232) hoặc **HL7 Network** (HL7 2.5). Tài liệu này mô tả nhánh ASTM. Mặc định data manager là **TCP client**, LIS là server; với tường lửa cobas IT cổng ASTM cố định **50001**.
- **Hai chiều**: data manager hỏi chỉ định (`TSREQ`) khi ống qua đầu đọc mã vạch; LIS trả `TSDWN`. LIS cũng đẩy chỉ định theo lô, hỏi chỉ định và hỏi kết quả (`TSREQ`/`RSREQ` từ phía LIS).
- Điểm khó nhất: LIS phải trả ACK tầng thấp trong **10 ms** và trả chỉ định trung bình **< 1,5 s** (hết hạn 16 s → máy chạy không chỉ định); data manager **gộp nhiều record vào một frame** 240 byte, mã hóa **UTF-8**.

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Đối tác của LIS | cobas 8000 **data manager** (máy chủ G5/G6/G8), không phải mô-đun | ch. 2, 5 |
| Cổng mạng | Card mạng riêng "Network connection to LIS or host" trên máy chủ data manager; cáp Cat.5e trở lên, chéo hay thẳng đều được; mọi thiết bị trung gian ≥ 100 Mbit/s | tr. 118–121 |
| Vai trò TCP | **Mặc định data manager là TCP/IP client**, LIS (hoặc tường lửa cobas IT) là **server**. Chạy data manager làm server được nhưng phải do kỹ thuật Roche cấu hình (Service Manual) | tr. 120–121 |
| Cổng TCP | Có tường lửa cobas IT hoặc FortiGate-40C: **ASTM 50001**, HL7 50002 (cố định). Không có tường lửa: Tài liệu không nêu — cần xem cấu hình tại chỗ | tr. 121 |
| Serial (ASTM Serial) | COM1, EIA-232-D, cáp bọc chống nhiễu ≤ 15 m; mặc định **19200, 8, N, 1** (đổi được qua tệp cấu hình bởi kỹ thuật Roche). HL7 không chạy qua serial | tr. 122, 439 |
| Bộ chuyển serial–mạng | Nếu LIS/data manager đi qua bộ chuyển serial–LAN thì tính là kết nối serial, không phải "network" | tr. 121 |
| Cài đặt | data manager: `Workplaces > cobas 8000 data manager Service > Service-related configuration > Interface options` — chọn giao thức (HL7 network / ASTM network / ASTM serial / No host); Sending application name (H-5, mặc định `cobas 8000`); **Host timeout** (mặc định 16 s); **Send query to host**, **Always ask for test selection**; Throttle instrument; Pass-through mode (patient/QC/calibration); Trace level. Control unit: bật giao tiếp với data manager, chế độ upload (By test / By sample / By cluster) | tr. 99–117, 125–134 |
| Mất mạng | Có thể phải khởi động lại kết nối host trên data manager và driver phía LIS | tr. 121 |
| Không có host | Chọn `No host`: bản tin ghi ra tệp `.dat` ở `C:\data\host\out` | tr. 100, 105 |

**Đối chiếu BVĐK 10:** InsID 19 khai TCP, LIS cũ là **client** (`ConType = S`) → máy là server. Trái với mặc định của tài liệu → hoặc data manager đã được Roche cấu hình làm server, hoặc LIS cũ nối vào một thiết bị trung gian (tường lửa cobas IT / phần mềm middleware) *(suy luận)*. Phải xem cấu hình tại chỗ trước khi chọn vai trò cho driver; driver nên hỗ trợ cả hai vai.

## 3. Khung truyền và bắt tay

Tầng thấp ASTM E1381-91 / tầng trên E1394-91 (LIS2-A2), **giống nhau cho ASTM Serial và ASTM Network** — trên TCP vẫn có ENQ/ACK/EOT, frame và checksum (ví dụ trace ở tr. 189).

| Ký tự | Hex |
|---|---|
| STX / ETX / EOT / ENQ / ACK | 02 / 03 / 04 / 05 / 06 |
| NAK / ETB / CR / LF | 15 / 17 / 0D / 0A |

- Frame giữa: `<STX> FN text <ETB> C1 C2 <CR><LF>`; frame cuối: `<STX> FN text <ETX> C1 C2 <CR><LF>`.
- FN = số frame mod 8, bắt đầu 1 (1…7, 0, 1…).
- Text ≤ **240 byte**/frame (frame ≤ 247). Data manager **gộp nhiều record (ngăn bằng `<CR>`) vào một frame** để nhanh; bên nhận phải nối mọi frame đến `EOT` rồi mới tách record theo `<CR>`. Một ký tự UTF-8 nhiều byte có thể **bị cắt giữa hai frame**.
- Checksum: tổng byte từ FN đến hết ETB/ETX, mod 256, 2 chữ số hex hoa. Ví dụ của hãng `<STX>1Test<ETX>` → `D4` (đã kiểm lại bằng script).
- Ba pha: thiết lập (ENQ → ACK), truyền (frame → ACK), kết thúc (EOT). **LIS phải trả ACK trong 10 ms** — chậm hơn vẫn chạy nhưng thông lượng giảm.
- Throttle instrument: data manager chờ tối đa 10 s ACK cuối của LIS rồi mới báo nhận cho máy.
- NAK, số lần gửi lại, timeout chờ ACK, xử lý tranh chấp (contention): **Tài liệu không nêu — cần đo trên máy thật** (theo chuẩn E1381: timeout 15 s, gửi lại ≤ 6 lần, máy ưu tiên khi tranh chấp *(suy luận)*).
- Ký tự phân cách cố định: `|` trường, `\` lặp, `^` thành phần, `&` escape (**không hỗ trợ** chuỗi escape). Record của data manager **kết thúc bằng `|`** (khác cobas 6000).
- Mã hóa: **UTF-8** (ký tự ≤ 127 trùng ASCII; `µ` = 2 byte).

## 4. Bản tin kết quả (máy → LIS)

Bản tin `RSUPL` (H-11): `H → P → O → C → {R → C(I) → [C(G)…]} → L`.

| Record | Trường | Nội dung |
|---|---|---|
| H | H-3 | mã bản tin (số nguyên tăng) |
| | H-5 | `cobas 8000^<phiên bản>` (tên đổi được) |
| | H-10 | `host` |
| | **H-11** | loại bản tin: `RSUPL` (kết quả BN thời gian thực/lô), `RSUPL^REAL` (QC lần đầu; cũng dùng cho BN), `RSUPL^BATCH` (gửi lại QC, hoặc trả lời truy vấn kết quả), `PCUPL^REAL`/`ICUPL^REAL`/`ECUPL^REAL` (chuẩn quang/ISE/miễn dịch) |
| | H-12, H-13, H-14 | `P`, `1`, ngày giờ tạo |
| P | P-4 | mã BN (≤ 16), P-6 `Họ^Tên` (≤ 30 mỗi phần), P-8 ngày sinh, P-9 `M`/`F`/`U`. QC: `P|1|` rỗng |
| O | **O-3** | **Mã mẫu (barcode, ≤ 22 ký tự)**. QC: `Tên^Lô^ID^Hạn dùng^Mã QC`. Rỗng nếu định danh bằng số thứ tự |
| | O-4 | `SeqNo^RackID^Vị trí^^Loại rack^Loại cốc^Pha loãng` — SeqNo = 0 khi dùng barcode; loại rack `S1` huyết thanh/huyết tương, `S2` nước tiểu… `S6` máu toàn phần, `QC`; cốc `SC`, `MC`, `NST0`, `FBT1-3`; `not`/`pre-diluted` |
| | O-5 | danh sách test `^^^code^dil` lặp bằng `\` |
| | O-6 | `R` thường / `S` cấp cứu |
| | **O-12** | `N` kết quả BN, **`Q` kết quả QC**, `O` trả lời truy vấn chỉ định |
| | O-16 | loại bệnh phẩm `1`…`A` (khớp ký tự 2 của loại rack) |
| | O-26 | `F` (từ data manager) |
| C (sau O) | C-4 | 5 chú thích `c1^…^c5`, C-5 `G` |
| R | R-2 | số thứ tự trong O |
| | **R-3** | `^^^Mã/Pha loãng/Tiền pha loãng` (vd `^^^627/1/not`); cobas e flow: thêm `^^Mã con/pha loãng` ở thành phần 6 |
| | **R-4** | định lượng: `Giá trị^[cờ định tính]`; định tính: `mã^giá trị` (mô-đun e: `-1` âm, `0` ranh giới, `1` dương; thành phần 2 là COI); thiếu/ẩn: **7 khoảng trắng** |
| | R-5 | đơn vị |
| | R-6 | khoảng tham chiếu `dải^TECH\dải^NORM\dải^CRIT\dải^USER` (rỗng với QC) |
| | **R-7** | cờ bất thường: `HH`, `H`, `N`, `L`, `LL`, `A`, `AA`, `HU` (độ bất định cao), `LoD` (dưới giới hạn phát hiện, EP17-A, thấy trong ví dụ), `eFlow.E`, `eFlow.W`; rỗng = chưa so dải; nhiều cờ → chỉ gửi cờ nặng nhất |
| | R-9 | `F` lần đầu/QC, `C` sửa (rerun), `M` sửa tay, `X` test tính không tính được |
| | R-11 | `người dùng control unit^người dùng data manager` (`SYSTEM` = tự duyệt) |
| | R-12, R-13 | giờ hút mẫu, giờ có kết quả (duyệt) |
| | R-14 | `Loại mô-đun^Mô-đun con^AnalyticalUnitID^InstrumentID^CalID^Lọ^Số lọ dự phòng` (vd `e602^1^MU1#e602#3#1^11^24`) |
| C (sau R) | C-3 = `I`, C-5 = `I` | **cờ máy, luôn có**: `C|1|I|0|I|` = không lỗi; `C|1|I|23^ISE Sample range over|I|` = mã báo động dữ liệu (ch. 14) |
| | C-3 = `L`, C-5 = `G` | chú thích nhập ở data manager `Nội dung^Người^Thời gian` |
| M | M-3 `PCR`/`ICR`/`ECR` | kết quả chuẩn (chỉ khi bật Pass-through → Calibration) |
| L | | `L|1|N|` |

- **Mẫu thường / QC / chuẩn**: BN → O-12 `N`, H-11 `RSUPL`; QC → O-12 `Q`, O-4.5 `QC`, O-3 là mô tả vật liệu QC (chỉ gửi khi bật Pass-through → QC results); chuẩn → bản tin M riêng (`PCUPL`/`ICUPL`/`ECUPL`).
- Kết quả gửi khi gom đủ theo mẫu, theo test hoặc theo cụm (cài ở control unit/data manager); rerun được gửi lại với R-9 = `C`.
- Báo động thiết bị (instrument alarm) **không** gửi lên LIS.

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

**Chế độ** (data manager → Interface options): *Realtime* (Send query = Y, Always ask = Y) — luôn hỏi; *Mixed* (Y/N) — chỉ hỏi khi không có chỉ định sẵn hoặc khi hỏi rerun; *Batch* (N/N) — không hỏi, LIS phải đẩy trước khi ống tới đầu đọc. Data manager luôn nhận chỉ định đẩy lô.

**Data manager hỏi** (`H-11 = TSREQ`), ngay khi rack qua đầu đọc mã vạch:

`Q|1|^^<SID>^<SeqNo=0>^<RackID>^<Vị trí>^^<Loại rack>^<Loại cốc>^<R1|R2>||ALL|||||||<R|S>|O|`

(Q-3.10 `R1` lần đo đầu, `R2` hỏi rerun; Q-12 ưu tiên; Q-13 `O`.)

**LIS trả lời** (`H-11 = TSDWN`): `H | P | O | [C] | L`.
- O-3 = SID, O-4 = `0^RackID^Vị trí^^Loại rack^Loại cốc^not` (lấy lại từ Q), O-5 = `^^^code^dil` lặp bằng `\` (mã ≤ 5 chữ số, máy bỏ số 0 đầu; pha loãng rỗng = 1), O-6 `R`/`S`, **O-12 `A` thêm / `R` chạy lại / `C` hủy**, O-16 loại bệnh phẩm, O-26 `O`.
- **Không có chỉ định**: vẫn gửi `TSDWN` với **O-5 rỗng** (`O|1|<SID>|0^…||R||||||A||||1||||||||||O`). Nếu LIS biết mẫu nhưng chưa có test, nên gửi đủ P (thông tin BN) — vì data manager ghi đè thông tin BN theo bản tin mới.
- Mã test lạ: có cả mã đúng và sai → nhận phần đúng, ghi lỗi; toàn mã sai → từ chối cả chỉ định. Test chỉ số huyết thanh (L/I/H) phải gửi đủ ba hoặc cả profile.
- **Timeout**: data manager chờ LIS theo *Host timeout* (mặc định **16 s**, phải nhỏ hơn timeout control unit 18 s ít nhất 2 s, khuyến nghị 8 s); hết hạn → gửi chỉ định rỗng xuống máy (máy không chạy mẫu). LIS nên trả trung bình **< 1,5 s**.

**LIS hỏi** (data manager không trả lời trong realtime, chỉ ở chế độ lô):
- Hỏi chỉ định: `H-11 = TSREQ`, `Q|1|^<Loại rack>^<SID>|||||||||||<O hoặc rỗng>|` → data manager trả `TSUPL` (O-12 = `O`).
- Hỏi kết quả: `H-11 = RSREQ`, `Q|1|^S1^<SID>||||||||||F|` (`F` chỉ kết quả cuối, rỗng = tất cả) → trả `RSUPL^BATCH`. Loại rack ở Q-3.2 là **bắt buộc** từ 1.03.

## 6. Bảng mã chỉ số

Mã trong O-5.4/R-3.4 là **mã ứng dụng (application code / host code)** cài trên máy, tối đa 5 chữ số, máy bỏ số 0 đầu. Tài liệu không có bảng mã đầy đủ — chỉ có ví dụ (ISE `989`/`990`/`991`, c-module `8717`, `870`, e602 `24`, e801 `604`, `171`, e flow `15000`…). **Phải chụp danh sách mã ứng dụng trên data manager/control unit của BVĐK 10.**

Bảng LIS cũ (InsID 19, 22 mã) — **chưa đối chiếu được với tài liệu** (tài liệu không liệt kê); định dạng số 2–3 chữ số **phù hợp** quy tắc mã ứng dụng:

| Mã máy | Chỉ số | Ghi chú |
|---|---|---|
| `49` | CEA | |
| `52` | CA 15-3 | |
| `53` | CA 125 | |
| `54` | CA 19-9 | |
| `67` | Ferritin | |
| `89` | Cortisol sáng **và** chiều | **trùng mã** — một mã máy ứng với 2 dịch vụ |
| `134` | AFP | |
| `148` | Beta-hCG | |
| `186` | C-Peptid | |
| `202` | Anti-CCP | |
| `286` | Anti-HCV | |
| `302` | FT4 | |
| `307` | CA 72-4 | |
| `499` | PSA | |
| `503` | HCV Ab | trùng nghĩa với `286`? — rà với khoa XN |
| `515` | HBsAg | định tính: R-4 = `mã^COI` |
| `574` | NT-proBNP | |
| `576` | Troponin T-hs | |
| `580` | Procalcitonin | |
| `627` | TSH | |
| `765` | Anti-HBs | |
| `813` | FT3 | |

Đơn vị lấy từ R-5 (máy gửi), không cố định trong driver.

## 7. Bản tin mẫu

Dữ liệu giả (SID `260915001`, BN `BN000123`, tên `MAU^THU`). Checksum tính bằng script Python (tổng byte UTF-8 từ FN đến ETB/ETX, mod 256), đã kiểm với ví dụ `D4` của hãng.

**Hỏi chỉ định và trả lời (realtime):**

```
DM   <ENQ>                                                    LIS <ACK>
DM   <STX>1H|\^&|1001||cobas 8000^1.06|||||host|TSREQ|P|1|20261103090000|<CR>Q|1|^^260915001^0^50012^1^^S1^SC^R1||ALL|||||||R|O|<CR>L|1|N|<CR><ETX>39<CR><LF>
                                                              LIS <ACK>
DM   <EOT>
LIS  <ENQ>                                                    DM  <ACK>
LIS  <STX>1H|\^&|||LIS-THU|||||cobas 8000^1.06|TSDWN|P|1|20261103090001<CR>P|1||BN000123||MAU^THU||19800101|F<CR>O|1|260915001|0^50012^1^^S1^SC^not|^^^627^1\^^^302^1|R||||||A||||1||||||||||O<CR>L|1|N<CR><ETX>F6<CR><LF>
                                                              DM  <ACK>
LIS  <EOT>
```

**Trả lời "không có chỉ định"** (O-5 rỗng):

```
LIS  <STX>1H|\^&|||LIS-THU|||||cobas 8000^1.06|TSDWN|P|1|20261103090001<CR>P|1|<CR>O|1|260915002|0^50012^2^^S1^SC||R||||||A||||1||||||||||O<CR>L|1|N<CR><ETX>BD<CR><LF>
```

**Kết quả** (TSH bình thường, FT4 cao) — data manager gộp record, cắt frame theo 240 byte (`µ` = 2 byte):

```
DM   <STX>1H|\^&|1002||cobas 8000^1.06|||||host|RSUPL|P|1|20261103093000|<CR>P|1||BN000123||MAU^THU||19800101|F|<CR>O|1|260915001|0^50012^1^^S1^SC^not|^^^627^1\^^^302^1|R|20261103090001|20261103090001||||N||||1||||||||||F|<CR>C|1|I|^^^^|G|<CR>R|1|^^^627/1/not|1.2<ETB>BD<CR><LF>
DM   <STX>23|µIU/mL|^TECH\0.27 - 4.20^NORM\^CRIT\^USER|N||F||op01^SYSTEM|20261103091000|20261103092900|e602^1^MU1#e602#3#1^11^101|<CR>C|1|I|0|I|<CR>R|2|^^^302/1/not|25.4|pmol/L|^TECH\12 - 22^NORM\^CRIT\^USER|H||F||op01^SYSTEM|20261103091000|20261103092900|<ETB>21<CR><LF>
DM   <STX>3e602^1^MU1#e602#3#1^11^102|<CR>C|1|I|0|I|<CR>L|1|N|<CR><ETX>F2<CR><LF>
```

(Mỗi frame LIS trả `<ACK>`; mở bằng `<ENQ>`, kết thúc `<EOT>`. Giá trị `1.23` bị cắt giữa frame 1 và 2 — minh họa vì sao phải nối frame trước khi tách record.)

**QC** (dạng theo ví dụ hãng, giá trị giả): `O|1|PNU^150403^2^20271231^27|0^30005^1^^QC^SC^not|^^^8685^1|R||||||Q||||1||||||||||F|` — O-12 = `Q`.

## 8. Bẫy và lưu ý khi viết driver

1. **Đối tác là data manager, không phải mô-đun.** Mọi cấu hình (vai trò TCP, chế độ realtime/batch, pass-through QC) nằm ở data manager; R-14 cho biết mô-đun nào đo.
2. **Vai trò TCP**: mặc định data manager là client → driver làm **server** cổng 50001. BVĐK 10 cấu hình ngược (LIS cũ là client) — driver phải hỗ trợ cả hai và tự nối lại khi đứt.
3. **Gộp record trong frame + cắt giữa giá trị/giữa ký tự UTF-8**: không phân tích theo frame; nối hết đến EOT, giải mã UTF-8 trên toàn bộ byte rồi mới tách `<CR>`.
4. **ACK trong 10 ms**: không làm việc nặng (ghi CSDL, tra chỉ định) trước khi ACK frame.
5. **R-3.4 dạng `mã/pha loãng/tiền pha loãng`** (`627/1/not`, `8717/Inc/not`): phải cắt trước `/`. LIS cũ đọc nguyên thành phần 4 làm mã → **khả năng cao là lý do chỉ nhận được 1 kết quả** *(suy luận)*.
6. **Giá trị 7 khoảng trắng** = thiếu/ẩn (mẫu thiếu, ngoài giới hạn kỹ thuật, test nhúng không báo cáo) — không lưu thành 0; xem C(I) để biết lý do.
7. **Cờ C(I) luôn đi sau mỗi R** — `0` là bình thường; mã khác là báo động dữ liệu (ch. 14), nên lưu kèm kết quả.
8. **Nhiều R cùng mã** trong bản tin lô (lần đầu `F` và rerun `C`) — lấy kết quả mới nhất theo R-13, giữ lịch sử.
9. **Mã trùng `89`** (Cortisol sáng/chiều) và nghi trùng `286`/`503` — không đoán, cảnh báo.
10. **cobas e flow (e 801)**: R-3 có thành phần 6 (mã con); kết quả chính R-4 có thể là mã số; các R con không phải chỉ số riêng — mặc định bỏ qua R có thành phần 6 trừ khi đã map.
11. **Khác biệt so với cobas e 411** (`roche-cobas-e411.md`):

| Điểm | cobas e 411 | cobas 8000 data manager |
|---|---|---|
| Kết nối | Chỉ RS-232 | TCP (mặc định, DM là client) hoặc RS-232 19200 8N1; còn có HL7 2.5 |
| Frame | Mỗi record một frame (kiểu Elecsys) | Gộp nhiều record/frame, cắt theo 240 byte |
| Mã hóa | ASCII | UTF-8 |
| Mã test R-3 | `^^^10^^0` (Elecsys) hoặc `^^^10/…` (cobas) | luôn `^^^mã/pha loãng/tiền pha loãng` |
| Loại bản tin | Không có H-11 kiểu `TSREQ`… | H-11 bắt buộc: `TSREQ`/`TSDWN`/`RSUPL`/`RSREQ`/`TSUPL`… |
| Hỏi chỉ định | `Q|1|^SID^…^SAMPLE^NORMAL…` | `Q|1|^^SID^0^Rack^Pos^^S1^SC^R1||ALL…|R|O|` |
| Không có chỉ định | `L|1|I` | `TSDWN` có O với O-5 rỗng |
| Hạn trả lời | 15 s | 16 s mặc định (cấu hình), khuyến nghị < 1,5 s |
| Thông tin BN | P-record | P-record (cobas 6000 cũ để ở C sau O) |
| Cờ | Bảng số ở record C | R-7 chữ (`H`/`L`/`HH`…) + C(I) mã báo động |
| QC | Nhận diện riêng | O-12 = `Q`, O-3 mô tả vật liệu QC |
| Truy vấn từ LIS | Không | Có (`TSREQ`/`RSREQ` từ LIS) |

## 9. Kiểm thử

- Bản ghi thô (bật Trace level = Information trên data manager, lấy tệp trace; hoặc bắt gói TCP 50001): một mẫu nhiều test e602 + c-module, một mẫu thiếu (7 khoảng trắng), một rerun (R-9 `C`), một kết quả định tính (HBsAg), một QC (`RSUPL^REAL`), một cặp TSREQ/TSDWN có và không có chỉ định, một TSREQ rerun (`R2`).
- Kiểm checksum với ví dụ `D4` và mục 7 (`39`, `F6`, `BD`, `BD`, `21`, `F2`).
- Lỗi: frame cắt giữa ký tự UTF-8; nhiều bản tin liền trong một lần đọc socket; TCP đứt giữa phiên; LIS trả chỉ định sau 16 s (máy đã chạy rỗng); mã test lạ trong TSDWN; NAK checksum.
- Đo thời gian ACK của driver (< 10 ms) và thời gian trả TSDWN (< 1,5 s).

## 10. Nguồn

- *cobas® 8000 data manager Host Interface Manual*, Version 16, phần mềm 1.06.01 (Roche, 12/2016) — `tai-lieu-hang/Roche_cobas-8000_data-manager_Host_Interface_Manual_v16_SW1.06.01.pdf`:
  - ch. 3 *Batch and realtime processing* (tr. 61–64); ch. 5 *The host connection* (tr. 99–100), *Interface options* (tr. 101–117: timeout 16/18 s, chế độ hỏi, throttle, pass-through), *Physical layer specifications* (tr. 118–122: client mặc định, cổng 50001/50002, serial 19200 8N1);
  - ch. 7 *ASTM protocol (LIS2-A2)* (tr. 141–150: frame, 240 byte, ACK 10 ms, checksum);
  - ch. 8 *ASTM text content* (tr. 153–186: H, P, O, C, R, Q, M(PCR/ICR/ECR), L);
  - ch. 9 *ASTM communication examples* (tr. 189–215: trace tầng thấp, TSREQ/TSDWN, kết quả, QC, truy vấn);
  - ch. 14 *Data alarms* (tr. 335–393); ch. 19 *Preparing the host communication* (tr. 439–456: khác biệt với MODULAR/cobas 6000).
- LIS cũ: `khao-sat/seed/du-lieu/Instrument.json` InsID 19 (`ConType = S`), `khao-sat/seed/anh-xa-ma-may.json`.
- **Còn thiếu:** *cobas 8000 data manager Service Manual* (cấu hình data manager làm TCP server, cổng khi không có tường lửa); danh sách mã ứng dụng (application code) thật trên máy BVĐK 10; tham số timeout/gửi lại tầng thấp.
