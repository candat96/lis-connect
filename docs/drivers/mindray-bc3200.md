# Mindray BC-3200 (họ BC-2300 / BC-2600 / BC-2800 / BC-3000Plus / BC-3200) — huyết học 3 thành phần bạch cầu

> Trạng thái tài liệu: ✅ đủ để viết driver (Phụ lục D sổ tay BC-3200 bản tiếng Tây Ban Nha có bảng trường đầy đủ; đối chiếu được với phụ lục D BC-3000Plus tiếng Trung và dữ liệu thô BC-3000Plus/BC-2300). Còn một điểm phải đo trên máy thật: có trường **giây** hay không (xem §8).
> Kiểm chứng: chưa đối chiếu máy BC-3200 thật. Bố cục trường đã khớp từng byte với 6 bản ghi thô BC-3000Plus (10ID) và 2 bản ghi thô 8ID (kho tham khảo, không phải của BVĐK 10).
> Driver: `mindray-bc3k` — họ giao thức: `fixed-offset` (khung riêng của Mindray, chữ số cố định độ rộng, **không phải ASTM E1394**)

## 1. Tóm tắt

- Máy đếm tế bào máu 3 thành phần bạch cầu (WBC/LYM/MID/GRAN), 19 chỉ số + 3 histogram (WBC, RBC, PLT; mỗi cái 256 kênh).
- Nối qua **RS-232 (DB9, cổng nối tiếp số 2)**, **chỉ một chiều**: máy đẩy kết quả, LIS không gửi chỉ định được.
- Khung truyền là **khối chữ số độ rộng cố định** mở bằng `<STX>` (hoặc `<ENQ>` khi bật xác nhận) và đóng bằng `<EOF>` 0x1A (hoặc `<EOT><ETX>`). Không có record H/P/O/R, không có `|`, không có checksum.
- **LIS cũ khai máy này là "ASTM" — sai theo tài liệu hãng.** Có thể trạm cũ có bộ đọc riêng nhưng ghi nhãn ASTM *(suy luận)*. Việc 54 kết quả cũ đều có SID rỗng khớp với giao thức này: máy chỉ gửi **số ID 8 hoặc 10 chữ số** do người dùng gõ, không có barcode, không có trường SID kiểu ASTM.
- Điểm khó: không có dấu phân cách — đọc sai một độ rộng là lệch toàn bộ phần sau; có biến thể 8ID/10ID và (theo tài liệu BC-3200) có thể thêm trường giây.

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | RS-232, đầu DB9, **cổng nối tiếp số 2** của máy | BC-3200 D.2; BC-3000Plus D.1 |
| Dây nối | Chỉ 3 sợi: chân 2 (RXD), 3 (TXD), 5 (GND). Dài tối đa 12 m | BC-3200 D.2 |
| Data bits / stop bits | **7 data bit, 1 stop bit** (cố định với giao thức 8ID/10ID) | BC-3200 D.4.1; BC-3000Plus D.4.1 |
| Tốc độ baud | Tài liệu BC-3200 chỉ dẫn sang chương 5.6 (không có trong phụ lục). Máy cùng họ BC-1800/BC-2600: chọn 1200 / 2400 / 4800 / 9600 / 19200, **mặc định 9600** *(suy luận cho BC-3200)* | sổ tay BC-1800 §5.5.2, BC-2600 §5.3.2 |
| Parity | Máy cùng họ: lẻ / chẵn / không, **mặc định không** *(suy luận cho BC-3200)* | như trên |
| Bắt tay phần cứng | Không (chỉ nối 3 sợi) | BC-3200 D.2 |
| Cấu hình thực tế đã thấy | BC-2300 tại một bệnh viện Trung Quốc: `9600,7,N,1`, tự động gửi = bật, xác nhận (握手) = tắt | `tai-lieu-hang/Mindray_giao-thuc-goc_CN/血常规/迈瑞bc2300血球联机原始数据/bc2300联机设置.txt` |
| Menu trên máy | **Configuración → Transmisión** (Cài đặt → Truyền thông): tốc độ, parity, "Solic. confir." (xác nhận/握手), tự động gửi | BC-3200 D.4.1 |
| Chọn 8ID / 10ID | Mặc định **10 chữ số**; đổi sang 8 phải nhờ kỹ sư Mindray | BC-3200 D.3.2 (ghi chú) |
| TCP | Không có. Muốn qua mạng thì dùng bộ chuyển RS-232 ↔ TCP (Moxa…) *(suy luận)* | — |

