# Các máy chạy ASTM chung ở BVĐK 10 — e2010, COBAS 8000, Cell-Dyn Ruby ×2, BC-3200, HLC-723GX, CHEM7, CON500, EBA200, BIOLIS50, ST200/ST200PRO, CelltacG

> Trạng thái tài liệu: ⚠️ lớp ASTM chung đủ (xem `astm-e1394.md`); **chưa có tài liệu host interface của từng máy** — trừ ba máy đã tách file riêng có tài liệu hãng: **e2010 → `roche-cobas-e411.md`** (✅), **BC-3200 → `mindray-bc3200.md`** (✅, không phải ASTM), **Cell-Dyn Ruby → `abbott-celldyn-ruby.md`** (✅), **COBAS 8000 → `roche-cobas-8000.md`** (✅, LIS nói với data manager), **HLC-723GX → `tosoh-hlc723-g11.md`** (✅, không phải ASTM). Máy chưa từng gửi kết quả coi như ❌.
> Kiểm chứng: chưa đối chiếu máy thật (chỉ có số lượng kết quả trong `Result_OUT` của LIS cũ)
> Driver: `generic-astm` (mã ở thành phần 4 của Universal Test ID), `abbott-celldyn` (thành phần 7) — họ giao thức: astm-e1394

## 1. Tóm tắt

Trong 20 máy khai ở LIS cũ (`Instrument.json`), 14 máy khai protocol `ASTM`. Tệp này gom 13 máy trong số đó (CA-500 và CYBOW có tài liệu riêng: `sysmex-ca500.md`, `cybow-r50s.md`). Ba máy nay đã có file riêng theo tài liệu hãng và **file đó là nguồn chính**: e2010 (`roche-cobas-e411.md`), BC-3200 (`mindray-bc3200.md`), HLC-723GX (`tosoh-hlc723-g11.md`); các mục dưới về ba máy này chỉ giữ dữ liệu LIS cũ (bảng mã, số kết quả). LIS cũ dùng **một** driver ASTM cho tất cả, chỉ khác nhau ở các cột vị trí trong `Instrument`:

| Cột LIS cũ | Giá trị chung | Cell-Dyn Ruby | Ý nghĩa *(suy luận)* |
|---|---|---|---|
| `SIDIndex` | 2 | 2 | SID ở **O.3** (đếm từ 0: `O`=0, seq=1, SID=2) |
| `TestCodeIndex` | 3 | **6** | mã chỉ số ở thành phần thứ 3 (đếm từ 0) của R.3 = thành phần 4 đếm từ 1 (`^^^MÃ`) — Cell-Dyn: thành phần 7 |
| `ValueIndex` | 3 | 3 | giá trị ở R.4 |
| `UnitIndex` | 4 | 4 | đơn vị ở R.5 |

Chỉ **4 thiết bị trong nhóm có cấu hình cổng** trong CSDL (e2010, COBAS 8000, Cell-Dyn Ruby ×2) và **5 thiết bị từng gửi kết quả** (thêm BC-3200, cấu hình nằm ở file trạm). Còn lại khai trong danh mục nhưng chưa hề chạy với LIS cũ — không biết chúng có thật sự nói ASTM hay không.

## 2. Kết nối vật lý — tổng hợp

