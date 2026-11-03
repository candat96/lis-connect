# DIRUI CS-T240 / CS-T240plus — sinh hóa

> Trạng thái tài liệu: ✅ đủ để viết driver (ASTM E1381/E1394 có ví dụ frame + checksum, có luồng hỏi chỉ định) — còn vài chỗ tài liệu tự mâu thuẫn hoặc bỏ trống (ETB/độ dài frame, chế độ "Simplified", đơn vị timeout, cờ QC), xem mục 8
> Kiểm chứng: chưa đối chiếu máy thật. Đã giải mã bản ghi thô `PK-BINHMINH_cst240_*.jsonl` ngày 09/09/2026 → 05/10/2026 (13 tệp, ~3.000 phiên ASTM) nhưng **toàn bộ là dữ liệu do bộ giả lập `lis-agent/tools/simulate.ts` phát** — bố cục O/R của giả lập **khác** tài liệu hãng, không dùng làm chuẩn
> Driver: `dirui-cst240` — họ giao thức: astm-e1394 (khung E1381, RS-232)

## 1. Tóm tắt

- Máy sinh hóa tự động; phần mềm điều khiển chạy trên **PC Windows đi kèm máy** (máy phân tích ↔ PC qua RS-232). LIS nối vào **một cổng COM khác của PC đó** (COM1 không dùng được).
- Giao thức ASTM E1394 (mục 6–13) trên khung E1381: ENQ/ACK/EOT, `STX FN … ETX CS CR LF`, checksum mod 256.
- **Hai chiều**: gửi kết quả (bệnh nhân + QC) tự động/thủ công; máy gửi bản ghi `Q` hỏi chỉ định theo **mã vạch** hoặc **số mẫu**, LIS mở phiên mới gửi `H/P/O…/L`.
- Điểm khó nhất: **mã xét nghiệm là tên mục do người dùng tự khai trên máy** (không cố định); mã mẫu nằm ở thành phần 1 **hoặc** 2 của O-3/Q-3 tùy chế độ "Gather Sample Mode".

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | RS-232 trên PC điều khiển (không phải trên thân máy phân tích) | §1.4, §5.8, Addendum D |
| Baud | 4800 / 9600 / **19200** (mặc định 19200) | §5.8 (11), Add. D |
| Data bits | 7 / **8** (mặc định 8) | §5.8 (12) |
| Parity | **N** / M / E / O / S (mặc định N) | §5.8 (14) |
| Stop bits | **1** / 2 (mặc định 1) | §5.8 (13) |
| Bắt tay phần cứng | **Tài liệu không nêu — cần đo trên máy thật** (giả định không RTS/CTS *(suy luận)*) | — |
| TCP | Không có (PC có cổng mạng nhưng giao thức LIS chỉ mô tả serial) | §1.4.1 |
| Cáp | "USB Serial cable" và "Communicate cable" có trong danh sách phụ kiện; sơ đồ chân **Tài liệu không nêu** | Add. F |
| Cài đặt trên máy | `System Setup → LIS setting`: tích **Execute LIS Communication**; tích **Bidirectional** nếu dùng hỏi chỉ định; **Data Transmission Mode** = Real-time; **Gather Sample Mode** = Barcode (khuyến nghị) hoặc Sample No.; **Sample Save Mode** = First Analyzer / First LIS; **Send QC Result To LIS** nếu cần QC; Analyzer ID, Host ID; Serial port (≠ COM1); Timeout retry time (1–15) và Timeout retry count (1–6) | §5.8, Add. D |
| Chế độ mã vạch | Gather Sample Mode = Barcode đòi bật "sample barcode" ở `Other Settings` | §5.8 (6) |
| Phiên bản phần mềm trong tài liệu | `CST240EnV1.06Setup.exe`, giao thức "Version 1.1", tài liệu 2012/8 | §2, Add. D |

## 3. Khung truyền và bắt tay

**Ký tự điều khiển** (Add. D §1):

