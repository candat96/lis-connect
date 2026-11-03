# Tài liệu driver máy xét nghiệm — mục lục

Mỗi file trong thư mục này mô tả giao thức một máy (hoặc một họ máy) theo khung bắt buộc ở [`_KHUNG.md`](_KHUNG.md).
Ký hiệu trạng thái tài liệu: ✅ đủ để viết driver · ⚠️ thiếu một phần · ❌ chưa có đặc tả giao thức.
"Một/hai chiều" là theo tài liệu; "hai chiều" nghĩa là máy hỏi chỉ định (host query) hoặc nhận worklist.

## 1. Bảng tất cả máy

### 1.1. Ba máy ưu tiên

| Máy | Loại | Họ giao thức | Kết nối | Một/hai chiều | Trạng thái | File |
|---|---|---|---|---|---|---|
| Mindray BC-5380 / BC-5180 | Huyết học 5 thành phần | hl7-mllp (HL7 v2.3.1) | TCP — máy là client (mặc định) hoặc server | Hai chiều | ✅ | [mindray-bc5380.md](mindray-bc5380.md) |
| Beckman Coulter AU480 / AU680 | Sinh hóa | beckman-au | COM (RS-232C) | Hai chiều | ✅ | [beckman-au.md](beckman-au.md) |
| Beckman Coulter Access 2 (UniCel DxI) | Miễn dịch | astm-e1394 | COM (RS-232, DB-9) | Hai chiều | ✅ | [beckman-access2.md](beckman-access2.md) |

### 1.2. Các máy khác

