# Beckman Coulter (Olympus) AU480 / AU680 / AU2700 / AU5800 / AU640 — sinh hóa

> Trạng thái tài liệu: ✅ **AU480/AU680** (Online Spec V8 của Beckman) | ✅ **AU2700** (AU2700/AU5400 Online Specifications của Olympus, 2000 — khác biệt ở mục 4.8) | ✅ **AU5800 qua RS-232C** (AU5800 Online Specification v5.0, ấn bản 11, 2015 — khác biệt ở mục 4.9) — ⚠️ **AU5800 qua TCP/IP**: đặc tả gốc cũng chỉ mô tả RS-232C, vẫn chưa biết máy là client hay server, cổng, khung (mục 2.1) | ⚠️ **AU640**: chỉ biết RS-232C 1 kênh, bố cục suy theo AU2700 cùng thế hệ Olympus (mục 2.2), chưa có Online Spec riêng
> Kiểm chứng: chưa đối chiếu máy thật (chưa có bản ghi thô từ máy nào trong họ AU)
> Driver: `beckman-au` — họ giao thức: `beckman-au` (giao thức "AU Online" riêng của hãng, **không phải ASTM**)

## 1. Tóm tắt

- Máy sinh hóa tự động. Nối **RS-232C bán song công** (AU680, AU5800 có thêm TCP/IP); LIS gọi là "external computer".
- Bản tin là **chuỗi ký tự định vị theo cột (fixed-position)**, không có dấu phân cách. Mỗi bản tin
  kẹp giữa mã bắt đầu/kết thúc (mặc định `STX`/`ETX`), có thể kèm **BCC = XOR**. Hai lớp bắt tay:
  Class A (không ACK/NAK) hoặc Class B (có ACK/NAK).
- **Hai chiều**: máy hỏi chỉ định (`R␣`, `RH`, `Rh`), LIS trả lời (`S␣`, `SH`, `Sh`, `SE`); máy gửi kết quả
  theo phiên `DB` → `D␣`/`DH`/`DR`/`DA`/`DQ`/`d␣`/`dH` → `DE`. Có chế độ Real-time và Batch.
- **Điểm khó nhất:** bố cục bản tin **thay đổi theo cài đặt trên màn [Online]/[Requisition Format]**
  (có/không Rack No., số chữ số Rack, độ dài Sample ID 4–26, có/không giới, tuổi, 6 trường thông tin
  bệnh nhân, thông tin thuốc thử, số chữ số kết quả 6/9, số cờ 2/4, số chữ số mã XN 2/3).
  Driver phải đọc **hồ sơ cấu hình của từng máy** chứ không được cứng một bộ vị trí byte.

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | RS-232C, đồng bộ start-stop, **bán song công**, 1 kênh | Online Spec 2.1 |
| Tốc độ | 4800 / **9600** bps (mặc định 9600) | Online Spec 2.1; AU480 UG bảng Protocol |
| Ký tự | 1 start bit; độ dài **7** / 8 bit (mặc định **7**); parity **None** / Even / Odd (mặc định None); stop 1 / 2 (mặc định 1) | Online Spec 2.1; AU480 UG, AU680 RM |
| Cài đặt thực tế tại BVĐK 10 (hệ cũ) | `9600, 8, N, 1` cho mọi cổng COM (AU640 ở COM6, AU2700 ở COM5, trạm DK10.1) | khảo sát `lis-hien-huu.md` mục 3 |
| Bắt tay phần cứng | Không dùng bắt tay thật. RTS (chân 7) và CTS (chân 8): bản AU680 ghi "always must be abbreviated"; bản AU5800 v5.0 và AU2700 ghi rõ **"RTS and CTS shall be always short-circuited"** → hiểu "abbreviated" là dịch sai của "nối tắt": **nối tắt chân 7–8 ở đầu máy** *(suy luận cho AU680/AU480, có nguồn cho AU5800/AU2700)* | Online Spec 6.1; AU5800 Spec 6.1 |
| Đầu nối phía máy | **D-Sub 9 chân cái (female)** | Online Spec 6.4 |
| Chân | 3 = TxD (máy phát), 2 = RxD (máy thu), 5 = SG; FG = đất an toàn. Không nối các chân khác | Online Spec 6.1 |
| Cáp | Không kèm máy; dùng cáp có bọc chống nhiễu, **≤ 15 m** | Online Spec 6.3 |
| Mức tín hiệu | SPACE ≥ +3 V, MARK ≤ −3 V | Online Spec 6.2 |
| TCP/IP | AU680 và AU5800: có tùy chọn TCP/IP (do Beckman cài). Chi tiết AU5800 ở mục 2.1. **Vai trò client/server, cổng TCP: Tài liệu không nêu — cần đo trên máy thật**. AU480, AU2700, AU640: chỉ RS-232C | AU680 RM B63185AA tr. 2-86–2-90; AU5800 RM B81389AA tr. 2-81–2-85 |
| Menu trên máy | AU480/AU680/AU5800: **Menu List > System > Online** (tab Set Up / Protocol / Format Configuration / Online Test No.). AU640: **[Parameters] > [Online]** (F4 Set). AU2700: **[PARAMETER] > [ONLINE]** (SETUP / PROTOCOL / PROTOCOL-SPECIAL / TEST NO.) và **[PARAMETER] > [FORMAT] > [REQUISITION FORMAT]** | AU480 UG 4.12.1; AU640 UG D-115; AU2700 Spec 1, A.2 |
| Chân AU2700 | 2 RxD, 3 TxD, 5 SG, FG; **RTS (7) và CTS (8) phải nối tắt với nhau** ở đầu máy; phía host (DB-25 trong tài liệu): 3↔3, 2↔2, 5↔7. Không dùng chân khác ngoài 2, 3, 5, 7, 8. Đầu máy theo chuẩn IBM PC DB-9, cáp cần đầu DB-9 cái; cáp bọc chống nhiễu, không kèm máy | AU2700 Spec 6.1–6.4 |

### 2.1 AU5800 — RS-232C và TCP/IP (Reference Manual B81389AA, tr. 2-75–2-85)

Mặc định máy nối **RS-232C**; chuyển sang TCP/IP phải nhờ Beckman (tùy chọn có từ phần mềm 4.11, IFU A98352AB 10/2012).
Menu: **Menu List > System > Online** — tab Setup / Protocol / Format Configuration / Online Test No.

| Mục | RS-232C | TCP/IP |
|---|---|---|
| Chế độ nhận chỉ định (T.R.I. Receive) — riêng cho thường / cấp cứu / chạy lại | **Realtime** / **Batch** / None (mặc định None) | **Realtime** / **Host Direction** / None (mặc định None) — **không có Batch** |
| Chế độ gửi kết quả — riêng cho thường / cấp cứu / chạy lại / reagent blank / chuẩn / QC | Realtime / Batch / None | Realtime / Batch / None |
| Host Direction | — | LIS **đẩy chỉ định** xuống máy, máy lưu lại không cần gửi câu hỏi, cả khi đang chạy và khi ở chế độ khác |
| Other Transfer | — | "Equipment Status" Enable/None — phải Enable khi dùng Host Direction (máy gửi trạng thái thiết bị cho host) *(nội dung bản tin trạng thái: Tài liệu không nêu — cần đặc tả TCP)* |
| Error Control (T.R.I. / kết quả) | Stop / Continue (mặc định **Stop**) | Stop / Continue (mặc định Stop) |
| Ký tự | 7/8 bit (mặc định **7**), parity None/Even/Odd (mặc định None), stop 1/2 (mặc định 1) | — |
| Start code 1 / 2 | 01h–1Fh, mặc định `02` STX / None | None hoặc 01h–1Fh, mặc định `02` STX / **`03` ETX** (bảng in như vậy — nhiều khả năng lỗi in, cột mặc định lệch một dòng *(suy luận)*) |
| End code 1 / 2 | 01h–1Fh, mặc định `03` ETX / None | None hoặc 01h–1Fh, mặc định **None** / None |
| Text Length | 256 / 512 / 1024 (mặc định 1024) | Tài liệu không nêu |
| Device No. | bật/tắt, `00`–`99` | bật/tắt, **chuỗi tối đa 32 ký tự** |
| HOST ID | — | bật/tắt, chuỗi tối đa 32 ký tự |
| ETB Control | bật/tắt (mặc định tắt) | — |
| Tốc độ | 4800 / 9600 (mặc định 9600) | — |
| Class | A (không ACK/NAK, mặc định) / B | Tài liệu không nêu (không có mục Class) |
| BCC | bật/tắt (mặc định tắt) | — (không có mục BCC) |
| Retry | 0–3, mặc định **1** | 0–3, mặc định **3** |
| Timeout (×100 ms, 1–99) | T1 20, T2 15, T3 15, T4 20, T5 20, T6 10, T7 20 | **chỉ T1–T4** (00–99): T1 **10**, T2 **40**, T3 **20**, T4 **10** |
| Format Configuration | Rack No. Digit 4/5, Online Test No. Digit 2/3, Result Digit 6/9, No. of Data Flags 2/4, Cal./Control No. Digit 2/3, các mục Used/Unused | chỉ các mục Used/Unused (không có ô số chữ số) |
| **Vai trò TCP (client/server), địa chỉ IP, số cổng** | — | **Tài liệu không nêu — cần đo trên máy thật** (Reference Manual không có màn cài IP/cổng; IFU chỉ ghi "TCP/IP cable or RS232C cable") |

