# ASTM E1381 / E1394 (CLSI LIS1-A / LIS2-A2) — họ giao thức dùng chung cho nhiều loại máy

> Trạng thái tài liệu: ✅ đủ để viết lớp giao thức chung (tầng thấp + phân tích record). Mỗi máy cụ thể vẫn cần tài liệu riêng cho phần "biến thể" (vị trí mã xét nghiệm, cờ, hỏi chỉ định).
> Kiểm chứng: chưa đối chiếu máy thật (chưa có bản ghi byte thô của BVĐK 10)
> Driver: `generic-astm` (và các biến thể `abbott-celldyn`, `dirui-cst240`… dùng chung lớp này) — họ giao thức: astm-e1394

## 1. Tóm tắt

ASTM là cặp chuẩn mà phần lớn máy sinh hóa, miễn dịch, huyết học, đông máu dùng để nói chuyện với LIS:

- **E1381** (nay là CLSI **LIS1-A**) — tầng thấp: bắt tay ENQ/ACK/NAK/EOT, đóng khung frame, checksum, timeout. Chạy trên RS-232; trên TCP hầu hết máy giữ nguyên cách bắt tay này *(suy luận — phải xác nhận từng máy)*.
- **E1394** (nay là CLSI **LIS2-A2**) — tầng bản tin: các record `H P O R C Q L` phân cách bằng `| \ ^ &`.

Hai chiều được: máy gửi kết quả (H-P-O-R-L), máy hỏi chỉ định (Q) và LIS trả lời (H-P-O-L). Điểm khó nhất không nằm ở chuẩn mà ở **biến thể từng hãng**: mã xét nghiệm nằm ở thành phần thứ mấy của Universal Test ID, SID ở O.3 hay O.4, cờ bất thường viết thế nào, có dùng tách frame ETB hay không.

## 2. Kết nối vật lý

| Mục | Giá trị |
|---|---|
| Cổng | RS-232 (DB-9/DB-25, cáp null-modem chéo TxD/RxD) hoặc TCP/IP |
| Tham số serial | Chuẩn không cố định; thực tế phổ biến 9600 8N1, không bắt tay phần cứng. Toàn bộ máy ở BVĐK 10 đang chạy `9600,8,N,1` (bảng `InterfacePara` hệ cũ) |
| Bán song công | Có — tại một thời điểm chỉ một bên được gửi (do giao thức ENQ quyết định) |
| Vai trò TCP | Tùy máy: máy là server (LIS nối vào) hoặc máy là client (LIS lắng nghe). Chuẩn E1381 không quy định |
| Mã hóa ký tự | Chuẩn quy định ASCII 7-bit (mã 0x20–0x7E cho dữ liệu). Tiếng Việt có dấu thường không đi qua được — xem mục 8 |

## 3. Khung truyền và bắt tay (E1381 / LIS1-A)

### 3.1 Ký tự điều khiển

| Tên | Hex | Vai trò |
|---|---|---|
| `<STX>` | 0x02 | Bắt đầu frame |
| `<ETX>` | 0x03 | Kết thúc frame cuối của một record/bản tin |
| `<EOT>` | 0x04 | Kết thúc phiên truyền (trả đường truyền về trạng thái rỗi) |
| `<ENQ>` | 0x05 | Xin quyền gửi |
| `<ACK>` | 0x06 | Đồng ý / nhận frame tốt |
| `<NAK>` | 0x15 | Từ chối / frame lỗi |
| `<ETB>` | 0x17 | Kết thúc frame trung gian (record dài bị tách) |
| `<CR>` | 0x0D | Kết thúc record (nằm trong phần text) và trong đuôi frame |
| `<LF>` | 0x0A | Byte cuối cùng của frame |

Ký tự **không được** xuất hiện trong phần text: SOH, STX, ETX, EOT, ENQ, ACK, DLE, NAK, SYN, ETB, LF, DC1–DC4. (CR được phép — dùng để kết thúc record.)

### 3.2 Ba pha của một phiên

```
Gửi                         Nhận
<ENQ>               ──►
                    ◄──     <ACK>          (pha thiết lập)
<STX>1...<ETX>C1C2<CR><LF> ──►
                    ◄──     <ACK>          (pha truyền: lặp cho từng frame)
<STX>2...<ETX>C1C2<CR><LF> ──►
                    ◄──     <ACK>
<EOT>               ──►                    (pha kết thúc)
```

