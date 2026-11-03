# Tosoh HLC-723 GX / G11 (và định dạng G8/G9/G7) — HbA1c (sắc ký lỏng HPLC)

> Trạng thái tài liệu: ✅ **HLC-723GX** — có đặc tả gốc *HLC-723GX Host Connection Specifications Rev.2* (khung, BCC, hỏi chỉ định, 5 định dạng kết quả, cờ). ⚠️ **HLC-723 G11** — chưa có tài liệu riêng; áp đặc tả GX ở định dạng **G8** (định dạng GX cho chọn để tương thích các đời G7/G8/G9) *(suy luận — cần xác nhận G11 có cùng menu TRANS MODE)*
> Kiểm chứng: chưa đối chiếu máy thật (LIS cũ chỉ nhận **1** kết quả từ G11; GX chưa từng gửi)
> Driver: `tosoh-g8` (dùng cho cả GX và G11) — họ giao thức: tosoh-g8 (khối `STX…ETX BCC`, ACK/NAK từng khối, `EOT`; trường rộng cố định)

## 1. Tóm tắt

Máy HPLC đo HbA1c và các phân đoạn Hb. BVĐK 10 có hai máy Tosoh: **G11** (InsID 18, RS-232 COM5 trạm DK10.2, 9600 8N1) và **HLC-723GX** (InsID 5, LIS cũ khai protocol `ASTM` — **sai**, GX dùng giao thức handshake riêng của Tosoh). Máy gửi mỗi kết quả trong một khối `<STX>text<ETX><BCC>`, LIS trả ACK/NAK, máy gửi `EOT` khi hết. **Hai chiều**: khi bật QUERY, máy đọc mã vạch rồi hỏi `G,<ID>`, LIS trả `A,<ID>,…` trong 30 s. Điểm khó nhất: bố cục trường đổi theo 5 cài đặt trên máy (TRANS MODE, số chữ số số mẫu, số chữ số ID, PERCENT DECIMAL, TRANS COMPONENT/TRANS CONFIG) — driver phải cấu hình theo đúng máy.

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Chuẩn | EIA-574 (EIA-232, RS-232C), bất đồng bộ, **bán song công** | GX §2 (1) |
| Baud | 1200 / 2400 / 4800 / 9600 | GX §2, §3 |
| Data bits / Parity / Stop | 7 hoặc 8 / even, odd, none / 1 hoặc 2 | GX §2, §3 |
| Mã ký tự | ASCII | GX §2 |
| Đầu nối | Máy: D-SUB 9 chân **đực**; đầu cáp phía máy: D-SUB 9 cái | GX §2 (3) |
| Chân (máy 9 chân → host 9/25 chân) | 2 RxD→2/3 · 3 TxD→3/2 · 4 DTR→4/20 · 5 SG→5/7 · 6 DSR→6/6 · 7 RTS→7/4 · 8 CTS→8/5 (bảng hãng ghi tên tín hiệu cùng phía; cáp thật là chéo hay thẳng: **Tài liệu không nêu — cần đo trên máy thật**) | GX §2 (4) |
| Bắt tay phần cứng | Tài liệu có nối DTR/DSR/RTS/CTS nhưng không nói có dùng hay không — cần đo | GX §2 (4) |
| TCP | Không có. Có thể qua bộ chuyển serial–LAN: dự án mã nguồn mở `G8TcpClient` dùng Moxa NPort, LIS là TCP client nối cổng **4003** của NPort *(nguồn mã nguồn mở)* | https://github.com/BaltringusMaximus/G8TcpClient |
| Cài đặt (màn **RS232C**) | `SMP 3/5/8` (số chữ số số mẫu, khuyến nghị 8); `BC 13/18/20/NO` (số chữ số ID, khuyến nghị 20); data length; stop bit; parity; baud; **Query with ID** (bật hỏi chỉ định); **Automatically transmitted** (tự gửi kết quả) | GX §3 |
| Cài đặt (màn **PARAMETER**) | `TRANS MODE` = GX FORM (mặc định) / G9 / G8 / G7 / GHb-V / GHb-V(SP); `RP MODE` = **NO** (YES chỉ khi nối phần mềm 501RP); `PERCENT DECIMAL`; `TRANS COMPONENT`; `TRANS CONFIG`; `#ERR TRANS FORM`; `SMP MODE` | GX §4, §6 |
| G11 ở BVĐK 10 | COM5 trạm DK10.2, 9600 8N1. Thông tin thu thập về G11: RS-232C bán song công 1200–19200, 7/8 bit — chưa có tài liệu gốc | `InterfacePara`; thông tin đặc tính máy |