| InsID | Máy | Loại | Cổng ở BVĐK 10 | Mã đã map (dòng / mã khác nhau) | Đã gửi KQ (`Result_OUT`) | Driver |
|---|---|---|---|---|---|---|
| 2 | Roche Elecsys/cobas **e2010** | Miễn dịch (điện hóa phát quang) | RS-232 COM6, trạm DK10.3, 9600 8N1 | 21 / 20 | ✅ 418 | `roche-e411` → xem `roche-cobas-e411.md` |
| 19 | Roche **COBAS 8000** | Sinh hóa + miễn dịch, mô-đun | **TCP, máy là server** (LIS nối vào) | 23 / 22 | ✅ 1 | `roche-c8000` → xem `roche-cobas-8000.md` |
| 16 | Abbott **Cell-Dyn Ruby** (_1) | Huyết học 5 thành phần | RS-232 COM4, DK10.3, 9600 8N1 | 22 / 22 | ✅ 294 | `abbott-celldyn` → xem `abbott-celldyn-ruby.md` |
| 17 | Abbott **Cell-Dyn Ruby** (_2) | Huyết học 5 thành phần | RS-232 COM8, DK10.2, 9600 8N1 | 22 / 22 | ✅ 67 | `abbott-celldyn` → xem `abbott-celldyn-ruby.md` |
| 3 | Mindray **BC-3200** | Huyết học 3 thành phần | **Không có trong CSDL** (cấu hình ở file trạm) | 19 / 19 | ✅ 54 (SID rỗng) | **`mindray-bc3k`, không phải ASTM** → xem `mindray-bc3200.md` |
| 5 | Tosoh **HLC-723GX** | HbA1c (HPLC) | Không có | 1 / 1 | — | **`tosoh-g8`, không phải ASTM** → xem `tosoh-hlc723-g11.md` |
| 7 | **CHEM7** | Sinh hóa *(suy luận từ mã; có thể là Erba Chem-7 bán tự động)* | Không có | 27 / 27 | — | — |
| 8 | **CON500** | Huyết học 3 thành phần *(suy luận từ mã)* | Không có | 21 / 21 | — | — |
| 9 | **EBA200** | Sinh hóa *(suy luận từ mã)* | Không có | 13 / 12 | — | — |
| 11 | **BIOLIS50** | Sinh hóa *(suy luận; có thể là Biolis 50i)* | Không có | 18 / 18 | — | — |
| 13 | **ST200** | Điện giải *(suy luận từ mã Na/K/Cl/iCa)* | Không có | 4 / 4 | — | — |
| 14 | **ST200PRO** | Điện giải *(suy luận)* | Không có | 4 / 4 | — | — |
| 20 | **CelltacG** | Huyết học *(suy luận theo tên: Nihon Kohden Celltac G)* | Không có | 0 | — | — |

Tham số serial mọi máy có cấu hình: `9600,8,N,1`. Với COBAS 8000, LIS cũ đóng vai **client** nối tới máy (`ConType = S`) → máy là server TCP; địa chỉ/cổng lấy theo cấu hình tại chỗ, không ghi ở tài liệu.

## 3. Khung truyền và bắt tay

Theo `astm-e1394.md` (E1381): ENQ/ACK/NAK/EOT, frame `<STX>FN…<ETX>C1C2<CR><LF>`, checksum mod 256, frame ≤ 247 byte. Riêng từng máy:

- **e2010** — chi tiết đầy đủ ở **`roche-cobas-e411.md`** (Host Interface Manual e411 v2.2 và US 2016). Tóm tắt: tài liệu của cobas e 411 (máy kế nhiệm, giữ giao thức Elecsys 2010 dưới tên "Elecsys type"): RS-232 4800/9600/19200, khuyến nghị 9600 8N1; frame 247 byte; bán song công; contention máy ưu tiên (chờ 1 s gửi lại ENQ), bị NAK chờ 10 s, tối đa 6 lần, timeout 15 s. e2010 còn có **record M (Manufacturer Specific)** mà e411 bỏ — driver phải bỏ qua record M không lỗi.
- **COBAS 8000** — chi tiết ở **`roche-cobas-8000.md`** (Host Interface Manual data manager v16, SW 1.06.01). LIS nói chuyện với **data manager**, không với từng module c/e. Giữ nguyên tầng thấp ASTM trên TCP (ENQ/ACK/EOT, frame ≤ 240, checksum mod 256), nhiều record trong một frame, UTF-8. Mặc định data manager là **TCP client**, LIS là server (cổng 50001 khi có firewall cobas IT); BVĐK 10 đang chạy ngược lại (LIS cũ là client) → driver phải hỗ trợ cả hai vai trò.
- **Cell-Dyn Ruby** — chi tiết ở **`abbott-celldyn-ruby.md`** (Host Interface Specification 09H05-01 Rev. C): RS-232 DB-9, 9600 8N1 mặc định; record được **đóng gói nối nhau trong frame 240 ký tự** — phải ghép frame rồi mới tách theo `<CR>`.
- **Các máy còn lại** — tài liệu không nêu — cần đo trên máy thật.