Ý nghĩa T1–T4 ở TCP/IP: Reference Manual không định nghĩa lại; *(suy luận)* cùng nghĩa với RS-232C (T1 chờ bắt đầu nhận bản tin kế, T2 thời gian nhận trọn bản tin, T3 thời gian gửi trọn, T4 chờ trả lời). Không có T5–T7 và không có Class/BCC gợi ý TCP dùng cơ chế xác nhận riêng *(suy luận)* — cần *AU5800 Online Specification* (bản TCP/IP) để chốt.

Hệ quả cho driver: với RS-232C, AU5800 dùng bố cục AU680 cộng các khác biệt ở **mục 4.9** (đã có đặc tả gốc *AU5800 Online Specification for Version 5.0 or later*, D-160845, ấn bản 11, 17/4/2015). Đặc tả này **chỉ mô tả RS-232C** ("1 channel (COM 1)"), không có chữ nào về TCP/IP. Với TCP/IP: chưa viết được driver chắc chắn — phải biết máy mở cổng nghe hay chủ động kết nối, số cổng, và cách đóng khung khi Start/End code = None: **Tài liệu không nêu — cần đo trên máy thật** (hoặc xin Beckman bản đặc tả TCP/IP).

### 2.2 AU640 (Service Manual Olympus, Part B tr. B2-3, B2-11; Part D D1-1)

- Vào/ra online: **RS-232C, 1 hệ (1 kênh)** — không có TCP/IP. Nhập chỉ định: bàn phím/chuột, online, màn cảm ứng (tùy chọn).
- Cổng online là **COM3** của máy tính điều khiển (DPR) — bo mở rộng 1, Serial 1 hoặc 2 tùy cấu hình SIO (địa chỉ 0x3E8, IRQ 9). COM1/COM2 là DPR↔ANL, COM4 modem. Đầu nối, chân: Tài liệu không nêu — cần đo trên máy thật.
- Tốc độ, khung, mã phân biệt: Service Manual không có mục đặc tả online. Bảng alarm (E23) có **2131 ONLINE ERROR (aa)(bbbbbbbbb-ccccc-dd)** với mã lỗi `01`–`12` và danh sách text `RB`/`RE`/`R␣`/`RH` y hệt AU2700 → AU640 dùng **cùng thế hệ giao thức Olympus với AU2700** (`R␣`/`RH`, không có `Rh`) *(suy luận)*.
- Xử lý hỏi chỉ định real-time (Part E, E2): mẫu không có ống → máy trả NOP, không hỏi host; mẫu đã nhận chỉ định có thể cấu hình hỏi lại host hay không (internal flag); barcode đọc lỗi → hỏi host bằng S.ID báo lỗi hoặc tự sinh theo số mẫu được cấp.
- Kết luận: viết driver AU640 theo bố cục AU2700 (mục 4.8) + hồ sơ cấu hình đọc từ màn [Parameters] > [Online] của máy; **bắt buộc** ghi bản thô để xác nhận.

Ghi chú: mặc định nhà máy là **7 bit**, trong khi hệ cũ chạy 8N1. Khi lắp phải đọc đúng cài đặt
trên máy (hoặc phiếu A.4 "online condition parameter sheet") rồi khai báo cổng theo, không tự giả định.

## 3. Khung truyền và bắt tay

### 3.1 Ký tự điều khiển

| Ký hiệu | Hex | Vai trò | Nguồn |
|---|---|---|---|
| Mã bắt đầu (Start code 1st) | mặc định `02` (STX); cấu hình được 01–1F | đầu bản tin | Online Spec 2.3(1) 1) |
| Start code 2nd | mặc định None; 01–1F | tùy chọn, bản tin có 2 byte mở đầu | A.3 |
| Mã kết thúc (End code 1st) | mặc định `03` (ETX); 01–1F | cuối bản tin | 2.3(1) 5) |
| End code 2nd | mặc định None | tùy chọn | A.3 |
| ETB | `17` | kết thúc khối giữa khi bật "ETB Control" | 2.3(2) B |
| ACK | `06` | Class B: chấp nhận | 2.2 |
| NAK | `15` | Class B: từ chối, yêu cầu gửi lại | 2.2 |
| BCC | `00`–`FF` (**1 byte nhị phân**, không phải 2 ký tự hex) | kiểm tra khối | 2.3(1) 6) |
| Δ (trong tài liệu) | `20` | dấu cách | 2.3 *1 |

Dữ liệu: ký tự 1 byte `20`–`7E` (và mã JIS/Shift-JIS khi máy cài tiếng Nhật — bỏ qua với máy ở VN).

### 3.2 Cấu trúc một bản tin

```
[Start code 1–2 byte] [Mã phân biệt 2 ký tự] [System No. 0/2] [Dữ liệu bản tin] [End code 1–2 byte] [BCC 0/1 byte]
     1)                      2)                     3)               4)                  5)               6)
```

- `System No.` (00–99) chỉ có khi bật "Device No."; dùng để LIS phân biệt máy nguồn. **Chỉ có trong bản
  tin máy gửi (`R*`, `D*`); bản tin LIS gửi (`S*`, `SE`) không có trường này** (theo hình 3.3 và 3.2/3.4).
- **BCC = XOR của mọi byte từ 2) đến 5)**, tức là từ mã phân biệt đến hết mã kết thúc, **không gồm mã bắt
  đầu**. Chỉ có khi bật "BCC Check" (mặc định tắt). Nguồn: 2.3(1) 6).

Ví dụ tính BCC (dữ liệu giả, đã kiểm bằng script):

| Bản tin (không kể STX) | BCC |
|---|---|
| `DB` + ETX | `0x05` |
| `DE` + ETX | `0x02` ← **trùng STX** |
| `SE` + ETX | `0x15` ← **trùng NAK** |
| `RB` + ETX | `0x13` |

### 3.3 Độ dài và chia khối (blocking)

- "Text Length" (độ dài bản tin tối đa một lượt) = 256 / 512 / **1024** byte (mặc định 1024), tính từ 1) đến 6).
- Bản tin cố định (`RB R␣ RH Rh RE SE DB DE`) không chia khối.
- Bản tin biến đổi (`S␣ SH Sh D␣ DH DR DA DQ d␣ dH`) = **phần cố định (header)** + **Data classification
  No.** (1 ký tự) + **phần biến đổi**. Nếu dài hơn Text Length thì chia khối: khối đầu số `0`, rồi `1`… `9`,
  **khối cuối luôn là `E`**; không chia thì chỉ có một khối mang `E`. Tối đa 11 khối. Mỗi khối lặp lại
  nguyên phần cố định *(suy luận từ hình 3.3/3.4: header đứng trước mỗi chỗ cắt ↓)*.
