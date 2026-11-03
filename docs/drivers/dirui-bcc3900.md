# DIRUI BCC-3900 / BCC-3960 — huyết học (3 thành phần bạch cầu)

> Trạng thái tài liệu: ✅ đủ để viết driver nhận kết quả và trả worklist (HL7 v2.3.1 + MLLP, có bảng segment, bảng mã OBX-3, bản tin mẫu) — ⚠️ còn thiếu: cổng TCP mặc định, máy có chờ ACK cho ORU hay không, ý nghĩa OBX-8/OBX-13, cách trả lời "không tìm thấy" worklist
> Kiểm chứng: chưa đối chiếu máy thật. Đã giải mã bản ghi thô `PK-BINHMINH_bcc3900_*.jsonl` ngày 09/09/2026 → 05/10/2026 (13 tệp, 3.499 frame MLLP, segment MSH/PID/PV1/OBR/OBX) nhưng **toàn bộ là dữ liệu do bộ giả lập `lis-agent/tools/simulate.ts` phát** (MSH-4 `machineid3900`/`sim`, ảnh PNG giả `iVBORw0KGgo=`) — chỉ xác nhận bản cũ tự nhất quán, không xác nhận máy thật
> Driver: `dirui-bcc3900` — họ giao thức: hl7-mllp (TCP)

## 1. Tóm tắt

- Máy đếm tế bào máu 3 thành phần (WBC/LYM/MXD/NEU + hồng cầu + tiểu cầu, 21 thông số số + 3 biểu đồ PNG), phần mềm nhúng Linux/ARM.
- Nối **TCP qua LAN**; trên máy khai IP + cổng của LIS rồi bấm kết nối → máy là **TCP client**, LIS là **server** *(suy luận từ menu Network)*.
- HL7 v2.3.1, khung MLLP `<VT> … <FS><CR>`, mã hóa UTF-8.
- **Hai chiều**: máy gửi `ORU^R01` (kết quả bệnh nhân và 4 loại QC); máy gửi `ORM^O01` hỏi worklist theo mã vạch, LIS trả `ORR^O02`.
- Điểm khó nhất: bản tin mẫu của hãng **ghi sai đơn vị** ở nhiều dòng OBX; mã vạch nằm ở **OBR-2**, còn OBR-3 là số mẫu máy tự đánh — code cũ lấy nhầm OBR-3.

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | Ethernet, TCP trong LAN cô lập ("physically isolated") | §2.4 |
| Vai trò TCP | Máy kết nối tới "LIS server" → máy = client, LIS = server *(suy luận)* | §3.10.3 |
| Cổng TCP mặc định | Không có mặc định trong phần chữ — người dùng tự nhập. Giá trị **ví dụ 9527** được ghi nhận khi đọc Appendix A/hình màn hình Network (bản trích chữ không còn thấy số này, có thể nằm trong ảnh Fig. 3-10-3) — chỉ là ví dụ, cổng thật phải xem trên máy | §3.10.3, App. A |
| RS-232 | Không được mô tả cho LIS | — |
| Cài đặt trên máy | `Menu → Settings → Software → Network`: LIS IP, port, IP máy, subnet mask → Save → kết nối LIS. `Mode`: tích **Automatic communication** (tự gửi kết quả), **Use worklist** (dùng worklist tải về). Biểu tượng trạng thái LIS ở vùng trạng thái màn hình chính | §3.10.3, §3.2, §2.4.2.3 |
| Gửi thủ công | `Result query → LIS transmission` (kết quả), `QC → Transmit LIS` (QC) | §8.5, §7 |
| Đầu đọc mã vạch | Cắm cổng USB của máy; dùng để nhập mã vạch mẫu / tải worklist | §2.3.5, §6.3 |
| Mã mẫu | Số mẫu chỉ gồm số và `-`, tối đa 12 ký tự | §3.2 |

## 3. Khung truyền và bắt tay