## 3. Khung truyền và bắt tay

Ký tự điều khiển (BC-3200 D.3.1):

| Ký hiệu | Hex | Ý nghĩa |
|---|---|---|
| `<ENQ>` | 0x05 | Máy mời truyền (chỉ khi bật xác nhận) |
| `<STX>` | 0x02 | Mở khối (khi **tắt** xác nhận) |
| `<EOT>` | 0x04 | Hết khối (khi bật xác nhận) |
| `<EOF>` | 0x1A | Hết khối (khi **tắt** xác nhận) |
| `<ETX>` | 0x03 | Máy hỏi "nhận được chưa" (khi bật xác nhận) |
| `<ACK>` / `<NACK>` | 0x06 / 0x15 | LIS trả lời |
| `A` / `B` / `C` | 0x41/0x42/0x43 | Loại khối: kết quả mẫu / QC L-J chuẩn / QC L-J đang chạy |
| `#` | 0x30–0x39 | Một chữ số |
| `*` | 0x2A | Giá trị bị gắn cờ / ô trống (thay cho chữ số) |

**Chế độ tắt xác nhận ("Solic. confir." = tắt — dữ liệu thô đều ở chế độ này):**

```
<STX> A … (thân khối) … <EOF>
```
Máy gửi xong là thôi, không đọc gì từ LIS. LIS không được gửi ACK (gửi cũng bị bỏ qua).

**Chế độ bật xác nhận:**

1. Máy gửi `<ENQ>`, chờ 4 s. Không có `<ACK>` → gửi lại `<ENQ>`, chờ 4 s nữa; vẫn không → báo lỗi truyền. Nhận ký tự khác `<ACK>` → gửi lại `<ENQ>` (tối đa 2 lần `<ENQ>`).
2. Nhận `<ACK>` → máy gửi thân khối (bắt đầu bằng `A`/`B`/`C`, **không có `<STX>`** *(suy luận: bảng trường ghi `[ENQ]` thay chỗ `[STX]`)*), rồi `<EOT>`, rồi `<ETX>`.
3. Máy chờ 4 s sau `<ETX>`: `<ACK>` → xong; `<NACK>` → gửi lại từ bước 3 (thân khối); ký tự khác → gửi lại `<ETX>`; im lặng 4 s → gửi lại `<ETX>`, thêm 4 s nữa thì báo lỗi.

- Checksum: **không có**. Đã kiểm bằng script: thân khối dài đúng bằng tổng độ rộng các trường, không có byte thừa trước `<EOF>` (§9).
- Độ dài khối (không kể ký tự điều khiển): 10ID = **2464 ký tự**, 8ID = **2452 ký tự** (đo trên dữ liệu thô BC-3000Plus/BC-2300). Nếu BC-3200 có trường giây thì +2 (2466 / 2454) — phải đo.
- Một khối = một mẫu. Gửi lại nhiều mẫu (từ màn Revisar) là nhiều khối nối tiếp.
- Tài liệu bản Tây Ban Nha in nhầm "Si la solicitud de confirmación está activada [STX]/[EOF]" — đúng ra là *desactivada* (đối chiếu bản tiếng Trung: 如果不选择握手).

## 4. Bản tin kết quả (máy → LIS)

Khối `A` — kết quả mẫu. Vị trí tính từ ký tự `A` (vị trí 0), giá trị là chữ số ASCII, **không truyền dấu thập phân**: số chữ số = số dấu `#` trong mẫu định dạng, chia theo số chữ số sau dấu chấm.