## 4. Bản tin kết quả (máy → LIS)

Chung: `H → P → O (SID ở O.3) → R… → L`; mã chỉ số trong R.3, giá trị R.4, đơn vị R.5.

Riêng:

| Máy | Universal Test ID (R.3) | Ghi chú |
|---|---|---|
| e2010 | Elecsys type: `^^^<mã>^<pha loãng>` (vd `^^^10^^0`); cobas type: `^^^<mã>/<pha loãng>/<tiền pha loãng>` | Mã = "host code" số (tối đa 3 chữ số) cài trên máy cho từng test. Ở cobas type thành phần 4 là `100/1` → phải cắt sau `/` |
| COBAS 8000 | `^^^<mã ứng dụng>/<pha loãng>/<tiền pha loãng>` (vd `^^^627/1/not`), mã tối đa 5 chữ số | LIS cũ đọc nguyên thành phần 4 không cắt `/` — có thể là lý do chỉ nhận 1 kết quả *(suy luận)*. QC: O-12 = `Q`. Xem `roche-cobas-8000.md` |
| Cell-Dyn Ruby | Mã ở **thành phần 7** — **đã xác nhận** theo tài liệu hãng: `^^^CBC^^^MCHC` (thành phần 4 là loại xét nghiệm CBC/CBC+NOC/RETIC). Dùng `generic-astm` → mã ra `CBC`, sai. Xem `abbott-celldyn-ruby.md` | Đơn vị kiểu Mỹ: `K/µL`, `M/µL`, `g/dL` |
| BC-3200 | **Không có R.3** — máy không nói ASTM | Theo Phụ lục D sổ tay BC-3200: khối chữ số độ rộng cố định (`<STX>A…<EOF>`), chỉ có **số ID 8/10 chữ số**, không có barcode → giải thích 54 kết quả SID rỗng. Xem `mindray-bc3200.md` |
| Còn lại | Không có dữ liệu | |

QC / chuẩn: LIS cũ có trạng thái `QC`, `CAL` nhưng không ghi dựa vào trường nào — tài liệu không nêu, cần đo trên máy thật.

## 5. Hỏi chỉ định / gửi worklist

- **e2010 / cobas e411:** có hỏi chỉ định bằng record `Q` (vd `Q|1|^SID^…^SAMPLE^NORMAL||ALL||||||||O`); host trả `H-P-O-L`, không có chỉ định thì `L|1|I` (theo Host Interface Manual e411, chương ví dụ). Trạng thái ở BVĐK 10: LIS cũ không có dấu vết dùng hai chiều.
- **BC-3200:** chỉ một chiều (Phụ lục D BC-3200) — xem `mindray-bc3200.md`.
- **HLC-723GX:** hai chiều, hỏi `G,<ID>` / trả `A,<ID>,…` trong 30 s — xem `tosoh-hlc723-g11.md`.
- **COBAS 8000:** hai chiều — data manager hỏi `TSREQ`, LIS trả `TSDWN` (mặc định chờ 16 s); xem `roche-cobas-8000.md`.
- **Cell-Dyn Ruby:** hai chiều — máy hỏi `Q|1|^ALL` định kỳ hoặc theo ID; xem `abbott-celldyn-ruby.md`.
- **Các máy khác:** tài liệu không nêu. LIS cũ chỉ nhận kết quả.

## 6. Bảng mã chỉ số

Mã máy ↔ chỉ số đúng như LIS cũ (`anh-xa-ma-may.json`). Đơn vị là đơn vị trong danh mục LIS.

