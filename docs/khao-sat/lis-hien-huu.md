# Khảo sát hệ LIS hiện hữu (BVĐK 10) — dữ liệu để LIS Connect chạy đúng

Tài liệu này ghi lại kết quả khảo sát bản backup CSDL `LIS10` (SQL Server 2019,
bản chụp 07/09/2026) của hệ LIS đang vận hành. Mục đích: LIS Connect nói chuyện
được với đúng những máy đó, đúng những mã xét nghiệm đó, và hiểu đúng cách hệ cũ
diễn giải kết quả.

Nguồn: `LIS10.bak` / `07092026.bak` (hai bản trùng khớp tuyệt đối về nội dung),
`Northwinds.bak` (DB auto-update của phần mềm, không liên quan nghiệp vụ XN).

## 1. Bức tranh chung

Hệ cũ là phần mềm desktop WinForms .NET Framework 4.0 (DevExpress v19.1), chạy
phân tán trên các trạm trong khoa. Mỗi trạm chạy một tiến trình kết nối máy
(`LISInterface`), đọc byte từ COM/TCP, dịch giao thức, rồi **gọi stored procedure
trong CSDL** để ghi kết quả. Nghĩa là phần lớn logic nghiệp vụ nằm trong SQL chứ
không nằm trong ứng dụng — thuận lợi cho ta, vì đọc được trực tiếp.

Luồng dữ liệu:

```
HIS (SmartHOS) --REST--> LIS_Api --> LIS
                                      |
   máy XN --serial/TCP--> LISInterface --> Result_Out_Insert --> Result_OUT
                                                                    |
                                             KTV duyệt --> Result_Out_Upd_Result
                                                                    |
                                                                 Result --REST--> HIS
```

`Result_OUT` là hàng đợi thô: mọi thứ máy bắn về đều rơi vào đây, kể cả bản ghi
không khớp bệnh nhân. `Result` là kết quả chính thức đã được duyệt.

## 2. Hai hệ bảng song song

CSDL chứa hai bộ bảng mô tả cùng một dữ liệu, số dòng khớp nhau từng cái:

| Nội dung | Bảng cũ (tiếng Việt) | Bảng mới (tiếng Anh) | Số dòng |
|---|---|---|---|
| Danh sách máy | `tbl_Mayxetnghiem` | `Instrument` | 19 / 20 |
| Ánh xạ mã máy ↔ dịch vụ | `tbl_Mamayxetnghiem` | `InsTest` | 292 |
| Danh mục dịch vụ XN | `tbl_Dichvu` | `TestCode` | 404 |

**Dùng bộ tiếng Anh.** Bộ tiếng Việt là di sản, không có bảng cấu hình cổng và
không được stored procedure nào tham chiếu.

Lệch duy nhất: `Instrument` có InsID 20 (CelltacG) không tồn tại bên bảng cũ và
chưa map mã XN nào.

## 3. Danh sách máy xét nghiệm

20 máy khai báo, nhưng thực tế chỉ **9 máy có cấu hình cổng** và **11 máy từng
gửi kết quả**. Ba tập hợp này không trùng nhau — chi tiết ở mục 7.

| InsID | Tên máy | Protocol | Kết nối | Trạm | Mã XN | Đã gửi KQ |
|---|---|---|---|---|---|---|
| 1 | AU640 | AU | COM6 | DK10.1 | 26 | ✅ 181 |
| 2 | e2010 | ASTM | COM6 | DK10.3 | 21 | ✅ 418 |
| 3 | BC3200 | ASTM | — | — | 19 | ✅ 54 |
| 4 | CYBOW | ASTM | — | — | 11 | ✅ 265 |
| 5 | HLC723GX | ASTM | — | — | 1 | — |
| 6 | Sysmex CA500 | ASTM | COM7 | DK10.2 | 4 | ✅ 39 |
| 7 | CHEM7 | ASTM | — | — | 27 | — |
| 8 | CON500 | ASTM | — | — | 21 | — |
| 9 | EBA200 | ASTM | — | — | 13 | — |
| 10 | U120 | U500 | — | — | 9 | — |
| 11 | BIOLIS50 | ASTM | — | — | 18 | — |
| 12 | AIA360 | AIA360 | COM1 | DK10.3 | 20 | ✅ 6 |
| 13 | ST200 | ASTM | — | — | 4 | — |
| 14 | ST200PRO | ASTM | — | — | 4 | — |
| 15 | AU2700 | AU | COM5 | DK10.1 | 26 | ✅ 12 |
| 16 | CDRUBY (_1) | ASTM | COM4 | DK10.3 | 22 | ✅ 294 |
| 17 | CDRUBY (_2) | ASTM | COM8 | DK10.2 | 22 | ✅ 67 |
| 18 | HbA1C G11 | TOSHO | COM5 | DK10.2 | 1 | ✅ 1 |
| 19 | COBAS 8000 | ASTM | TCP 192.168.1.4:50001 | DK10.3 | 23 | ✅ 1 |
| 20 | CelltacG | ASTM | — | — | 0 | — |