## 3. Khung truyền và bắt tay

| Mã | Hex | Ý nghĩa |
|---|---|---|
| STX | 0x02 | Đầu văn bản |
| ETX | 0x03 | Cuối văn bản |
| EOT | 0x04 | Kết thúc truyền |
| ACK | 0x06 | Nhận đúng |
| NAK | 0x15 | Nhận sai |

Một phiên (bên gửi có thể là máy hoặc LIS):

```
Bên gửi  <STX> text <ETX> <BCC>   → Bên nhận <ACK> (hoặc <NAK>)
         … (mỗi bộ dữ liệu một khối)
Bên gửi  <EOT>
```

- **BCC** = XOR từng ký tự từ đầu text (sau STX) đến **hết ETX**, 1 byte nhị phân, giá trị 0x00–0x7F. BCC **có thể trùng mã điều khiển** (vd 0x15 = NAK, 0x04 = EOT) — đọc byte ngay sau ETX luôn là BCC, không diễn giải nó. Ví dụ tính bằng script ở mục 7.
- NAK → máy gửi lại khối, tối đa **5 lần**; quá 5 lần máy gửi EOT và khởi tạo lại.
- Toàn bộ từ đầu text đến BCC phải truyền xong trong **20 s**; không có ACK/NAK trong **20 s** → máy gửi EOT, khởi tạo lại.
- Nhiều bộ dữ liệu: mỗi khối được ACK riêng; EOT sau khối cuối.
- Hãng lưu ý: host phải tự lọc byte rác do nhiễu.
- Contention: bán song công; luồng hỏi–đáp cho thấy LIS chỉ gửi sau khi máy đã gửi EOT *(suy luận)*.

## 4. Bản tin kết quả (máy → LIS)

Chọn ở `TRANS MODE`. Hãng khuyến nghị **GX** hoặc **G8**. G9 và G7 **giống hệt G8**. Mọi giá trị phần trăm căn phải, dạng `0.0–100.0` hoặc `0.00–100.00` theo PERCENT DECIMAL; IFCC (mmol/mol) dạng `0–99999`, `0–999999` hoặc `0.0–9999.9`.

### 4.1. Định dạng G8 (= G9 = G7) — khuyến nghị cho driver chung GX/G11

| Trường | Rộng | Nội dung |
|---|---|---|
| Chế độ đo | 1 | `1` = VAR (cố định) |
| Vị trí lấy mẫu | 1 | `2` = khay (cố định) |
| Số mẫu | 3 / 5 / 8 | theo `SMP x`; 8 chữ số = `MMDD` + số mẫu; mẫu thường 0001–7999, **chuẩn (CALIB) 9001–9999** |
| Thành phần 1…10 | 5 (1 số lẻ) hoặc 6 (2 số lẻ) mỗi trường | theo TRANS COMPONENT (bảng dưới) |
| Cờ | 2 | `00` bình thường, `01`–`99` mã cờ (mục 4.4) |
| Sample ID | 0 / 13 / 18 / 20 | mã vạch, căn trái đệm khoảng trắng, không bỏ số 0 đầu |

TRANS COMPONENT (G8/G9/G7 chỉ có 2 kiểu):