**e2010 (InsID 2)** — `100` CA 125 · `11` PSA toàn phần · `111` Anti SARS-CoV-2 S · `13` HBsAg (COI) · `15` FT4 · `19` AFP · `200` → `hc12` (**không tên, không đơn vị** — dòng rác) · `222` Troponin T-hs · `3` Cortisol sáng **và** chiều (trùng) · `301` CEA · `332` CA 15-3 · `351` CA 19-9 · `360` CA 72-4 · `381` Ferritin · `410` Anti-HBs · `411` FT3 · `440` HBeAg · `510` PCT · `761` Beta-hCG · `9` TSH.

**COBAS 8000 (InsID 19)** — `134` AFP · `148` Beta-hCG · `186` C-Peptid · `202` Anti-CCP · `286` Anti-HCV · `302` FT4 · `307` CA 72-4 · `49` CEA · `499` PSA · `503` HCV Ab · `515` HBsAg · `52` CA 15-3 · `53` CA 125 · `54` CA 19-9 · `574` NT-proBNP · `576` Troponin T-hs · `580` Procalcitonin · `627` TSH · `67` Ferritin · `765` Anti-HBs · `813` FT3 · `89` Cortisol sáng **và** chiều (trùng).

**Cell-Dyn Ruby (InsID 16 và 17 — giống hệt nhau)** — `WBC` `NEU` `LYM` `MONO` `EOS` `BASO` (K/µL) · `%N` `%L` `%M` `%E` `%B` (%) · `RBC` (M/µL) · `HGB` (g/dL) · `HCT` (%) · `MCV` (fL) · `MCH` (pg) · `MCHC` (g/dL) · `RDW` (%) · `PLT` (K/µL) · `MPV` (fL) · `PCT` (%) · `PDW` (%).

**BC-3200 (InsID 3)** — `WBC` `LYM#` `MID#` `GRAN#` (10^9/L) · `LYM` `MID` `GRAN` (% — mã **không có `%`**) · `RBC` (10^12/L) · `HGB` (g/L) · `HCT` (L/L) · `MCV` · `MCH` · `MCHC` · `RDW` (→ RDW-CV) · `RDW-SD` · `PLT` · `MPV` · `PCT` · `PDW`.

**HLC-723GX (InsID 5)** — `A1C` HbA1c (%).

**CHEM7 (InsID 7)** — `ALB` `AMY` `ASO` `BID` `BII` `BIT` `CHE` `CHO` `CK` `CKMB` `CRE` `CRP` `FER` `GGT` `GHB` `GLU` `HDL` `LDL` `LIP` `PHO` `PRO` `RF` `SGOT` `SGPT` `TRI` `UAC` `URE`. Lạ: `GHB` → xét nghiệm **nồng độ cồn** (`244ACo`); `GLU` → **đường huyết sau ăn** (`120GLUS`) chứ không phải Glucose thường.