| Máy | Loại | Họ giao thức | Kết nối | Một/hai chiều | Trạng thái | File |
|---|---|---|---|---|---|---|
| DIRUI BCC-3900 / BCC-3960 | Huyết học 3 thành phần | hl7-mllp (HL7 v2.3.1) | TCP — máy là client | Hai chiều | ✅ (thiếu vài chi tiết) | [dirui-bcc3900.md](dirui-bcc3900.md) |
| DIRUI CS-T240 / CS-T240plus | Sinh hóa | astm-e1394 | COM trên PC điều khiển (≠ COM1), 19200 8N1 | Hai chiều | ✅ | [dirui-cst240.md](dirui-cst240.md) |
| DIRUI HC-300 (họ H) | Nước tiểu | dòng chữ cố định Dirui H | COM | Một chiều | ⚠️ | [dirui-hc300.md](dirui-hc300.md) |
| Beckman/Olympus AU2700 (AU5400) | Sinh hóa | beckman-au | COM (RS-232C) | Hai chiều | ✅ | [beckman-au.md](beckman-au.md) |
| Beckman AU5800 | Sinh hóa | beckman-au | COM hoặc TCP (vai trò TCP chưa rõ) | Hai chiều | ✅ RS-232C · ⚠️ TCP (vai trò, cổng chưa rõ) | [beckman-au.md](beckman-au.md) |
| Olympus AU640 | Sinh hóa | beckman-au | COM (1 kênh RS-232C) | Hai chiều *(suy luận — cùng họ AU)* | ⚠️ (suy theo AU2700) | [beckman-au.md](beckman-au.md) |
| Roche Elecsys 2010 / cobas e411 | Miễn dịch | astm-e1394 | COM (BVĐK 10: COM6, 9600 8N1) | Hai chiều | ✅ (bảng mã test lấy từ máy) | [roche-cobas-e411.md](roche-cobas-e411.md) |
| Roche cobas 8000 (qua **data manager**) | Sinh hóa + miễn dịch | astm-e1394 qua TCP (giữ ENQ/ACK, checksum) | TCP — mặc định data manager là client (cổng 50001 khi có firewall); BVĐK 10 đang chạy LIS là client | Hai chiều (TSREQ/TSDWN) | ✅ | [roche-cobas-8000.md](roche-cobas-8000.md) |
| Abbott Cell-Dyn Ruby (×2) | Huyết học 5 thành phần | astm-e1394 (mã ở thành phần 7, record nối trong frame 240) | COM (DB-9, 9600 8N1) | Hai chiều (máy hỏi `Q`) | ✅ | [abbott-celldyn-ruby.md](abbott-celldyn-ruby.md) |
| Mindray BC-3200 (họ BC-2800/3000Plus) | Huyết học 3 thành phần | fixed-offset Mindray (khối chữ số độ rộng cố định, **không phải ASTM**) | COM | Một chiều | ✅ | [mindray-bc3200.md](mindray-bc3200.md) |
| Tosoh HLC-723GX | HbA1c (HPLC) | tosoh-g8 (LIS cũ khai ASTM — sai) | COM (RS-232C, D-SUB 9) | Hai chiều (hỏi `G,<ID>`) | ✅ | [tosoh-hlc723-g11.md](tosoh-hlc723-g11.md) |
| Tosoh HLC-723 G11 | HbA1c (HPLC) | tosoh-g8 *(suy luận — theo đặc tả GX, định dạng G8)* | COM (BVĐK 10: COM5, 9600 8N1) | Hai chiều *(suy luận)* | ⚠️ (chưa có tài liệu riêng G11) | [tosoh-hlc723-g11.md](tosoh-hlc723-g11.md) |
| Tosoh AIA-360 | Miễn dịch | fixed-offset (khung chưa rõ) | COM (COM1, 9600 8N1) | Một chiều | ❌ | [tosoh-aia360.md](tosoh-aia360.md) |
| ACON Mission U500 / U120 | Nước tiểu | văn bản bản in (U120) / HL7 (U500, chưa có đặc tả) | COM / USB | Một chiều | ⚠️ | [acon-mission-u500.md](acon-mission-u500.md) |
| CYBOW R-50S (DFI) | Nước tiểu | chưa xác định | USB-serial (COM ảo) | Một chiều | ❌ | [cybow-r50s.md](cybow-r50s.md) |
| Sysmex CA-500 series | Đông máu | sysmex-ca (CA1000/CA500) hoặc astm-e1394 | COM (DB-9 "HOST") | Hai chiều (Class B / ASTM) | ✅ (ASTM theo đặc tả CA-600 — cần xác nhận bằng bản ghi thô) | [sysmex-ca500.md](sysmex-ca500.md) |
| Sysmex CA-600 series | Đông máu | sysmex-ca hoặc astm-e1394 | COM | Hai chiều (Class B) | ✅ (CA1000/CA500 và ASTM) | [sysmex-ca500.md](sysmex-ca500.md) |
| Erba/khác CHEM7, EBA200 | Sinh hóa | chưa rõ (LIS cũ khai ASTM) | Chưa cấu hình | Chưa rõ | ❌ | [cac-may-astm-bvdk10.md](cac-may-astm-bvdk10.md) |
| BIOLIS50 (Biolis 50i?) | Sinh hóa | chưa rõ (LIS cũ khai ASTM) | Chưa cấu hình | Chưa rõ | ❌ | [cac-may-astm-bvdk10.md](cac-may-astm-bvdk10.md) |
| CON500 | Huyết học | chưa rõ (LIS cũ khai ASTM) | Chưa cấu hình | Chưa rõ | ❌ | [cac-may-astm-bvdk10.md](cac-may-astm-bvdk10.md) |
| ST200 / ST200PRO | Điện giải | chưa rõ (LIS cũ khai ASTM) | Chưa cấu hình | Chưa rõ | ❌ | [cac-may-astm-bvdk10.md](cac-may-astm-bvdk10.md) |
| Nihon Kohden Celltac G (MEK-9100) | Huyết học | chưa rõ | Chưa cấu hình | Chưa rõ | ❌ | [cac-may-astm-bvdk10.md](cac-may-astm-bvdk10.md) |
| Mindray A-series | **Máy gây mê — không thuộc phạm vi LIS** | (hl7-mllp, IHE PCD) | — | — | không áp dụng | [mindray-a-series.md](mindray-a-series.md) |