| Ký hiệu | Hex | Vai trò |
|---|---|---|
| `<SB>` (VT) | 0x0B | Mở bản tin |
| `<CR>` | 0x0D | Kết thúc mỗi segment |
| `<EB>` (FS) | 0x1C | Đóng bản tin |
| `<CR>` | 0x0D | Byte cuối sau `<EB>` |

Cấu trúc: `<SB> MSH…<CR> PID…<CR> … OBX…<CR> <EB><CR>` — một bản tin HL7 trọn vẹn trong một khung.

- Dữ liệu trong khung: mọi byte > 0x1F và `<CR>` (A.3) — tức UTF-8 đa byte được phép.
- **Checksum: không có** (MLLP). Độ dài tối đa: không nêu; OBX-5 tối đa 65 536 byte cho mỗi ảnh PNG base64 × 3 ảnh → một bản tin có thể tới **~200 KB** *(suy luận)*.
- **ACK mức ứng dụng**: tài liệu định nghĩa segment MSA (AA/AE/AR + mã 0/100–207) nhưng **chỉ có ví dụ MSA trong ORR trả worklist**. Máy có chờ `ACK^R01` sau `ORU^R01` hay không, timeout và gửi lại bao nhiêu lần: **Tài liệu không nêu — cần đo trên máy thật**.
- Contention: không áp dụng (TCP song công, mỗi bên gửi khi cần).
- Mất kết nối: máy có tự nối lại không — **Tài liệu không nêu**.

## 4. Bản tin kết quả (máy → LIS)

Thứ tự segment: `MSH` → `PID` → `PV1` → `OBR` → `OBX` × n.

**MSH**

| Trường | Ý nghĩa | Ví dụ |
|---|---|---|
| MSH-2 | Ký tự mã hóa (chuẩn `^~\&`; ví dụ hãng lại viết `^~&`) | `^~\&` |
| MSH-3 | Ứng dụng gửi | `BCC-3900` |
| MSH-4 | Mã máy | `machineid3900` |
| MSH-7 | Thời điểm gửi `YYYY[MM[DD[HH[MM[SS]]]]]` | `20261103091500` |
| MSH-9 | `ORU^R01` (kết quả) / `ORM^O01` (hỏi worklist) | `ORU^R01` |
| MSH-10 | Message control ID | `2` |
| MSH-11 | `P` = mẫu bệnh nhân/worklist, **`Q` = QC** | `P` |
| MSH-12 | `2.3.1` | |
| MSH-18 | `UTF-8` | |

**PID** (tùy chọn): PID-3 mã bệnh nhân (≤16), PID-5 họ tên (≤30), PID-8 giới `M/F/O`, PID-31 tuổi `số^đơn vị` (`Y/M/D/H`).
**PV1** (tùy chọn): PV1-3 khoa/nơi (vd `Outpatient`), PV1-20 loại chi trả (vd `Self Pay`).

**OBR**

| Trường | Ý nghĩa | Ví dụ |
|---|---|---|
| OBR-2 | **Mã vạch mẫu** (≤22) — với QC L-J/X-bar: số lô | `9900000123` |
| OBR-3 | **Số mẫu** máy đánh (≤12) — với QC: số file | `0042` |
| OBR-4 | Loại kết quả (mục 6, bảng OBR-4) | `1001^CountResults` |
| OBR-6 | Giờ lấy mẫu (QC: hạn dùng) | |
| OBR-7 | Giờ đếm | `20261103091500` |
| OBR-10 / OBR-20 / OBR-28 | Người gửi / bác sĩ / người duyệt | |
| OBR-14 | Giờ gửi mẫu | |
| OBR-22 | Giờ duyệt | |

**OBX**

