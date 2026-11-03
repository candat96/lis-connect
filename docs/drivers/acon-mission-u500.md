# ACON Mission U500 / U120 — nước tiểu (máy đọc que)

> Trạng thái tài liệu: ⚠️ có bố cục bản in (U120 gửi ra cổng đúng nội dung bản in), thiếu khung truyền, tham số cổng chính thức và đặc tả "HL-7" của U500
> Kiểm chứng: chưa đối chiếu máy thật
> Driver: `mission-u500` (đề xuất; LIS cũ gọi protocol `U500`, driver `MissionU500`) — họ giao thức: văn bản dòng (U120) / HL7 (U500, theo tờ quảng cáo)

## 1. Tóm tắt

Hai máy đọc que nước tiểu của ACON: **U500** bán tự động 500 test/giờ, **U120** nhỏ 60–120 test/giờ. Cả hai có cổng RS-232C và USB để gửi dữ liệu. Với U120, sổ tay ghi: *khi bật Auto-print, dữ liệu in ra máy in trong cũng được gửi ra cổng RS232 và USB* — tức bản tin là **văn bản bản in**, mỗi chỉ số một dòng. U500 được quảng cáo "LIS Interface HL-7 compliant", đặc tả nằm ở một "Communication Insert" riêng chưa có. **Chỉ một chiều** (không thấy chức năng nhận chỉ định).

Ở BVĐK 10 máy khai tên **U120** (InsID 10, protocol `U500`), 9 mã đã map, **chưa nối, chưa từng gửi kết quả**.

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | RS-232C chuẩn (dùng chung cho đầu đọc barcode, truyền dữ liệu, cập nhật phần mềm) + USB (truyền dữ liệu) + cổng song song 25 chân (máy in ngoài) | Brochure U500; U120 Manual App. 1 |
| Dùng chung RS-232 | Đầu đọc barcode và truyền dữ liệu chung một cổng — hãng bán "Serial Splitter Cable" | U120 Manual, phụ kiện |
| Tham số cổng | U120: **Tài liệu không nêu — cần đo trên máy thật** (sổ tay bảo gọi hãng). U500: theo HDSD U500 bản lưu hành trên mạng: 4800/9600/19200 baud, 8 data bit, không parity, 1 stop bit, không điều khiển luồng — **chưa đối chiếu với bản chính hãng** | |
| Vai trò TCP | Không có cổng mạng | |
| Cài đặt trên máy (U120) | *Printer Setup → Auto-print = On* để mỗi kết quả tự gửi ra cổng; *Units* (Conventional/SI) quyết định cột đơn vị | U120 Manual (Setup), Section 6 |
| Đang dùng ở BVĐK 10 | Chưa cấu hình cổng | `Instrument.json` InsID 10 |

## 3. Khung truyền và bắt tay

Tài liệu không nêu — cần đo trên máy thật (ký tự đầu/cuối, có checksum hay không, có chờ ACK không).

Với U120: vì dữ liệu gửi ra là "dữ liệu in", nhiều khả năng là văn bản ASCII từng dòng kết thúc CR/LF, không bắt tay, không checksum; cắt bản tin theo khoảng lặng hoặc theo dòng `ID:` đầu bản in *(suy luận)*.

U500 "HL-7": nếu đúng HL7 v2 thì thường đóng khung MLLP `<VT>…<FS><CR>` *(suy luận)* — xem tài liệu HL7 dùng chung khi có.

## 4. Bản tin kết quả (máy → LIS)

Bố cục **bản in U120** (Appendix 4 — Result Print-Out), theo thứ tự dòng:

| Dòng | Nội dung | Ví dụ |
|---|---|---|
| Mã BN / mẫu | `ID: <mã>` (nhập tay hoặc barcode, tối đa 25 ký tự theo brochure U500) | `ID: 260915001` |
| Ngày giờ | `Date: YYYY-MM-DD HH:MM` (định dạng ngày theo cài đặt: MM-DD-YY / YY-MM-DD / DD-MM-YY; giờ 24h/12h) | `Date: 2026-09-15 08:45` |
| Người làm | `Operator: NN` | `Operator: 01` |
| Số thứ tự | `No. NNNNNN` | `No. 000011` |
| Mỗi chỉ số | `[*]<mã> <ký hiệu> <giá trị đơn vị>` | `*GLU 1+ 250 mg/dL` |

- **Thứ tự chỉ số** theo loại que đang dùng (máy tự nhận) và có thể đổi trong cài đặt "thứ tự thông số" (U500).
- **Ký hiệu Arbitrary luôn in** (`-`, `±`, `1+`, `2+`, `3+`); kèm Conventional **hoặc** SI tùy cài đặt Units.
- **Cờ bất thường:** dấu `*` đứng trước mã chỉ số.
- Mã mẫu nằm ở dòng `ID:`; nếu không nhập ID thì chỉ có `No.` *(suy luận)*.
- QC: U120 có chế độ QC test riêng; cách đánh dấu trong bản tin — tài liệu không nêu.

Khoảng cách cột, ký tự đệm, có dòng tiêu đề/ký tự điều khiển máy in hay không: tài liệu không nêu — cần đo trên máy thật.

## 5. Hỏi chỉ định / gửi worklist

Chỉ một chiều.

## 6. Bảng mã chỉ số

**Đang map ở BVĐK 10** (InsID 10 "U120", 9 mã):

