# Mindray BC-5380 / BC-5180 — huyết học (CBC + 5DIFF)

> Trạng thái tài liệu: ✅ đủ để viết driver (Phụ lục C sổ tay vận hành có đặc tả HL7 đầy đủ, kèm bản tin mẫu)
> Kiểm chứng: chưa đối chiếu máy thật
> Driver: `mindray-bc5380` — họ giao thức: `hl7-mllp` (HL7 v2.3.1, UTF-8, qua TCP)

## 1. Tóm tắt

- Máy đếm tế bào máu 5 thành phần bạch cầu.
- Nối với LIS **chỉ qua mạng (TCP)**; không có cổng nối tiếp cho LIS. Máy làm TCP client (mặc định) hoặc TCP server.
- Giao thức HL7 v2.3.1 đóng khung MLLP, chữ mã hóa UTF-8.
- **Hai chiều:**
  - máy đẩy kết quả `ORU^R01`, LIS trả `ACK^R01`;
  - khi quét/nhập mã mẫu, máy hỏi chỉ định bằng `ORM^O01`, LIS trả `ORR^O02`.
- Điểm khó:
  - Bản tin rất dài, vì có ảnh histogram/scattergram mã Base64 trong OBX kiểu `ED`.
  - Một bản tin có thể chứa nhiều cặp OBR (đếm máy + soi tay).
  - Mã chỉ số trộn LOINC (`LN`) và mã riêng Mindray (`99MRC`).

## 2. Kết nối vật lý

| Mục | Giá trị |
|---|---|
| Cổng | Ethernet (RJ45) trên máy tính đi kèm máy (IPU). "Only support network port communication" |
| Vai trò TCP | Mặc định **máy là client**: điền IP + cổng của LIS. Bật "IPU as server" thì máy **nghe** ở cổng khai báo, LIS nối vào |
| Cổng mặc định | Tài liệu không nêu — người cài đặt tự chọn (cả hai phía khai giống nhau) |
| Kết nối | Máy giữ kết nối thường trực; mất kết nối thì tự nối lại. Thoát phần mềm máy / đổi cài đặt thì đóng kết nối |
| Menu cài đặt | Setup → Communication (sổ tay vận hành mục 5.3, sổ tay bảo trì mục 3.6.1) |

Các tùy chọn trên máy mà driver phải biết:

| Tùy chọn trên máy | Ảnh hưởng tới driver |
|---|---|
| Protocol type = "HL7+UTF8" | Chỉ có lựa chọn này |
| Auto-communication (Auto Transmission) | Bật thì có kết quả là gửi ngay; tắt thì người dùng gửi tay hàng loạt ở màn List Review/QC |
| Bidirectional LIS/HIS communication | Bật thì máy gửi `ORM^O01` hỏi chỉ định |
| ACK synchronous / Communication Acknowledgement | Bật (khuyến nghị): máy chờ ACK, quá hạn thì báo lỗi gửi. Tắt: máy coi như gửi xong, không cần ACK |
| ACK overtime | Thời gian chờ ACK (mặc định 10 giây theo Phụ lục C) |
| Histogram transmission | Not transmit / Bitmap (mặc định) / Data (dữ liệu thô) |
| Scattergram transmission | Not transmit / Bitmap (mặc định) / Data |
| L-J QC results transmitted in the format of sample results | Bật thì QC L-J gửi như mẫu thường (MSH-11 = `P`), dễ lẫn với mẫu bệnh nhân |
| Version 1.0 / 1.1 | Bản 1.1 thêm các cờ bất thường hệ thống (WBC/DIFF/RBC system abnormal, aspiration abnormal, RBC clump) |

**Khuyến nghị cài đặt khi triển khai:**
- Auto-communication: On.
- Bidirectional: On, nếu cơ sở dùng barcode.
- ACK: On.
- Histogram: Not transmit. Scattergram: Not transmit. Chỉ bật khi cần in biểu đồ lên phiếu, vì mỗi ảnh BMP Base64 nặng hàng chục KB.
- QC L-J: Off.

## 3. Khung truyền và bắt tay

**MLLP**