- Khi bật ETB Control và End code = ETX: khối giữa kết thúc bằng `ETB`, khối cuối bằng `ETX`. Không bật
  ETB: mọi khối kết thúc bằng End code đã đặt. Nguồn: 2.3(2) B.
- Chỗ cắt khối chỉ nằm **giữa hai mục xét nghiệm** (hoặc sau khối thông tin bệnh nhân), không cắt giữa trường.

### 3.4 Class A / Class B, thời gian chờ, gửi lại

| Mục | Class A | Class B | Nguồn |
|---|---|---|---|
| Xác nhận | Không ACK/NAK; bên gửi gửi theo nhịp thời gian | Bên nhận trả `ACK` (06) hoặc `NAK` (15) sau mỗi bản tin, **cả hai chiều** (LIS cũng phải ACK từng `R*`/`D*`; máy ACK từng `S*`) | 2.1, 4.1, hình 4.2(3) |
| Gửi lại | — | Nhận NAK → chờ ≥ T6 rồi gửi lại cùng bản tin; số lần **Retry 0–3** (mặc định 1) | 2.1, 4.2 |
| Mặc định | **Class A** | | AU480 UG, AU680 RM |

Bảng thời gian (Online Spec 4.2; đơn vị cài 0,1 s × n, n = 1–99):

| Ký hiệu | Ý nghĩa | Chuẩn |
|---|---|---|
| T1 | Từ khi gửi/nhận xong đến khi bắt đầu nhận bản tin kế (timeout) | 2 s |
| T2 | Từ lúc bắt đầu nhận đến khi nhận xong (timeout) | (Text length × độ dài ký tự / tốc độ) + 0,5 s |
| T3 | Từ lúc bắt đầu gửi đến khi gửi xong | như T2 |
| T4 | Từ khi gửi xong đến khi nhận được trả lời ACK/NAK (timeout) | 2 s |
| T5 | Khoảng tối thiểu giữa hai bản tin liên tiếp | 2 s |
| T6 | Khoảng tối thiểu từ khi nhận NAK đến khi gửi lại | 1 s |
| T7 | Từ khi gửi NAK đến khi bắt đầu nhận bản tin gửi lại (timeout) | 0,5 s |
| t1–t4 | Khoảng nghỉ tối thiểu cố định (không cài được) | 0,5 s |

Mặc định trên màn hình AU480/AU680 (×100 ms): T1 = 20, T2 = 15, T3 = 15, T4 = 20, T5 = 20, T6 = 10, T7 = 20.
Lưu ý T7 mặc định màn hình (2 s) khác "chuẩn" trong bảng 4.2 (0,5 s) — theo giá trị thực trên máy.

**Hệ quả cho LIS:** trả lời `S␣` cho một `R␣` phải tới **trong T1 (≈ 2 s)**; ở Class B, ACK phải tới trong
T4 (≈ 2 s) và **không gửi ACK sớm hơn t4 = 0,5 s** *(theo t4: "shortest time … until response transmission"
là ràng buộc của máy; với LIS nên giữ ≥ 0,5 s cho an toàn — suy luận)*.

Tranh chấp đường truyền: bán song công; tài liệu không định nghĩa cơ chế contention như ASTM. Mục 5.3 quy
định: trong lúc một phiên hỏi chỉ định đang chờ trả lời thì máy không gửi kết quả, và ngược lại.
Ngoài ra **Tài liệu không nêu — cần đo trên máy thật** việc LIS chủ động gửi khi máy đang phát.

## 4. Bản tin kết quả (máy → LIS)

### 4.1 Phiên gửi kết quả

```
DB  →  (D␣ | DH | DR | DA | DQ | d␣ | dH) …  →  DE
```

| Mã | Ý nghĩa | Ghi chú |
|---|---|---|
| `DB` | Bắt đầu phiên gửi kết quả | Real-time: gửi khi máy chuyển từ Stand-by sang "Analysis 1" |
| `D␣` | Kết quả mẫu thường (Routine / Emergency / STAT) | **bản tin chính cần xử lý** |
| `DH` | Kết quả chạy lại (repeat), gồm cả auto-repeat | có thêm Original sample No. |
| `DR` | Reagent blank | bỏ qua hoặc lưu log |
| `DA` | Calibration | có Calibrator No. |
| `DQ` | QC | có Control No. → đưa sang mô-đun nội kiểm |
| `d␣` / `dH` | STAT quick output (chỉ Real-time) | xuất sớm kết quả STAT |
| `DE` | Kết thúc phiên | khi máy về Stand-by/Stop, hoặc khi phiên bị hủy do lỗi |

Real-time: máy gửi `D*` cho một mẫu **khi đã có đủ kết quả mọi xét nghiệm được chỉ định** của mẫu đó
(5.2(1) B). Batch: người dùng chọn khoảng mẫu ở màn [Online] để gửi lại; Batch không có `d*`.

### 4.2 Phần cố định của `D␣` (mẫu thường)

Thứ tự trường theo hình 3.4(2). Độ dài phụ thuộc cài đặt; cột "Vị trí mẫu" tính từ byte ngay sau STX
(offset 0) cho **cấu hình tham chiếu**: không System No., có Rack No. 4 chữ số, Sample ID 10 chữ số.

| # | Trường | Độ dài | Nội dung | Vị trí mẫu |
|---|---|---|---|---|
| 2) | Mã phân biệt | 2 | `D␣` | 0–1 |
| 3) | System No. | 0/2 | `00`–`99` (nếu bật Device No.) | — |
| | Rack No. | 0/4/5 | `0001`–`9999` (hoặc 5 chữ số). STAT: toàn dấu cách. Mẫu RB…: toàn `0` | 2–5 |
| | Cup position | 0/2 | rack `01`–`10`; STAT `01`–`22`. Có/không đi cùng Rack No. | 6–7 |
| 7) | Sample type | 1 | `␣` huyết thanh, `U` nước tiểu, `X` khác, `Y` khác-1, `W` máu toàn phần, `N` chưa xác định | 8 |
| | Sample No. | 4 | `0001`–`9999` thường, `E001`–`E999` cấp cứu, `P001`–`P999` STAT | 9–12 |
| | Sample ID | 4–26 (cài đặt) | barcode, **căn phải, đệm dấu cách bên trái** | 13–22 |
| 8) | Dummy | 4 | 4 dấu cách | 23–26 |
| 9) | Data classification No. | 1 | `0`–`9` hoặc `E` (khối cuối) | 27 |

Nguồn: Online Spec 3.4(2), 3.5(1).

### 4.3 Phần biến đổi của `D␣`

```
[10) Sex 0/1] [Year age 0/3] [Month age 0/2] [Patient info 1..6, mỗi trường 0–20]   ← chỉ ở khối đầu
{ [11) Online test No. 2/3] [Reagent info 0 hoặc nhiều byte] [Analysis data 6/9] [Data flag 2/8] } × n
```

| Trường | Độ dài | Nội dung |
|---|---|---|
| Sex | 0/1 | `M`, `F`, `␣` không trả lời, `0` không có |
| Year age / Month age | 0/3, 0/2 | `000`–`150` / `00`–`11`; dấu cách = không ghi |
| Patient information 1–6 | mỗi trường 1–20 (cài ở [Requisition Format]) | văn bản/số |
| 11) Online test No. | 2 hoặc 3 | `01`–`99` hoặc `001`–`120` — **mã kênh** (bảng mục 6) |
| Reagent lot No. / bottle No. | tài liệu ghi "4x4" mỗi loại | R1(R1-1), R2(R2-1) [và R1-2, R2-2 nếu bật]; không dùng thì toàn dấu cách. **Độ dài chính xác của từng ô: Tài liệu không nêu rõ — cần đo trên máy thật** |
| Analysis data | 6 hoặc 9 | xem 4.4 |
| Data flag | 2 hoặc 8 | xem 4.5 |