| Trường | Ý nghĩa | Ví dụ |
|---|---|---|
| OBX-1 | Số thứ tự | `5` |
| OBX-2 | Kiểu: `NM` số, `IS` mã liệt kê, `ST` chuỗi, `ED` ảnh base64 | `NM` |
| OBX-3 | `mã^tên` — **chỉ mã (thành phần 1) là định danh**, tên chỉ để đọc | `2006^V_WBC` |
| OBX-5 | Giá trị; QC Xbar-R dạng `mean^R` (vd `4.2^0.12`); `ED` là PNG base64 | `6.85` |
| OBX-6 | Đơn vị (đổi theo cài đặt Unit trên máy) | `10^9/L` |
| OBX-7 | Khoảng tham chiếu `thấp-cao` | `4-10` |
| OBX-8 | Cờ bất thường — **không có trong bảng định nghĩa**; ví dụ hãng ghi `H` cho mọi dòng kể cả giá trị trong khoảng; bộ giả lập cũ ghi `N`. **Tài liệu không nêu — cần đo trên máy thật** | |
| OBX-11 | Trạng thái, luôn `F` | `F` |
| OBX-13 | QC có giá trị `E` — **ý nghĩa Tài liệu không nêu** | |

**Mã mẫu**: ưu tiên OBR-2 (mã vạch); rỗng thì dùng OBR-3 (số mẫu máy — chỉ ghép tay). Ví dụ bản tin bệnh nhân của hãng có OBR-2 rỗng, OBR-3 = `7250`.

**Phân biệt QC**: MSH-11 = `Q` **và** OBR-4 ∈ {1002, 1003, 1004, 1006}; PID/PV1 rỗng; có OBX `2005^Level` (0 cao, 1 trung bình, 2 thấp).

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

**Khi nào máy hỏi**: người dùng mở `Worklist → Download`, quét/nhập mã vạch, bấm OK (§6.3). Máy giữ tối đa **200** worklist. Không thấy mô tả máy tự hỏi khi chạy mẫu.

**Máy gửi `ORM^O01`:**

```
MSH|^~\&|BCC-3900||||<thời gian>||ORM^O01|<id>|P|2.3.1||||||UTF-8
ORC|RF||<mã vạch>||IP
```

**LIS trả `ORR^O02` khi có:**

| Segment | Trường dùng |
|---|---|
| MSH | MSH-3 `LIS`, MSH-9 `ORR^O02`, MSH-10 = MSH-10 của ORM (theo ví dụ), MSH-11 `P^S` (theo ví dụ) |
| MSA | MSA-1 `AA`, MSA-2 = MSH-10 của bản tin ORM, MSA-6 `0` |
| PID | PID-3 mã BN, PID-5 họ tên, PID-8 giới, PID-31 tuổi |
| PV1 | PV1-3 khoa, PV1-20 loại chi trả |
| ORC | ORC-1 `AF`, ORC-2 = mã vạch |
| OBR | OBR-2 mã vạch, OBR-3 số mẫu, OBR-4 `1001^Count`, OBR-6 giờ lấy mẫu, OBR-10 người gửi, OBR-14 giờ gửi |
| OBX | `2001^MODE` (0 máu toàn phần, 1 vi lượng, 2 pha loãng trước), `2002^MODE_EX` (không có trong bảng A.7 — **ý nghĩa Tài liệu không nêu**), `2003^Ref` nhóm tham chiếu (0 thường, 1 nam, 2 nữ, 3 trẻ em, 4 sơ sinh, 5–9 tự định nghĩa), `2004^Note` ghi chú |

**Khi không có**: máy hiện thông báo "mã vạch không khớp LIS" (§6.3 (4)) nhưng **bản tin LIS phải trả không được mô tả** — đề xuất `ORR^O02` với `MSA|AR|<id>||||204` (Unknown key identifier) *(suy luận từ bảng MSA-6)*; cần thử trên máy thật.

**Timeout chờ ORR**: **Tài liệu không nêu — cần đo trên máy thật**.

Máy không nhận danh sách xét nghiệm từ LIS (máy chạy cả bộ CBC); worklist chỉ mang thông tin bệnh nhân + chế độ.