| Phần | Byte | Ghi chú |
|---|---|---|
| Đầu khối `<SB>` | `0x0B` (VT) | Không nhầm với STX/SOH |
| Dữ liệu | Bản tin HL7, UTF-8 | Các segment ngăn nhau bằng `<CR>` (`0x0D`) |
| Cuối khối `<EB>` | `0x1C` (FS) | Không nhầm với ETX/EOT |
| Kết thúc | `0x0D` (CR) | |

- Không có checksum. TCP lo tính toàn vẹn.
- Một kết nối TCP chở nhiều bản tin nối tiếp nhau. Driver phải tự cắt theo `0x0B … 0x1C 0x0D`, vì một lần đọc socket có thể nhận nửa bản tin hoặc nhiều bản tin.

**Bắt tay**

- Đồng bộ từng bản tin: máy gửi một bản tin, chờ xác nhận tối đa **10 giây**, rồi mới gửi bản tin kế tiếp.
- Quá 10 giây thì máy coi bản tin đó gửi lỗi và **bỏ qua, sang bản tin sau**. Máy **không gửi lại**.
- Hệ quả cho LIS: phải ACK nhanh. Nên ghi bản tin thô xuống đĩa trước rồi ACK, xử lý nghiệp vụ sau.
- Hỏi chỉ định: máy chờ `ORR^O02` tối đa 10 giây. Quá hạn thì máy coi là hỏi thất bại.
- ACK gồm hai segment:
  - `MSH`, với `MSH-9 = ACK^R01`, `MSH-11` lặp lại giá trị của bản tin nhận (`P` hoặc `Q`);
  - `MSA`, với `MSA-1` là mã nhận và `MSA-2` bằng `MSH-10` của bản tin nhận.

| `MSA-1` | Nghĩa | Mã lỗi `MSA-6` (tùy chọn) |
|---|---|---|
| `AA` | Nhận thành công | `0` |
| `AE` | Lỗi nội dung | `100` sai thứ tự segment, `101` thiếu trường bắt buộc, `102` sai kiểu dữ liệu, `103` không có trong bảng mã |
| `AR` | Từ chối | `200` không hỗ trợ loại bản tin, `201` không hỗ trợ event, `202` không hỗ trợ processing ID, `203` không hỗ trợ version, `204` không biết khóa (mã BN), `205` trùng khóa, `206` bản ghi bị khóa, `207` lỗi nội bộ |

**Ký tự phân cách và thoát**
- Phân cách: `|` trường, `^` thành phần, `&` thành phần con, `~` lặp, `\` thoát.
- Chuỗi chứa ký tự phân cách được thoát thành `\F\ \S\ \T\ \R\ \E\`. Xuống dòng trong chuỗi được thoát thành `\.br\`.
- Driver phải bỏ thoát khi đọc. Khi gửi `ORR^O02` (họ tên, chẩn đoán, ghi chú) thì phải thoát.

## 4. Bản tin kết quả (máy → LIS)

**Cấu trúc `ORU^R01`**

```
MSH
{ PID
  [PV1]
  { OBR
    {OBX} } }