| Vị trí (10ID) | Độ rộng | Trường | Định dạng | Ghi chú |
|---|---|---|---|---|
| 0 | 1 | Loại khối | `A` | |
| 1 | 2 | Phiên bản | `##` | Dữ liệu thô: chữ `AA` (không phải số) |
| 3 | 3 | Độ dài ID | `###` | Dữ liệu thô: `I10` (10ID), `I08` (BC-2300) |
| 6 | 3 | Số tham số | `###` | Dữ liệu thô: `P19` = 19 chỉ số |
| 9 | 2 | Số mô tả định dạng | `##` | Dữ liệu thô BC-3000Plus: `00`; BC-2300: `01` kèm vài ký tự mô tả — chưa rõ cấu trúc |
| 11 | 10 | ID mẫu | `##########` | Số do người dùng nhập, đệm 0 bên trái |
| 21 | 1 | Chế độ mẫu | `#` | Theo bản tiếng Trung (15ID): 0 máu toàn phần, 1 pha loãng trước… *(suy luận áp cho 8/10ID)* |
| 22 | 2 | Tháng | `##` | **Thứ tự tháng–ngày–năm** |
| 24 | 2 | Ngày | `##` | |
| 26 | 4 | Năm | `####` | |
| 30 | 2 | Giờ | `##` | |
| 32 | 2 | Phút | `##` | |
| (34) | (2) | Giây | `##` | **Chỉ có trong tài liệu BC-3200**; không có trong bản BC-3000Plus và dữ liệu thô. Nếu có, mọi vị trí phía sau +2 |
| 34 | 4 | WBC | `###.#` | ×10⁹/L |
| 38 | 4 | Lymph# | `###.#` | |
| 42 | 4 | Mid# | `###.#` | |
| 46 | 4 | Gran# | `###.#` | |
| 50 | 3 | Lymph% | `##.#` | |
| 53 | 3 | Mid% | `##.#` | |
| 56 | 3 | Gran% | `##.#` | |
| 59 | 3 | RBC | `#.##` | ×10¹²/L. Bản BC-3200 in `##.#` ở mục mẫu nhưng `#.##` ở mục QC; bản tiếng Trung và dữ liệu thô (3,81 hợp với HCT/MCV) cho `#.##` |
| 62 | 3 | HGB | `###` | g/L |
| 65 | 4 | MCHC | `####` | g/L |
| 69 | 4 | MCV | `###.#` | fL |
| 73 | 4 | MCH | `###.#` | pg |
| 77 | 3 | RDW-CV | `##.#` | % |
| 80 | 3 | HCT | `##.#` | **%** (không phải L/L) |
| 83 | 4 | PLT | `####` | ×10⁹/L |
| 87 | 3 | MPV | `##.#` | fL |
| 90 | 3 | PDW | `##.#` | không đơn vị |
| 93 | 3 | PCT | `.###` | % (ví dụ `154` = 0,154 %) |
| 96 | 4 | RDW-SD | `###.#` | fL |
| 100 | 12 | Dự phòng | | Toàn `0` trong dữ liệu thô |
| 112 | 8 | Cờ histogram | 8 × `#` | Rm, R1, R2, R3, R4, Pm, rồi **Ps, Pl** (bản BC-3200) hoặc **Pl, Ps** (bản tiếng Trung) — thứ tự 2 cờ cuối phải xác nhận |
| 120 | 24 | Vạch phân vùng L1–L8 | 8 × `###` | Số kênh 0–255. L1–L4 trên histogram WBC, L5–L6 RBC, L7–L8 PLT |
| 144 | 16 | Dự phòng | | |
| 160 | 768 | Histogram WBC | 256 × `###` | Mỗi kênh 3 chữ số, 0–255 |
| 928 | 768 | Histogram RBC | 256 × `###` | |
| 1696 | 768 | Histogram PLT | 256 × `###` | |
| 2464 | | Hết | | rồi `<EOF>` hoặc `<EOT><ETX>` |

- **8ID:** bỏ 4 trường đầu (phiên bản, độ dài ID, số tham số, số mô tả); `A` + ID 8 chữ số, rồi từ "Chế độ mẫu" trở đi giống hệt. Nghĩa là vị trí 8ID = vị trí 10ID − 12. Đã thấy trong dữ liệu thô (bản tiếng Trung ghi 4 trường đầu "chỉ truyền ở 10ID"; bản BC-3200 không ghi chú này).
- **Mã mẫu:** chỉ có ID số (vị trí 11, 10 ký tự). Không có barcode, không có họ tên. Để ghép với phiếu chỉ định, LIS phải dùng **số thứ tự mẫu trong ngày** + ngày giờ chạy *(suy luận)*.
- **Mẫu thường / QC:** phân biệt bằng ký tự loại khối: `A` mẫu, `B` QC L-J chuẩn (thông tin lô), `C` kết quả QC L-J đang chạy. LIS chỉ cần lưu `A`; `B`/`C` ghi nhật ký hoặc bỏ qua.
- **Cờ bất thường:** không có cờ H/L trong bản tin. Chỉ có:
  - ký tự `*` thay chữ số khi giá trị bị máy gắn cờ/không tính được (BC-3200 D.3.1) — driver lưu kết quả dạng chữ "*" và cờ "nghi ngờ", không đổi thành số;
  - 8 cờ histogram (`0` = không báo động *(suy luận)*). H/L phải do LIS tự so với khoảng tham chiếu.