Toàn bộ cổng serial dùng chung tham số `9600, 8, N, 1`.

`Instrument.ConType`: `C` = serial (19 máy), `S` = socket TCP (1 máy). Với máy
socket, hệ cũ đóng vai **client** nối tới `RemoteIP:RemotePort`.

Bảng `InterfacePara` khoá theo `HostID` + `InstrumentID`, nên cùng số hiệu COM
xuất hiện ở nhiều trạm khác nhau là bình thường (COM5 có ở cả DK10.1 và DK10.2).
`HostID` (DK10.1/2/3) là tên do người dùng tự đặt, không khớp hostname Windows.

Phần mềm cũ có sẵn 28 driver protocol (ASTM, HL7, ABX, ADVIA, AU480, AU5800,
Abacus, Celltac, Chorus, DestinyPlus, HbA1c_G8, LabUMat_UriSed, MissionU500,
P8000, Urit500C, VariantNBS…) nhưng CSDL chỉ dùng 5 giá trị: `ASTM`, `AU`,
`AIA360`, `TOSHO`, `U500`.

## 4. Ánh xạ mã máy ↔ mã dịch vụ

Bảng `InsTest`, khoá tra là cặp **(`InsID`, `TestcodeIN`)**:

| Cột | Ý nghĩa |
|---|---|
| `InsID` | máy nào |
| `TestcodeIN` | **mã kênh máy trả về** (thường 2 chữ số: `01`, `08`…) |
| `TestCode` | mã dịch vụ nội bộ của LIS (`123AST`, `126ChoT`…) |
| `TestCodeIn2` | mã phụ — rỗng toàn bộ 292 dòng |
| `Round` | số chữ số thập phân khi làm tròn |
| `AllowInsert` | cho phép tự thêm chỉ định khi BN chưa được chỉ định XN này |
| `Omit` | bỏ qua dòng này khi tra |
| `VALhigher` / `VALlower` | giữ lại dấu `>` / `<` trong kết quả |

Dữ liệu đầy đủ 292 dòng: [`seed/anh-xa-ma-may.json`](seed/anh-xa-ma-may.json).

**Cặp (InsID, TestcodeIN) KHÔNG duy nhất.** Hệ cũ gỡ nhập nhằng bằng `TOP 1` với
thứ tự ưu tiên:

1. `Omit` tăng dần — dòng không bị bỏ qua thắng
2. Xét nghiệm mà **bệnh nhân này thực sự có chỉ định** thắng
3. Hoà thì lấy theo `TestCode` tăng dần (alphabet)

Tiêu chí 3 là chỗ sai thầm lặng: trên AU640, `128LDL` và `244ACo` (nồng độ cồn)
cùng map kênh `19`. Nếu BN được chỉ định cả hai, hoặc không chỉ định cái nào, kết
quả sẽ về nhầm xét nghiệm mà không có cảnh báo nào.

## 5. Quan hệ dịch vụ cha — chỉ số con

Ba bảng: `Profile` (nhóm cha) → `ProfileTest` (thành viên) → `TestCode.Profile`
(cờ đánh dấu cha) / `TestCode.ProfileID` (thuộc nhóm nào).

Chỉ 2 nhóm được cấu hình:

| ProfileID | Tên | Chỉ số con | Mã cha |
|---|---|---|---|
| `TPTMau22` | Tổng phân tích máu 22 thông số | 23 | `027TB`, `002HD` |
| `TPTNT` | Tổng phân tích nước tiểu | 10 | `400TPTNT` |