## 6. Bảng mã chỉ số

**OBR-4 — loại kết quả**

| Mã | Ý nghĩa |
|---|---|
| 1001 | Kết quả đếm mẫu (bệnh nhân) |
| 1002 | QC L-J |
| 1003 | QC X-bar |
| 1004 | QC X-B (chỉ MCV/MCH/MCHC) |
| 1006 | QC X-bar-R |

Không có mã 1005 trong bảng A.6 (dải QC là 1002–1004 và 1006). Gặp OBR-4 lạ → ghi nhận "loại kết quả chưa biết", không coi là mẫu bệnh nhân.

**OBX-3 — chỉ số** (đơn vị = mặc định theo Bảng 3-3-1; map theo `lis-server/seed/dirui-bcc3900.sql`)

| Mã máy gửi | Tên trong bản tin | Chỉ số | Đơn vị mặc định (khác cài được) | Map danh mục / ghi chú |
|---|---|---|---|---|
| 2001 | MODE | Chế độ phân tích (IS) | — | không phải kết quả |
| 2003 | Ref | Nhóm tham chiếu (IS) | — | không phải kết quả |
| 2004 | Note | Ghi chú (ST) | — | không phải kết quả |
| 2005 | Level | Mức QC (IS) | — | chỉ QC |
| 2006 | V_WBC | Bạch cầu | 10⁹/L (10³/μL, 10²/μL, /nL) | `002WBC` |
| 2007 | V_NEU_p | NEU% (bạch cầu hạt) | % | `012GRAN%` |
| 2008 | V_LYM_p | LYM% | % | `010LYM%` |
| 2009 | V_MXD_p | MXD% (tế bào cỡ trung) | % | `011MONO%` (tên trong danh mục: MID%) |
| 2012 | V_NEU_c | NEU# | 10⁹/L | `006GRAN#` |
| 2013 | V_LYM_c | LYM# | 10⁹/L | `004LYM#` |
| 2014 | V_MXD_c | MXD# | 10⁹/L | `005MONO#` (tên: MID#) |
| 2017 | V_RBC | Hồng cầu | 10¹²/L (10⁶/μL, 10⁴/μL, /pL) | `015RBC` |
| 2018 | V_HGB | Hemoglobin | g/L (g/dL, mmol/L) | `016HGB` |
| 2019 | V_MCV | MCV | fL (μm³) | `018MCV` |
| 2020 | V_HCT | Hematocrit | **L/L** (hoặc %) | `017HCT` — kiểm đơn vị danh mục |
| 2021 | V_MCH | MCH | pg (fmol) | `019MCH` |
| 2022 | V_MCHC | MCHC | g/L (g/dL, mmol/L) | `020MCHC` |
| 2023 | V_RDW_SD | RDW-SD | fL | `021RDWCV` (mã ghi CV nhưng **tên** là RDW-SD) |
| 2024 | V_RDW_CV | RDW-CV | % | `021RDWD` (mã ghi D nhưng **tên** là RDW-CV) |
| 2025 | V_PLT | Tiểu cầu | 10⁹/L (10³/μL, 10⁴/μL, /nL) | `022PLT` |
| 2026 | V_MPV | MPV | fL | `024MPV` |
| 2027 | V_PCT | Thể tích khối tiểu cầu | % (mL/L) | `023PCT` |
| 2028 | V_PDW | PDW | **fL** theo Bảng 3-3-1 (ví dụ hãng và giả lập ghi `%`) — cần đo | `025PDW` |
| 2029 | V_P_LCR | P-LCR | % *(suy luận — không có trong Bảng 3-3-1)* | `026PLCR` |
| 2030 | V_P_LCC | P-LCC | 10⁹/L *(suy luận)* | chưa có mã |
| 2101 | V_WBCScattergram.PNG | Biểu đồ WBC (ED, PNG base64) | — | lưu ảnh riêng, không phải kết quả |
| 2102 | V_RBCScattergram.PNG | Biểu đồ RBC | — | như trên |
| 2103 | V_PLTScattergram.PNG | Biểu đồ PLT | — | như trên |