Khối `B` (QC L-J chuẩn): `B`, số tệp `#`, số lô `######`, tháng `##`, ngày `##`, năm `####`, rồi giá trị đích WBC, RBC, HGB, PLT, Lymph#, Lymph%, Gran#, Gran%, HCT, MCV, MCH, MCHC, rồi giới hạn cùng thứ tự. Lô/ngày bỏ trống thì truyền `*`.
Khối `C` (QC L-J đang chạy): `C`, tháng, ngày, năm, giờ, phút, rồi 12 chỉ số như trên (không có Mid, RDW, PLT phụ). Không có ID mẫu.

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

**Chỉ một chiều.** Tài liệu không có bản tin nào từ LIS sang máy ngoài `<ACK>`/`<NACK>`. Máy gửi tự động sau mỗi lần đếm (nếu bật "tự động gửi") hoặc gửi tay từ màn Revisar / bảng QC L-J.

## 6. Bảng mã chỉ số

Máy **không gửi mã** — chỉ số được nhận diện theo vị trí. Driver tự đặt mã sau (mã cột 1 là đề xuất cho màn "map máy ↔ chỉ số"; cột "Mã LIS cũ" là mã trạm cũ ở BVĐK 10 đã dùng).

| Mã driver | Mã LIS cũ | Tên | Đơn vị máy gửi | Chia cho | Ghi chú |
|---|---|---|---|---|---|
| `WBC` | WBC | Bạch cầu | 10⁹/L | 10 | |
| `LYM#` | LYM# | Lympho tuyệt đối | 10⁹/L | 10 | |
| `MID#` | MID# | Tế bào trung gian tuyệt đối | 10⁹/L | 10 | |
| `GRAN#` | GRAN# | Bạch cầu hạt tuyệt đối | 10⁹/L | 10 | |
| `LYM%` | LYM | Lympho % | % | 10 | Mã cũ không có `%` |
| `MID%` | MID | Trung gian % | % | 10 | |
| `GRAN%` | GRAN | Bạch cầu hạt % | % | 10 | |
| `RBC` | RBC | Hồng cầu | 10¹²/L | 100 | |
| `HGB` | HGB | Huyết sắc tố | g/L | 1 | |
| `MCHC` | MCHC | Nồng độ HGB trung bình | g/L | 1 | |
| `MCV` | MCV | Thể tích HC trung bình | fL | 10 | |
| `MCH` | MCH | Lượng HGB trung bình | pg | 10 | |
| `RDW-CV` | RDW | Độ phân bố HC (CV) | % | 10 | |
| `HCT` | HCT | Hematocrit | % | 10 | LIS cũ hiển thị L/L — đổi đơn vị ở tầng chuẩn hóa, không ở driver |
| `PLT` | PLT | Tiểu cầu | 10⁹/L | 1 | |
| `MPV` | MPV | Thể tích TC trung bình | fL | 10 | |
| `PDW` | PDW | Độ phân bố TC | — | 10 | |
| `PCT` | PCT | Thể tích khối TC | % | 1000 | |
| `RDW-SD` | RDW-SD | Độ phân bố HC (SD) | fL | 10 | |

Đúng 19 chỉ số — khớp trường "số tham số" `P19` và 19 mã mà trạm cũ nhận. Không có P-LCR (chỉ có ở giao thức 15ID+2 của BC-2600/BC-3000Plus, BC-3200 không có giao thức này).

## 7. Bản tin mẫu