Lưu ý: **mã cha cũng nằm trong `ProfileTest`** như một thành viên, phải lọc bằng
`TestCode.Profile = 0` khi liệt kê con.

Có 14 mã mang cờ `Profile = 1` hoặc `TestHead = 1` nhưng chỉ 3 mã có `ProfileID`.
11 mã cha còn lại (`920IONDO` điện giải đồ, `059NM` nhóm máu, `766HE3` ROMA test,
`911SN` soi nhuộm…) **không khai báo con ở đâu cả** — quan hệ chỉ tồn tại trên
mặt trình bày: tên con bị thụt đầu dòng bằng khoảng trắng và sắp theo
`PrintOrder`. Nếu LIS Connect cần cây cha–con đầy đủ thì phải bổ sung thủ công,
không đọc ra được từ CSDL.

Các cột `IsSubTest`, `IsMerge` tồn tại nhưng bằng 0 ở cả 404 dòng — chưa dùng.

## 6. Quy tắc xử lý kết quả

Trích từ hai stored procedure `Result_Out_Insert` (lúc nhận) và
`Result_Out_Upd_Result` (lúc KTV duyệt).

### 6.1 Khớp bệnh nhân

Lúc **nhận**, máy chỉ gửi phần đuôi barcode nên hệ cũ tra ngược mờ:
`SID LIKE '%-' + <sid máy gửi>`, giới hạn **10 ngày gần nhất**, SID tối thiểu 3
ký tự, lấy bản ghi mới nhất.

Lúc **duyệt**, SID được dựng lại thành dạng đầy đủ `ddmmyy-<số>` rồi so khớp
chính xác.

Rủi ro: hai bệnh nhân trùng đuôi SID trong vòng 10 ngày sẽ khớp nhầm.

### 6.2 Làm tròn

Chỉ áp dụng khi kết quả là số. Thay `,` thành `.`, bỏ khoảng trắng, `ROUND` theo
`InsTest.Round`, rồi **cắt chuỗi** thay vì format số — cách này giữ nguyên số chữ
số thập phân kể cả các số 0 ở cuối. Thêm `0` đứng đầu cho dạng `.5`, bỏ dấu `.`
thừa ở cuối.

### 6.3 Kết quả không phải số

- Dạng `a/b` hoặc `a:b` (hiệu giá TPHA, RF) được **quy đổi sang thập phân** rồi
  mới so ngưỡng: `1:160` → `0.00625`
- Không quy đổi được thì so chuỗi với `TestCode.Criteria`

### 6.4 Cờ màu và biện luận

`Color`: **1 = thấp, 2 = cao, 0 = bình thường**.

Thứ tự áp dụng:

1. Lấy khoảng tham chiếu **cá thể hoá** từ `PNormalRange` theo 4 chiều:
   `InsID` + `TestCode` + tuổi (`FromAge`–`ToAge`) + giới tính (`PSex`: 0 nữ,
   1 nam, NULL chung). Chiều cân nặng có nhưng phải bật `WeightCalculate = 1`.
   Tìm thấy thì ghi đè cả `NormalRange`, `TestName`, `Unit` trên kết quả.

   > **`PNormalRange` hiện có 0 dòng.** Toàn bộ nhánh cá thể hoá này chưa bao giờ
   > chạy trên dữ liệu thật — ngưỡng luôn lấy từ `TestCode.LowerLimit` /
   > `HigherLimit` dùng chung cho mọi bệnh nhân. Cơ chế đã cài sẵn trong SP nhưng
   > chưa ai nhập dữ liệu. LIS Connect nên cài đúng cơ chế (khoảng tham chiếu
   > theo tuổi/giới là yêu cầu chuyên môn thật), nhưng đừng kỳ vọng di trú được
   > dữ liệu từ hệ cũ vì không có gì để di trú.
2. So ngưỡng → ra `Color`
3. Vùng xám (`PLowerLimitG`–`PHigherLimitG`) → lấy biện luận `TestCode.BLGray`
4. Áp `ResultMapping` (khoá `InstrumentID` + `TestCode` + `ResultIn`) — bảng này
   được phép **ghi đè cả kết quả, biện luận lẫn màu**. Đây là chỗ `neg` → "Âm tính".