Lưu ý: trong bảng A.7, tên ở OBX-3 là `2101^WBC Scattergram.PNG`; trong bản tin mẫu là `2101^V_WBCScattergram.PNG` — bám mã số, không bám tên.

## 7. Bản tin mẫu

Dữ liệu giả, dựng theo bảng A.5 và ví dụ A.8; đơn vị lấy theo mặc định Bảng 3-3-1 (không chép đơn vị sai của ví dụ hãng). Ảnh PNG rút gọn.

**Kết quả bệnh nhân (máy → LIS):**

```
<SB>MSH|^~\&|BCC-3900|machineid3900|||20261103091500||ORU^R01|12|P|2.3.1||||||UTF-8<CR>
PID|1||BN0000001||Nguyễn Văn Mẫu|||M|||||||||||||||||||||||40^Y<CR>
PV1|1||Ngoại trú|||||||||||||||||Tự trả<CR>
OBR||9900000123|0042|1001^CountResults||20261103090000|20261103091500|||||||20261103091000||||||||||||||<CR>
OBX|1|IS|2001^MODE||0||||||F||<CR>
OBX|2|IS|2003^Ref||1||||||F||<CR>
OBX|3|NM|2006^V_WBC||6.85|10^9/L|4-10|N|||F||<CR>
OBX|4|NM|2013^V_LYM_c||2.10|10^9/L|0.8-4|N|||F||<CR>
OBX|5|NM|2014^V_MXD_c||0.55|10^9/L|0.1-0.9|N|||F||<CR>
OBX|6|NM|2012^V_NEU_c||4.20|10^9/L|2-7|N|||F||<CR>
OBX|7|NM|2008^V_LYM_p||30.7|%|20-40|N|||F||<CR>
OBX|8|NM|2009^V_MXD_p||8.0|%|3-15|N|||F||<CR>
OBX|9|NM|2007^V_NEU_p||61.3|%|50-70|N|||F||<CR>
OBX|10|NM|2017^V_RBC||4.85|10^12/L|4-5.5|N|||F||<CR>
OBX|11|NM|2018^V_HGB||142|g/L|120-160|N|||F||<CR>
OBX|12|NM|2019^V_MCV||88.2|fL|80-100|N|||F||<CR>
OBX|13|NM|2020^V_HCT||0.428|L/L|0.4-0.5|N|||F||<CR>
OBX|14|NM|2021^V_MCH||29.3|pg|27-34|N|||F||<CR>
OBX|15|NM|2022^V_MCHC||332|g/L|320-360|N|||F||<CR>
OBX|16|NM|2023^V_RDW_SD||42.1|fL|35-56|N|||F||<CR>
OBX|17|NM|2024^V_RDW_CV||12.9|%|11-16|N|||F||<CR>
OBX|18|NM|2025^V_PLT||251|10^9/L|100-300|N|||F||<CR>
OBX|19|NM|2026^V_MPV||10.1|fL|7-11|N|||F||<CR>
OBX|20|NM|2027^V_PCT||0.254|%|0.1-0.28|N|||F||<CR>
OBX|21|NM|2028^V_PDW||15.9|fL|9-17|N|||F||<CR>
OBX|22|NM|2029^V_P_LCR||27.4|%|13-43|N|||F||<CR>
OBX|23|NM|2030^V_P_LCC||69|10^9/L|13-129|N|||F||<CR>
OBX|24|ED|2101^V_WBCScattergram.PNG||iVBORw0KGgo…(base64)…||||||F||<CR>
OBX|25|ED|2102^V_RBCScattergram.PNG||iVBORw0KGgo…(base64)…||||||F||<CR>
OBX|26|ED|2103^V_PLTScattergram.PNG||iVBORw0KGgo…(base64)…||||||F||<CR>
<EB><CR>
```