Lớp giao thức chung: [astm-e1394.md](astm-e1394.md) (ASTM E1381/E1394 — tầng thấp, record, checksum).

## 2. Họ giao thức

Đầu vào thiết kế driver dùng chung: mỗi họ là một bộ "khung + bộ phân tích" dùng lại được; phần riêng từng máy chỉ là cấu hình (vị trí mã, bảng mã, tham số cổng).

| Họ | Đặc điểm khung | Máy thuộc họ |
|---|---|---|
| **hl7-mllp** | HL7 v2.x trong khung MLLP `<VT>…<FS><CR>`, TCP; ACK bằng bản tin `ACK` | Mindray BC-5380/5180, DIRUI BCC-3900; (Mindray A-series — ngoài phạm vi); ACON U500 *(theo quảng cáo, chưa có đặc tả)* |
| **astm-e1394 qua COM** | E1381: ENQ/ACK/EOT, `<STX>FN…<ETX>C1C2<CR><LF>`, checksum mod 256; record H/P/O/R/C/Q/L | Beckman Access 2, DIRUI CS-T240, Roche e411/Elecsys 2010, Abbott Cell-Dyn Ruby, Sysmex CA-500/600 (định dạng ASTM/ASTM2); Mindray A50 HbA1c (Bi-LIS, bộ tiếng Trung) |
| **astm-e1394 qua TCP** | Record E1394 trên socket; có giữ ENQ/ACK/checksum hay không tùy máy (cobas 8000: giữ nguyên tầng thấp E1381) | Roche cobas 8000 data manager; các máy Mindray BS/CL/BC-6800 khi chọn ASTM (bộ tiếng Trung) |
| **beckman-au** | Giao thức "AU Online" riêng: `<STX>` + mã phân biệt văn bản 2 ký tự + trường cố định độ rộng + `<ETX>`, Class A/B, hỏi chỉ định `R␣`/`S␣`; độ dài trường/timeout khác theo đời máy | Beckman AU480, AU680, AU640, AU2700/AU5400, AU5800 (RS-232C; TCP/IP của AU680/AU5800 chưa có đặc tả) |
| **sysmex-ca** | Định dạng CA1000/CA500: `STX` + văn bản cố định vị trí + `ETX`, không checksum; Class A (một chiều) / Class B (ACK, hỏi chỉ định) | Sysmex CA-500 series, CA-600 series |
| **tosoh-g8** | Handshake Tosoh: `<STX>text<ETX><BCC>` (BCC = XOR, 1 byte), ACK/NAK từng khối, gửi lại ≤ 5 lần, `EOT` kết thúc; text là trường độ rộng cố định (định dạng GX/G8/G9/G7/GHbV); hỏi chỉ định `G,`/`A,` | Tosoh HLC-723GX, G8, G9, G7, G11 *(G11: suy luận)* |
| **dòng chữ cố định Dirui H** | `STX CR LF` + các dòng văn bản (`Date:`, `No.`, `ID`, mỗi chỉ số một dòng: cờ 1 ký tự + mã + giá trị) + `ETX`; không checksum, không ACK | DIRUI HC-300, H-100, H-800 |
| **văn bản bản in** | Máy đẩy đúng nội dung bản in ra cổng; cắt bản tin theo khoảng lặng/dòng trống | ACON U120, CYBOW R-50S *(suy luận)* |
| **fixed-offset** | Đọc theo vị trí byte cố định trên một bản ghi, khung chưa rõ | Tosoh AIA-360 (theo LIS cũ) |
| **fixed-offset Mindray BC** | `<STX>`/`<ENQ>` + khối chữ số độ rộng cố định (ID 8/10 chữ số, 19 chỉ số, histogram 256 kênh) + `<EOF>`/`<EOT><ETX>`, không checksum | Mindray BC-3200, BC-3000Plus, BC-2800, BC-2600, BC-2300, BC-1800 |