| Ký tự | Hex |
|---|---|
| STX | 0x02 |
| ETX | 0x03 |
| EOT | 0x04 |
| ENQ | 0x05 |
| ACK | 0x06 |
| LF / CR | 0x0A / 0x0D |
| NAK | 0x15 |
| ETB | **không được nhắc tới** trong tài liệu |

**Frame**: `<STX> FN <text> <CR> <ETX> CH CL <CR> <LF>`

- `FN`: số frame `'0'..'7'`, bắt đầu từ `'1'` trong một phiên, sau `'7'` là `'0'`.
- `<text>`: **một bản ghi** kết thúc bằng `<CR>` (CR nằm **trước** ETX và được tính vào checksum — mọi ví dụ của hãng đều vậy).
- **Checksum**: tổng byte từ `FN` đến hết `ETX` (gồm cả ETX, không gồm STX), lấy **mod 256**, viết 2 chữ số hex in hoa. (Văn bản hãng viết nhầm "modulo 8", nhưng ví dụ tính ra mod 256.)
  - Ví dụ của hãng: `[STX]1Test[ETX]` → 49+84+101+115+116+3 = 468 → 468 mod 256 = 212 = `D4` → `[STX]1Test[ETX]D4[CR][LF]`.
  - Đã tính lại các frame mẫu P/O/R/L/Q trong Add. D: **khớp**. Hai frame `H` mẫu (checksum `24`, `25`) **không khớp** khi tính lại (ra `53`/`54`) — lỗi chép trong tài liệu, không phải thuật toán khác.
- **Độ dài tối đa / tách frame ETB**: **Tài liệu không nêu — cần đo trên máy thật**. Driver vẫn phải nhận `ETB` (0x17) theo E1381 nếu gặp *(suy luận)*.

**Bắt tay** (theo luồng ví dụ Add. D "Examples"):

```
Bên gửi  ENQ            → Bên nhận ACK
Bên gửi  STX 1 H… ETX cs CR LF → ACK
…  (mỗi frame chờ một ACK)
Bên gửi  EOT
```

- Hai bên đều có thể là bên gửi (máy gửi kết quả / Q; LIS gửi chỉ định).
- NAK → gửi lại frame: E1381 quy định tối đa 6 lần *(suy luận theo chuẩn)*; máy cho cấu hình "Timeout retry count" 1–6 và "Timeout retry time" 1–15 (đơn vị **Tài liệu không nêu** — nhiều khả năng là giây *(suy luận)*).
- **Contention** (cả hai cùng gửi ENQ): **Tài liệu không nêu — cần đo trên máy thật**. Áp dụng quy tắc E1381: bên LIS nhường, chờ ≥ 20 s mới ENQ lại *(suy luận theo chuẩn)*.
- LIS phải trả **ACK cho ENQ và cho từng frame**; trả NAK khi checksum sai.

## 4. Bản tin kết quả (máy → LIS)