5. Ghi vào `Result`, rẽ nhánh theo cờ `TestCode.GetComment`:
   - `0` → cột `Result` giữ **giá trị số**, biện luận vào `Comment`
   - `1` → cột `Result` nhận **luôn câu biện luận** (`BLLower`/`BLHigher`)

Ngoại lệ đáng nhớ: kết quả thấp mà `BLLower = 'Âm tính'` thì ép `Color = 0`, để
âm tính không bị tô màu bất thường.

Đơn vị của LIS **luôn thắng** đơn vị máy gửi lên; đơn vị máy chỉ dùng khi danh
mục để trống.

### 6.5 Bảng mã trạng thái (`Result_OUT.Status`)

| Mã | Nghĩa |
|---|---|
| `VAL` | hợp lệ, chờ duyệt |
| `UPL` | đã đẩy sang `Result` thành công |
| `REV` | máy báo cờ bất thường (`<`, `>`, `HH`, `LL`), cần xem lại |
| `QC` | mẫu nội kiểm — mọi lệnh cập nhật đều loại trừ trạng thái này |
| `CAL` | mẫu chuẩn máy |
| `E00` | không tìm thấy SID trong `Patient` |
| `E01` | có bệnh nhân nhưng chưa có chỉ định xét nghiệm đó |
| `E03` | ô kết quả đã có giá trị, từ chối ghi đè |
| `E05` | phiếu đã in (`Patient.Valid = 1`), khoá |

Cờ `Upload = 1` khi trạng thái là `UPL` hoặc `E03` (coi như đã xử lý xong).

Bản ghi không khớp bệnh nhân **vẫn được ghi vào `Result_OUT`** với `TestCode`
NULL — nên bảng này là nơi hứng tất cả, kể cả rác. Dữ liệu thật xác nhận: các
dòng của InsID 3 có `SID` rỗng.

## 7. Những chỗ dữ liệu không nhất quán

Ghi lại để LIS Connect không lặp lại, và để biết chỗ nào không tin được:

1. **`InterfacePara` không phải nguồn chân lý.** BC3200 (InsID 3) và CYBOW
   (InsID 4) gửi về tổng cộng 319 kết quả nhưng **không có dòng cấu hình cổng
   nào**. Cấu hình của chúng nằm ở file local trên trạm, CSDL không giữ. Muốn
   biết máy nào đang thật sự sống, tra `Result_OUT` group theo `InstrumentID` +
   `MAX(Runtime)`, đừng tra `Instrument.Active` (cả 20 máy đều `Active = 1`).
2. **Mã kênh trùng nhau trên cùng một máy** — xem mục 4.
3. **11 mã cha không khai báo con** — xem mục 5.
4. **Cột bị dùng sai mục đích** trong `Result`: `UserU` (người cập nhật) lưu
   `InsID` — số hiệu máy; `ResultMoM` (vốn là Multiple of Median của sàng lọc
   trước sinh) lưu `LocationID` của máy. Đọc theo tên cột sẽ hiểu sai.
5. **Truy vấn dùng `LIKE` thay `=`** trên `SID` và `TestCode` trong SP duyệt kết
   quả. CSDL có thật các mã chứa ký tự đại diện: `034NEU%`, `035LYM%`,
   `036MONO%`, `037EOS%`, `038BASO%`. Hiện chưa gây hại vì không có mã nào khác
   cùng tiền tố, nhưng thêm một mã kiểu `034NEUT` là ghi nhầm ngay. **LIS Connect
   phải escape hoặc dùng so khớp chính xác.**
6. **`TestCode_HIS`** (835 dòng map mã HIS) có cột `profileID` nhưng chỉ 14 dòng
   có giá trị thật, còn lại là chuỗi rỗng.
7. **`PNormalRange` rỗng** — xem hộp cảnh báo ở mục 6.4. Vùng xám
   (`PLowerLimitG`/`PHigherLimitG`) cũng nằm trong bảng này, nên biện luận
   `BLGray` của `TestCode` hiện cũng không bao giờ được kích hoạt.