| Vị trí | `1234567--T--` | `23456789AT--` |
|---|---|---|
| 1 | FP% | A1a% |
| 2 | A1a% | A1b% |
| 3 | A1b% | F% |
| 4 | F% | LA1c% |
| 5 | LA1c% | **SA1c%** (hoặc IFCC nếu TRANS CONFIG = `SA1C->IFCC`) |
| 6 | **SA1c%** (hoặc IFCC) | A0% |
| 7 | A0% | H-V0% |
| 8 | dự trữ | H-V1% |
| 9 | dự trữ (hoặc IFCC nếu `BLK->IFCC`) | H-V2% |
| 10 | Total A1% (hoặc IFCC nếu `TA1->IFCC`) | Total A1% (hoặc IFCC) |

Độ dài text G8 (SMP 8, BC 20, 1 số lẻ): 1+1+8+10×5+2+20 = **82** ký tự.

### 4.2. Định dạng GX (mặc định của GX)

Tên máy 12 (`HLC-723GX` đệm SP) · chế độ 1 · vị trí 1 · **loại mẫu 1** (`0` mẫu, `1` chuẩn) · số lẻ 1 (`1`/`2`) · số mẫu 3/5/8 · 12 thành phần × 6 · Total A1% 6 · **SA1c(JDS)% 6 · SA1c(NGSP)% 6** · SA1c(Mono S)% 6 · 4 dự trữ × 6 · **SA1c(IFCC) 5** · loại chuẩn 2 (`00` JDS, `01` NGSP, `02` IFCC(JDS), `03` IFCC(NGSP), `04` Mono S, `05` IFCC(Mono S), `06` NGSP(JDS), `07` JDS(NGSP)) · dự trữ 2 · **cờ 2** · dự trữ 4 · Sample ID 0/13/18/20. TRANS COMPONENT ở GX chỉ có `23456789A---` hoặc `1234567-----` (thứ tự như bảng 4.1, không có Total A1 ở vị trí 10).

Ưu điểm GX: có sẵn SA1c theo NGSP **và** IFCC trong cùng bản tin, có cờ "loại mẫu" phân biệt chuẩn. Nhược: chỉ máy GX có *(suy luận — G11 có thể có định dạng riêng của G11)*.

### 4.3. Định dạng GHbV (tương thích A1c 2.2)

Số mẫu · 9 thành phần · **cờ 5 ký tự** (căn trái) · thành phần 10 · Sample ID. Thứ tự thành phần khác G8 (bảng 6.2.5 của hãng). Không khuyến nghị.

### 4.4. Mẫu thường / chuẩn / cờ

- **Chuẩn (calibrator)**: số mẫu 9001–9999 (G8: nhận qua số mẫu; GX: thêm trường loại mẫu = `1`). Chuẩn không hỏi chỉ định. **QC**: tài liệu không có trường riêng — QC chạy như mẫu thường, phân biệt bằng Sample ID/vị trí do phòng XN quy ước *(suy luận)*.
- **Cờ**: mỗi cờ trên màn FLAG đặt mức 0 hoặc 1; nhiều cờ cùng lúc chỉ gửi **một mã** (mức 1 ưu tiên, cùng mức thì dòng thấp nhất trong bảng FLAG). Mức 0: vẫn gửi giá trị. **Mức 1: giá trị bị thay bằng khoảng trắng hoặc 0** (theo `#ERR TRANS FORM` = SPACE/ZERO) — driver **không được** lưu `0.0` như kết quả thật khi cờ ≠ `00`.

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

Bật `Query with ID` trên màn RS232C. Máy đọc mã vạch → gửi hỏi → chờ trả lời **30 s**; quá hạn coi như không có chỉ định.

Máy → LIS: `G` · `,` · Sample ID 20 (căn trái, đệm SP).

LIS → máy (gửi sau khi máy đã gửi EOT, theo cùng handshake):