## 3. Tài liệu còn thiếu

Đã có (đưa vào `tai-lieu-hang/`, không còn thiếu): HC-300 User Manual, CA-600 ASTM Host Interface Specification, Phụ lục D BC-3200, AU5800 Online Specification v5.0, HLC-723GX Host Connection Specifications, CELL-DYN Ruby Host Interface Specification, cobas 8000 data manager Host Interface Manual.

Người dùng tự tải từ link (Scribd/pdfcoffee cần tài khoản) rồi đặt vào `tai-lieu-hang/`, hoặc xin nhà phân phối.

| Máy | Tài liệu cần | Nơi lấy |
|---|---|---|
| Roche cobas 8000 | Service Manual (đặt data manager làm TCP server, cổng khi không có firewall); bảng mã ứng dụng trên máy | Xin nhà phân phối Roche (bản Host Interface 1.06 trên Scribd chỉ để đối chiếu: https://www.scribd.com/document/786006390/cobas-8000-Host-Interface-manual-1-06-01) |
| Tosoh HLC-723 G11 | Operator's Manual HLC-723G11 VAR Rev B (xác nhận G11 có định dạng G8) | https://pdfcoffee.com/17operatorsmanual-hlc-723g11-var-revb-pdf-free.html |
| Mindray BC-3200 | Chương 5.6 sổ tay (baud/parity mặc định); bản đối chiếu "Protocolo de Comunicación BC-2800/3200" (không bắt buộc) | https://www.scribd.com/document/326701650/Protocolo-de-Comunicacion-Mindray-Bc-2800-3200 |
| Beckman AU5800 / AU680 qua TCP/IP | Đặc tả TCP/IP online (vai trò client/server, cổng, khung) | Xin nhà phân phối Beckman |
| Olympus AU640 | AU640 Online Specification | Xin nhà phân phối |
| Roche Elecsys 2010 | Elecsys 2010 Host Interface Manual (nếu máy BVĐK 10 đúng là 2010) + bảng Host Test Code trên máy | Xin nhà phân phối Roche |
| Tosoh AIA-360 | Host interface specification | Xin nhà phân phối |
| ACON Mission U120 / U500 | Communication Insert / đặc tả "HL-7" của U500, tham số cổng U120 | Xin nhà phân phối |
| CYBOW R-50S | Đặc tả truyền dữ liệu (khung, bố cục) | Xin nhà phân phối |
| Nihon Kohden Celltac G MEK-9100 | Host interface specification | Xin nhà phân phối |
| Biolis 50i | Host interface specification | Xin nhà phân phối |
| Erba EBA200 / Chem 7 | Host interface specification (xác định đúng hãng/model trên tem máy trước) | Xin nhà phân phối |
| CON500 | Host interface specification (xác định đúng hãng/model) | Xin nhà phân phối |
| ST200 / ST200PRO | Host interface specification (xác định đúng hãng/model) | Xin nhà phân phối |

## 4. Thư viện tài liệu hãng (`tai-lieu-hang/`)

Thư mục bị `.gitignore` — có trên máy dev, không có trong repo.

| Tệp | Máy | Nội dung chính | Dùng trong |
|---|---|---|---|
| `Abbott_CELL-DYN_Ruby_Host_Interface_Specification_09H05-01C.pdf` | Abbott CELL-DYN Ruby | Đặc tả giao tiếp host (ASTM), rev C | abbott-celldyn-ruby.md |
| `ACON_Mission_U500_Urine_Analyzer.pdf`, `…_US.pdf` | ACON U500 | Brochure (cổng, "HL-7 compliant") — không phải đặc tả | acon-mission-u500.md |
| `Beckman_Access2_UniCel-DxI_LIS_Vendor_Information_C03112-AB_2022.pdf` | Access 2 / DxI | LIS Vendor Information: tầng thấp, bản tin, mã test | beckman-access2.md |
| `Beckman_AU680_AU480_Online_Specifications_2010.pdf` | AU480/AU680 | Online Specification (giao thức AU) | beckman-au.md |
| `Beckman_AU5800_Online_Specification_v5.0_2015.pdf` | AU5800 | Online Spec v5.0, ấn bản 11 (RS-232C) | beckman-au.md |
| `Beckman_AU5800_Reference_Manual_B81389AA.pdf` | AU5800 | Reference Manual, tr. 2-75…2-85 cài đặt online, TCP/IP, T1–T4 | beckman-au.md |
| `Beckman_AU5800_IFU_A98352AC.pdf` | AU5800 | Hướng dẫn sử dụng (lịch sử phiên bản: TCP/IP từ phần mềm 4.11) | beckman-au.md |
| `Beckman_AU640_Users_Guide_Vol1.pdf` | AU640 | Hướng dẫn sử dụng | beckman-au.md |
| `Olympus_AU640_Service_Manual.pdf` | AU640 | Service manual (RS-232C 1 kênh, B2-11) | beckman-au.md |
| `Olympus_AU2700_AU5400_Online_Specifications_2000.pdf` (+ `.txt`) | AU2700/AU5400 | Online Specifications đầy đủ | beckman-au.md |
| `CYBOW_R-50S_User_Manual.pdf` | CYBOW R-50S | User manual (USB, baud) — không có khung | cybow-r50s.md |
| `Dirui_BCC-3900_Manual_ES.pdf` | DIRUI BCC-3900 | User manual + Appendix A HL7 V1.1 | dirui-bcc3900.md |
| `Dirui_CS-T240_User_Manual.pdf` | DIRUI CS-T240 | User manual + Addendum D ASTM | dirui-cst240.md |
| `Dirui_HC-300_User_Manual_EN_2023-08.pdf` | DIRUI HC-300 | User manual, Appendix B định dạng truyền, Appendix C quy đổi | dirui-hc300.md |
| `Dirui_H-Series_H-100_User_Manual_REV06-2017.pdf` (+ `.txt`) | DIRUI H-100 | Đối chiếu cùng họ H | dirui-hc300.md |
| `Mindray_BC-3200_Manual_ES_Apendice-D_Comunicacion.pdf` | BC-3200 | Phụ lục D giao tiếp (lớp chữ hỏng — đọc ảnh) | mindray-bc3200.md |
| `Mindray_BC-3000Plus_Communication_Protocol_V16_CN_BC-3200-family.pdf` | BC-3000Plus | Phụ lục D tiếng Trung (8ID/10ID/15ID) | mindray-bc3200.md |
| `Mindray_BC-2600_15ID+2_Protocol_CN.pdf` | BC-2600 | Phụ lục D tiếng Trung (8ID/15ID) | mindray-bc3200.md |
| `Mindray_BC-3000_mau_du_lieu_tho/` | BC-3000Plus | Dữ liệu thô mẫu (hex) — chỉ để xác minh, không chép | mindray-bc3200.md |
| `Mindray_BC-5380_Operator_Manual_v2.0_2011.pdf` | BC-5380/5180 | Operator's Manual, Appendix C HL7 | mindray-bc5380.md |
| `Mindray_BC-5380_BC-5180_Service_Manual_2019.pdf` | BC-5380/5180 | Service manual mục 3.6 LIS | mindray-bc5380.md |
| `Mindray_giao-thuc-goc_CN/` | Nhiều dòng Mindray | Bộ giao thức gốc tiếng Trung (mục 4.1) | — |
| `Roche_cobas-8000_data-manager_Host_Interface_Manual_v16_SW1.06.01.pdf` | cobas 8000 (data manager) | Host Interface Manual v16, SW 1.06.01 (TCP, ASTM, TSREQ/TSDWN) | roche-cobas-8000.md |
| `Roche_cobas_e411_Host_Interface_Manual_v2.2.pdf`, `…_US_2016.pdf` | cobas e411 / Elecsys 2010 | Host Interface Manual | roche-cobas-e411.md |
| `Sysmex_CA-500_Service_Manual.pdf` | CA-500 | Service manual (cổng, định dạng CA500) | sysmex-ca500.md |
| `Sysmex_CA-600_User_Manual.pdf` | CA-600 | User manual mục 14.4–14.5 | sysmex-ca500.md |
| `Sysmex_CA-600_ASTM_Host_Interface_Specification_v1.03_2014.pdf` | CA-600 (và CA-500) | Đặc tả ASTM/ASTM2 | sysmex-ca500.md |
| `Tosoh_HLC-723GX_Host_Connection_Specifications_Rev2.pdf` | HLC-723GX (G8/G9/G7) | Handshake, BCC, hỏi chỉ định, 5 định dạng | tosoh-hlc723-g11.md |
| `KHONG-DUNG_Mindray_A-series_may-gay-me_HL7.pdf` | Mindray A-series | **Máy gây mê — không thuộc LIS** | mindray-a-series.md |
| `KHONG-DUNG_Mindray_PatientDataShare_monitor_HL7_v14.2.pdf` | Monitor Mindray | **Không thuộc LIS** | — |

### 4.1. Bộ giao thức gốc Mindray tiếng Trung — danh mục

Chưa viết driver cho các máy này; danh mục để biết tài liệu nào sẵn có khi cần.

| Tệp (trong `tai-lieu-hang/Mindray_giao-thuc-goc_CN/`) | Model | Loại máy | Giao thức |
|---|---|---|---|
| `LIS协议接口手册(中文).pdf` | BS series | Sinh hóa | HL7 v2.3.1 hoặc ASTM E1394-97 qua TCP/IP, hai chiều |
| `生化/-BS-300&380&400&480&800&2000_LIS协议接口手册_V4.0_CH.pdf` | BS-300/380/400/480/800/2000 | Sinh hóa | HL7 v2.3.1 / ASTM E1394-97, TCP/IP |
| `生化/BS-400LIS协议接口手册(01.03.14适用).pdf` | BS-400 | Sinh hóa | HL7 v2.3.1 / ASTM E1394-97, TCP/IP |
| `生化/BS800 LIS协议接口手册.doc` | BS-800 | Sinh hóa | HL7 v2.3.1 / ASTM E1394-97, TCP/IP |
| `生化/BS300-HL7接口手册[中文].pdf` | BS-300 | Sinh hóa | HL7 v2.3.1, TCP |
| `生化/BS2000M/bs2000M.txt` | BS-2000M | Sinh hóa | Bản ghi thô HL7 `ORU^R01` (không phải tài liệu) |
| `发光/CL-2000i_LIS协议接口手册_V2.0_CH.pdf` | CL-2000i | Miễn dịch hóa phát quang | HL7 v2.3.1 / ASTM E1394-97, TCP/IP |
| `发光/SAL-8000&CL-2000i_LIS协议接口手册_V2.0_CH.pdf` | SAL-8000 + CL-2000i | Dây chuyền + miễn dịch | như trên (trùng nội dung) |
| `发光/IMS1200 LIS接口(HL7).doc` | IMS1200 | Miễn dịch | HL7 v2.3.1, TCP |
| `血凝/迈瑞EXC810.doc` | ExC810 (MPCoagu) | Đông máu | HL7 qua TCP (máy server hoặc client) + ASTM LIS2-A2; có nhắc cổng nối tiếp |
| `血常规/BC-1800_中文使用说明书_V16.0_CH.pdf` | BC-1800 | Huyết học 3 thành phần | RS-232 khung riêng 8ID (họ BC-3200); baud 1200–19200 |
| `血常规/BC-2600_操作说明书_V16.0_CH.PDF` | BC-2600 | Huyết học 3 thành phần | RS-232 khung riêng 8ID/15ID |
| `血常规/BC-2600 15ID+2 说明.pdf` | BC-2600 | Huyết học 3 thành phần | RS-232 khung riêng 8ID/15ID/15ID+2 |
| `血常规/BC-3000Plus__通信协议___V16.0_CH.pdf` | BC-3000Plus | Huyết học 3 thành phần | RS-232 khung riêng 8ID/10ID/15ID (dùng cho `mindray-bc3200.md`) |
| `血常规/迈瑞bc2300血球联机原始数据/` | BC-2300 | Huyết học 3 thành phần | Bản ghi thô 10ID; cài đặt 9600,7,N, tắt xác nhận |
| `血常规/BC_5500.pdf`, `血常规/迈瑞BC5500参考资料给第三方lis公司/…` | BC-5500 | Huyết học 5 thành phần | Khung nhị phân riêng qua TCP, máy là server, cổng 5500; kèm thư viện trung gian |
| `血常规/5800&5600通信部分.pdf` (+ bản trùng trong `迈瑞BC-5800&BC-5600LIS接口手册/`) | BC-5800/5600 | Huyết học 5 thành phần | HL7 qua TCP, có trả lời hỏi worklist |
| `血常规/迈瑞BC-5800&BC-5600LIS接口手册/迈瑞5800&5600通信差异.pdf` | BC-5800 | Huyết học | Khác biệt 15ID(1.0)/15ID(1.1) |
| `血常规/血常规BC5300.pdf` | BC-5300 (nhắc BC-5380) | Huyết học 5 thành phần | HL7 v2.3.1, TCP |
| `血常规/BC-5390&BC-5310&BC-5180_通信说明_V1.0_CH.pdf` | BC-5390/5310/5180 | Huyết học 5 thành phần | HL7, TCP |
| `血常规/BC5390CRP.pdf` | BC-5390 CRP / BC-5180 CRP | Huyết học + CRP | HL7, TCP |
| `血常规/BC-6800_通信协议_V1.0_CH.pdf` | BC-6800/6600 | Huyết học 5 thành phần | HL7 qua TCP (server hoặc client) + ASTM LIS2-A2 |
| `血常规/BC6000_labXpert 通信协议_V2.0_CH(1).pdf` (+ bản trùng) | BC-6000 / labXpert | Huyết học | HL7 + ASTM LIS2-A2, TCP |
| `血常规/H50&H50P_通讯协议_V1.0_CH.pdf` | H50/H50P | Huyết học | HL7 v2.3.1, TCP |
| `糖化/双向[A50]Bi-LIS Protocol_Serial_0.3.pdf` | A50 (giao thức Bi-LIS) | HbA1c | RS-232, khung kiểu ASTM `<STX>FN…<ETX>C1C2`, Q/O/R, hai chiều |
| `bc3000原始数据/` | BC-3000Plus | Huyết học | Bản sao dữ liệu thô — không dùng |

Giao thức xác định qua mục lục/từ khóa, chưa đọc hết từng tệp.

## 5. Nguyên tắc chung

1. **Không commit PDF/tài liệu của hãng.** Tệp gốc nằm ở `docs/drivers/tai-lieu-hang/` (thư mục bị `.gitignore`); file driver chỉ ghi tên tài liệu, phiên bản, trang/mục và tham chiếu bằng đường dẫn tương đối `tai-lieu-hang/<tên tệp>`.
2. **Mọi driver phải kiểm chứng bằng bản ghi thô từ máy thật** trước khi bật cho người bệnh. Các bản ghi trong kho cũ (`lis-agent/.demo-data/rawlog/…`) là **dữ liệu do bộ giả lập phát**, không có giá trị kiểm chứng.
3. **Dữ liệu mẫu chỉ dùng dữ liệu giả** (tên chứa "Mẫu/Thử", mã số giả) — không chép dữ liệu người bệnh thật vào tài liệu hay test.
4. Điều tài liệu không nói ghi "Tài liệu không nêu — cần đo trên máy thật"; điều suy luận đánh dấu *(suy luận)*; nguồn mã nguồn mở ghi rõ là mã nguồn mở, không coi như tài liệu hãng.