### 3.3 Cấu trúc một frame

```
<STX> FN text <ETB>|<ETX> C1 C2 <CR> <LF>
```

| Phần | Độ dài | Ghi chú |
|---|---|---|
| `<STX>` | 1 | |
| `FN` | 1 | Số frame, ký tự ASCII `'0'`–`'7'`. Frame đầu tiên của mỗi phiên là `1`, tăng dần, sau `7` quay về `0` (modulo 8) |
| text | ≤ 240 | Thường là một record kết thúc bằng `<CR>` |
| `<ETB>` hoặc `<ETX>` | 1 | ETB = còn frame tiếp theo của cùng record; ETX = frame cuối |
| `C1 C2` | 2 | Checksum, 2 ký tự hex **in hoa** |
| `<CR><LF>` | 2 | |

**Giới hạn độ dài:** text tối đa **240** ký tự; cả frame tối đa **247** ký tự (1 STX + 1 FN + 240 + 1 ETB/ETX + 2 checksum + CR + LF). Record dài hơn 240 ký tự phải tách: các frame giữa kết thúc bằng ETB, frame cuối bằng ETX. Bên nhận ghép phần text của các frame lại rồi mới tách record.

### 3.4 Checksum

- Cộng giá trị byte **từ FN đến hết ETB/ETX** (không tính STX; tính cả FN, toàn bộ text, CR trong text và ETB/ETX).
- Lấy **modulo 256** (giữ 8 bit thấp).
- Viết thành 2 ký tự hex in hoa (`0x0A` → `"0A"`).

**Ví dụ 1 — frame header ngắn** `<STX>1H|\^&<CR><ETX>??<CR><LF>`:

| Ký tự | `1` | `H` | `\|` | `\` | `^` | `&` | `<CR>` | `<ETX>` | Tổng |
|---|---|---|---|---|---|---|---|---|---|
| Hex | 31 | 48 | 7C | 5C | 5E | 26 | 0D | 03 | |
| Thập phân | 49 | 72 | 124 | 92 | 94 | 38 | 13 | 3 | **485** |

485 mod 256 = 229 = 0xE5 → frame đầy đủ: `<STX>1H|\^&<CR><ETX>E5<CR><LF>`.

**Ví dụ 2 — record bị tách bằng ETB** (minh họa; thực tế chỉ tách khi text > 240):

| Frame | Phần được cộng | Tổng | Checksum |
|---|---|---|---|
| `<STX>5R\|1\|^^^WBC\|7.5\|10^3/uL<ETB>31<CR><LF>` | `5R\|1\|^^^WBC\|7.5\|10^3/uL` + ETB | 1841 | `31` |
| `<STX>6\|4.0 to 10.0\|N\|\|F<CR><ETX>3E<CR><LF>` | `6\|4.0 to 10.0\|N\|\|F` + CR + ETX | 1342 | `3E` |

Ghép lại: `R|1|^^^WBC|7.5|10^3/uL|4.0 to 10.0|N||F`. Lưu ý chỗ tách có thể rơi giữa một trường — **không** được coi ETB là ranh giới trường.

### 3.5 Bắt tay, timeout, gửi lại

| Tình huống | Quy tắc chuẩn |
|---|---|
| Bên nhận bận khi nhận ENQ | Trả `<NAK>`; bên gửi chờ **≥ 10 s** rồi gửi lại ENQ |
| Không có trả lời cho ENQ hoặc cho frame | Bên gửi chờ **15 s** rồi gửi `<EOT>` và bỏ phiên |
| Bên nhận chờ frame kế tiếp / EOT | Chờ tối đa **30 s**; quá hạn thì coi phiên đã hỏng, bỏ bản tin dở |
| Frame lỗi (sai checksum, sai FN, thiếu ký tự) | Bên nhận trả `<NAK>`; bên gửi gửi lại **đúng frame đó** (cùng FN) |
| Số lần gửi lại | Tối đa **6** lần cho một frame; quá thì gửi `<EOT>` |
| Bên nhận muốn ngắt | Trả `<EOT>` thay cho `<ACK>` — bên gửi coi như ACK, nên dừng sớm (trong 15 s) và nhường đường truyền |
| Frame trùng (cùng FN với frame vừa ACK) | Bên nhận ACK lại nhưng **bỏ** nội dung trùng |

### 3.6 Tranh chấp đường truyền (contention)

Hai bên cùng gửi `<ENQ>` cùng lúc:

- **Máy xét nghiệm được ưu tiên.** LIS phải dừng, trả `<ACK>` cho ENQ của máy và chuyển sang nhận.
- LIS chờ **≥ 20 s** sau khi đường truyền rỗi rồi mới xin gửi lại.
- Máy chờ **≥ 1 s** rồi gửi lại `<ENQ>`.

Ví dụ thực tế: Roche cobas e 411 ghi rõ khi tranh chấp thì máy được ưu tiên, máy chờ 1 s rồi gửi lại ENQ, chờ 10 s khi bị NAK, tối đa 6 lần thử (Host Interface Manual v2.2, phụ lục D).

## 4. Bản tin kết quả (máy → LIS) — E1394 / LIS2-A2

### 4.1 Ký tự phân cách

Định nghĩa ngay trong record H, trường 2 (`H|\^&`): ký tự sau `H` là phân cách trường, rồi lặp, thành phần, thoát.

| Vai trò | Mặc định | Chuỗi thoát |
|---|---|---|
| Trường (field) | `\|` | `&F&` |
| Lặp (repeat) | `\` | `&R&` |
| Thành phần (component) | `^` | `&S&` |
| Thoát (escape) | `&` | `&E&` |

Driver **phải đọc phân cách từ H.2** chứ không cố định cứng.

### 4.2 Các loại record và cây phân cấp

| Mã | Tên | Cấp |
|---|---|---|
| `H` | Header — đầu bản tin | 0 |
| `P` | Patient — người bệnh | 1 |
| `O` | Order — chỉ định / mẫu | 2 (dưới P) |
| `R` | Result — kết quả | 3 (dưới O) |
| `C` | Comment — chú thích, gắn vào record ngay trước nó | bất kỳ |
| `Q` | Request information — hỏi chỉ định | 1 |
| `M` | Manufacturer — record riêng của hãng | bất kỳ |
| `S` | Scientific — hiếm dùng | — |
| `L` | Terminator — cuối bản tin | 0 |

Thứ tự điển hình: `H → P → O → R → R … → (O → R …) → (P → O → R …) → L`. Số thứ tự (trường 2) **đánh lại từ 1** dưới mỗi cha.

### 4.3 Trường hay dùng (đánh số từ 1, trường 1 là mã record)

**H — Header**

| Trường | Ý nghĩa | Ví dụ |
|---|---|---|
| H.2 | Phân cách | `\^&` |
| H.5 | Tên/ID người gửi (máy) | `MAYXN^1` |
| H.10 | ID người nhận | `LIS` |
| H.12 | Processing ID: `P` thật, `T` thử, `D` debug, `Q` QC | `P` |
| H.13 | Phiên bản | `1` hoặc `LIS2-A2` |
| H.14 | Thời gian gửi `YYYYMMDDHHMMSS` | `20260915084500` |

**P — Patient**

| Trường | Ý nghĩa |
|---|---|
| P.2 | Số thứ tự |
| P.3 | Mã BN do nơi chỉ định gán |
| P.4 | Mã BN do phòng XN gán |
| P.6 | Họ tên (`Họ^Tên^Đệm…`) |
| P.8 | Ngày sinh `YYYYMMDD` |
| P.9 | Giới `M`/`F`/`U` |

**O — Order**

| Trường | Ý nghĩa | Ghi chú |
|---|---|---|
| O.3 | Specimen ID — mã mẫu do LIS gán (barcode) | Nơi SID nằm ở đa số máy |
| O.4 | Instrument Specimen ID — mã/vị trí do máy gán | Một số máy (vd Sysmex) để SID ở đây |
| O.5 | Universal Test ID (lặp bằng `\`) | `^^^GLU\^^^ALT` |
| O.6 | Ưu tiên `S` (stat) / `R` (thường) | |
| O.7 | Thời gian chỉ định | |
| O.12 | Action code: `N` mới, `A` thêm, `C` hủy, `Q` mẫu QC, `P` chờ… | `Q` = mẫu nội kiểm |
| O.16 | Loại mẫu (`SERUM`, `URINE`, `BLOOD`…) | |
| O.26 | Report type: `O` chỉ định, `F` kết quả cuối, `P` sơ bộ, `X` không làm được, `Y` không có chỉ định, `Z` không có hồ sơ BN, `Q` trả lời hỏi | |

**R — Result**

| Trường | Ý nghĩa | Ví dụ |
|---|---|---|
| R.2 | Số thứ tự | `1` |
| R.3 | **Universal Test ID** `^^^<mã>^<phụ>…` | `^^^GLU` |
| R.4 | Giá trị | `5.6`, `<0.01`, `NEG` |
| R.5 | Đơn vị | `mmol/L` |
| R.6 | Khoảng tham chiếu (chuẩn: `3.9 to 6.4`; nhiều hãng dùng `3.9^6.4`) | |
| R.7 | Cờ bất thường: `L` `H` `LL` `HH` `<` `>` `N` `A` `AA` `U` `D` `B` `W` | |
| R.9 | Trạng thái: `F` cuối, `C` sửa, `P` sơ bộ, `X` không làm được, `I` đang chờ, `S` một phần, `M` MIC, `R` đã gửi lần trước, `N` trả lời hỏi | |
| R.11 | Người thao tác | |
| R.12 / R.13 | Bắt đầu / kết thúc đo | |
| R.14 | ID máy/module | |

**Universal Test ID (R.3, O.5)** có 4 phần chuẩn: `ID chuẩn^tên^bộ mã^mã cục bộ`. Ba thành phần đầu thường rỗng, **mã của máy nằm ở thành phần 4** (`^^^GLU`). Nhiều hãng thêm thành phần thứ 5, 6… (độ pha loãng, loại kết quả, tên). Đây là chỗ biến thể nhiều nhất — mỗi máy phải khai `testIdComponent` riêng (vd Cell-Dyn Ruby: thành phần 7, xem `cac-may-astm-bvdk10.md`).

**L — Terminator**: L.3 mã kết thúc: `N` bình thường, `T` người gửi hủy, `R` người nhận yêu cầu hủy, `E` lỗi không rõ, `Q` lỗi trong yêu cầu hỏi, `I` **không có thông tin cho lần hỏi vừa rồi**, `F` đã xử lý xong yêu cầu hỏi.

### 4.4 Phân biệt mẫu thường / QC / chuẩn

Chuẩn không có một trường duy nhất cho việc này. Cách các hãng hay dùng: O.12 = `Q`; H.12 = `Q`; hoặc SID mang tiền tố riêng; hoặc record M riêng. Phải lấy từ tài liệu từng máy. Hệ cũ BVĐK 10 gắn trạng thái `QC` / `CAL` cho `Result_OUT` nhưng không ghi lại dựa vào trường nào.

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

**Máy hỏi** (thường khi đọc được barcode trên ống, hoặc khi bắt đầu chạy rack): mở phiên ENQ, gửi `H → Q → L`, kết thúc EOT.

| Trường Q | Ý nghĩa |
|---|---|
| Q.3 | Phạm vi bắt đầu: `mã BN^mã mẫu^…` — SID thường ở thành phần 2 (`^260915001`) |
| Q.4 | Phạm vi kết thúc (thường rỗng hoặc `ALL`) |
| Q.5 | Xét nghiệm hỏi — `ALL` hoặc danh sách |
| Q.13 | Mã trạng thái yêu cầu: `O` = hỏi chỉ định, `A` = hủy yêu cầu trước, `F`… |

**LIS trả lời**: đợi máy gửi xong EOT, LIS mở phiên mới bằng ENQ của mình rồi gửi:

- Có chỉ định: `H → P → O (O.5 = danh sách mã, O.26 = O hoặc Q) → L|1|N`.
- Không có chỉ định: `H → L|1|I` (mã `I` = không có thông tin), hoặc `H → P → O` với O.26 = `Y` / `Z` tùy hãng yêu cầu.

**Timeout trả lời:** chuẩn không quy định thời gian LIS phải trả lời câu hỏi; mỗi máy tự đặt (vài giây đến vài chục giây). *Tài liệu không nêu — cần đo trên máy thật / đọc tài liệu từng máy.* Nguyên tắc cho driver: phải trả lời ngay khi máy nhả đường truyền, không xếp hàng sau các việc khác.

## 6. Bảng mã chỉ số

Không có bảng chung — mã xét nghiệm do từng máy (thậm chí do người dùng đặt trên máy) quyết định. Xem bảng mã trong tài liệu từng máy và `khao-sat/seed/anh-xa-ma-may.json`.

## 7. Bản tin mẫu

Dữ liệu giả hoàn toàn. Checksum đã tính đúng theo mục 3.4. `→` máy gửi, `←` LIS gửi.

**Kết quả hai xét nghiệm của một mẫu:**

```
→ <ENQ>
← <ACK>
→ <STX>1H|\^&|||MAYXN^1|||||LIS||P|1|20260915084500<CR><ETX>04<CR><LF>
← <ACK>
→ <STX>2P|1||BN000123||NGUYEN^VAN MAU||19800101|M<CR><ETX>56<CR><LF>
← <ACK>
→ <STX>3O|1|260915001||^^^GLU\^^^ALT|R|20260915080000|||||N||||SERUM||||||||||F<CR><ETX>33<CR><LF>
← <ACK>
→ <STX>4R|1|^^^GLU|5.6|mmol/L|3.9 to 6.4|N||F||||20260915084500<CR><ETX>15<CR><LF>
← <ACK>
→ <STX>5R|2|^^^ALT|65|U/L|0 to 40|H||F||||20260915084500<CR><ETX>DE<CR><LF>
← <ACK>
→ <STX>6L|1|N<CR><ETX>09<CR><LF>
← <ACK>
→ <EOT>
```

(Họ tên giả "Nguyễn Văn Mẫu" viết không dấu vì khung ASTM là ASCII.)

**Hỏi chỉ định theo barcode `260915001`:**

```
→ <ENQ>
← <ACK>
→ <STX>1H|\^&|||MAYXN^1|||||LIS||P|1|20260915090000<CR><ETX>FC<CR><LF>
← <ACK>
→ <STX>2Q|1|^260915001||ALL||||||||O<CR><ETX>E2<CR><LF>
← <ACK>
→ <STX>3L|1|N<CR><ETX>06<CR><LF>
← <ACK>
→ <EOT>
```

**LIS trả lời — có chỉ định:**

```
← <ENQ>
→ <ACK>
← <STX>1H|\^&|||LIS|||||MAYXN||P|1|20260915090001<CR><ETX>6E<CR><LF>
→ <ACK>
← <STX>2P|1||BN000123||NGUYEN^VAN MAU||19800101|M<CR><ETX>56<CR><LF>
→ <ACK>
← <STX>3O|1|260915001||^^^GLU\^^^ALT|R|20260915090001|||||A||||SERUM||||||||||O<CR><ETX>31<CR><LF>
→ <ACK>
← <STX>4L|1|N<CR><ETX>07<CR><LF>
→ <ACK>
← <EOT>
```

**LIS trả lời — không có chỉ định:**

```
← <STX>1H|\^&|||LIS|||||MAYXN||P|1|20260915090001<CR><ETX>6E<CR><LF>
← <STX>2L|1|I<CR><ETX>00<CR><LF>
```

## 8. Bẫy và lưu ý khi viết driver

1. **Checksum chữ thường / chữ hoa:** chuẩn là hex in hoa, nhưng nên chấp nhận cả chữ thường khi nhận; khi gửi luôn in hoa.
2. **Đuôi frame thiếu `<LF>` hoặc thừa ký tự** ở một số máy cũ — framer nên cắt theo ETX/ETB + 2 ký tự checksum, rồi bỏ qua CR/LF thay vì đòi đúng tuyệt đối.
3. **Không ACK trước khi kiểm checksum xong.** ACK nhầm frame hỏng = mất dữ liệu không dấu vết.
4. **FN modulo 8:** frame thứ 8 có FN `0`, không phải `8`. Bản tin dài (nhiều chỉ số huyết học) chắc chắn quay vòng.
5. **Frame trùng** sau khi ACK bị mất trên đường truyền: nhận lại cùng FN → ACK nhưng không ghi kết quả lần hai.
6. **Một bản tin có nhiều P/O**: máy chạy lô có thể gửi nhiều mẫu trong một phiên; R luôn thuộc O gần nhất phía trên.
7. **Phân cách lấy từ H.2**, không cố định. Một số máy dùng `!` hoặc `~` cho lặp.
8. **Universal Test ID khác nhau từng hãng** — `^^^100/1` (Roche kiểu cobas, có `/độ pha loãng`), `^^^10^^0` (Roche kiểu Elecsys), thành phần 7 (Cell-Dyn). Cấu hình theo máy, không đoán.
9. **Giá trị không phải số:** `<0.01`, `>500`, `NEG`, `1+`, `1:160`, dấu phẩy thập phân. Hệ cũ có cờ `VALhigher/VALlower` để giữ `<`/`>` — giữ nguyên chuỗi gốc, tách số riêng.
10. **Mã hóa:** chuẩn là ASCII; máy có thể gửi Latin-1/Windows-1252/Shift-JIS cho tên BN. Đọc theo byte, chỉ giải mã ở tầng record. Khi LIS gửi tên BN xuống máy: bỏ dấu tiếng Việt.
11. **Escape `&`:** giá trị chứa `|` `^` `\` `&` phải được máy thoát (`&F&`…). Khi gửi xuống máy cũng phải thoát.
12. **Tranh chấp:** LIS **luôn nhường** máy. Hàng đợi gửi của LIS phải chịu được việc bị hoãn ≥ 20 s.
13. **TCP:** nhiều máy giữ nguyên ENQ/ACK/checksum trên socket, một số bỏ (gửi record trần). Cấu hình theo máy; đừng giả định. Gói TCP có thể cắt giữa frame hoặc gộp nhiều frame — framer phải làm việc trên dòng byte.
14. **Rác trước STX** (nhiễu khi bật máy, ký tự XON/XOFF): bỏ qua cho tới STX hoặc ENQ.

## 9. Kiểm thử

- **Bộ bản tin mẫu:** các bản tin ở mục 7 (đã có checksum đúng) + mỗi máy ít nhất một bản ghi thô thật đã khử định danh.
- **Mô phỏng:** cặp cổng COM ảo (com0com trên Windows) hoặc socket loopback; một trình "máy giả" phát đúng chuỗi byte, có tùy chọn chèn lỗi.
- **Tình huống lỗi bắt buộc thử:**
  - checksum sai → LIS phải NAK, máy gửi lại cùng FN → nhận đúng một lần;
  - mất ACK (máy gửi lại frame trùng) → không ghi trùng kết quả;
  - máy im lặng giữa phiên > 30 s → LIS bỏ bản tin dở, về trạng thái rỗi;
  - tranh chấp ENQ cùng lúc → LIS nhường, gửi lại sau ≥ 20 s;
  - record > 240 ký tự tách ETB, chỗ tách rơi giữa trường;
  - FN quay vòng qua `7 → 0`;
  - nhận NAK cho ENQ của LIS → chờ ≥ 10 s rồi thử lại, tối đa 6 lần;
  - bản tin nhiều P/O trong một phiên;
  - hỏi chỉ định: có chỉ định / không có chỉ định / LIS trả lời chậm.

## 10. Nguồn

- ASTM E1381-02 *Standard Specification for Low-Level Protocol to Transfer Messages Between Clinical Laboratory Instruments and Computer Systems* — nay là **CLSI LIS1-A** (đã rút, vẫn là tham chiếu thực tế). Mục: establishment/transfer/termination phase, frame, checksum, timers, contention.
- ASTM E1394-97 *Standard Specification for Transferring Information Between Clinical Instruments and Computer Systems* — nay là **CLSI LIS2-A2**. Mục: record types, delimiters, field definitions.
- Bản chuẩn gốc phải mua từ ASTM/CLSI; tài liệu này viết từ hiểu biết chuẩn công khai, không chép nguyên văn.
- Đối chiếu một hiện thực cụ thể: *cobas e 411 analyzer Host Interface Manual, Version 2.2* (Roche, 11/2013; tệp `tai-lieu-hang/Roche_cobas_e411_Host_Interface_Manual_v2.2.pdf`, chi tiết ở `roche-cobas-e411.md`), phụ lục B (cổng serial, frame 247 byte) và D (pha thiết lập/truyền, contention, timeout) — bản công khai: https://community.senaite.org/uploads/short-url/rYdQadmOAcZfwvVi8FWLcSdgMSS.pdf
- Hiện thực sẵn có: `lis-agent/src/drivers.ts` (driver `generic-astm`: checksum mod 256 gồm ETX, đuôi CR LF, tự ACK, trả lời ENQ, `testIdComponent: 4`).