```

Một bản tin thường có một `PID`. Bên dưới có `OBR|1` (`00001` Automated Count) và có thể thêm `OBR|2` (`00002` Manual Count, kết quả soi tay).

**MSH**

| Trường | Ý nghĩa | Ví dụ |
|---|---|---|
| MSH-3 | Ứng dụng gửi: "Analyzer" hoặc tên máy ("BC-5300"…) | `Analyzer` |
| MSH-4 | Cơ sở gửi | `Mindray` |
| MSH-7 | Thời điểm tạo bản tin `YYYYMMDDHHMMSS` (giờ máy) | `20080617143943` |
| MSH-9 | Loại bản tin | `ORU^R01` |
| MSH-10 | Mã bản tin, dùng để ACK | `1` |
| MSH-11 | `P` mẫu/hỏi chỉ định · `Q` kết quả QC · `D` cài đặt QC · `T` kết quả QC *(bảng hãng ghi lẫn Q/T; driver nhận cả hai là QC)* | `P` |
| MSH-12 | Phiên bản | `2.3.1` |
| MSH-18 | Bộ ký tự | `UNICODE` (thực chất UTF-8) |

**PID / PV1**

| Trường | Ý nghĩa |
|---|---|
| PID-3 | Mã bệnh nhân `ID^^^^MR`. Trong bản tin QC đây là số lô QC |
| PID-5 | Họ tên `Họ^Tên` |
| PID-7 | Ngày sinh `YYYYMMDDHHMMSS`. Trong bản tin QC đây là hạn dùng |
| PID-8 | Giới tính, dạng chữ (`Male`/`Female`/tùy biến) |
| PV1-3 | `Khoa^^Giường` |

**OBR**

| Trường | Ý nghĩa |
|---|---|
| OBR-3 | **Mã mẫu (sample ID/barcode)** trong bản tin kết quả. Đây là khóa để khớp với chỉ định |
| OBR-4 | Loại kết quả (xem bảng ở mục 6) |
| OBR-6 | Thời điểm lấy mẫu |
| OBR-7 | **Thời điểm chạy máy** |
| OBR-10 | Người lấy/giao mẫu |
| OBR-13 | Thông tin lâm sàng/chẩn đoán |
| OBR-14 | Thời điểm nhận mẫu |
| OBR-15 | Nguồn mẫu: `BLDV` máu tĩnh mạch, `BLDC` máu mao mạch |
| OBR-22 | Thời điểm duyệt trên máy |
| OBR-24 | `HM` (huyết học) |
| OBR-28 | Người duyệt |
| OBR-32 | Người chạy máy (với QC là người cài đặt) |

**OBX**

| Trường | Ý nghĩa |
|---|---|
| OBX-2 | `NM` số, `ST` chuỗi, `IS` mã liệt kê, `ED` dữ liệu đóng gói (ảnh/biểu đồ Base64) |
| OBX-3 | `Mã^Tên^Hệ mã`. **Khóa map = Mã + Hệ mã**, tên chỉ để hiển thị |
| OBX-5 | Giá trị. Có thể rỗng (máy không đo chỉ số đó), hoặc `***.*` khi kết quả không hợp lệ hay vượt dải |
| OBX-6 | Đơn vị theo ISO: `10*9/L`, `10*12/L`, `g/L`, `fL`, `pg`, `%`, `mL/L`… |
| OBX-7 | Khoảng tham chiếu do máy cài: `thấp-cao`, `<cao` hoặc `>thấp` |
| OBX-8 | Cờ: `N` bình thường, `A` bất thường, `H` cao, `L` thấp. Có thể ghép bằng `~`, ví dụ `H~A` |
| OBX-11 | `F` kết quả cuối |
| OBX-13 | Dấu sửa: `O` thuốc thử hết hạn, `E` sửa chủ động, `e` sửa bị động. Ghép dạng `O-E` |

**QC**
- MSH-11 = `Q`.
- Mỗi điểm QC là một nhóm `PID` + `OBR` + `OBX`. PID-3 là số lô, OBR-3 là số file QC, OBR-4 là loại QC.
- Driver ghi QC vào kho QC riêng, **không** đưa vào kết quả bệnh nhân.

## 5. Hỏi chỉ định / gửi worklist (hai chiều)

**Khi nào máy hỏi**
- Mỗi khi mở chế độ hai chiều, lưu worklist hoặc trước khi đếm mẫu, nếu đã nhập hoặc quét mã mẫu.
- Ở chế độ nạp tự động (autoloader), nếu đọc barcode lỗi thì mã mẫu gửi lên là chuỗi `Invalid`. LIS phải trả `AR`.

**Bản tin hỏi `ORM^O01`**
```
MSH|^~\&|Analyzer|Mindray|||<thời điểm>||ORM^O01|<mã bản tin>|P|2.3.1||||||UNICODE
ORC|RF||<mã mẫu>||IP
```
- `ORC-1 = RF` (yêu cầu điền lại chỉ định).
- `ORC-3` là mã mẫu.
- `ORC-5 = IP`.

**LIS trả `ORR^O02` khi tìm thấy**
- `MSH` (`MSH-9 = ORR^O02`)
- `MSA|AA|<MSH-10 của bản tin hỏi>`
- `PID`, `PV1`
- `ORC|AF|<mã mẫu>`: `ORC-1 = AF`, `ORC-2` là mã mẫu
- `OBR|1|<mã mẫu>|…`: **OBR-2 phải đúng bằng ORC-2**, nếu không máy coi bản tin là sai
- Các `OBX` thông tin chạy mẫu:

| OBX | Mã | Giá trị LIS gửi |
|---|---|---|
| Take Mode | `08001^Take Mode^99MRC` | `O` ống mở · `A` nạp tự động · `C` ống kín |
| Blood Mode | `08002^Blood Mode^99MRC` | `W` máu toàn phần · `P` pha loãng trước |
| Test Mode | `08003^Test Mode^99MRC` | `CBC` hoặc `CBC+5DIFF`. **Đây là cách LIS chọn xét nghiệm cho máy chạy** |
| Ref Group | `01002^Ref Group^99MRC` | Nhóm tham chiếu đã khai trên máy (vd `Woman`, `Man`, `Child`…). Phải trùng tên trên máy |
| Age | `30525-0^Age^LN` | Số + đơn vị `yr`/`mo`/`d`/`hr` ở OBX-6 |
| Remark | `01001^Remark^99MRC` | Ghi chú, kiểu ST |

Có thể điền thêm vào OBR:
- OBR-6 thời điểm lấy mẫu;
- OBR-10 người lấy mẫu;
- OBR-13 chẩn đoán;
- OBR-14 thời điểm nhận mẫu;
- OBR-22 thời điểm duyệt;
- OBR-28 người duyệt;
- OBR-32 người chạy.

**LIS trả khi không có chỉ định**
```
MSH|^~\&|LIS||||<thời điểm>||ORR^O02|<mã>|P|2.3.1||||||UNICODE
MSA|AR|<MSH-10 của bản tin hỏi>
```
- `AR`: từ chối, không có mẫu.
- `AE`: lỗi khi tra.

*(suy luận)*
- BC-5380 chỉ có 2 chế độ xét nghiệm là `CBC` và `CBC+5DIFF`.
- Map dịch vụ trong LIS sang Test Mode như sau:
  - chỉ định "Tổng phân tích tế bào máu" thì gửi `CBC+5DIFF`;
  - chỉ định chỉ đếm các dòng tế bào thì gửi `CBC`.
- Cần khai báo trong màn map máy.

## 6. Bảng mã chỉ số

**OBR-4: loại kết quả**

| Mã | Tên | Dùng cho |
|---|---|---|
| `00001` | Automated Count | Kết quả máy đếm |
| `00002` | Manual Count | Kết quả soi tay nhập trên máy |
| `00003` | LJ QCR | QC L-J |
| `00004` | X QCR | QC X |
| `00005` | XB QCR | QC X-B |
| `00006` | XR QCR | QC X-R |
| `00007` | X QCR Mean | Trung bình QC X |
| `00008` | XR QCR Mean | Trung bình QC X-R |

**Chỉ số kết quả** (OBX-2 = `NM`)

| Mã + hệ mã | Tên | Đơn vị điển hình | Ghi chú |
|---|---|---|---|
| `6690-2` LN | WBC | 10*9/L | |
| `704-7` LN | BAS# | 10*9/L | |
| `706-2` LN | BAS% | % hoặc phân số | Bản tin mẫu của hãng gửi dạng phân số (`0.000-0.010`) khi đơn vị trống. Xem mục 8 |
| `751-8` LN | NEU# | 10*9/L | |
| `770-8` LN | NEU% | % | |
| `711-2` LN | EOS# | 10*9/L | |
| `713-8` LN | EOS% | % | |
| `731-0` LN | LYM# | 10*9/L | |
| `736-9` LN | LYM% | % | |
| `742-7` LN | MON# | 10*9/L | |
| `5905-5` LN | MON% | % | |
| `26477-0` LN | *ALY# (lympho không điển hình) | 10*9/L | Thông số nghiên cứu, có dấu `*` |
| `13046-8` LN | *ALY% | % | Nghiên cứu |
| `10000` 99MRC | *LIC# (tế bào lớn chưa trưởng thành) | 10*9/L | Nghiên cứu |
| `10001` 99MRC | *LIC% | % | Nghiên cứu |
| `789-8` LN | RBC | 10*12/L | |
| `718-7` LN | HGB | g/L | Có thể là g/dL tùy cài đặt |
| `787-2` LN | MCV | fL | |
| `785-6` LN | MCH | pg | |
| `786-4` LN | MCHC | g/L | |
| `788-0` LN | RDW-CV | % hoặc phân số | |
| `21000-5` LN | RDW-SD | fL | |
| `4544-3` LN | HCT | % hoặc phân số (L/L) | |
| `777-3` LN | PLT | 10*9/L | |
| `32623-1` LN | MPV | fL | |
| `32207-3` LN | PDW | — | |
| `10002` 99MRC | PCT | mL/L hoặc % | |
| `10003` 99MRC | GRAN-X | — | Thông số nội bộ, bỏ qua |
| `10004` 99MRC | GRAN-Y | — | Nội bộ |
| `10005` 99MRC | GRAN-Y(W) | — | Nội bộ |
| `10006` 99MRC | WBC-MCV | — | Nội bộ |
| `30341-2` LN | ESR | — | |

**Soi tay** (thuộc OBR `00002`, đơn vị %)

| Mã + hệ mã | Tên |
|---|---|
| `747-6` LN | Myeloblasts% |
| `783-1` LN | Promyelocytes% |
| `749-2` LN | Myelocytes% |
| `740-1` LN | Metamyelocyte% |
| `764-1` LN | Neuts Band% |
| `769-0` LN | Neuts Seg% |
| `714-6` LN | Eosinophils% |
| `707-0` LN | Basophils% |
| `33831-9` LN | Lymphoblasts% |
| `6746-2` LN | Prolymphocytes% |
| `737-7` LN | Lymphocytes% |
| `29261-5` LN | Abnormal Lymphs% |
| `33840-0` LN | Monoblasts% |
| `13599-6` LN | Promonocytes% |
| `744-3` LN | Monocytes% |
| `18309-5` LN | NRBCs% |
| `31112-6` LN | Reticulocytes% |
| `11000` 99MRC | Undefined Cells% |
| `11001` 99MRC | Other Abnormal Cells% |

Chuỗi (ST):
- `882-1` Blood Type: ABO là `A`/`B`/`O`/`AB`, Rh là `RH+`/`RH-`;
- `11156-7` WBC Morphology;
- `6742-1` RBC Morphology;
- `11125-2` PLT Morphology.

**Cờ cảnh báo** (OBX-2 = `IS`, giá trị `T` là có / `F` là không)

| Mã | Tên | Mã | Tên |
|---|---|---|---|
| `12000` | WBC Abnormal scattergram | `12011` | WBC Abnormal |
| `12001` | WBC Abnormal histogram | `17790-7` LN | Left Shift? |
| `12002` | Leucocytosis | `34165-1` LN | Imm Granulocytes? |
| `12003` | Leucopenia | `15192-8` LN | Atypical Lymphs? |
| `12004` | Neutrophilia | `34525-6` LN | rstRBC (RBC Lyse Resist?) |
| `12005` | Neutropenia | `12012` | Erythrocytosis |
| `12006` | Lymphocytosis | `12013` | RBC Abnormal distribution |
| `12007` | Lymphopenia | `15150-6` LN | Anisocytosis |
| `12008` | Monocytosis | `15198-5` LN | Macrocytes |
| `12009` | Eosinophilia | `15199-3` LN | Microcytes |
| `12010` | Basophilia | `10379-6` LN | RBC Dual Pop |
| `12014` | Anemia | `15180-3` LN | Hypochromia |
| `12015` | HGB Interfere | `12016` | PLT Abnormal Distribution |
| `12017` | Thrombocytosis | `12018` | Thrombopenia |
| `7796-6` LN | Platelet Clump? | | |

Các mã không ghi hệ mã trong bảng trên đều là `99MRC`.
- Cờ chỉ lưu khi giá trị là `T`.
- Cờ hiển thị ở màn duyệt kết quả.
- Cờ không map thành chỉ số in trên phiếu.

**Biểu đồ** (OBX-2 = `ED` hoặc `NM`/`IS`)

| Mã | Nội dung |
|---|---|
| `15000` / `15050` / `15100` | Dữ liệu thô histogram WBC / RBC / PLT. ED: `^Application^Octet-stream^Base64^…` |
| `15001`–`15003` | WBC: vạch trái / phải / giữa (NM) |
| `15004` | WBC: độ dài dữ liệu gốc |
| `15005`–`15007` | WBC: cờ đã chỉnh vạch (IS) |
| `15008` / `15056` / `15116` | Ảnh BMP histogram WBC / RBC / PLT. ED: `^Image^BMP^Base64^…` |
| `15051`–`15055` | RBC: vạch, độ dài, cờ chỉnh |
| `15111`–`15115` | PLT: vạch, độ dài, cờ chỉnh |
| `15200` | Ảnh BMP scattergram DIFF |

## 7. Bản tin mẫu

Kết quả mẫu bệnh nhân, đã rút gọn. Dữ liệu là của ví dụ hãng, họ tên đã thay bằng tên giả. `<SB>` = 0x0B, `<EB>` = 0x1C, `<CR>` = 0x0D, mỗi dòng dưới đây kết thúc bằng `<CR>`:

```
<SB>MSH|^~\&|Analyzer|Mindray|||20080617143943||ORU^R01|1|P|2.3.1||||||UNICODE
PID|1||7393670^^^^MR||Mẫu^Nguyễn Văn||19900804000000|Female
PV1|1||nk^^001
OBR|1||20071207011|00001^Automated Count^99MRC||20080508140600|20080508150616|||Kỹ thuật viên||||20080508150000||||||||||HM||||||||Mindray
OBX|1|IS|08001^Take Mode^99MRC||O||||||F
OBX|2|IS|08002^Blood Mode^99MRC||W||||||F
OBX|3|IS|08003^Test Mode^99MRC||CBC||||||F
OBX|4|IS|01002^Ref Group^99MRC||Woman||||||F
OBX|5|NM|30525-0^Age^LN||18|yr|||||F
OBX|6|NM|6690-2^WBC^LN||9.81|10*9/L|4.00-10.00|N|||F||E
OBX|21|NM|789-8^RBC^LN||4.53|10*12/L|3.50-5.00|N|||F
OBX|22|NM|718-7^HGB^LN||65|g/L|110-150|L|||F
OBX|29|NM|777-3^PLT^LN||212|10*9/L|100-300|N|||F
OBX|33|IS|12014^Anemia^99MRC||T||||||F
OBX|38|ED|15008^WBC Histogram. BMP^99MRC||^Image^BMP^Base64^Qk0…||||||F
<EB><CR>
```

ACK của LIS:
```
<SB>MSH|^~\&|LIS||||20080617143944||ACK^R01|1|P|2.3.1||||||UNICODE
MSA|AA|1
<EB><CR>
```

Hỏi chỉ định và trả lời có chỉ định:
```
<SB>MSH|^~\&|Analyzer|Mindray|||20081120174836||ORM^O01|4|P|2.3.1||||||UNICODE
ORC|RF||2611030001||IP
<EB><CR>