Dữ liệu **giả** (ID 123, ngày 03/11/2026 08:30), chế độ tắt xác nhận, 10ID, không có trường giây. Ba histogram rút gọn (thực tế mỗi cái đủ 768 chữ số).

```
<STX>AAAI10P1900000000001230110320260830006800210005004230907461745213503330897029913140502360911582150452000000000000000000000110500742550252200162360000000000000000
000000001003…(768 chữ số WBC)…000
000000002005…(768 chữ số RBC)…000
000000004009…(768 chữ số PLT)…000<EOF>
```
(Xuống dòng chỉ để dễ đọc — bản tin thật **không có CR/LF**.)

Giải mã phần đầu: `A` | `AA` | `I10` | `P19` | `00` | `0000000123` | `0` | `11` `03` `2026` `08` `30` | WBC `0068`=6,8 | LYM# `0021`=2,1 | MID# `0005`=0,5 | GRAN# `0042`=4,2 | LYM% `309`=30,9 | MID% `074`=7,4 | GRAN% `617`=61,7 | RBC `452`=4,52 | HGB `135` | MCHC `0333` | MCV `0897`=89,7 | MCH `0299`=29,9 | RDW-CV `131`=13,1 | HCT `405`=40,5 | PLT `0236` | MPV `091`=9,1 | PDW `158`=15,8 | PCT `215`=0,215 | RDW-SD `0452`=45,2 | dự phòng 12×`0` | cờ `00000000` | L1–L8 `011 050 074 255 025 220 016 236` | dự phòng 16×`0`.

Chế độ bật xác nhận (giả):
```
máy → <ENQ>
LIS → <ACK>
máy → AAAI10P19…(2464 ký tự)…<EOT><ETX>
LIS → <ACK>
```

## 8. Bẫy và lưu ý khi viết driver

1. **Không phải ASTM.** Đừng gắn vào bộ đọc `astm-e1394` dùng chung. Không có `H|`, không có checksum, không có frame số.
2. **Trường giây:** tài liệu BC-3200 có `Segundos ##`, bản BC-3000Plus và mọi dữ liệu thô thì không. Driver nên **nhận diện theo độ dài thân khối**: 2464/2452 → không có giây; 2466/2454 → có giây. Độ dài khác → báo lỗi, lưu thô, không đoán.
3. **8ID vs 10ID:** nhận diện bằng ký tự thứ 2: `A` theo sau là `AA…I10` → 10ID; là chữ số → 8ID *(suy luận từ dữ liệu thô)*. Trường "độ dài ID" (`I10`/`I08`) nên đọc để cắt ID thay vì cố định 10.
4. **Trường "số mô tả định dạng" khác `00`** (BC-2300 gửi `01` + vài ký tự) → bố cục phía sau lệch. Chưa có tài liệu mô tả; gặp thì từ chối và lưu thô.
5. **Ký tự `*`** có thể xuất hiện ở bất kỳ ô số nào → không `parseInt` mù.
6. **Ngày theo thứ tự tháng–ngày–năm**, năm 4 chữ số. Giờ máy thường lệch — lấy giờ nhận làm mốc phụ.
7. **7 bit dữ liệu**: cổng phía LIS phải mở 7 data bit, nếu mở 8N1 sẽ nhận rác khi máy để parity khác "không".
8. **Nhiều khối liền nhau** khi gửi lại hàng loạt: tách theo `<STX>`/`<EOF>` (hoặc `<ENQ>`/`<ETX>`), không theo độ dài đọc được từ một lần `read()`. Bộ dữ liệu "少一项接到的数据" (tiếng Trung: "nhận thiếu một mục") gồm 6 khối 2466 byte liên tiếp, một khối lặp lại — cho thấy gửi lại thì **trùng mẫu**; driver phải chống trùng theo (ID, ngày giờ).
9. **ID trùng giữa các ngày:** ID là số thứ tự người dùng nhập, máy tự tăng; reset theo ngày tùy cách dùng. Ghép chỉ định phải kèm ngày.
10. **Hai cờ Pl/Ps** đảo thứ tự giữa hai bản tài liệu — chỉ lưu thô 8 cờ, chưa diễn giải cho tới khi đo máy thật.
11. Histogram 256 kênh × 3 chữ số — lưu nếu cần vẽ đồ thị (ảnh đính kèm phiếu), không bắt buộc.

