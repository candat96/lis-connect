# Beckman Coulter Access 2 (và UniCel DxI) — miễn dịch

> Trạng thái tài liệu: ✅ đủ để viết driver (có đặc tả LIS gốc của hãng, 2 bản AB/2022 và AF/2026 khớp nhau về giao thức)
> Kiểm chứng: chưa đối chiếu máy thật (BVĐK 10 hiện không có máy Access trong danh sách máy)
> Driver: `beckman-access` — họ giao thức: `astm-e1394` (khung ASTM E1381-95, bản tin E1394-97)

## 1. Tóm tắt

- Máy miễn dịch hóa phát quang. Access 2 và UniCel DxI dùng **cùng một đặc tả LIS** ("Except as noted,
  both systems behave identically").
- Nối **RS-232 (EIA-232)**, khung **ASTM E1381** (ENQ/STX/ETX/checksum mod 256/EOT), bản tin **ASTM E1394**
  (record H, P, O, R, C, Q, L).
- **Hai chiều**: LIS gửi chỉ định trước (download) hoặc máy **hỏi chỉ định (Host Query)** bằng record `Q` khi
  nạp rack; máy gửi kết quả theo từng xét nghiệm hoặc theo cả ống mẫu.
- **Điểm khó nhất:** cờ kết quả không nằm trong record `R` mà ở **record `C` đi ngay sau**; kết quả định tính
  có hai thành phần `giá trị^diễn giải`; nhiều mã pha loãng / mã dẫn xuất (FTIndex, phi, sTfR Index)
  không được đặt từ LIS; máy coi **tin nhắn kế tiếp** trong thời gian chờ là trả lời cho câu hỏi.

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | EIA-232-D bất đồng bộ; máy là **DTE** | LIS Vendor Info §2 Physical Layer |
| Đầu nối | **DB-9 đực**, cổng **COM2** phía sau máy tính ngoài (external PC) của máy | §2; App. D |
| Chân | 2 = RD (máy nhận), 3 = TD (máy phát), 5 = GND. **Không hỗ trợ tín hiệu khác**; hãng khuyên chỉ nối 3 dây | App. D, hình D-1 |
| Cáp | Thường cần **null modem** (máy tính LIS cũng là DTE): TD↔RD chéo, GND–GND | §2; App. D |
| Khung ký tự | 1 start, **8 data, không parity, 1 stop** — **cố định, không đổi được** | §2 |
| Tốc độ | mặc định **9600**; chọn 1200 / 2400 / 4800 / 9600 / 14400 / 19200 | §2 |
| Bắt tay phần cứng | Không (chỉ 3 dây) | App. D |
| Chạy cáp xa | Cáp Cat5 + đầu chuyển DB-9↔RJ-45 (chân DB-9 2/3/5 = dây xanh lá/vàng/đỏ; DB-25 2/3/7 = vàng/xanh lá/đỏ); đầu chuyển gần máy đấu null-modem, gần LIS đấu thẳng | App. D, hình D-2, D-3 |
| TCP/IP | **Tài liệu không nêu** — đặc tả chỉ mô tả RS-232. Nếu cần mạng, dùng bộ chuyển serial↔TCP *(suy luận)* | — |
| Cài đặt trên máy | System ID (màn **System Setup**); LIS Send Mode **By Test / By Sample Container**; bật **Host Query** và thời gian chờ trả lời (màn **LIS setup**); màn **LIS Diagnostics** để gửi query thử | §3 H, R; §4 Example 1 |

Lưu ý: đặc tả **không áp dụng** cho Access 2 nằm trong UniCel DxC 600i Synchron Access Clinical System và
DxI nằm trong DxC Integrated Workstation (Preface).

## 3. Khung truyền và bắt tay

### 3.1 Ký tự điều khiển

| Ký tự | Hex | Dùng |
|---|---|---|
| ENQ | `05` | mở phiên |
| ACK | `06` | chấp nhận |
| NAK | `15` | từ chối / bận |
| STX | `02` | đầu frame |
| ETB | `17` | kết thúc frame giữa (record > 240 ký tự) |
| ETX | `03` | kết thúc frame cuối của record |
| EOT | `04` | đóng phiên |
| CR / LF | `0D` / `0A` | CR kết thúc record (nằm **trong** DATA, trước ETX); `CR LF` kết thúc frame |

### 3.2 Frame

```
<STX> [frame#] [DATA] <ETX|ETB> [C1] [C2] <CR> <LF>
```

- `frame#`: một ký tự `0`–`7`. **Frame đầu tiên của phiên là `1`**, tăng modulo 8 (… 7, 0, 1 …); frame gửi lại
  giữ nguyên số. Sai số frame → frame hỏng.
- **Một frame chỉ chứa một record**; mỗi record bắt đầu frame mới. DATA tối đa **240 ký tự**; frame tối đa 247
  ký tự. Record dài hơn chia thành các frame `ETB` rồi frame cuối `ETX`.
- Record tối đa **1024 ký tự** (giới hạn bộ phân tích).
- **Checksum** = tổng (mod 256) các byte của `frame#` + DATA + `ETX`/`ETB`; ghi thành **2 ký tự hex HOA**
  (`C1` = 4 bit cao, `C2` = 4 bit thấp). Không tính `STX`, không tính `CR LF` sau checksum.
- Ký tự cấm trong DATA: SOH, STX, ETX, EOT, ENQ, ACK, DLE, NAK, SYN, ETB, LF, DC1–DC4. Dữ liệu gửi xuống máy
  chỉ dùng ASCII 32–126 (§3 Allowed Characters).

Ví dụ checksum (đã kiểm bằng script, khớp tài liệu):

| Frame | Checksum |
|---|---|
| `<STX>1H|\^&|<CR><ETX>` | `61` |
| `<STX>4L|1|F<CR><ETX>` | `FF` |
| `<STX>1H|\^&|||ACCESS^500001|||||LIS||P|1|20111231235959<CR><ETX>` | `20` |

### 3.3 Phiên và thời gian chờ

| Giai đoạn | Quy tắc | Nguồn |
|---|---|---|
| Thiết lập | Bên gửi phát `ENQ` khi đường rảnh. Bên nhận: `ACK` = sẵn sàng; `NAK` = chưa sẵn sàng → bên gửi **chờ 10 s** rồi `ENQ` lại; ở trạng thái rảnh bên nhận bỏ qua mọi ký tự khác `ENQ` | §2 Establishment |
| Tranh chấp (cả hai cùng `ENQ`) | **Máy**: chờ 1 s rồi `ENQ` lại. **LIS**: về trạng thái rảnh, chờ tối đa **20 s** hoặc tới khi nhận xong tin của máy rồi mới thử lại → **máy được ưu tiên** | §2 Table 2-1, Time-outs |
| Không trả lời `ENQ` | sau **15 s** bên gửi vào kết thúc (`EOT`) | §2 Time-outs |
| Truyền | Mỗi frame chờ `ACK`; `NAK` hoặc ký tự lạ → gửi lại cùng số frame; **6 lần** không được thì dừng, phát `EOT` | §2 Defective Frames |
| Bên gửi chờ trả lời frame | **15 s**, quá thì hủy, `EOT`; phải giữ tin để gửi lại toàn bộ sau | §2 Time-outs |
| Bên nhận chờ frame kế / `EOT` | **30 s**, quá thì bỏ tin dở dang, về rảnh | §2 Receiver Waiting |
| Kết thúc | `EOT`, không cần trả lời | §2 Termination |
| Frame hỏng (bên nhận trả `NAK`) | lỗi khung ký tự; checksum sai; số frame không bằng frame trước hoặc frame trước + 1 | §2 Defective Frames |

## 4. Bản tin kết quả (máy → LIS)

### 4.1 Thứ tự record

```
H  →  P  →  O  →  R [→ C]  →  R [→ C] …  [→ O → R …]  →  L
```

- **LIS Send Mode = By Test**: mỗi kết quả một tin riêng. **By Sample Container**: mọi kết quả của một ống
  trong một tin (§3 Result Record; §4 Example 2).
- `C` chỉ xuất hiện khi kết quả có cờ, đứng **ngay sau** `R` mà nó thuộc về.
- Kết quả bị **cờ nghiêm trọng (fatal)** không được gửi (§4 Example 3) — hoặc được gửi với Result Status `X`
  và giá trị rỗng (§3 Result Record). Driver phải chịu được cả hai.

### 4.2 Header `H` (máy gửi)

| Trường | Vị trí (số `|`) | Ví dụ | Ghi chú |
|---|---|---|---|
| Record ID + delimiter | H.1–H.2 | `H|\^&` | `|` trường, `\` lặp, `^` thành phần, `&` escape |
| Sender Name ^ System ID | H.5 | `ACCESS^500001` | System ID cài ở System Setup |
| Receiver ID | H.10 | `LIS` | |
| Processing ID | H.12 | `P` | |
| Version | H.13 | `1` | |
| Ngày giờ tin | H.14 | `YYYYMMDDHHmmss` | |

### 4.3 Patient `P`

| Trường | Vị trí | Gửi lên? | Ghi chú |
|---|---|---|---|
| Sequence | P.2 | có | 1, tăng theo từng P trong tin |
| Practice Assigned Patient ID | P.3 | có (nếu có) | ≤ 15 ký tự chữ-số, phân biệt hoa thường. Nhận > 15 ký tự → máy vẫn nhận chỉ định nhưng **để trống Patient ID** |
| Tên, ngày sinh, giới, bác sĩ | P.6, P.8, P.9, P.14 | **không** | máy chỉ nhận khi tải xuống |

### 4.4 Order `O` (máy gửi)

| Trường | Vị trí | Ví dụ | Ghi chú |
|---|---|---|---|
| Sequence | O.2 | `1` | P mới đặt lại về 1 |
| **Specimen ID** | O.3 | `SPEC1234` | **= Sample ID / barcode**, ≤ 15 ký tự, `0-9 A-Z a-z + / - *`, phân biệt hoa thường |
| Instrument Specimen ID | O.4 | `^9^2` | thành phần 2 = **Rack** (1–9999), 3 = **vị trí** (Access 2: 1–10; DxI: 1–4) |
| Universal Test ID | O.5 | `^^^TSH^1\^^^FT4^1` | thành phần 4 = mã XN (≤ 8), 5 = lần lặp (1–99); có thể lặp `\` |
| Specimen Type | O.16 | `Serum` | |
| Report Type | O.26 | `F` / `X` | `F` cuối cùng; `X` không cho ra kết quả / hủy |

### 4.5 Result `R`

| Trường | Vị trí | Ví dụ | Ghi chú |
|---|---|---|---|
| Sequence | R.2 | `1` | O mới đặt lại về 1 |
| Universal Test ID | R.3 | `^^^Ferritin^1` | thành phần 4 = mã XN, 5 = lần lặp; **không lặp** |
| Giá trị ^ Diễn giải | R.4 | `105.6` hoặc `0.24^Non-React.` | giá trị ≤ 9 ký tự, có thể là `Cancelled`, `N/A`, `No Value`; rỗng nếu cờ fatal. Diễn giải: `Reactive`, `Non-React.`, `Confirmed`, `Not Conf.`, `Negative`, `Positive`, `Equivocal` |
| Đơn vị | R.5 | `ng/mL` | ≤ 8 ký tự; rỗng nếu fatal |
| Khoảng tham chiếu ^ loại | R.6 | `23.9 to 336.2^Normal` | loại `Normal` / `Panic` |
| Cờ bất thường | R.7 | `N` | `L`, `H`, `LL`, `HH`, `>`, `N`, `<` (xem 4.7) |
| Nature of Abnormality | R.8 | `A` | `A` tuổi, `S` giới, `N` chung; có thể lặp |
| Result Status | R.9 | `F` | `F` cuối cùng, `X` không có kết quả |
| Thời điểm hoàn thành | R.13 | `YYYYMMDDHHmmss` | |
| Instrument ID | R.14 | | System ID |

### 4.6 Comment `C` (cờ kết quả và thông báo lỗi)

`C|1|I|<văn bản>|<loại>` — Source luôn `I`; loại `I` = cờ máy cho kết quả, `G` = thông báo lỗi chữ tự do.
Nhiều cờ ngăn bằng **dấu chấm phẩy** (`CEX;PEX`). Văn bản ≤ 160 ký tự.

| Cờ | Ý nghĩa |
|---|---|
| `CEX` | Đường chuẩn / cut-off hết hạn |
| `CLX` | Lô chất chuẩn hết hạn |
| `CRH` / `CRL` | Cao / thấp hơn ngưỡng báo động (panic) |
| `ORH` / `ORL` | Cao / thấp hơn khoảng tham chiếu |
| `OVR` | Ngoài dải chuẩn (vượt đường chuẩn) |
| `LOW` | Thấp hơn giá trị báo cáo tối thiểu |
| `LRH` / `LRL` | Cao / thấp hơn dải LIS |
| `GRY` | Kết quả định tính **vùng xám** |
| `EXS` | Cơ chất hết hạn |
| `LEX` | Lô thuốc thử / dung dịch pha loãng hết hạn |
| `PEX` | Hộp thuốc thử / pha loãng đã mở quá hạn ổn định |
| `QCF` | QC ngoài giới hạn (Westgard) |
| `QEX` | Lô QC hết hạn |
| `RFX` | Kết quả từ xét nghiệm reflex |
| `TRA` / `TRR` | Nhiệt độ mâm mẫu / buồng thuốc thử ngoài khoảng |

Nguồn: §3 Comment Record.

### 4.7 Cờ trong R.7

| Mã | Nghĩa | Cờ máy tương ứng |
|---|---|---|
| `L` / `H` | Dưới / trên khoảng tham chiếu | ORL / ORH |
| `LL` / `HH` | Dưới / trên ngưỡng panic | CRL / CRH |
| `>` | Vượt dải cao | OVR |
| `<` | Dưới ngưỡng (xét nghiệm độ dốc dương) hoặc trên (độ dốc âm), ngưỡng trong file APF | |
| `N` | Bình thường | |

### 4.8 Mẫu thường / QC / chuẩn

Đặc tả chỉ mô tả kết quả bệnh nhân. Specimen ID trùng tên chất chuẩn (vd `AFPCal`) bị **từ chối** khi tải
xuống. **Máy có gửi kết quả QC/chuẩn qua LIS không: Tài liệu không nêu — cần đo trên máy thật** (cờ `QCF`
gắn trên kết quả bệnh nhân).

### 4.9 Kết quả dẫn xuất

`FTIndex` (từ TotT4 + TU), `phi-HYB`/`phi-WHO` (từ PSA, freePSA, p2PSA), `sTfRIndx` (từ sTfR + Ferritin):
máy **tự sinh và gửi lên**, LIS **không được đặt** các mã này. Trong O có thể xuất hiện ở trường lạ
(ví dụ §4 Example 5 có `^^^FTIndex^1` lọt vào O.6) → driver không nên dựa vào O.5 để biết danh sách R.

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

### 5.1 LIS gửi chỉ định trước (download)

`H` → (`P` → `O`…)… → `L`. Một tin có thể chứa nhiều bệnh nhân, mỗi bệnh nhân nhiều `O`.

Record `O` tải xuống:

| Trường | Vị trí | Bắt buộc | Ghi chú |
|---|---|---|---|
| Specimen ID | O.3 | **có** | ≤ 15 ký tự, bộ ký tự hạn chế (mục 4.4) |
| Universal Test ID | O.5 | **có** | `^^^MÃ` — chỉ dùng thành phần 4; nhiều XN lặp bằng `\`; lần lặp (thành phần 5) bị bỏ qua. Muốn chạy 2 lần: hai `O` riêng hoặc lặp mã |
| Priority | O.6 | | `S` = STAT, `R` hoặc rỗng = thường; mã khác → **từ chối cả order** |
| Action Code | O.12 | | `A` thêm (mặc định khi rỗng; mẫu chưa có trên máy thì vào LIS workpool), `N` mới (mẫu đã có XN thì từ chối), `C` hủy |
| Specimen Type | O.16 | | Serum, Plasma, Blood, Urine, CSF, Saliva, Amniotic, Cervical, Synovial, Urethral, Ratio, Other; rỗng → loại mặc định của XN |

Record `P` tải xuống: P.3 Patient ID (rỗng thì không cập nhật nhân khẩu), P.6 tên `Họ^Tên^Đệm^Hậu tố^Danh xưng`
(≤ 40), P.8 ngày sinh `YYYYMMDD` (sai thì bỏ ngày sinh, ghi Event Log, gửi lỗi nhưng vẫn nhận order),
P.9 giới (`M`/`F`/khác = `U`; quá 6 ký tự → **từ chối cả tin**), P.14 bác sĩ (≤ 40).

Header tải xuống tối thiểu `H|\^&|`. Nếu H.10 = `ACCESS` thì thành phần System ID phải trùng System ID máy
(hoặc rỗng), nếu không **cả tin bị bỏ qua** (§3 Header).

Khi order lỗi, máy gửi ngược `P` + `O` (Report Type `X`) + `C` loại `G` chứa thông báo, vd
`Sample already exists`, `Assay Not Enabled` (§3 Comment; §4 Example 4).

### 5.2 Host Query

- **Khi nào:** khi **nạp rack**, nếu mẫu chưa có order đang chờ và **Host Query được bật**, máy gửi một tin
  query cho từng Specimen ID; cũng có thể gửi thủ công ở màn LIS Diagnostics (§4 Example 1).
- **Tin hỏi:**

```
H|\^&|||ACCESS^500001|||||LIS||P|1|<giờ>
Q|1|^<SpecimenID>||ALL||||||||O
L|1|F
```

  Q.3 thành phần 2 = Specimen ID (≤ 15); Q.5 luôn `ALL`; Q.13 luôn `O` (chỉ hỏi nhân khẩu và order).
- **LIS trả lời:** một tin download bình thường (H, P, O, L) cho đúng Specimen ID đó. Không có chỉ định:
  `H` + `L` (không P/O).
- **Thời gian chờ:** cài trong LIS setup của máy (**giá trị cụ thể: Tài liệu không nêu — cần đọc trên máy**).
  Máy coi **tin đầu tiên nhận được trong thời gian chờ** là câu trả lời → LIS không được chen tin khác
  (vd order của mẫu khác) vào giữa.
- Sau khi máy gửi `EOT` của query, LIS phải tự mở phiên mới (`ENQ`) để trả lời.

## 6. Bảng mã chỉ số

Mã gửi/nhận là **Access Code** ở thành phần 4 của Universal Test ID (≤ 8 ký tự, **không phân biệt hoa
thường**, có thể có `-`). Danh sách thực tế trên máy xem ở màn **Tests**. Bảng dưới trích Appendix B
(Table B-1, bản AF 05/2026) — các mã thường dùng; đơn vị lấy từ R.5 khi nhận, không cố định ở đây.

| Mã máy | Xét nghiệm | Ghi chú |
|---|---|---|
| `TSH3` | TSH (3rd IS) | `TSH3d` = TSH pha loãng |
| `FT4II` | Free T4 (mới) | `FRT4` = Free T4 bản cũ |
| `FT3` | Free T3 | |
| `TotT3` | Total T3 | |
| `TotT4` / `TT4II` | Total T4 / Total T4 II | `TT4II` thêm ở bản AF |
| `TU` | Thyroid Uptake | |
| `FTIndex` | Free Thyroxine Index | **dẫn xuất — không đặt** |
| `TPOAb` / `Tg-Ab` / `TgAby` | Anti-TPO / Anti-Tg / Anti-Tg (khử biotin) | |
| `Tg2` / `Thyg` | Thyroglobulin / Thyroglobulin (khử biotin) | |
| `PSA-Hyb` / `PSA-WHO` | PSA toàn phần (chuẩn Hybritech / WHO) | |
| `freePSA` / `fPSA-WHO` | PSA tự do | |
| `p2PSA`, `phi-HYB`, `phi-WHO` | p2PSA, Prostate Health Index | phi là dẫn xuất |
| `HCG5` / `TBhCG2` | Total βhCG (5th IS) / (P/N 33500) | `HCG5d`, `Dil-hCG2` pha loãng |
| `Ferritin` | Ferritin | `Dil-Fer` pha loãng |
| `TropI` / `TnIA2` / `hsTnI` / `TnIhs` | Troponin I: `TropI` (AccuTnI), `TnIA2` (AccuTnI+3 cho Access 2; `TnIDx` cho DxI), `hsTnI` (hs, ngoài Mỹ), `TnIhs` (hs, Mỹ) | |
| `CK-MB`, `MYO` | CK-MB, Myoglobin | |
| `BNP2`, `NTBNP` | BNP, NT-proBNP | `NTBNP` thêm ở bản AF |
| `CEA2` | CEA | |
| `AFP` | AFP | `Dil-AFP` pha loãng |
| `OV125Ag` | CA 125 (OV Monitor) | |
| `GI19-9Ag` | CA 19-9 (GI Monitor) | |
| `BR15-3Ag` | CA 15-3 (BR Monitor) | |
| `VitdA` | 25(OH) Vitamin D Total — **Access 2** | `VitdD` dành cho DxI |
| `VitB12` / `B12II` | Vitamin B12 / B12 II | |
| `FOLW` / `RBCW` | Folate / Folate hồng cầu | |
| `Insulin` | Insulin siêu nhạy | |
| `CPep` | C-Peptide | |
| `Cortisol` | Cortisol | |
| `E2` / `SNSE2` | Estradiol / Estradiol nhạy | |
| `Prog` / `PROG2` | Progesterone | |
| `Testo` | Testosterone | |
| `PRL` | Prolactin | |
| `hFSH`, `hLH` | FSH, LH | |
| `AMH` / `AMH2` | AMH / AMH Advanced | |
| `DHE-S`, `SHBG` | DHEA-S, SHBG | |
| `PTH` / `PTHIO` | PTH nguyên vẹn (thường / trong mổ) | |
| `PCT` / `PrCT` | Procalcitonin (ngoài Mỹ / Mỹ) | |
| `IL-6` | Interleukin 6 | |
| `EPO` | Erythropoietin | |
| `sTfR` / `sTfRIndx` | sTfR / chỉ số sTfR | chỉ số là dẫn xuất |
| `HBsAgV3`, `HBsCtV3`, `HBAb3`, `HBc-Ab`, `HBc-IgM` | HBsAg, HBsAg khẳng định, anti-HBs, anti-HBc, anti-HBc IgM | định tính |
| `HCV-V3` | Anti-HCV V3 | định tính |
| `HIVc2` / `HIVco` | HIV combo V2 / HIV combo | định tính |
| `HAVAb`, `HAV-IgM` | Anti-HAV, HAV IgM | định tính |
| `Rub-IgG`, `MRub` | Rubella IgG / IgM | định tính |
| `GToxo`, `IgM-Toxo` | Toxo IgG / IgM | định tính |
| `CMV-G`, `CMV-M` | CMV IgG / IgM | định tính |
| `TPAb` | Giang mai (Syphilis) — chỉ Access 2 | định tính |
| `Dig` | Digoxin | |

Quy ước pha loãng: mã có tiền tố `d`/`Dil-` hoặc hậu tố `d` là bản **pha loãng**. Phần lớn
"Onboard Dilution" (`dXXX`) **chỉ có trên DxI**; Access 2 dùng các mã `Dil-…`/`…d` có sẵn (vd `Dil-Fer`,
`Dil-AFP`, `HCG5d`, `TSH3d`). Bảng đầy đủ ~120 mã: Appendix B; cột "DxC Integrated Code" (`A02`, `A19`…)
và "Remisol Code" không dùng cho kết nối trực tiếp.

## 7. Bản tin mẫu

Dữ liệu giả; checksum tính bằng script theo quy tắc mục 3.2.

### 7.1 Kết quả (By Sample Container), có cờ

```
Máy → LIS: <ENQ>
LIS → máy: <ACK>
<STX>1H|\^&|||ACCESS^500001|||||LIS||P|1|20261103091500<CR><ETX>12<CR><LF>
<STX>2P|1|BNTHU0001<CR><ETX>FD<CR><LF>
<STX>3O|1|2611030001|^12^3|^^^TSH3^1\^^^FT4II^1|||||||||||Serum||||||||||F<CR><ETX>A1<CR><LF>
<STX>4R|1|^^^TSH3^1|2.15|uIU/mL||N||F||||20261103091200<CR><ETX>72<CR><LF>
<STX>5R|2|^^^FT4II^1|28.4|pmol/L||H||F||||20261103091310<CR><ETX>EC<CR><LF>
<STX>6C|1|I|ORH|I<CR><ETX>25<CR><LF>
<STX>7L|1|F<CR><ETX>02<CR><LF>
Máy → LIS: <EOT>
```

(LIS trả `<ACK>` sau mỗi frame — lược bỏ cho gọn.) Mẫu `2611030001` ở rack 12 vị trí 3; FT4 cao (cờ `H`,
comment `ORH`).

Kết quả định tính (theo dạng tài liệu): `R|1|^^^Rub-IgG^1|0.24^Non-React.|S/CO||N||F||||<giờ>`.

### 7.2 Host Query — có chỉ định

```
Máy → LIS (sau ENQ/ACK):
<STX>1H|\^&|||ACCESS^500001|||||LIS||P|1|20261103090000<CR><ETX>0C<CR><LF>
<STX>2Q|1|^2611030001||ALL||||||||O<CR><ETX>08<CR><LF>
<STX>3L|1|F<CR><ETX>FE<CR><LF>
<EOT>

LIS → máy (ENQ/ACK mới):
<STX>1H|\^&|||LIS|||||ACCESS^500001||P|1|20261103090002<CR><ETX>0E<CR><LF>
<STX>2P|1|BNTHU0001|||Mau^Thu||19800101|F<CR><ETX>71<CR><LF>
<STX>3O|1|2611030001||^^^TSH3\^^^FT4II|R||||||A||||Serum<CR><ETX>A6<CR><LF>
<STX>4L|1|F<CR><ETX>FF<CR><LF>
<EOT>
```

### 7.3 Host Query — không có chỉ định

```
<STX>1H|\^&|||LIS|||||ACCESS^500001||P|1|20261103090002<CR><ETX>0E<CR><LF>
<STX>2L|1|F<CR><ETX>FD<CR><LF>
```

## 8. Bẫy và lưu ý khi viết driver

1. **Cờ ở record `C` riêng**, không ở `R`. Gắn `C` vào `R` **ngay trước nó**; nhiều cờ ngăn bằng `;`.
2. **Kết quả định tính**: R.4 = `giá trị^diễn giải` (vd `0.24^Non-React.`). Lưu cả chỉ số S/CO và diễn giải;
   cờ `GRY` = vùng xám → không duyệt tự động *(suy luận)*.
3. **Giá trị không phải số**: `Cancelled`, `N/A`, `No Value`, rỗng (fatal). Không ép kiểu số mù.
4. **Lần lặp**: R.3 thành phần 5 (`^1`, `^2`) — cùng mã nhiều lần, LIS phải chọn quy tắc (lấy lần cuối / trung bình
   / giữ cả) *(suy luận)*; khóa duy nhất của kết quả là (Specimen ID, mã, lần lặp, thời điểm).
5. **Mã dẫn xuất** (`FTIndex`, `phi-*`, `sTfRIndx`) xuất hiện dù LIS không đặt; trong ví dụ §4 Example 5 tài liệu
   còn đặt nó ở O.6 — đừng dựa vào O để kiểm tra.
6. **Mã pha loãng là mã riêng** (vd `Dil-Fer` khác `Ferritin`) → bảng map phải có cả hai, cùng trỏ một chỉ số
   (kết quả đã nhân hệ số trên máy — *cần xác nhận trên máy thật*).
7. **Specimen ID ≤ 15 ký tự, chỉ `0-9 A-Z a-z + / - *`**: barcode LIS dài hơn hoặc có dấu cách/dấu chấm sẽ bị
   từ chối. Phân biệt hoa thường. Không được trùng tên chất chuẩn.
8. **Patient ID > 15 ký tự** bị máy bỏ trống im lặng (chỉ ghi Event Log) → dùng mã ngắn.
9. **Giới > 6 ký tự** → cả tin bị từ chối; **Priority khác S/R** → order bị từ chối.
10. **H.10 = `ACCESS`** mà System ID không khớp → máy **bỏ qua cả tin** không báo. An toàn nhất: để
    Receiver ID rỗng hoặc đúng `ACCESS^<SystemID>`.
11. **Không dùng `/`, `-`, `*` làm delimiter** (mã XN có các ký tự này).
12. **Trả lời query**: máy coi tin đầu tiên trong thời gian chờ là câu trả lời → phải xếp hàng các tin download khác.
13. **Contention**: máy ưu tiên; LIS gặp `ENQ` khi đang `ENQ` phải lùi tối đa 20 s.
14. Số frame bắt đầu từ `1`, không phải `0`; record kết thúc bằng `<CR>` **bên trong** frame, trước `<ETX>`.
15. **Lỗi trong ví dụ của tài liệu** (đừng dùng làm dữ liệu kiểm thử mù quáng):
    - Ví dụ Q ở §3: `<STX>2Q|1|^Samp45||ALL||||||||O<CR><ETX>15` — checksum tính được là **`14`**.
    - Ví dụ §4 Example 1 viết `ALL|||||||O` (thiếu một `|` so với bảng trường) → parser Q phải lấy "trường
      cuối" thay vì đếm cứng *(chỉ ảnh hưởng nếu LIS mô phỏng máy)*.
    - §4 Example 2: `^^^EPO\^^Ferritin` (thiếu một `^`), `R|1|^^^EPO^1|0.13|mIU/mL||N|F|…` (thiếu một `|`).
16. Máy cài tiếng Nhật/Trung có thể gửi Katakana nửa độ rộng / GB2312 trong tên, Patient ID, comment loại `G`
    (App. C). Máy ở VN cài tiếng Anh → ASCII; vẫn nên đọc byte an toàn.

## 9. Kiểm thử

- **Bộ bản tin**: kết quả By Test và By Sample Container; kết quả có 1 và nhiều cờ (`CEX;PEX`); định tính
  (`Reactive`, `Non-React.`, `GRY`); `Cancelled`/Status `X`; nhiều lần lặp; FTIndex dẫn xuất; tin từ chối
  (`Sample already exists`); query có/không chỉ định; record > 240 ký tự (chia `ETB`); số frame quay vòng 7 → 0.
- **Mô phỏng máy**: cặp COM ảo (com0com) + bộ phát kịch bản ASTM E1381 (ENQ/ACK, checksum, NAK, timeout);
  có thể đối chiếu với thư viện mã nguồn mở ASTM (vd gói Python `astm`) để tự kiểm checksum.
- **Tình huống lỗi**: checksum sai → LIS `NAK`, máy gửi lại cùng số frame; 6 lần NAK → máy `EOT`; LIS không
  trả `ACK` 15 s; máy ngừng giữa chừng > 30 s (LIS bỏ tin dở); contention ENQ–ENQ; LIS trả lời query quá
  thời gian chờ; trả lời query chen tin khác; Specimen ID có ký tự cấm; Patient ID 16 ký tự.
- **Máy thật**: ghi lại cài đặt LIS setup (baud, Send Mode, Host Query, timeout, System ID) và ≥ 1 ngày bản thô.

## 10. Nguồn

| Tài liệu | Phiên bản | Dùng cho | Nơi lấy |
|---|---|---|---|
| **UniCel DxI and Access 2 LIS Vendor Information**, Beckman Coulter | **C03112-AB, 10/2022**, 65 trang | **Nguồn chính**: §2 Low Level Transfer Protocol, §3 Message Format, §4 Sample Messages, App. B Test Codes, App. D cáp | `tai-lieu-hang/Beckman_Access2_UniCel-DxI_LIS_Vendor_Information_C03112-AB_2022.pdf` (thư mục .gitignore) |
| UniCel DxI and Access 2 LIS Vendor Information | **C03112-AF, 05/2026**, 66 trang | Đối chiếu: nội dung giao thức trùng bản AB; chỉ thêm mã `BV-Score`, `NTBNP`, `TT4II` ở App. B | https://www.beckmancoulter.com/download/file/wsr-228138/C03112AF?type=pdf (chính hãng) |
| ASTM E1381-95 / E1394-97 | | chuẩn gốc (đặc tả hãng đã tóm đủ phần dùng) | ASTM (có phí; nay là CLSI LIS1-A / LIS2-A2) |

Ghi chú lịch sử: hai tệp người dùng gửi trước đó (cho Access 2 và cho một máy thứ ba) **bị hỏng** — một tệp
là trang SEO, một tệp là trang lỗi PHP — không có nội dung đặc tả. Tài liệu gốc Access 2 hiện có là bản
C03112-AB người dùng gửi sau và bản C03112-AF tải từ beckmancoulter.com.

**Còn thiếu — cần xin Beckman Coulter:**
- Giá trị và phạm vi **thời gian chờ Host Query** trong màn LIS setup của Access 2 (Operator's Guide /
  Reference Manual Access 2, chương LIS setup).
- Xác nhận máy có gửi **kết quả QC/chuẩn** qua LIS hay không.
- Nếu muốn nối mạng: tài liệu Ethernet/TCP cho DxI (Access 2 không có trong đặc tả này).