Nguồn: 3.4(2) B, 3.5(1). Các trường có/không theo bảng A.3 (Sex, Age, Patient info 1–6, Reagent Inf.,
R2-1/R2-2 Use, Result Digit, Zero Suppress, No. of Data Marks, Online Test No. Digit).

`DH` khác `D␣`: thay Sample No. bằng **Repeat run sample No. (4)**, sau Sample ID là **Original sample No. (4)**
thay cho Dummy, và phần biến đổi **không có** Sex/Age/Patient info. `DR` thêm "Reagent blank data
classification" (`1`/`2`); `DA` có Calibrator No.; `DQ` có Control No. (2/3 chữ số; dạng 2 chữ số dùng bảng
quy đổi `A0`…`K0` cho 100–200). Nguồn: 3.4(3)–(8), 3.5(7)–(9).

### 4.4 Định dạng giá trị (Analysis data)

- Rộng cố định 6 hoặc 9 ký tự, **căn phải**, có thể có dấu `-` và dấu thập phân `.`.
- Không bật Zero Suppress: đệm `0` bên trái (vd `0123.4`, `-0123.456`); bật: đệm dấu cách (vd `-␣1234`, `-␣123.456`).
- **Vượt số chữ số**: toàn `9` (`999999` / `999999999`) → driver phải coi là "vượt định dạng", **không** lưu
  thành số 999999.
- Giá trị OD: dạng `0.1234` (6) hoặc `-␣␣0.1234` (9); OD âm ở dạng 6 chữ số thành "digit over".
- **Mục LIH** (chỉ số huyết thanh): giá trị là 3 mức `nL nI nH` (6 chữ số: `0nL0nI0nH`; 9 chữ số: `00nL00nI00nH`),
  mỗi n: `0` bình thường, `1`–`5` = `+`…`+++++`, `6` ABN, `9` không đo.

Nguồn: 3.5(2). Lưu ý tài liệu ghi "no zero suppression" cho cả bốn hình A–D — hình C, D thực chất là
**có** zero suppression (lỗi biên tập của tài liệu).

### 4.5 Cờ dữ liệu (Data flag)

- "No. of Data Marks" = 2 loại → trường 2 ký tự, **mỗi cờ chỉ lấy ký tự đầu** (vd cờ `F_` và `ph` → `Fp`).
- = 4 loại → trường 8 ký tự, tối đa 4 cờ mỗi cờ 2 ký tự (vd `F␣P␣1Q␣␣`).
- Bảng cờ (A.1, 61 cờ, theo thứ tự ưu tiên). Các cờ driver phải xử lý:

| Cờ | Ý nghĩa | Xử lý gợi ý *(suy luận)* |
|---|---|---|
| `H` / `L` | Cao / thấp hơn khoảng tham chiếu | hiển thị cờ bất thường |
| `ph` / `pl` | Vượt ngưỡng báo động (panic) | cảnh báo đỏ |
| `F` / `G` | Cao / thấp hơn dải đo (dynamic range) | không duyệt tự động, đề nghị pha loãng |
| `Fx` / `Gx` | OD vượt dải đo | như trên |
| `#` | Thiếu mẫu | chặn kết quả |
| `%` | Phát hiện cục đông | chặn kết quả |
| `?` | Không tính được kết quả | chặn |
| `!` | Không tính được nồng độ | chặn |
| `R` | Thiếu thuốc thử | chặn |
| `l` / `i` / `h` | Ảnh hưởng bởi đục mỡ / vàng da / tan máu | cảnh báo |
| `a`, `ba`, `bh`, `bn` | Thuốc thử hết hạn, chuẩn hết hạn, chưa có chuẩn hợp lệ, dùng master curve | cảnh báo chất lượng |
| `P` / `N` | Dương tính / âm tính | dùng cho XN định tính |
| `J` / `K`, `fh` / `fl` | Ngoài khoảng quyết định chạy lại / reflex | gợi ý chạy lại |
| `1Q`–`8Q`, `xQ` | Lỗi QC (Westgard) | chỉ có ý nghĩa với `DQ` |
| `S` | Kết quả đã thay bằng lần chạy lại | |
| `e`, `c` | Dữ liệu đã bị người dùng sửa | lưu vết |
| `r` | Đã gửi lên host | |
| `/` | Chưa đo / đang chờ | không lưu |

Mẫu có cờ `#` hoặc `%`: máy **không** tự chạy lại nhưng **vẫn gửi `Rh`** (hỏi chỉ định chạy lại tự động)
để đăng ký — LIS phải trả lời đúng (A.1 *).

### 4.6 Phân biệt mẫu thường / QC / chuẩn

Theo **mã phân biệt** (`D␣`/`DH` bệnh nhân; `DQ` QC; `DA` chuẩn; `DR` blank) và tiền tố Sample No.
(`Q…`, `A…`, `R…`). Sample ID nằm ở trường Sample ID của phần cố định; với `DA`/`DQ` trường đó là
Calibrator ID / Control ID.

### 4.7 Xét nghiệm tính toán

Mã XN tính toán (vd LDL tính, A/G) LIS gửi trong chỉ định sẽ bị máy bỏ qua; máy tự tính và gửi khi đủ
thành phần, không gửi nếu thành phần thiếu hoặc có cờ `?`. Bilirubin toàn phần/trực tiếp là "sample blank
test": test blank không có Online Test No. Nguồn: 3.5(4); AU680 RM 2-85.

### 4.8 AU2700 / AU5400 — khác biệt so với AU680/AU480

Nguồn: *AU2700/5400 Online Specifications* (Olympus, phát hành 16/3/2000, sửa 8/6/2000). Cùng họ giao thức: khung
`[Start][2 ký tự phân biệt][Unit No. 0/2][dữ liệu][End][BCC]`, BCC = XOR từ mã phân biệt đến hết End code, Class A/B,
chia khối `0`…`9`/`E`, phiên `RB…RE` và `DB…DE`, bảng thời gian T1–T7/t1–t4. Những chỗ **khác**:

| Mục | AU680/AU480 (V8, 2010) | AU2700/AU5400 (2000) | Hệ quả cho driver |
|---|---|---|---|
| Mã phân biệt | `R␣ RH Rh`, `S␣ SH Sh SE`, `D␣ DH DR DA DQ d␣ dH` | **Không có `Rh`/`Sh`** (hỏi chạy lại tự động). `d␣`/`dH`: AU2700 = STAT fast, AU5400 = cấp cứu fast | Không chờ `Rh` |
| Số máy | "System No." 0/2 (Device No.) | "**Unit No.**" 0/2 (`00`–`99` hoặc dấu cách) | cùng vị trí |
| Online Test No. | 2 **hoặc 3** chữ số | **chỉ 2 chữ số** `01`–`99` | |
| Kết quả | 6/9 | 6/9 | giống |
| Cờ | 2 hoặc 8 ký tự; bảng 61 cờ, nhiều cờ 2 ký tự (`ph`, `Fx`, `1Q`…) | **luôn 2 ký tự** ("Flags digits is 2"); cờ là **1 ký tự** (bảng A.1, xem dưới) | Cách ghép khi có > 2 cờ: Tài liệu không nêu — cần đo trên máy thật |
| Sample type | `␣ U X Y W N` | `␣` huyết thanh, `U` nước tiểu, `X` khác; **có thể tắt hẳn trường** (PROTOCOL-SPECIAL) | hồ sơ cấu hình có cờ "có Sample type" |
| Dummy 4 dấu cách (bản tin kết quả) | luôn có | **bật/tắt được** | |
| Block Identification No. | luôn có | **bật/tắt được**; tắt thì máy tự đặt End code = ETX, dùng ETB | |
| Thứ tự Sample No. / Sample ID | cố định No. rồi ID | **đảo được** riêng cho text T.R.I. và text kết quả | hồ sơ cấu hình |
| Mỗi mục kết quả | `test No. + [thuốc thử] + kết quả + cờ` | `test No.(2) + [R1 lot, R1 bottle, R2 lot, R2 bottle] + [Cuvette side 1] + [ANL unit No. 1] + kết quả 6/9 + cờ 2` — ANL unit (`1` với AU2700) và Cuvette side (`0` trong/ISE, `1` ngoài) **bật/tắt được**; mục tính toán thì hai ô này là dấu cách. Thứ tự Cuvette side ↔ ANL unit trong hình và trong bảng ngược nhau — cần đo trên máy thật | |
| Lot/bottle thuốc thử | R1-1, R2-1 [R1-2, R2-2] | 4 ô "4*4" R1 lot, R1 bottle, R2 lot, R2 bottle (độ dài từng ô: Tài liệu không nêu rõ) | |
| Thông tin bệnh nhân 1–6 | 1–20 ký tự mỗi trường | bảng ghi 20; phiếu tham số cho đặt số chữ số → lấy theo cấu hình máy | |
| Pha loãng trong trả lời | `S␣` có Diluent type nếu bật; `SH` luôn có | `S␣` **không có**; `SH` có "Method" `0` thường / `1` pha loãng / `2` cô đặc sau mỗi mã | |
| Rack No. | 0/4/5 | 4/5; không đặt thì là `00000`; STAT là dấu cách | |
| LIH | mã 3 mức | như AU680; có phân tích LIH hay không phụ thuộc "LIH Measure" (All/Select/No) — chế độ Select cần LIS gửi mã LIH trong chỉ định | |
| Phiên hỏi Batch | | `R␣` và `RH` ở **phiên riêng** (Real-time thì trộn được) | |
| Retry Class B | 0–3, mặc định 1 | 0–3; mặc định Tài liệu không nêu | |
| T7 chuẩn | 0,5 s (bảng 4.2) | **2 s** | lấy theo màn hình máy |
| Ký tự, tốc độ mặc định | 7 bit, None, 1 stop, 9600 | Tài liệu không nêu (phiếu tham số để trống) — cần đọc trên máy | |
| Start/End code 2 | None/01–1F | 00–1F (`00` = không dùng) | |
| Chân RTS/CTS | "abbreviated" (không nối) | **nối tắt 7–8** ở đầu máy | cáp khác nhau |
| Mã lỗi ONLINE ERROR `aa` | 6031 | `01` device name, `02` framing, `03` overrun, `04` parity, `05` timeout, `06` nhận NAK, `07` BCC sai, `08` khác, `09` chạy song công, `10` unit name, `11` tham số, `12` hủy yêu cầu | |

Cờ AU2700/AU5400 (A.1, rút gọn): `(` không đủ dung dịch chống nhiễm chéo; `%` kim hút tắc; `?` dữ liệu quang bất thường;
`#` lỗi mức mẫu; `R` lỗi mức thuốc thử; `!` không tính được; `U`/`u`/`Y`/`y` OD thuốc thử ngoài giới hạn đầu/cuối; `@` kết quả
quá cao (OD > 2,5); `$` không đủ điểm kiểm tuyến tính; `D`/`B`/`*` lỗi phản ứng/tuyến tính; `&`/`Z` prozone; `F`/`G` trên/dưới
dải đo; `x`, `1`–`7` lỗi QC; `p` vượt ngưỡng báo động; `T` lỗi kiểm chéo giữa các XN; `P`/`N` trên/dưới khoảng quyết định;
`H`/`L` cao/thấp; `J`/`K` ngoài khoảng chạy lại; `S` được chọn chạy lại; `/` chưa đo; `r` đã gửi host; `e` đã sửa; `c` hiệu chỉnh
tay; `a` quá hạn ổn định trên máy; `b` chuẩn quá hạn/không có; `)` lô thuốc thử khác lô lúc chuẩn.
Lưu ý `P`/`N` ở AU2700 là **khoảng quyết định**, không phải "dương/âm tính" như cách hiểu ở bảng 4.5 *(so sánh của người viết)*;
AU2700 chỉ có `p` (không tách `ph`/`pl`); số `1`–`7` là cờ QC một ký tự.

Mã phân biệt mẫu trong alarm: `␣␣` thường, `␣E` cấp cứu, `␣P` STAT, `U␣/UE/UP` nước tiểu, `X␣/XE/XP` khác.

### 4.9 AU5800 (Online Spec v5.0, 2015) — khác biệt so với AU680/AU480

Nguồn: *AU5800 Online Specification for Version 5.0 or later* (Beckman Coulter K.K., D-160845, ấn bản 11, 17/4/2015).
Cùng khung, cùng mã phân biệt (`RB R␣ RH Rh RE`, `S␣ SH Sh SE`, `DB D␣ DH DR DA d␣ dH DQ DE`), cùng BCC, Class A/B, chia khối,
T1–T7/t1–t4 (chuẩn: T1 2 s, T4 2 s, T5 2 s, T6 1 s, **T7 2 s**). Khác:

| Mục | AU680/AU480 | AU5800 v5.0 | Hệ quả |
|---|---|---|---|
| Đường truyền | RS-232C (AU680 có thêm TCP/IP) | RS-232C, "1 channel (COM 1)"; 4800/9600; 7/8 bit | TCP/IP xem 2.1 |
| Loại mẫu | thường / cấp cứu / STAT (`P…`) | chỉ **Routine / Emergency**; không có STAT `P001`; `d␣`/`dH` = "Emergency quick output" | |
| Sample type | `␣ U X Y W N` | như AU680; khi cho trộn loại mẫu mà máy không xác định được → gửi `N`, **LIS phải trả loại cụ thể khác `N`** | |
| **Loading Time** (mới) | — | 14 ký tự `YYYYMMDDHHMMSS` — lúc máy đọc barcode; nằm cuối phần thông tin bệnh nhân (D␣/d␣) hoặc đầu phần biến đổi (DH/dH/DR/DA/DQ); bật/tắt ở [Online] | thêm vào hồ sơ cấu hình |
| Unit (mỗi mục) | — | chỉ có khi **nhiều bộ phân tích** (`1` Unit 1/ISE … `4`), bật/tắt được; máy 1 bộ: **không có trường** | |
| Cuvette (mỗi mục) | — | `0` trong (ISE) / `1` ngoài, bật/tắt được; mục tính toán là dấu cách | |
| Reagent Inf. | R1-1, R2-1 [R1-2, R2-2] | lot + bottle, mỗi ô 4 ký tự: R1 lot, R1 bottle, R2 lot, R2 bottle [R1-2 lot, R1-2 bottle] | độ dài ô = 4 (đã rõ) |
| Rack No. / Sample ID dài hơn số chữ số đặt | | Rack: đặt nhiều hơn → đệm 0; ít hơn → **cắt phần sau**. Sample ID: căn phải đệm dấu cách; ngắn hơn số đọc được → **cắt phần sau** | |
| Calibrator No. | 2/3 | 3 chữ số `001`–`200`, hoặc 2 ký tự theo bảng quy đổi `A0`…`K0` | |
| Control No. | 2/3 | `001`–`100` hoặc `01`…`A0` | |
| LIH | mức `0`–`6`, `9` | thêm **`7` ABN H, `8` ABN L** | |
| Giá trị khi đổi tham số ở System Maintenance mà quy đổi lỗi | | gửi **dấu cách** | không ép thành 0 |
| HbA1c máu toàn phần | | chỉ định chỉ gửi mã **HbA1c%**; kết quả bệnh nhân/QC chỉ có HbA1c% (ô thuốc thử chứa thông tin thuốc tiền xử lý); RB/ACAL chỉ có T-Hb và HbA1c | |
| Cờ (A.1, 62 cờ) | 61 cờ | thêm `d_` loại khỏi QC, `Wa` cuvette lỗi, `M` **trùng Sample ID**, `n_` chưa làm LIH, `l1`–`l5`/`i1`–`i5`/`h1`–`h5` (mức ảnh hưởng mỡ/vàng/tan), `E_` phản ứng quá mức, `Tx` T-Hb/HbA1c vượt dải, `bz` đường chuẩn prozone, `Va` kiểm đo lặp NG; `@_` là OD > 3,0 | `R_ #_ M` → máy không tự chạy lại nhưng **vẫn gửi `Rh`** |
| Chế độ "Automation Ready" (vd Power Processor) | | mẫu thường trên rack thường: máy hỏi `R␣` với Sample No. **cố định `0001`**; LIS trả `0001` (thường) hoặc `E001` (cấp cứu) để máy xếp loại; Sample No. trong `D␣` khác `R␣` → **LIS chỉ được khớp theo Sample ID** | |
| Thứ tự gửi kết quả real-time | | "Data Sort Mode": **Rack Set** (theo thứ tự nạp mẫu) hoặc **Measure Data** (xong mẫu nào gửi mẫu đó) | không giả định thứ tự |
| Realtime và Batch | | chọn Realtime vẫn làm Batch được (không khi đang truyền real-time); chọn Batch thì không có real-time | |
| Chân, cáp | RTS/CTS "abbreviated" | **RTS–CTS luôn nối tắt**; TxD 3, RxD 2, SG 5, FG; cáp bọc ≤ 15 m; đầu cáp phía máy **D-Sub 9 cái** | |