Thứ tự bản ghi: `H` → `P` → `O` → `R` (1 bản ghi / xét nghiệm) → `L`. Dấu phân cách: trường `|`, lặp `\`, thành phần `^`, thoát `&`, bản ghi `CR`.

**H — Message Header**

| Trường | Ý nghĩa | Ví dụ |
|---|---|---|
| H-2 | Dấu phân cách | `\^&` |
| H-5 | Sender (Analyzer ID cài trên máy) | `Analyzer` |
| H-10 | Receiver (Host ID) | `Host` |
| H-13 | Version | `1` |
| H-14 | Thời điểm gửi `YYYYMMDDHHMMSS` | `20261103091500` |

> Lưu ý: bảng trường ghi version ở H-13, nhưng ví dụ luồng của hãng chỉ có 2 dấu `|` sau `Host` (version rơi vào H-12). Driver **không** được đọc version/thời gian theo vị trí cứng.

**P — Patient**

| Trường | Ý nghĩa | Ví dụ |
|---|---|---|
| P-2 | Số thứ tự | `1` |
| P-6 | Họ tên (≤ 30) | thường để trống |
| P-9 | Giới `M/F/U` | `M` |
| P-15 | Tuổi `^` đơn vị (`Y/M/D`) | `40^Y` |
| — | Mẫu QC: P-6 = `QC` | `P|1||||QC|||||||||^` |

**O — Order**

| Trường | Ý nghĩa | Ví dụ |
|---|---|---|
| O-3.1 | **Mã vạch** (chế độ Barcode, ≤ 22) | `9900000123` |
| O-3.2 | **Số mẫu** (chế độ Sample No., ≤ 6) | `1` |
| O-3.3 | Số đĩa (disk) | `1` |
| O-3.4 | Vị trí (1–50 thường, 51–70 cấp cứu) | `10` |
| O-3.5 | Pha loãng `Y/N` | `N` |
| O-4.1 / O-4.2 | **Tên QC / lô QC** (chỉ khi là kết quả QC) | `LANDOX-1^2333` |
| O-6 | Ưu tiên `S`/`R` | `R` |
| O-7 | Thời điểm chỉ định | `20261103091500` |
| O-16 | Loại mẫu: 1 huyết thanh, 2 nước tiểu, 3 huyết tương, 4 dịch vị, 5 dịch cổ trướng, 6 dịch não tủy, 7 khác | `1` |
| O-26 | Report type `O/F/I/C/X` | `O` |

**R — Result**

| Trường | Ý nghĩa | Ví dụ |
|---|---|---|
| R-2 | Số thứ tự | `1` |
| R-3 | Universal Test ID — **mã xét nghiệm ở thành phần 4** (`^^^ALT`), ≤ 10 ký tự | `^^^ALT` |
| R-4 | Giá trị (≤ 12, có thể âm, vd `-8.57`) | `48` |
| R-5 | Đơn vị (≤ 10, tùy cài trên máy) | `U/L` |
| R-6 | Khoảng tham chiếu: `thấp^cao` `\` `thấp^cao` (bình thường `\` nguy hiểm). Với QC: `mean^SD`? (vd `2^22`) — **ý nghĩa với QC Tài liệu không nêu** | `0^40\^` |
| R-7 | Cờ `L/H/N/A`; với QC là luật Westgard (vd `1-3s`) | `H` |
| R-8 | Nature of abnormality; với QC vd `>+3SD` | |
| R-9 | Trạng thái `F`/`C` | `F` |
| R-13 | Thời điểm hoàn tất | `20261103091500` |

**Mã mẫu**: chế độ Barcode → O-3.1; chế độ Sample No. → O-3.1 rỗng, lấy O-3.2. Ví dụ luồng của hãng còn có dạng 4 thành phần `128123^1^1^2` (không có O-3.5).

**Phân biệt QC**: O-3 dạng `^0^1^^` (số mẫu 0) + O-4 có tên/lô QC + P-6 = `QC`. Không có cờ action code riêng. Chuẩn (calibration): **Tài liệu không nêu**.

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

Điều kiện: bật **Bidirectional**. Máy hỏi khi người dùng bấm **LIS Request** (theo dải số mẫu / mã vạch + đĩa + vị trí) hoặc tự động khi quét mã vạch mà không có chương trình cục bộ (Add. D §5.2). "Sample Save Mode" quyết định chỉ định LIS có ghi đè chỉ định đã nhập trên máy hay không.

**Máy hỏi** (một phiên ENQ…EOT):

| Trường Q | Ý nghĩa |
|---|---|
| Q-3 | `mã vạch^số mẫu^đĩa^vị trí^pha loãng` (giống O-3); chế độ Sample No. thì thành phần 1 rỗng: `^130^5^45^N` |
| Q-5 | `ALL` |
| Q-13 | `O` (hoặc `A`) |

Hỏi hàng loạt: nhiều bản ghi `Q|1`, `Q|2`… trong cùng một phiên.

**LIS trả lời** — mở **phiên mới** (LIS gửi ENQ) sau khi máy gửi EOT:

- Có chỉ định: `H|\^&` → `P|1||<mã mẫu>||<tên>|||<giới>||||||<tuổi>^<đv>` → **mỗi xét nghiệm một bản ghi O** (`O|n|<O-3 như Q-3>||^^^<mã>|R|<thời gian>|||||||||<loại mẫu>||||||||||O`) → `L|1|N`. Hỏi hàng loạt thì lặp nhóm `H…L` cho từng mẫu trong cùng một phiên.
- **Không có chỉ định**: gửi `ENQ` rồi `EOT` ngay (phiên rỗng) — tài liệu ghi rõ là bắt buộc.
- "Simplified Mode": gửi chỉ định theo đơn vị mẫu thay vì theo từng mục — **cú pháp Tài liệu không nêu — cần đo trên máy thật**; mặc định để **tắt**.
- Thời gian máy chờ trả lời: chỉ có tham số "Timeout retry time/count" chung — **Tài liệu không nêu** con số cụ thể cho chờ worklist.
- O-5 gửi xuống phải là **đúng tên mục đã khai trên máy** (vd `ALT`, `TP`), không phải mã dịch vụ LIS.

## 6. Bảng mã chỉ số

Mã gửi là **tên mục người dùng khai trên máy**. Bảng dưới là tên mặc định trong Addendum B (bảng thuốc thử thường dùng) — phải đối chiếu với máy tại phòng xét nghiệm. Đơn vị do người dùng cài, tài liệu không cố định.

| Mã máy gửi | Tên chỉ số | Ghi chú map (theo `lis-server/seed/dirui-cst240.sql`) |
|---|---|---|
| `ALT` | ALT (GPT) | → `124ALT` |
| `AST` | AST (GOT) | → `123AST` |
| `ALP` | Phosphatase kiềm | → `242ALK` |
| `GGT` | GGT | → `130GGT` |
| `TBA` | Acid mật toàn phần | chưa có mã |
| `TBIL` | Bilirubin toàn phần | → `134BilT` |
| `DBIL` | Bilirubin trực tiếp | → `135BilD` |
| `TP` | Protein toàn phần | → `232Prou` |
| `ALB` | Albumin | → `231Alb` |
| `LAP`, `CHE`, `ICDH`, `GLDH`, `FMN`, `HBDH` | Các men khác | chưa có mã |
| `AMY` | Amylase | → `239AMY` |
| `BUN` | Urê | → `122BUN` |
| `CRE-E` / `CRE-J` | Creatinin (enzym / Jaffe) | cả hai → `121CreS` |
| `GLU` / `GLU(HK)` | Glucose (oxidase / hexokinase) | cả hai → `342GLU` |
| `TC` | Cholesterol toàn phần | → `126ChoT` |
| `TG` | Triglycerid | → `127Tri` |
| `HDL-C` / `LDL-C` | HDL / LDL | → `129HDL` / `128LDL` |
| `CK` / `CK-MB` | CK / CK-MB | → `248CK` / `249CKMB` |
| `LDH` | LDH | → `807LDH` |
| `UA` | Acid uric | → `125UricS` |
| `P` | Phospho | → `236Phos` |
| `Mg` | Magie | → `237mg` |
| `Cl` | Clorua | → `923Cl` (không phải `415CL` "cặn lắng") |
| `Ca-CPC` / `Ca-ARS` | Calci toàn phần (2 phương pháp) | chưa map — mã `924Ca` của danh mục đang mâu thuẫn tên |

## 7. Bản tin mẫu

Dữ liệu giả; checksum đã tính lại theo thuật toán mục 3. Họ tên để trống (máy không bắt buộc gửi tên; mã hóa ký tự tiếng Việt trên máy chưa rõ).

**Kết quả (máy → LIS), chế độ Barcode:**

```
Máy  <ENQ>                                   LIS <ACK>
Máy  <STX>1H|\^&|||Analyzer|||||Host|||1|20261103091500<CR><ETX>88<CR><LF>   LIS <ACK>
Máy  <STX>2P|1|||||||M||||||40^Y<CR><ETX>F3<CR><LF>                          LIS <ACK>
Máy  <STX>3O|1|9900000123^1^1^10^N|||R|20261103091500|||||||||1||||||||||O<CR><ETX>F0<CR><LF>   LIS <ACK>
Máy  <STX>4R|1|^^^GLU|5.6|mmol/L|3.9^6.1\^|N||F||||20261103091500<CR><ETX>FB<CR><LF>           LIS <ACK>
Máy  <STX>5R|2|^^^ALT|48|U/L|0^40\^|H||F||||20261103091500<CR><ETX>C8<CR><LF>                  LIS <ACK>
Máy  <STX>6L|1|N<CR><ETX>09<CR><LF>                                          LIS <ACK>
Máy  <EOT>
```

**Hỏi chỉ định (máy → LIS):**

```
Máy  <ENQ>                                                  LIS <ACK>
Máy  <STX>1H|\^&|||Analyzer|||||Host|||1|20261103091000<CR><ETX>83<CR><LF>   LIS <ACK>
Máy  <STX>2Q|1|9900000123^^0^1^N||ALL||||||||O<CR><ETX>DB<CR><LF>           LIS <ACK>
Máy  <STX>3L|1|N<CR><ETX>06<CR><LF>                                         LIS <ACK>
Máy  <EOT>
```

**LIS trả chỉ định (GLU + ALT):**

```
LIS  <ENQ>                                                  Máy <ACK>
LIS  <STX>1H|\^&<CR><ETX>E5<CR><LF>                         Máy <ACK>
LIS  <STX>2P|1||9900000123|||||M||||||40^Y<CR><ETX>EB<CR><LF>   Máy <ACK>
LIS  <STX>3O|1|9900000123^^1^10^N||^^^GLU|R|20261103091000|||||||||1||||||||||O<CR><ETX>BC<CR><LF>   Máy <ACK>
LIS  <STX>4O|2|9900000123^^1^10^N||^^^ALT|R|20261103091000|||||||||1||||||||||O<CR><ETX>B7<CR><LF>   Máy <ACK>
LIS  <STX>5L|1|N<CR><ETX>08<CR><LF>                         Máy <ACK>
LIS  <EOT>
```

**LIS không có chỉ định:** `LIS <ENQ>` → `Máy <ACK>` → `LIS <EOT>`.

(Đĩa/vị trí trong O-3 trả lời: ví dụ hãng điền số đĩa/vị trí thực; quy tắc chép lại từ Q-3 hay LIS tự gán — **Tài liệu không nêu** *(suy luận: chép lại đĩa/vị trí từ Q-3)*.)

## 8. Bẫy và lưu ý khi viết driver

1. **Mã xét nghiệm do người dùng đặt** → màn "map máy ↔ chỉ số" phải học mã lạ từ dữ liệu thật; không seed cứng mà không đối chiếu.
2. **Mã mẫu**: phải tách thành phần của O-3/Q-3. Code cũ `codec/astm.ts` lấy **nguyên trường** O-3 (`9900000123^1^1^10^N`) làm mã mẫu, và với Q-3 chế độ Sample No. (`^130^5^45^N`) lấy thành phần 1 **rỗng** → bỏ mất câu hỏi.
3. **QC**: code cũ nhận QC qua O-12 = `Q` hoặc mã bắt đầu `qc/cal/ctrl` — **không khớp** máy này (QC nhận qua O-4 tên/lô QC, P-6 = `QC`, số mẫu `0`).
4. **CR trước ETX** nằm trong checksum; bỏ CR sẽ lệch checksum.
5. **Số frame reset về 1 ở mỗi nhóm H** trong ví dụ trả chỉ định hàng loạt (không chạy liên tục 1→7→0 trong cả phiên như E1381) — driver nhận không nên bắt lỗi FN quá chặt; driver gửi nên làm theo đúng ví dụ hãng.
6. **Vị trí trường H không ổn định** giữa bảng và ví dụ (mục 4) — không đọc H theo chỉ số cứng.
7. **Giá trị âm và dạng chuỗi** (`-8.57`): giữ nguyên chuỗi, không ép số rồi làm tròn.
8. **Đơn vị trong R-5 tùy cài đặt** (ví dụ hãng có `g/l` chữ thường) — LIS dùng đơn vị danh mục, chỉ dùng R-5 để cảnh báo lệch.
9. **ETB và frame dài**: tài liệu im lặng; vẫn phải hỗ trợ ghép frame ETB.
10. **Bộ giả lập cũ** (`tools/simulate.ts`) phát `H|\^&|||CS-T240plus^1.0|||||||P|1|…`, O-3 chỉ có mã vạch, R-3 `^^^342GLU^` (dùng **mã dịch vụ LIS** chứ không phải tên mục máy), không chờ ACK giữa frame → rawlog cũ không phản ánh máy thật; test dựng trên nó là test sai chuẩn.
11. **Batch mode / gửi thủ công**: nhiều mẫu có thể đến liên tiếp trong một phiên (nhiều nhóm H…L) *(suy luận từ ví dụ batch)*.
12. Chuỗi rác/space thừa: ví dụ hãng có `| Analyzer |` có khoảng trắng — trim khi đọc.

## 9. Kiểm thử

- Bộ mẫu cần: các frame ví dụ của Add. D (đã kiểm checksum); một phiên kết quả chế độ Barcode và một phiên chế độ Sample No.; một phiên QC (Send QC Result To LIS); hỏi đơn và hỏi hàng loạt; phiên batch nhiều mẫu; chụp thật từ máy có ký tự tiếng Việt trong P-6 (nếu phòng XN nhập tên).
- Mô phỏng: cặp COM ảo (com0com) + script đóng vai máy, **chờ ACK thật giữa từng frame**; script đóng vai LIS để thử máy thật ở chế độ hai chiều.
- Lỗi phải thử: checksum sai → NAK → máy gửi lại; không trả ACK → máy hết "retry count"; contention (cả hai cùng ENQ); EOT giữa chừng; LIS trả "không có chỉ định" (ENQ/EOT); mã mục lạ; frame > 240 ký tự (nếu có ETB); FN nhảy cóc; ghép frame dính nhau trong một lần đọc cổng.

## 10. Nguồn

| Tài liệu | Mục dùng | Nơi lấy |
|---|---|---|
| *CS-T240 Auto-Chemistry Analyzer User Manual* (bản 2012/8, phần mềm `CST240EnV1.06`) | §1.4 (cấu hình), §2.3.2.3 (cáp), **§5.8 LIS communication setup**, **Addendum B** (bảng mục thuốc thử), **Addendum D "Network Communication Interface Protocol For CS-240T Version 1.1"** (khung, bản ghi, ví dụ luồng) | `tai-lieu-hang/Dirui_CS-T240_User_Manual.pdf` |
| ASTM E1381 / E1394 | Quy tắc chuẩn cho chỗ tài liệu hãng bỏ trống (đánh dấu *(suy luận theo chuẩn)*) | tiêu chuẩn ASTM / CLSI LIS1-A, LIS2-A2 |
| Code bản cũ | `lis-agent/src/drivers.ts` (`dirui-cst240`), `codec/astm.ts`, `framer.ts`, `tools/simulate.ts` | `~/Downloads/lis-tham-khao/lis-agent/` |
| Bảng map kênh | `lis-server/seed/dirui-cst240.sql` | `~/Downloads/lis-tham-khao/lis-server/seed/` |
| Bản ghi thô | `lis-agent/.demo-data/rawlog/PK-BINHMINH_cst240_2026-09-09 … 2026-10-05.jsonl` — dữ liệu giả lập | như trên |

**Còn thiếu — cần xin hãng DIRUI / nhà phân phối:**
- *LIS Function Manual* của CS-T240 (tài liệu được nhắc ở mục "Send data to LIS" / "LIS communication log" nhưng không kèm).
- Bản giao thức mới hơn v1.1 cho **CS-T240plus** (tài liệu hiện có chỉ ghi CS-T240), cú pháp "Simplified Mode", bảng mã ký tự cho tên bệnh nhân, đơn vị của "Timeout retry time".