(Giá trị OBX-8 `N` là giả định; máy thật có thể gửi trống/`H`/`L`.)

**Hỏi worklist (máy → LIS) và trả lời (LIS → máy):**

```
<SB>MSH|^~\&|BCC-3900||||20261103085500||ORM^O01|7|P|2.3.1||||||UTF-8<CR>
ORC|RF||9900000123||IP<CR>
<EB><CR>

<SB>MSH|^~\&|LIS||||20261103085501||ORR^O02|7|P^S|2.3.1||||||UTF-8<CR>
MSA|AA|7||||0<CR>
PID|1||BN0000001||Nguyễn Văn Mẫu|||M|||||||||||||||||||||||40^Y<CR>
PV1|1||Ngoại trú|||||||||||||||||Bảo hiểm y tế<CR>
ORC|AF|9900000123|||<CR>
OBR|1|9900000123|0042|1001^Count||20261103085000||||BS Thử||||20261103085300||||||||||||||||<CR>
OBX|1|IS|2001^MODE||0||||||||<CR>
OBX|2|IS|2003^Ref||1||||||||<CR>
OBX|3|ST|2004^Note||||||||||<CR>
<EB><CR>
```

## 8. Bẫy và lưu ý khi viết driver

1. **Mã vạch ở OBR-2, không phải OBR-3.** Code cũ `codec/hl7.ts` ưu tiên OBR-3 (số mẫu máy) rồi mới OBR-2 → khi có mã vạch vẫn lấy số máy. Bộ giả lập cũ đặt mã vạch vào OBR-3 nên test cũ không bắt được lỗi.
2. **Đơn vị trong ví dụ hãng sai** (HGB ghi `10^9/L`, PLT ghi `pg`, NEU% ghi `10^9/L`…) — lệch theo dòng trong khối bản tin mẫu A.8; bảng A.7 (mã ↔ tên) khi trích bằng `pdftotext -layout` thì **khớp**, không lệch như ghi chú cũ trong seed. Dùng đơn vị danh mục LIS; chỉ dùng OBX-6 để phát hiện máy đổi đơn vị (vd HCT `L/L` → `%` làm giá trị đổi 100 lần).
3. **MSH-2 thiếu `\`** trong ví dụ (`^~&`): đọc ký tự phân cách từ MSH theo độ dài thực, không giả định đủ 4 ký tự; không xử lý escape `\` nếu MSH-2 không khai.
4. **Không phân biệt QC** trong code cũ (`isControl` luôn false) → QC lẫn vào kết quả bệnh nhân. Phải dựa MSH-11 = `Q` / OBR-4 ≠ 1001.
5. **Giá trị QC X-bar-R dạng `mean^R`** — tách thành phần, không lưu nguyên chuỗi.
6. **Ảnh PNG base64** lớn: bộ đệm khung phải ≥ 256 KB; không đẩy ảnh vào hàng đợi kết quả số.
7. **MSA-2 trong ví dụ ORR** (`MSA|AA|1`) không bằng MSH-10 của ORM (`4`), trái với chính định nghĩa MSA-2 — làm theo định nghĩa (MSA-2 = MSH-10 của ORM) và kiểm trên máy thật.
8. **ORR dùng MSH-11 `P^S` và MSH-18 `UTF8`** (không gạch) trong ví dụ — máy có kiểm chặt hai trường này không: **cần đo**.
9. **OBX-8 không được định nghĩa** — không suy cờ H/L từ OBX-8 khi chưa đo; có thể tự tính từ OBX-7.
10. Thứ tự OBX trong ví dụ hãng **không theo mã tăng dần** (2013, 2014, 2012, 2008…) — không đọc theo thứ tự.
11. **ACK cho ORU** chưa rõ → driver nên có tùy chọn bật/tắt gửi `ACK^R01` (`MSA|AA|<MSH-10>`), mặc định bật *(suy luận: máy HL7 thường bỏ qua ACK thừa, nhưng thiếu ACK có thể làm máy gửi lại)*.
12. **PV1 lệch vị trí**: định nghĩa A.5.3 đặt loại chi trả ở PV1-20 (ví dụ ORR khớp), nhưng ví dụ ORU bệnh nhân A.8 đặt ở PV1-23 — không đọc PV1 theo vị trí cứng; trường này không cần cho kết quả.
13. Họ tên tiếng Việt: MSH-18 khai UTF-8 → giải mã UTF-8; nhưng máy có thực sự gõ được tiếng Việt không — cần đo.

## 9. Kiểm thử

- Bộ mẫu cần chụp thật (bắt gói tin trên switch hoặc để driver ghi byte thô): kết quả bệnh nhân có mã vạch và không mã vạch; 3 chế độ phân tích (máu toàn phần / vi lượng / pha loãng); 4 loại QC (1002, 1003, 1004, 1006); một mẫu có giá trị ngoài khoảng (xem OBX-8 thật); một ORM/ORR thành công và một ORR "không tìm thấy"; bản tin sau khi đổi đơn vị HGB/HCT/PLT.
- Mô phỏng: TCP server của driver + script đóng vai máy phát lại byte thô; script đóng vai máy gửi ORM để test trả ORR. **Không dùng `tools/simulate.ts` cũ làm chuẩn** (sai vị trí mã vạch, ảnh PNG giả).
- Lỗi phải thử: bản tin bị cắt ngang giữa base64 rồi đứt TCP; hai bản tin dính trong một lần đọc socket; bản tin tách nhiều gói TCP; thiếu `<CR>` sau `<EB>`; LIS trả ACK chậm/không trả; máy nối lại sau khi LIS khởi động lại; MSH-2 3 ký tự; OBX mã lạ (máy đời mới) → ghi "kênh chưa biết".

## 10. Nguồn

| Tài liệu | Mục dùng | Nơi lấy |
|---|---|---|
| *Automatic Hematology Analyzer BCC-3900/BCC-3960 User's Manual* (tệp tên "Manual_ES" nhưng nội dung tiếng Anh) | §2.4 (môi trường, TCP LAN), §2.4.2.3, §3.2 (Mode), §3.3 (Bảng 3-3-1 đơn vị), §3.10.3 (Network), §6.3 (Download worklist), §8.5, **Appendix A "Network communication interface protocol V1.1"** (A.3 khung, A.5 segment, A.6 OBR-4, A.7 OBX-3, A.8 liệt kê + bản tin mẫu) | `tai-lieu-hang/Dirui_BCC-3900_Manual_ES.pdf` |
| Code bản cũ | `lis-agent/src/drivers.ts` (`dirui-bcc3900`), `codec/hl7.ts`, `hl7.test.ts`, `framer.ts`, `tools/simulate.ts` | `~/Downloads/lis-tham-khao/lis-agent/` |
| Bảng map kênh | `lis-server/seed/dirui-bcc3900.sql` | `~/Downloads/lis-tham-khao/lis-server/seed/` |
| Bản ghi thô | `lis-agent/.demo-data/rawlog/PK-BINHMINH_bcc3900_2026-09-09 … 2026-10-05.jsonl` — dữ liệu giả lập | như trên |

**Còn thiếu — cần xin hãng DIRUI / nhà phân phối:**
- Bản đầy đủ *BCC-3900 LIS Communication Protocol* (bản mới hơn V1.1) nêu rõ: cổng TCP mặc định, máy có chờ `ACK^R01` không và timeout/gửi lại, ý nghĩa OBX-8 và OBX-13, mã `2002 MODE_EX`, bản tin trả lời khi worklist không tồn tại.
- Xác nhận đơn vị/định dạng thật của PDW, P-LCR, P-LCC.