| Mã máy | Chỉ số | Đơn vị (LIS) | Mức đọc (Conventional, U120 App. 3) |
|---|---|---|---|
| `LEU` | Bạch cầu | Leu/µL | neg / ±15 / 1+ 70 / 2+ 125 / 3+ 500 Leu/µL |
| `NIT` | Nitrite | — | neg / pos |
| `URO` | Urobilinogen | mg/dL | 0.2 / ±1 / 1+ 2 / 2+ 4 / 3+ 8 mg/dL |
| `PRO` | Protein | mg/dL | neg / ±15 / 1+ 30 / 2+ 100 / 3+ 300 mg/dL |
| `PH` | pH | — | 5.0 – 9.0 bước 0.5 (máy in `pH`) |
| `BLO` | Hồng cầu | Ery/µL | neg / ±10 / 1+ 25 / 2+ 80 / 3+ 200 Ery/µL |
| `SG` | Tỷ trọng | — | 1.000 – 1.030 bước 0.005 |
| `KET` | Ketone | mg/dL | neg / ±5 / 1+ 15 / 2+ 40 / 3+ 80 mg/dL |
| `GLU` | Glucose | mg/dL | neg / ±100 / 1+ 250 / 2+ 500 / 3+ 1000 mg/dL |

Chưa map: `BIL` (neg / 1+ 1 / 2+ 2 / 3+ 4 mg/dL) có trên que 10U nhưng LIS cũ không khai; que U500 còn có `ASC` (vitamin C), `ALB`, `CRE`, `Ca`.

Đơn vị SI tương ứng (khi đặt Units = SI): GLU mmol/L, KET mmol/L, BIL µmol/L, URO µmol/L, PRO g/L.

## 7. Bản tin mẫu

Bản in U120 theo Appendix 4, thay dữ liệu bằng dữ liệu giả (khoảng trắng giữa các cột là minh họa, chưa biết số lượng thật):

```
ID: 260915001
Date: 2026-09-15 08:45
Operator: 01
No. 000011
LEU  -   neg
NIT  -   neg
URO  -   0.2 mg/dL
PRO  -   neg
pH       5.5
BLO  -   neg
SG       1.030
KET  ±   5 mg/dL
BIL  -   neg
*GLU 1+  250 mg/dL
```

Ký tự kết thúc dòng và ký tự đầu/cuối bản tin: chưa biết.

## 8. Bẫy và lưu ý khi viết driver

1. **Mã in ra có thể khác mã LIS cũ map:** bản in ghi `pH` còn LIS cũ map `PH` → so khớp mã không phân biệt hoa thường.
2. **Dấu `*` dính liền mã** (`*GLU`) là cờ bất thường — tách ra trước khi tra mã.
3. **Ký hiệu `±`** không phải ASCII — mã hóa thực tế (Latin-1 0xB1? CP437 0xF1?) phải đo trên máy. Đọc byte, không giả định UTF-8.
4. **Giá trị có dấu `-`** đứng trước (`- neg`, `- 0.2 mg/dL`) là ký hiệu Arbitrary "âm", không phải số âm.
5. Đổi Units trên máy là đổi đơn vị gửi lên; đơn vị LIS luôn thắng (quy tắc hệ cũ) nhưng giá trị số thì đổi theo — phải khóa cài đặt Units hoặc kiểm tra đơn vị nhận về.
6. RS-232 dùng chung với đầu đọc barcode — nếu bệnh viện dùng barcode reader cần cáp chia; cắm nhầm thì không nhận được gì.
7. Chỉ gửi khi Auto-print bật (U120); tắt Auto-print thì phải bấm in từng mẫu.
8. U120 và U500 có thể khác hẳn nhau về bản tin (U500 quảng cáo HL7). Xác định đúng model ở BVĐK 10 — CSDL ghi tên `U120` nhưng protocol `U500`.

## 9. Kiểm thử

- Bản ghi thô: một mẫu bình thường, một mẫu nhiều cờ `*`, một mẫu không nhập ID, gửi lại cả bộ nhớ ("all the data in database" — U500), cả Conventional và SI.
- Mô phỏng: phát lại byte thô qua COM ảo; thử bản in dính liền nhau không khoảng lặng.

## 10. Nguồn

- `tai-lieu-hang/ACON_Mission_U500_Urine_Analyzer.pdf` (brochure PN 2170004201, 07/2017) và `tai-lieu-hang/ACON_Mission_U500_Urine_Analyzer_US.pdf` (PN 22200027501, 02/2020) — cổng, "LIS Interface HL-7 compliant", tổ hợp que. Tải từ aconlabs.com. **Không phải tài liệu giao thức.**
- *Mission U120 Urine Analyzer Instruction Manual* (bản US, ACON) — Section 6 *Data/Communication*, Appendix 1 (cổng), Appendix 3 (bảng mức đọc), **Appendix 4 Result Print-Out**. Bản công khai do nhà phân phối lưu: https://palliance.se/wp-content/uploads/sites/6/2019/09/1150459703-Mission-U120-New-Manual-US.pdf
- Tham số cổng U500 (4800/9600/19200, 8N1): từ HDSD U500 bản lưu hành trên Scribd (https://www.scribd.com/document/353635623/Urine-Analyzer-U500-Manual) — chưa đối chiếu bản chính hãng.
- `khao-sat/lis-hien-huu.md`, `khao-sat/seed/du-lieu/Instrument.json` (InsID 10), `khao-sat/seed/anh-xa-ma-may.json`.
- **Cần xin hãng ACON / nhà phân phối:** "Communication Insert" (đặc tả định dạng dữ liệu LIS) của Mission **U120** và **U500** — gồm tham số RS-232, khung truyền, bố cục bản tin, bảng mã ký tự.