8. Ba biến thể SP song song: `Result_Out_Upd_Result`, `_ResultN`, `_ResultG`.
   Các dòng ví dụ trong mã cho thấy `_ResultN` được gọi cho InsID 19–20. Trước
   khi mô phỏng hành vi, cần xác định máy nào gọi bản nào.

## 8. Tích hợp HIS (tham chiếu)

Hệ cũ nói chuyện với HIS SmartHOS qua REST, thư viện `LIS_Api.dll`:

- Đăng nhập: `POST auth/lis-partner/api/v1/dang-nhap` → `access_token`, dùng
  `Bearer`, có cache theo hạn token
- `api/v1/integration/dc-lab/getDoctorCategory` — danh mục bác sĩ
- `api/v1/integration/dc-lab/getLabServices` — danh mục dịch vụ XN
- `api/v1/integration/dc-lab/getPatientHasOrder` — bệnh nhân có chỉ định
- `api/v1/integration/dc-lab/getOrderByBarcode` — chỉ định theo barcode
- `api/v1/integration/dc-lab/getPatientOrderByAppointmentCode` — theo mã hẹn
- `api/v1/integration/dc-lab/returnTestResultsByBarcode` — **trả kết quả về HIS**
- `api/v1/integration/dc-lab/updateOrderServiceStatus` — cập nhật trạng thái

Cặp trường `serviceCode` ↔ `partnerServiceCode` chính là lý do tồn tại bảng
`TestCode_HIS`.

Thông tin kết nối (URL, tài khoản) nằm ở `Sys_Parameter` — **lưu plaintext**.

## 9. Áp dụng vào LIS Connect

### 9.1 Cấu hình Agent theo trạm

Sinh sẵn theo đúng `AgentConfig` của `apps/agent/src/main/config-store.ts`:

| File | Trạm | Máy |
|---|---|---|
| [`seed/agent-dk10-1.json`](seed/agent-dk10-1.json) | DK10.1 | AU640, AU2700 |
| [`seed/agent-dk10-2.json`](seed/agent-dk10-2.json) | DK10.2 | CA500, CDRUBY_2, G11 |
| [`seed/agent-dk10-3.json`](seed/agent-dk10-3.json) | DK10.3 | e2010, CDRUBY_1, AIA360, COBAS 8000 |
| [`seed/may-chua-ro-cong.json`](seed/may-chua-ro-cong.json) | — | 11 máy chưa có cấu hình cổng |

Còn thiếu `coreUrl` và `agentKey` — điền khi cấp phát agent.

Đường dẫn serial đang để dạng Windows (`COM4`…). Trên Linux/macOS phải đổi sang
`/dev/ttyUSB*` hoặc `/dev/tty.usbserial-*`.

### 9.2 Driver còn thiếu

`driverRegistry` hiện chỉ có `astm-e1394`. Trong 9 máy có cấu hình cổng:

| Driver | Máy | Trạng thái |
|---|---|---|
| `astm-e1394` | e2010, CA500, CDRUBY ×2, COBAS 8000 | ✅ đã có |
| `beckman-au` | AU640, AU2700 | ❌ cần viết |
| `tosoh-aia360` | AIA360 | ❌ cần viết |
| `tosoh-g11` | HbA1C G11 | ❌ cần viết |
| `mission-u500` | U120 (chưa nối) | ❌ cần viết |

Các máy chưa có driver để `enabled: false` trong file seed.

Ưu tiên viết theo lưu lượng thực tế: e2010 (418 KQ) và CDRUBY (361 KQ) đã chạy
được bằng driver ASTM sẵn có; kế đến là `beckman-au` cho AU640 (181 KQ).

### 9.3 Dữ liệu danh mục đã xuất

Toàn bộ bảng danh mục ở [`seed/du-lieu/`](seed/du-lieu/), dạng JSON UTF-8, một
file một bảng, giữ nguyên tên cột gốc để đối chiếu ngược:

| File | Dòng | Nội dung |
|---|---|---|
| `TestCode.json` | 404 | danh mục dịch vụ XN đang dùng |
| `TestCodeNew.json` | 2.229 | danh mục mở rộng (chưa rõ vai trò, xem ghi chú dưới) |
| `TestCode_HIS.json` | 835 | ánh xạ mã LIS ↔ mã HIS |
| `TestCodeQuick.json` | 152 | mã nhập nhanh |
| `InsTest.json` | 292 | **ánh xạ mã kênh máy ↔ mã dịch vụ** |
| `Instrument.json` | 20 | danh sách máy |
| `InterfacePara.json` | 9 | cấu hình cổng theo trạm |
| `ResultMapping.json` | 56 | ánh xạ giá trị định tính theo máy |
| `Profile.json` / `ProfileTest.json` | 2 / 33 | nhóm cha và chỉ số con |
| `ResultType.json` / `ResultType_TestCode.json` | 13 / 33 | kiểu kết quả |
| `ResultGroup.json` | 5 | nhóm kết quả khi in |
| `SampleStatus.json` | 26 | trạng thái mẫu |
| `Category.json` | 13 | nhóm xét nghiệm |
| `Room_TestCode.json` | 15 | phân xét nghiệm theo phòng |
| `Location.json` | 1 | điểm lấy mẫu |

Tổng 4.138 dòng. Cột nhị phân (`image`, `varbinary`), `rowguid` và `timestamp`
đã lược bỏ.

`anh-xa-ma-may.json` ở thư mục cha là bản `InsTest` đã join sẵn tên xét nghiệm,
đơn vị và ngưỡng — dùng cái này khi cần đọc nhanh; dùng `du-lieu/InsTest.json`
khi cần đủ 27 cột gốc.

**Chưa xuất, có chủ ý:**

- `Patient`, `Result`, `Result_OUT`, `SampleReceive`, `PatientMoreInfo` và các
  bảng `*_His` / `*_Log`: chứa thông tin định danh bệnh nhân. Nếu cần dữ liệu
  thật để kiểm thử driver thì phải khử định danh trước (thay tên, năm sinh, địa
  chỉ, giữ nguyên `SID`, `TestCode`, `Result`, `Runtime`).
- `Sys_Parameter`, `Sys_Login`, `Sys_User`, `Sys_Permission`: chứa mật khẩu và
  dấu vết đăng nhập.
- `Object_Test` (21.977 dòng): bảng giá theo đối tượng BHYT/viện phí, không liên
  quan kết nối máy.
- `ICD10`, `PRODUCT`, `STOCK_*`, `QC_*`: ngoài phạm vi giai đoạn 1.

**Ghi chú về `TestCodeNew`:** 2.229 dòng, cùng cấu trúc `TestCode` nhưng nhiều
gấp 5,5 lần và không được stored procedure nào tham chiếu. Nhiều khả năng là
danh mục dùng chung của nhà cung cấp, nạp sẵn để cơ sở chọn ra danh mục riêng
(404 dòng trong `TestCode`). Cần xác nhận với đơn vị vận hành trước khi dùng làm
nguồn danh mục.

### 9.4 Việc cần làm tiếp

- [ ] Khảo sát tại chỗ cổng COM của BC3200 và CYBOW (đang chạy thật, thiếu cấu hình)
- [ ] Chốt cách gỡ nhập nhằng mã kênh trùng — đề xuất: từ chối map và ghi cảnh báo
      thay vì đoán theo alphabet như hệ cũ
- [ ] Bổ sung quan hệ cha–con cho 11 mã cha chưa khai báo
- [ ] Xác định `_ResultN` / `_ResultG` khác `Result_Out_Upd_Result` chỗ nào
- [ ] Escape ký tự `%` `_` trong mọi truy vấn theo mã xét nghiệm

## 10. Phụ lục — dựng lại môi trường khảo sát

```bash
docker run -d --name mssql2019 --platform linux/amd64 \
  -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<mật khẩu>' -e MSSQL_PID=Developer \
  -p 1433:1433 -v mssql2019-data:/var/opt/mssql \
  mcr.microsoft.com/mssql/server:2019-latest
```

Image chỉ có bản amd64, chạy emulation trên Apple Silicon.

Restore cần `MOVE` vì tên logical file không khớp tên DB: `LIS10.bak` chứa
`HIS` / `HISExtend` / `History` / `HIS_log`.

Công cụ trong image là `mssql-tools18`, phải thêm `-C` (sqlcmd) hoặc `-u` (bcp)
để bỏ qua self-signed certificate.