## 5. Hỏi chỉ định (hai chiều)

### 5.1 Phiên hỏi chỉ định

```
Máy:  RB → R␣ (mẫu 1) …………… R␣ (mẫu 2) …… RE
LIS:          S␣ (khối 0..E)         S␣ / SE        SE (theo hình 5.1)
```

| Mã | Chiều | Khi nào | Nguồn |
|---|---|---|---|
| `RB` | máy → LIS | Real-time: khi bắt đầu chạy từ Stand-by. Batch: khi bấm nhận ở màn [Test Requisition] | 5.1(1) B |
| `R␣` | máy → LIS | Real-time: **mỗi khi phát hiện cốc mẫu** và đọc được barcode (chế độ Sample ID) hoặc theo số mẫu; chỉ gửi nếu mẫu đó **chưa có chỉ định** trên máy | 5.1(1) B |
| `RH` | máy → LIS | Rack chạy lại: gửi bất kể đã có chỉ định chạy lại hay chưa | 5.1(1) B |
| `Rh` | máy → LIS | Chạy lại tự động (Real-time; không có ở Batch) | 5.1(1) B |
| `RE` | máy → LIS | Khi máy rời chế độ phân tích sang Stand-by/Stop, hoặc phiên bị hủy do lỗi | 5.1(1) B |
| `S␣` / `SH` / `Sh` | LIS → máy | Trả lời chỉ định, **trong thời hạn T1** sau khi máy gửi xong R* | 5.1(1) B |
| `SE` | LIS → máy | "Response stop": LIS không có thông tin cho mẫu này — **máy không đăng ký mẫu**; máy tiếp tục hỏi mẫu sau | 5.1(1) A C) a |

Hai cách báo "không có chỉ định" (hình 5.1 Example 3):
- trả `SE` → máy không đăng ký mẫu;
- trả `S␣` **không có mục xét nghiệm nào** → máy đăng ký mẫu "không có chỉ định", không chạy, không xuất kết quả.

Chế độ trên máy: [Online] > Set Up > "Test Requisition Information Receive" = Real-time / Batch / None,
riêng cho Routine Normal/Repeat, Emergency Normal/Repeat, STAT Normal/Repeat (A.3). Chế độ phân tích
[Analysis Mode]: **Sequential** (theo số thứ tự), **Rack No.** (Sample No. = (Rack ID − 1) × 10 + vị trí cốc,
AU640 UG B-3) hoặc **Sample ID** (đọc barcode). Khuyến nghị dùng chế độ Sample ID + Real-time.

### 5.2 Bản tin hỏi `R␣`

```
[STX] R␣ [System No. 0/2] [Rack No.] [Cup pos.] [Sample type] [Sample No.] [Sample ID] [ETX] [BCC]
```

`RH`: thay Sample No. bằng Repeat run sample No.; `Rh`: thay bằng Original sample No. Batch: Rack No. và
Cup position toàn dấu cách. Với AU480 "STAT repeat run", `RH` có Repeat run sample No. = `0000` — LIS tra
theo Sample ID và trả `SH` cũng với `0000` (3.5 *7). Nguồn: 3.2.

### 5.3 Bản tin trả lời `S␣`

Phần cố định (hình 3.3(1)) — **không có System No.**:

```
S␣ [Rack No.] [Cup pos.] [Sample type] [Sample No.] [Sample ID] [Dummy 4 dấu cách] [Data class. No.]
```

Phần biến đổi:

```
[Sex] [Year age] [Month age] [Patient info 1..6]      ← chỉ khi các mục này được bật ở [Requisition Format]
{ [Online test No. 2/3] [Diluent type 0/1] } × n
```

- **Phải chép nguyên** Rack No., Cup position, Sample No. từ bản tin `R␣` tương ứng (3.5(1)).
- Sample ID: chép nguyên, **hoặc** để toàn dấu cách (chế độ Sequential có đọc ID). Ở chế độ Sample ID phải
  trùng, sai → alarm 6042 ONLINE MISMATCH (5.1(2) A).
- Diluent type (nếu bật "Dilution Inf." — chỉ áp cho `S␣`; với `SH`/`Sh` **luôn có**): `0` thường, `1` pha loãng,
  `2` cô đặc.
- Mã XN tính toán, LIH với mẫu không phải huyết thanh, ISE với máu toàn phần, HbA1c với mẫu không phải
  máu toàn phần: máy bỏ qua (3.5(4)–(6),(10)).
- Năm tuổi trống nhưng tháng tuổi có giá trị → máy báo lỗi (3.5 *5).

`SH` (trả lời chạy lại): phần cố định thay Sample No. bằng Repeat run sample No., sau Sample ID là Original
sample No. (không có Dummy); phần biến đổi chỉ có danh sách `{test No. + Diluent type}`.

### 5.4 Lỗi phía máy khi LIS trả sai (A.5)

| Alarm | Nguyên nhân | Máy làm gì |
|---|---|---|
| 6031 ONLINE ERROR (aa)(bb cc dddd) | lỗi truyền: framing, overrun, parity, timeout, nhận NAK, **BCC sai**, ACK lỗi… | Stop: ngừng hỏi các mẫu sau đến lần chạy kế; Continue: hỏi mẫu kế |
| 6032 ONLINE FORMAT ERROR | thiếu trường bắt buộc theo cài đặt | bỏ bản tin, tiếp tục |
| 6033 ILLEGAL TEXT CODE | mã phân biệt không đúng | bỏ bản tin |
| 6034 ILLEGAL TEXT BLOCK No. | Data classification No. sai thứ tự | bỏ bản tin, Stop/Continue như 6031 |
| 6035 / 6036 | Sample No. / Rack No. ngoài phạm vi | bỏ bản tin |
| 6037 / 6038 | Giới / tuổi sai | bỏ bản tin |
| 6039 ANALYSIS METHOD MISMATCH | trả `S␣` cho `RH` hoặc ngược lại | bỏ bản tin |
| 6040 / 6041 | Sample No. / Rack-cup không khớp yêu cầu | bỏ bản tin |
| 6042 ONLINE MISMATCH | Sample ID không khớp | bỏ bản tin |
| 6043 TEST ITEM ERROR | Online test No. không tồn tại hoặc Diluent type sai | bỏ bản tin |
| 6044 REPEAT ITEM ERROR | mẫu gốc của chạy lại không tồn tại / đã chạy lại | bỏ bản tin |

Cảnh báo AU640 UG D-115: nếu bật Real-time T.R.I. Receive mà [System] > Test Requisition đặt **Sequential**,
máy có thể gán chỉ định kế tiếp của host cho mẫu sai → **kết quả gắn nhầm Sample ID**.