## 9. Kiểm thử

- **Bộ mẫu cần có:**
  - 10ID không giây (đã có từ dữ liệu thô BC-3000Plus — dùng để test bố cục, nhưng phải **thay toàn bộ số liệu bằng số giả** trước khi đưa vào repo);
  - 8ID;
  - khối có `*`;
  - khối `B` và `C`;
  - nhiều khối liền nhau;
  - bản ghi thật của BC-3200 ở BVĐK 10 (bắt buộc trước khi bật — xác định trường giây, tốc độ baud, parity).
- **Script kiểm độ dài/bố cục** (đã chạy với 6 + 2 bản ghi thô, đều khớp, không thừa byte nào → kết luận không có checksum):

```python
W = [1,2,3,3,2,10, 1,2,2,4,2,2, 4,4,4,4,3,3,3, 3,3,4,4,4,3,3, 4,3,3,3,4, 12,8,24,16] + [768]*3
assert sum(W) == 2464              # 10ID; 8ID = 2464 - 12 = 2452
body = raw[raw.index(b'\x02')+1 : raw.index(b'\x1a')]
assert len(body) in (2464, 2452)   # 2466/2454 nếu BC-3200 có trường giây
```
  Kiểm tra chéo hợp lý số liệu: Lymph% + Mid% + Gran% ≈ 100; HCT ≈ RBC × MCV / 10; MCHC ≈ HGB / HCT × 100; PCT ≈ PLT × MPV / 10000 — cả 8 bản ghi thô đều đạt, xác nhận đúng số chữ số thập phân.
- **Mô phỏng máy:** cổng COM ảo (socat/com0com) phát lại khối; thử hai chế độ xác nhận.
- **Tình huống lỗi:** LIS không trả `<ACK>` (máy gửi lại `<ENQ>` 1 lần rồi bỏ); LIS trả `<NACK>` (máy gửi lại thân khối); đứt giữa khối (thiếu `<EOF>` — hết thời gian chờ thì bỏ khối, lưu thô); khối dài sai.

## 10. Nguồn

- `tai-lieu-hang/Mindray_BC-3200_Manual_ES_Apendice-D_Comunicacion.pdf` — sổ tay vận hành BC-3200 bản tiếng Tây Ban Nha, Phụ lục D "Comunicación" (D-1…D-9), mã tài liệu 3005-20-87873 (1.0). Lớp chữ hỏng — đọc bằng ảnh trang. **Nguồn chính.**
- `tai-lieu-hang/Mindray_BC-3000Plus_Communication_Protocol_V16_CN_BC-3200-family.pdf` — BC-3000Plus sổ tay V16.0, Phụ lục D 通信 (D.2 8ID/10ID, D.3 15ID/15ID+2, D.4.1 7 bit dữ liệu). Phông chữ mã hóa lệch: chữ Hán lệch +0x4A2C, chữ Latin/chữ số lệch +0x1D; đã giải bằng script.
- `tai-lieu-hang/Mindray_BC-2600_15ID+2_Protocol_CN.pdf` — BC-2600 Phụ lục D (8ID và 15ID; 15ID dùng 8 bit dữ liệu, mã điều khiển khác: ENQ 0x10, ETX 0x0F, MS 0x05…). BC-3200 không có 15ID.
- `tai-lieu-hang/Mindray_BC-3000_mau_du_lieu_tho/` — 6 khối 10ID (BC-3000Plus, 2011) và 2 khối 8ID; chỉ có số liệu, không có tên người bệnh; không chép vào repo.
- `tai-lieu-hang/Mindray_giao-thuc-goc_CN/血常规/` — sổ tay BC-1800 (§5.5.2 tốc độ baud/parity), BC-2600 (§5.3.2), ghi chú cài đặt BC-2300 (`9600,7,n`).
- Bản tham khảo còn thiếu: "Protocolo de Comunicación Mindray BC-2800/3200" (Scribd 326701650) — chỉ để đối chiếu thêm, không bắt buộc.
- Cần xin hãng/nhà phân phối: **chương 5.6 "Configuración → Transmisión" sổ tay BC-3200** (tốc độ baud, parity mặc định) và xác nhận trường **giây** trong khối `A` theo phiên bản phần mềm máy đang chạy ở BVĐK 10.