| Trường | Rộng | Nội dung |
|---|---|---|
| `A` | 1 | mã trả lời |
| `,` | 1 | |
| Sample ID | 20 | đúng ID máy hỏi, căn trái |
| `,` | 1 | |
| Loại mẫu | 2 | `00` máu toàn phần, `01` đã pha loãng |
| Loại ống | 2 | `00` ống nguyên, `01` cốc mẫu |
| Dự trữ | 2 | SP |
| `,` | 1 | |
| Mục A1c | 2 | `00` không yêu cầu, `01` yêu cầu |
| Dự trữ | 6 | SP |
| `,` | 1 | |
| Dự trữ | 8 | SP |

Loại mẫu/loại ống chỉ có hiệu lực khi `SMP MODE` = HOST; các chế độ khác máy tự quyết theo cảm biến. "Không có chỉ định" → trả `A1c = 00` hoặc không trả (để máy hết 30 s) *(suy luận — tài liệu chỉ nêu hết giờ)*.

## 6. Bảng mã chỉ số

Không có mã chỉ số trong bản tin — kênh xác định **theo vị trí** và cấu hình TRANS COMPONENT/TRANS CONFIG.

| Kênh driver | Vị trí (G8, kiểu `23456789AT--`) | Chỉ số | Đơn vị | Map LIS cũ |
|---|---|---|---|---|
| `SA1c` | thành phần 5 | HbA1c ổn định (NGSP hoặc JDS theo cài đặt chuẩn) | % | `A1C` → `239HBA` (InsID 5, 18) |
| `IFCC` | thành phần 5/9/10 khi TRANS CONFIG chọn | HbA1c IFCC | mmol/mol | chưa map |
| `F` | thành phần 3 | HbF | % | chưa map |
| `LA1c` | 4 | HbA1c không ổn định | % | không map |
| `A1a`, `A1b`, `A0`, `H-V0..2`, `TA1` | 1, 2, 6, 7–9, 10 | phân đoạn / biến thể / tổng A1 | % | không map |
| `FLAG` | sau thành phần 10 | mã cờ | — | lưu làm cờ |

## 7. Bản tin mẫu

Dữ liệu giả, định dạng **G8**, SMP 8, BC 20, 1 số lẻ, TRANS COMPONENT `23456789AT--`. BCC tính bằng script Python (XOR từ sau STX đến hết ETX). `␣` = khoảng trắng.

```
Máy  <STX>1211030042␣␣0.7␣␣0.8␣␣1.4␣␣2.0␣␣5.8␣84.6␣␣0.0␣␣0.0␣␣0.0␣␣6.9009900000123␣␣␣␣␣␣␣␣␣␣<ETX><0x15>
LIS  <ACK>
Máy  <EOT>
```

Đọc: `1` VAR · `2` khay · `11030042` (03/11, mẫu 0042) · A1a 0.7 · A1b 0.8 · F 1.4 · LA1c 2.0 · **SA1c 5.8** · A0 84.6 · V0/V1/V2 0.0 · TA1 6.9 · cờ `00` · ID `9900000123`. BCC = **0x15** — trùng mã NAK, minh họa đúng cảnh báo của hãng.

Hỏi chỉ định (dữ liệu giả):

```
Máy  <STX>G,9900000123␣␣␣␣␣␣␣␣␣␣<ETX><0x68>          LIS <ACK>
Máy  <EOT>
LIS  <STX>A,9900000123␣␣␣␣␣␣␣␣␣␣,0001␣␣,01␣␣␣␣␣␣,␣␣␣␣␣␣␣␣<ETX><0x42>   Máy <ACK>
LIS  <EOT>
```

(`0001` = máu toàn phần + cốc mẫu; `01` = yêu cầu A1c.)

## 8. Bẫy và lưu ý khi viết driver