<SB>MSH|^~\&|LIS||||20081120174836||ORR^O02|1|P|2.3.1||||||UNICODE
MSA|AA|4
PID|1||BN000123^^^^MR||Mẫu^Nguyễn Văn||19810506|Male
PV1|1||Khoa Nội^^Giường 04
ORC|AF|2611030001
OBR|1|2611030001||||20261103080000||||Kỹ thuật viên|||Chẩn đoán thử
OBX|1|IS|08001^Take Mode^99MRC||A||||||F
OBX|2|IS|08002^Blood Mode^99MRC||W||||||F
OBX|3|IS|08003^Test Mode^99MRC||CBC+5DIFF||||||F
OBX|4|IS|01002^Ref Group^99MRC||Man||||||F
OBX|5|NM|30525-0^Age^LN||45|yr|||||F
<EB><CR>
```

Trả lời không có chỉ định:
```
<SB>MSH|^~\&|LIS||||20081120175238||ORR^O02|1|P|2.3.1||||||UNICODE
MSA|AR|9
<EB><CR>
```

## 8. Bẫy và lưu ý khi viết driver

1. **Cắt bản tin theo byte MLLP, không theo dòng.**
   - Ảnh Base64 làm bản tin dài hàng chục đến hàng trăm KB.
   - Đặt giới hạn bộ đệm khoảng 2 MB.
   - Chỉ giải mã UTF-8 sau khi đã cắt xong.
2. **ACK ngay, xử lý sau.** Máy chỉ chờ 10 giây và không gửi lại. Mất ACK nghĩa là mất kết quả, trừ khi người dùng gửi tay lại từ máy.
3. **Mã mẫu khác nhau theo chiều bản tin:**
   - kết quả: ở **OBR-3**;
   - hỏi chỉ định: ở **ORC-3**;
   - trả lời: ở **ORC-2 và OBR-2**.
   - Nhầm vị trí thì máy từ chối câu trả lời, hoặc kết quả không khớp mẫu.
4. **Khóa map chỉ số = mã + hệ mã** (`6690-2^LN`, `10002^99MRC`). Không dùng tên. Tên có thể có dấu `*` hoặc khoảng trắng thừa (bản in của hãng có "WBC   Histogram.   BMP").
5. **Đơn vị % hay phân số.**
   - Bản tin mẫu gửi BAS%, NEU%, HCT, RDW-CV dưới dạng phân số (0.405) và để trống đơn vị.
   - Quy tắc: nếu OBX-6 trống và giá trị ≤ 1 thì nhân 100 khi map sang chỉ số %.
   - Quy tắc này phải cấu hình được theo từng chỉ số, vì còn phụ thuộc cài đặt đơn vị trên máy.
   - *(suy luận — cần đo máy thật)*
6. **Giá trị không hợp lệ:**
   - `***.*`, `**.*` hoặc rỗng nghĩa là không có kết quả. Lưu là "không có giá trị", không lưu 0.
   - Giá trị dạng `.***` cũng vậy.
7. **Họ tên**: PID-5 dạng `Họ^Tên`. Gửi tiếng Việt UTF-8 được (máy dùng UTF-8).
8. **QC L-J giả dạng mẫu thường** nếu bật tùy chọn trên máy. Nên tắt khi cài đặt. Nếu để bật thì phân biệt bằng mã mẫu đặc biệt của QC.
9. **Bản 1.1** thêm cờ hệ thống. Driver nhận mã cờ lạ thì vẫn lưu, đánh dấu "chưa map", không báo lỗi.
10. **Ảnh biểu đồ**:
    - lưu tệp riêng, không lưu trong bảng kết quả;
    - chỉ giữ khi cấu hình in biểu đồ;
    - BMP có thể đổi sang PNG khi lưu.
11. **Một bản tin có 2 OBR** (`00001` + `00002`). Các OBX thuộc OBR đứng ngay trước nó.

## 9. Kiểm thử

- **Bộ bản tin mẫu:**
  - kết quả CBC+5DIFF đầy đủ, có và không có ảnh;
  - kết quả có OBR soi tay;
  - QC X-R (3 khối PID/OBR);
  - hỏi chỉ định có kết quả, không có kết quả, và `Invalid`.
- **Mô phỏng máy:**
  - chương trình đóng vai máy, chạy được cả hai vai: TCP client nối vào LIS, và TCP server cho LIS nối vào;
  - phát lại các tệp bản tin trên;
  - đo thời gian ACK.
- **Tình huống lỗi phải thử:**
  - bản tin đến cắt đôi qua nhiều gói TCP;
  - hai bản tin dính nhau trong một gói;
  - mất kết nối giữa bản tin, rồi máy nối lại;
  - ACK chậm quá 10 giây (máy bỏ qua);
  - ký tự `|` trong ghi chú (phải thoát `\F\`);
  - tên tiếng Việt có dấu;
  - mã chỉ số lạ.

## 10. Nguồn

- *BC-5380 Auto Hematology Analyzer Operator's Manual*, Mindray, P/N 046-001575-00 (2.0), 2011-05:
  - **Appendix C "Communication"**: toàn bộ giao thức, bảng mã, bản tin mẫu;
  - mục 5.3 "Communication": các tùy chọn cài đặt.
  - Tệp: `tai-lieu-hang/Mindray_BC-5380_Operator_Manual_v2.0_2011.pdf`.
- *BC-5380/BC-5180 Service Manual*, Mindray, 2019-08, mục 3.6 "LIS Setup and Connection": máy làm client/server, ACK overtime, phiên bản 1.0/1.1, truyền biểu đồ. Tệp: `tai-lieu-hang/Mindray_BC-5380_BC-5180_Service_Manual_2019.pdf`.
- Tài liệu hãng có ghi bảo mật. PDF nằm ở `tai-lieu-hang/` (thư mục bị .gitignore — không commit), chỉ tham chiếu.