## 6. Bảng mã chỉ số

Mã máy gửi là **Online Test No.** — số kênh do người cài đặt gán cho từng xét nghiệm ở tab
"Online Test No." (2 chữ số `01`–`99` hoặc 3 chữ số `001`–`120`), **không phải mã chuẩn của hãng**.
Mỗi máy một bảng riêng; phải chụp màn hình tab này khi lắp.

Ví dụ ánh xạ đang dùng ở hệ cũ BVĐK 10 (`khao-sat/seed/anh-xa-ma-may.json`: AU640 = InsID 1 có 26 dòng /
24 mã kênh; AU2700 = InsID 15 có 26 dòng / 25 mã kênh). Chỉ trích các kênh tiêu biểu:

| Kênh | AU640 (InsID 1) | AU2700 (InsID 15) | Đơn vị |
|---|---|---|---|
| `01` | Glucose (`342GLU`) **và** Đường huyết sau ăn (`120GLUS`) | Glucose **và** Đường huyết sau ăn | mmol/L |
| `02` | Urea | **AST** | mmol/L / U/L |
| `03` | Creatinin | ALT | µmol/L / U/L |
| `08` | AST | Creatinin | |
| `09` | ALT | HDL-C | |
| `14` | Acid uric | Bilirubin TP | µmol/L |
| `18` | HbA1c | HbA1c | % |
| `19` | **LDL-C (`128LDL`) và Nồng độ cồn (`244ACo`)** | — | mmol/L / mg/100mL |
| `35` | CRP định lượng | — | mg/L |
| `99` | — | Calci TP | mmol/L |

Đầy đủ: AU640 dùng kênh 01–05, 07–16, 18–22, 25, 35, 47, 59; AU2700 dùng 01–18, 21, 22, 24–26, 30, 99.
Hai máy **cùng tên chỉ số nhưng khác số kênh** (vd AST là `08` trên AU640, `02` trên AU2700) → bảng map
phải khóa theo (máy, kênh).

## 7. Bản tin mẫu

Dữ liệu giả. Cấu hình tham chiếu: Class B, BCC bật, không System No., Rack No. 4 chữ số, Sample ID 10
chữ số, không giới/tuổi/thông tin BN, không thông tin thuốc thử, Online Test No. 2 chữ số, kết quả 6 chữ số
có zero suppress, cờ 2 loại (2 ký tự), không Dilution Inf. `<BCC>` ghi giá trị hex của **1 byte**.

### 7.1 Gửi kết quả

```
Máy → LIS:  <STX>DB<ETX><BCC=05>
LIS → máy:  <ACK>
Máy → LIS:  <STX>D 001203 01232611030001    E01  5.42  03  86.0H 08  25.0  <ETX><BCC=72>
LIS → máy:  <ACK>
Máy → LIS:  <STX>DE<ETX><BCC=02>
LIS → máy:  <ACK>
```

Giải mã dòng `D␣` (offset tính từ byte sau STX):

| Offset | Dài | Giá trị | Trường |
|---|---|---|---|
| 0 | 2 | `D␣` | mẫu thường |
| 2 | 4 | `0012` | Rack No. |
| 6 | 2 | `03` | Cup position |
| 8 | 1 | `␣` | huyết thanh |
| 9 | 4 | `0123` | Sample No. |
| 13 | 10 | `2611030001` | Sample ID (barcode giả) |
| 23 | 4 | `␣␣␣␣` | Dummy |
| 27 | 1 | `E` | khối cuối |
| 28 | 2+6+2 | `01` `␣␣5.42` `␣␣` | kênh 01 = 5,42, không cờ |
| 38 | 2+6+2 | `03` `␣␣86.0` `H␣` | kênh 03 = 86,0, cờ H |
| 48 | 2+6+2 | `08` `␣␣25.0` `␣␣` | kênh 08 = 25,0 |

### 7.2 Hỏi chỉ định theo barcode

```
Máy → LIS:  <STX>RB<ETX><BCC=13>
LIS → máy:  <ACK>
Máy → LIS:  <STX>R 001203 01232611030001<ETX><BCC=57>
LIS → máy:  <ACK>
LIS → máy:  <STX>S 001203 01232611030001    E010308<ETX><BCC=19>     (chỉ định kênh 01, 03, 08)
Máy → LIS:  <ACK>
…
Máy → LIS:  <STX>RE<ETX><BCC=14>
LIS → máy:  <ACK>
```

Không có chỉ định: trả `<STX>SE<ETX><BCC=15>` (máy bỏ qua mẫu) hoặc
`<STX>S 001203 01232611030001    E<ETX><BCC=13>` (đăng ký mẫu rỗng).

## 8. Bẫy và lưu ý khi viết driver

1. **BCC là 1 byte nhị phân bất kỳ** (00–FF) và có thể trùng ký tự điều khiển: `DE` → BCC `0x02` (STX),
   `SE` → `0x15` (NAK), `RB`/`S…` → `0x13` (DC3). Bộ tách khung **phải đọc đúng 1 byte sau End code** khi bật
   BCC, không được coi byte đó là đầu khung mới hay là NAK. Khi tắt BCC thì không có byte này.
2. **Bố cục phụ thuộc cài đặt**: lưu hồ sơ cấu hình theo máy (có Rack No.? số chữ số Rack 4/5; Sample ID
   4–26; Sex; Age; Patient info 1–6 và độ dài; Reagent Inf.; R1-2/R2-2; Result 6/9; flag 2/8; test No. 2/3;
   Dilution Inf.; System No.). Tính offset từ hồ sơ, không cứng số.
3. **Sample ID căn phải đệm dấu cách** → phải `trim`. Barcode ngắn hơn độ dài cài đặt vẫn đúng; barcode dài
   hơn bị máy cắt *(suy luận)* — đặt Sample ID Digits ≥ độ dài barcode của LIS.
4. **Bản tin `S*` không có System No.** dù máy bật Device No. — đừng sinh nhầm.
5. **Sample type huyết thanh là dấu cách**, không phải ký tự chữ — dễ bị `trim` mất làm lệch cột.
6. **Giá trị `999999`/`999999999` = vượt định dạng**, không phải kết quả. Mục LIH là mã 3 mức, không phải số.
7. **Chế độ 2 cờ chỉ giữ ký tự đầu** → `p` có thể là `ph` hoặc `pl`, `F` có thể là `F` hoặc `Fx`. Muốn đủ
   thông tin, cài "No. of Data Marks = 4 types".
8. **Kênh trùng trên cùng máy** (hệ cũ): AU640 kênh `19` map cả LDL-C (`128LDL`) và Nồng độ cồn (`244ACo`);
   kênh `01` map cả Glucose và Glucose sau ăn. Hệ cũ gỡ bằng `TOP 1` theo chỉ định của BN rồi theo alphabet
   → **kết quả có thể về nhầm XN mà không cảnh báo**. Driver mới: một (máy, kênh) → một chỉ số; nếu cần
   phân biệt, dựa vào chỉ định của mẫu và báo lỗi khi mơ hồ.
9. **AU640 và AU2700 khác độ dài trường giá trị** (xem thêm 4.8: AU2700 có thể chèn Cuvette side + ANL unit 2 byte
   trước kết quả — 2 + 6 + 2 = **10** khớp với "giá trị dài 10" của hệ cũ; AU640 13 = 2 + 9 + 2 nếu kết quả 9 chữ số *(cả hai
   đều suy luận)*): hệ cũ đọc AU640 "mã XN byte 0 dài 2, SID byte 24 dài 9,
   giá trị byte 44 dài 13", AU2700 giống nhưng giá trị dài **10** (`lis-agent/src/drivers.ts`,
   `Instrument.json`). Đối chiếu với đặc tả AU680: SID byte 24 dài 9 khớp với Sample ID **20 ký tự** căn phải
   (byte 13–32) và barcode 9 số, khi không có System No., Rack 4 chữ số *(suy luận)*. Còn "mã XN byte 0" và
   "giá trị byte 44" không khớp trực tiếp với bố cục AU680 — có thể hệ cũ tách phần biến đổi thành từng mục
   trước khi cắt *(suy luận)*. **Cần bản ghi thô từ AU640/AU2700 để chốt.** Không dùng chung bố cục hai máy.