1. **BCC có thể là 0x02/0x03/0x04/0x06/0x15** — bộ tách khung phải đọc đúng 1 byte sau ETX làm BCC, không coi là ký tự điều khiển.
2. **Bố cục phụ thuộc cài đặt**: TRANS MODE, SMP 3/5/8, BC 0/13/18/20, PERCENT DECIMAL (độ rộng 5 hay 6), TRANS COMPONENT, TRANS CONFIG. Cấu hình driver phải khai đủ 6 tham số này và **kiểm tổng độ dài text** khớp, lệch thì báo lỗi cấu hình chứ không đọc bừa.
3. **Cờ mức 1 → giá trị rỗng hoặc 0**: không lưu 0 thành kết quả.
4. **Chuẩn 9001–9999** — loại khỏi kết quả bệnh nhân.
5. **InsID 5 (GX) khai `ASTM` trong LIS cũ là sai** — GX nói giao thức Tosoh, phải dùng `tosoh-g8`.
6. **G11 ở LIS cũ** đọc giá trị ở vị trí 72 (dài 6) và SID ở 156 (dài 8), bản ghi ≥ 164 byte — **không khớp** G8 (82 ký tự). Tính thử định dạng GX (SMP 8, có STX ở vị trí 0): thành phần 9 ở 73, Sample ID ở 160 — cũng không khớp hẳn. Kết luận: G11 ở BVĐK 10 đang chạy một định dạng khác (có thể định dạng riêng của G11, SMP/BC khác) *(suy luận)* → **chụp bản ghi thô trước**, rồi chọn TRANS MODE = G8 nếu G11 có.
7. **SID 8 byte** trong cấu hình cũ → đặt máy `BC 20` để nhận đủ mã vạch 9 chữ số.
8. RP MODE phải là **NO**; YES là giao thức riêng cho phần mềm 501RP.
9. Driver cũ khai "kiểu dòng" không ACK — sai: máy không nhận ACK sẽ gửi EOT sau 20 s và **không gửi tiếp** kết quả.

## 9. Kiểm thử

- Bản ghi thô từ cả GX và G11: mẫu thường, mẫu có cờ mức 0 và mức 1 (SPACE và ZERO), một lượt chuẩn (9001+), nhiều mẫu liên tiếp (nhiều khối trước EOT), một lần hỏi chỉ định có và không có trả lời.
- Bộ test BCC: ví dụ mục 7 (0x15, 0x68, 0x42); khối có BCC = 0x03/0x04.
- Lỗi: LIS gửi NAK → máy gửi lại tối đa 5 lần; LIS im 20 s → máy EOT; byte rác trước STX; hai khối dính trong một lần đọc cổng; độ dài text sai so với cấu hình.
- Mô phỏng: phát lại byte thô qua cặp COM ảo; script đóng vai máy có ACK/NAK.

## 10. Nguồn

- *HLC-723GX Host Connection Specifications*, Rev.2 (Tosoh Bioscience Division, 13/04/2012) — §2 (phần cứng, chân), §3 (màn RS232C), §4 (TRANS MODE, RP MODE), §5 (handshake, BCC, gửi lại, timeout), §6.1 (hỏi chỉ định), §6.2.1–6.2.5 (định dạng GX/G8/G9/G7/GHbV), §6.2.6 (cờ). Tệp: `tai-lieu-hang/Tosoh_HLC-723GX_Host_Connection_Specifications_Rev2.pdf`.
- Mã nguồn mở (tham khảo, không phải tài liệu hãng): `tosoh_host_com` — https://github.com/nishishailesh/tosoh_host_com (G8: khối STX…ETX+BCC, ACK mỗi khối, EOT, 9600; khớp với đặc tả GX); `G8TcpClient` — https://github.com/BaltringusMaximus/G8TcpClient (G8 qua Moxa NPort, cổng 4003).
- LIS cũ: `lis-agent/src/drivers.ts` (`tosoh-g11`); `khao-sat/seed/du-lieu/Instrument.json` InsID 5, 18; `InterfacePara.json`; `khao-sat/seed/anh-xa-ma-may.json`.
- **Còn thiếu:** tài liệu host interface / Operator's Manual của **HLC-723 G11** (bản VAR có trên pdfcoffee: https://pdfcoffee.com/17operatorsmanual-hlc-723g11-var-revb-pdf-free.html — người dùng tự tải) để xác nhận G11 có TRANS MODE = G8 và định dạng riêng của G11.