**CON500 (InsID 8)** — `WBC` `LY_` `MO_` `GR_` (#) · `LYZ` `MOZ` `GRZ` (%) · `RBC` `HGB` `HCT` `MCV` `MCH` `MCHC` `RDW` `RDW_SD` `PLT` `MPV` `PCT` `PDW`. Lạ: `GPT2` → **ALT** (chỉ số sinh hóa trên máy huyết học); `EO_` → **P-LCR**. Hai dòng này nhiều khả năng là khai nhầm.

**EBA200 (InsID 9)** — `AMY2` `CHO2` `CRE` `CRP` `GGT2` `GLU` `GOT4` `GPT2` `HDL` `TRI2` `URE` `URIC`. `GLU` map **2 dòng** (`120GLUS` và `233GLU` không tên).

**BIOLIS50 (InsID 11)** — `ALB*` `AMY*` `BILI-D` `BILI-T*` `CHO*` `CK-MB` `CRE*` `CRP*` `GGT*` `GLU*` `GOT*` `GPT*` `HDL` `LDL` `PRO-T*` `TRI*` `UA*` `URE*` — mã **có dấu `*` ở cuối**.

**ST200 (InsID 13), ST200PRO (InsID 14)** — `Na` `K` `Cl` `iCa` (mmol/L).

**CelltacG (InsID 20)** — chưa map mã nào; không có trong bảng máy cũ tiếng Việt.

## 7. Bản tin mẫu

Dữ liệu giả, checksum tính theo `astm-e1394.md` mục 3.4.

**e2010 (Elecsys type), kết quả TSH:**

```
→ <STX>1H|\^&|||MAYXN^1|||||LIS||P|1|20260915084500<CR><ETX>04<CR><LF>
→ <STX>2P|1||BN000123||NGUYEN^VAN MAU||19800101|M<CR><ETX>56<CR><LF>
→ … O|1|260915001|…|^^^9^1|R|…
→ … R|1|^^^9^^0|1.23|uIU/mL|0.27^4.20|N||F||…
→ <STX>…L|1|N<CR><ETX>…
```

(Dòng O và R chỉ minh họa vị trí; bố cục đầy đủ phải lấy theo Host Interface Manual và bản ghi thô.)

**Cell-Dyn Ruby:** xem bản tin mẫu (dữ liệu giả, checksum đã tính) ở `abbott-celldyn-ruby.md` mục 7.

## 8. Bẫy và lưu ý khi viết driver

1. **Cell-Dyn: mã ở thành phần 7** (đã xác nhận theo tài liệu hãng) — dùng nhầm `generic-astm` thì đọc ra `CBC`, mọi kết quả rơi vào "không khớp". Ở chế độ ống kín O.3 = `ID^rack^vị trí` — phải lấy thành phần 1 *(suy luận)*; tên cờ `WBC`/`MCHC`/`RRBC` trùng mã kết quả, phân biệt bằng R.4 = `FLAG`.
2. **Mã trùng một máy → hai dịch vụ:** e2010 `3` và COBAS 8000 `89` (Cortisol sáng/chiều), EBA200 `GLU`. LIS cũ gỡ bằng "BN có chỉ định nào thì lấy cái đó, hòa thì alphabet" — LIS mới nên từ chối đoán khi mơ hồ và cảnh báo.
3. **Dòng map sai hiển nhiên** (CON500 `GPT2`→ALT, `EO_`→P-LCR; CHEM7 `GHB`→nồng độ cồn; e2010 `200`→`hc12`) — rà lại với khoa XN trước khi di trú, đừng chép nguyên.
4. **Ký tự đặc biệt trong mã:** `*` (BIOLIS50), `%` (Cell-Dyn `%N`…), `#` (BC-3200 `LYM#`), `_` (CON500), `.`/`-`. Mã dịch vụ đích có `%` (`034NEU%`…) — LIS cũ so bằng `LIKE` nên `%` thành ký tự đại diện. So khớp chính xác, không dùng `LIKE`.
5. **Roche cobas type** gắn `/pha loãng` vào mã (`100/1`) — cắt trước khi tra. Biết máy đang đặt Elecsys type hay cobas type.
6. **e2010 có record M** — bỏ qua, không coi là lỗi bản tin.
7. **BC-3200: 54 kết quả SID rỗng** — đã giải thích: máy không nói ASTM, chỉ gửi số ID 8/10 chữ số (xem `mindray-bc3200.md`). Ghép mẫu phải theo ID nhập tay trên máy, không theo barcode.
7b. **HLC-723GX khai `ASTM` là sai** — dùng giao thức Tosoh (`tosoh-hlc723-g11.md`).
8. **Đơn vị Cell-Dyn (K/µL, g/dL) khác BC-3200/CON500 (10^9/L, g/L)** → LIS cũ map sang **bộ mã dịch vụ khác nhau** (`028WBC` vs `002WBC`). Không gộp.
9. **COBAS 8000 qua TCP:** theo tài liệu mặc định data manager là client; ở BVĐK 10 LIS cũ là client — xác nhận cấu hình tại chỗ; phải tự nối lại, gói TCP có thể cắt giữa frame; LIS phải ACK rất nhanh. Xem `roche-cobas-8000.md` mục 8.
10. **Máy chưa từng chạy** (CHEM7, CON500, EBA200, BIOLIS50, ST200, ST200PRO, CelltacG): giao thức `ASTM` trong CSDL chỉ là giá trị mặc định khi khai máy, **không phải bằng chứng** máy nói ASTM. Bật `raw-listen` trước.
11. Hai Cell-Dyn có cùng bảng mã nhưng ở hai trạm khác nhau — cấu hình máy phải tách theo thiết bị, bảng mã dùng chung được.

## 9. Kiểm thử

- Bản ghi thô (khử định danh) theo thứ tự ưu tiên lưu lượng: e2010 (418 KQ), Cell-Dyn Ruby ×2 (361 KQ), BC-3200, COBAS 8000.
- Mỗi máy: một mẫu thường đầy đủ chỉ số, một mẫu QC, một kết quả có cờ/ngoài dải (`<`, `>`), một mẫu không barcode; với e2010 thêm một lần hỏi chỉ định (nếu bật).
- Kiểm thử lớp ASTM chung theo `astm-e1394.md` mục 9; thêm TCP đứt/nối lại cho COBAS 8000.
- Kiểm thử ánh xạ: mã trùng (Cortisol), mã có `*`/`%`/`#`, mã chưa map.

## 10. Nguồn

- `khao-sat/lis-hien-huu.md` (mục 3, 4, 7, 9) — danh sách máy, số kết quả, cổng.
- `khao-sat/seed/du-lieu/Instrument.json` (cột `*Index`, `ConType`), `InterfacePara.json`, `khao-sat/seed/anh-xa-ma-may.json` (đếm mã theo `insId`), `ResultMapping.json`.
- `lis-agent/src/drivers.ts` — `generic-astm` (`testIdComponent: 4`), `abbott-celldyn` (`testIdComponent: 7`).
- *cobas e 411 analyzer Host Interface Manual, Version 2.2* (Roche, 11/2013) và bản US 2016 — `tai-lieu-hang/Roche_cobas_e411_Host_Interface_Manual_v2.2.pdf`, `tai-lieu-hang/Roche_cobas_e411_Host_Interface_Manual_US_2016.pdf`; chi tiết ở `roche-cobas-e411.md`. Bản công khai: https://community.senaite.org/uploads/short-url/rYdQadmOAcZfwvVi8FWLcSdgMSS.pdf
- **Tài liệu cần xin:**

| Máy | Cần xin |
|---|---|
| e2010 | Roche: Host Interface Manual của **Elecsys 2010** (bản e411 dùng tạm được cho "Elecsys type", nhưng thiếu record M) |
| COBAS 8000 | Đã có: `tai-lieu-hang/Roche_cobas-8000_data-manager_Host_Interface_Manual_v16_SW1.06.01.pdf` → `roche-cobas-8000.md`. Còn thiếu: Service Manual (cách đặt data manager làm TCP server, cổng khi không có firewall), bảng mã ứng dụng trên máy |
| Cell-Dyn Ruby | Đã có: `tai-lieu-hang/Abbott_CELL-DYN_Ruby_Host_Interface_Specification_09H05-01C.pdf` → `abbott-celldyn-ruby.md` |
| BC-3200 | Đã có Phụ lục D sổ tay BC-3200 (`tai-lieu-hang/Mindray_BC-3200_Manual_ES_Apendice-D_Comunicacion.pdf`). Còn thiếu: chương 5.6 (baud/parity mặc định) |
| HLC-723GX | Đã có: `tai-lieu-hang/Tosoh_HLC-723GX_Host_Connection_Specifications_Rev2.pdf` |
| CHEM7, CON500, EBA200, BIOLIS50, ST200/ST200PRO, CelltacG | Trước hết **xác định đúng hãng/model trên tem máy**, rồi xin tài liệu giao tiếp host của đúng model đó |