10. **Mặc định 7 bit** trên AU480/AU680/AU5800, còn hệ cũ chạy 8N1 — sai một bit là rác toàn bộ.
11. **Real-time + Sequential** có thể gán nhầm chỉ định (AU640 UG D-115). Dùng chế độ Sample ID.
12. Trả lời chậm hơn T1 (~2 s) → máy coi lỗi (6031, timeout). Tra chỉ định phải nhanh (cache theo barcode).
13. Khi "T.R.I. Receive Error Control" = Stop, một lỗi làm máy **ngừng hỏi** cho tới lần chạy sau → mọi mẫu sau
    phải nhập tay. Khuyến nghị đặt **Continue**.
14. Kết quả chạy lại (`DH`) đi kèm Original sample No.; LIS phải quyết định ghi đè hay giữ cả hai (cờ `S`).
15. AU680 có thể nối hệ tự động hóa (LAS) — một số quy tắc chạy lại khác AU480 (3.5 *3); AU480 không có LAS.
16. Màn hình AU640 gọi là "T.R.I. Receive" (Test Requisition Information) — cùng khái niệm "Test Requisition
    Information Receive" ở AU480/680.

## 9. Kiểm thử

- **Bộ bản tin cần có** (dựng giả theo mục 7, sau đó thay bằng bản ghi thô từ máy):
  `DB`/`DE`; `D␣` một khối; `D␣` nhiều khối (`0`,`1`,…,`E`) với Text Length 256; `DH`; `DQ`; `DA`; `DR`;
  `d␣`; giá trị âm, zero suppress bật/tắt, `999999`, LIH, OD; cờ 2 loại và 4 loại; Sample ID ngắn/dài;
  STAT (Rack toàn dấu cách); mẫu nước tiểu (`U`).
- **Mô phỏng máy**: cặp cổng COM ảo (com0com trên Windows) + công cụ phát bản tin theo kịch bản, tính BCC
  bằng XOR, hỗ trợ Class A và Class B, chỉnh T1/T4.
- **Tình huống lỗi**: BCC sai (LIS phải NAK ở Class B); BCC = `0x02`/`0x15`; mất ETX (timeout T2); máy gửi lại
  sau NAK (cùng nội dung, không ghi trùng); LIS trả `S␣` muộn > T1; trả `SE`; trả `S␣` không mục XN;
  Online test No. không có trong bảng map; kênh trùng (kênh 19); hai máy khác bảng kênh.
- **Đối chiếu máy thật**: chụp màn [Online] (cả 4 tab) và [Requisition Format] của từng máy, ghi vào hồ sơ
  cấu hình; ghi bản thô ≥ 1 ngày chạy (cả QC) để làm bộ hồi quy.

## 10. Nguồn

| Tài liệu | Phiên bản | Dùng cho | Nơi lấy |
|---|---|---|---|
| **AU680/AU480 Online Specifications** (Beckman Coulter) | 15/10/2010, bản V8, 55 trang | **Nguồn chính**: mục 2 (2.1–2.3), 3 (3.2–3.6), 4 (4.1–4.2), 5 (5.1–5.3), 6, A.1, A.3–A.5 `tai-lieu-hang/Beckman_AU680_AU480_Online_Specifications_2010.pdf` (không commit vào repo) |
| AU480 User's Guide, PN B28624AA (12/2013) | | 4.12.1 Program Online Parameters with RS232C: giá trị mặc định cổng, Class, BCC, T1–T7 | https://www.jantdx.com/wp-content/uploads/2024/05/AU480-user-guide-compressed.pdf (tài liệu hãng, nhà phân phối đăng lại) |
| AU680 Reference Manual, B63185AA (07/2015) | | Online Menu tr. 2-79–2-90: RS-232C và TCP/IP, HOST Direction | https://www.jantdx.com/wp-content/uploads/2024/05/AU680-Reference-Manual-compressed.pdf |
| **AU2700/AU5400 Online Specifications** (Olympus Optical) | phát hành 16/3/2000, sửa 8/6/2000, 65 trang | Toàn bộ mục 4.8; chân nối AU2700 (6.1–6.4); bảng cờ A.1; tham số A.2; alarm A.5 | `tai-lieu-hang/Olympus_AU2700_AU5400_Online_Specifications_2000.pdf` |
| AU5800 Reference Manual, B81389AA (10/2015) | | Online Menu tr. **2-75–2-85**: RS-232C (Setup/Protocol/Format Configuration/Online Test No.), TCP/IP (Realtime/Host Direction/Batch, T1–T4, Device No./HOST ID 32 ký tự) — mục 2.1 | `tai-lieu-hang/Beckman_AU5800_Reference_Manual_B81389AA.pdf` |
| **AU5800 Online Specification for Version 5.0 or later** (Beckman Coulter K.K., D-160845) | ấn bản 11, 17/4/2015, 61 trang | Mục 4.9; 2 (khung), 3 (bản tin), 4 (Class A/B, T1–T7), 5 (phiên), 6 (chân), A.1 cờ, A.3–A.5 | `tai-lieu-hang/Beckman_AU5800_Online_Specification_v5.0_2015.pdf` |
| AU5800 IFU A98352AC | | Lịch sử phiên bản: TCP/IP thêm từ phần mềm 4.11 (A98352AB, 10/2012); sơ đồ nối "TCP/IP cable or RS232C cable" (A-5) | `tai-lieu-hang/Beckman_AU5800_IFU_A98352AC.pdf` (chưa chuyển vào `tai-lieu-hang/`) |
| AU640 Service Manual (Olympus) | | B2-3, **B2-11** (online RS232C 1 hệ), D1-1 (COM3 = On-line), E2 (xử lý hỏi real-time), E23 alarm 2131 | `tai-lieu-hang/Olympus_AU640_Service_Manual.pdf` |
| AU640/AU640e User's Guide Vol 1 | 2002–2009 | B-3 (Real-time/Batch online), C-29–C-30 (tải chỉ định từ host), D-115 (màn [Online], cảnh báo Sequential) | `tai-lieu-hang/Beckman_AU640_Users_Guide_Vol1.pdf` |
| Khảo sát hệ LIS cũ BVĐK 10 | | AU640 InsID 1 (COM6), AU2700 InsID 15 (COM5), 9600 8N1, bố cục byte cũ, ánh xạ kênh | `lis-tham-khao/khao-sat/lis-hien-huu.md`, `seed/du-lieu/Instrument.json`, `seed/anh-xa-ma-may.json`, `lis-agent/src/drivers.ts` |

Bản scan "AU680 AU480 Instrument Online Specification Jan1-2011v9" có trên kupdf/pdfcoffee/Scribd — không dùng
(nguồn không chính thống); bản V8 người dùng cung cấp đã đủ.

**Còn thiếu — cần xin Beckman Coulter / nhà phân phối:**
- *AU640 Online Specifications* (hoặc *AU400/AU640 Online Specifications* của Olympus) — để chốt bố cục AU640 (hiện suy theo AU2700).
- ~~AU2700 / AU5400 Online Specifications~~ — đã có (bản 2000). Nếu máy BVĐK 10 chạy phần mềm mới hơn của Beckman, xin bản tương ứng.
- ~~AU5800 Online Specification for Version 5.0 or Later~~ — đã có (ấn bản 11, 2015), nhưng **chỉ có RS-232C**.
- *AU680 / AU5800 TCP/IP online specification* (**còn thiếu, quan trọng nhất**): vai trò client/server, cổng, khung khi Start/End code = None, bản tin Equipment Status/Host Direction.
- Phiếu "AU680/AU480 online condition parameter sheet" (A.4) điền sẵn của từng máy tại bệnh viện.
