# Tài liệu cần tải (danh sách lập ngày 03/11/2026)

Hướng dẫn ngắn: tải xong để vào `~/Downloads` rồi gửi đường dẫn cho Claude. Scribd cần đăng nhập mới tải được. pdfcoffee hay báo lỗi 503, gặp thì thử lại sau ít phút hoặc bấm nút **Download** trên trang.

Cách tìm: Google, Bing, DuckDuckGo và Brave đều chặn truy vấn tự động, nên danh sách này lấy từ Yahoo! Japan (dùng kết quả của Google), Naver và GitHub API. Mọi link dưới đây đều thấy thật trong kết quả tìm kiếm; tên tài liệu và số trang kiểm qua trang mô tả khi xem được. Những tài liệu đã có trong `tai-lieu-hang/` thì không ghi lại (AU5800 Online Spec v5.0, AU680/AU480 Online Spec 2010, HLC-723GX Host Connection Rev2, U120 manual của palliance.se, …).

Khả năng đúng: **Cao** là đúng tài liệu giao tiếp host của máy (hoặc của máy cùng họ, rất sát). **TB** là tài liệu có chương hoặc phụ lục về truyền dữ liệu, hoặc là của máy cùng hãng nhưng khác đời. **Thấp** là HDSD/brochure chỉ để tra cấu hình cổng.

---

## 1. Tosoh AIA-360 — Host Computer Connection Specifications

| # | Link | Trang web | Tài liệu | Khả năng đúng | Ghi chú |
|---|---|---|---|---|---|
| 1 | https://www.tosohbioscience.com/US-EN-diagnostics/products/aia-360 | Tosoh Bioscience (US) | Danh mục tài liệu của máy có mục "Host Computer Connection Specifications for the AIA-360 System" | Cao | Bản gốc của hãng. Trang chặn bot (403), phải mở bằng trình duyệt; có thể đòi đăng nhập portal khách hàng. **Thử link này trước.** |
| 2 | https://www.scribd.com/document/622539163/TOSOH-AIA-1800-Host-Computer-Connection-Specifications-Rev4 | Scribd | TOSOH_AIA-1800_Host Computer Connection Specifications_Rev4 (11 tr.) | Cao | Máy cùng họ AIA. Theo repo GitHub hmislk/AIA_360 và nishishailesh/tosoh_host_com, họ AIA dùng chung khung giao thức; vẫn phải đối chiếu với AIA-360. |
| 3 | https://pdfcoffee.com/aia-360operatorsmanual9pdf-pdf-free.html | pdfcoffee | AIA-360 Operator's Manual Revision 9 | TB | Chủ yếu để xem chương cài đặt Host/RS-232C (tốc độ baud, bật/tắt truyền). Bản trên Scribd: https://www.scribd.com/document/364680971/AIA-360OperatorsManual-9-pdf |
| 4 | https://www.scribd.com/document/229135232/AIA360-Operator-s-Manual | Scribd | AIA360 Operator's Manual (bản khác) | TB | Dự phòng nếu link #3 lỗi. |
| 5 | https://pdfcoffee.com/aia360-servicetrainingguiderev12pdf-pdf-free.html | pdfcoffee | AIA360 Service Training Guide Rev12 (Tosoh Europe) | Thấp | Tài liệu service, có thể có phần cấu hình cổng giao tiếp. |

## 2. Tosoh HLC-723 G11 — HLC-723G11 Host Computer Connection Specification Manual

HDSD G11 có ghi rõ phần giao tiếp nằm ở tài liệu riêng: "HLC-723G11 Host Computer Connection Specification Manual". Không tìm thấy bản công khai của tài liệu này.

| # | Link | Trang web | Tài liệu | Khả năng đúng | Ghi chú |
|---|---|---|---|---|---|
| 1 | https://www.scribd.com/document/829506617/HLC-723G8Host-Connect-Spec-Rev-5 | Scribd | HLC-723G8 Host Connection Specifications Rev.5 | Cao | Đời G8, cùng họ với G11. Mới hơn bản GX Rev2 đang có; G11 nhiều khả năng giữ khung giao thức tương tự. |
| 2 | https://www.tosohbioscience.com/US-EN-diagnostics/products/hlc-723g8 | Tosoh Bioscience (US) | Danh mục tài liệu có "Host Communication Specifications for the HLC-723 G8" | Cao | Bản gốc của hãng; trang chặn bot, mở bằng trình duyệt. Có thể xem trang G11 tương ứng (…/products/hlc-723g11). |
| 3 | https://www.scribd.com/document/550852631/g11var-Om-En | Scribd | HLC-723 G11 Operator's Manual (Variant Analysis Mode) | TB | Có mục cài đặt host, dẫn sang tài liệu Host Spec. |
| 4 | https://desego.com/wp-content/uploads/2019/09/Manual-de-Operacion-G11.pdf | Desego (Mexico) | Manual de Operación HLC-723 G11 (tiếng Tây Ban Nha) | TB | PDF tải thẳng, không cần đăng nhập. |
| 5 | https://www.diagnostics.us.tosohbioscience.com/File%20Library/TBI/Country%20Downloads/G11VAR_OM-Rev-F_FR--NOT-FOR-USA-.pdf | Tosoh Bioscience | HLC-723 G11 Manuel d'utilisation (Variant), Rev F, tiếng Pháp | TB | Bản gốc của hãng, mới hơn bản Rev.B trên pdfcoffee. |

## 3. Olympus/Beckman AU640 — Online Specifications

| # | Link | Trang web | Tài liệu | Khả năng đúng | Ghi chú |
|---|---|---|---|---|---|
| 1 | https://www.scribd.com/doc/207060139/Dsg-AU400-AU640-LISspecs | Scribd | Dsg AU400 AU640 LISspecs (49 tr.): định dạng trao đổi order/kết quả giữa AU400/AU640 và host, real-time và batch | Cao | **Đúng tài liệu cần.** |
| 2 | https://www.scribd.com/document/406751272/Olympus-AU640-user-manual-pdf | Scribd | Olympus AU640 User Manual | Thấp | Đã có User's Guide Vol1, chỉ tải nếu thiếu phần cài đặt online. |
| 3 | http://www.frankshospitalworkshop.com/equipment/documents/automated_analyzer/service_manuals/Olympus_M521-M526-AU640_-_Service_manual.pdf | Frank's Hospital Workshop | Olympus AU640 Service Manual | Thấp | Có thể trùng với `Olympus_AU640_Service_Manual.pdf` đang có, kiểm tra trước khi tải. |

## 4. Beckman AU680 / AU5800 / DxC 700 AU — Online Specification (LAN/TCP-IP)

| # | Link | Trang web | Tài liệu | Khả năng đúng | Ghi chú |
|---|---|---|---|---|---|
| 1 | https://www.scribd.com/document/545809198/AU680-AU480-Online-LAN-Specification-ver-1 | Scribd | AU680/480 Online LAN Specification ver.1 | Cao | **Bản TCP/IP còn thiếu.** Bản 2010 đang có là RS-232C. |
| 2 | https://www.scribd.com/document/515281389/DxC-700-AU-Online-Specification-v1-0-2016-08-24 | Scribd | DxC 700 AU Online Specification v1.0 (24/08/2016) | Cao | |
| 3 | https://www.scribd.com/document/545809774/AU680-Host-Interface | Scribd | AU680 Host Interface | TB | Chưa xem được nội dung; có thể trùng với #1. |
| 4 | https://www.scribd.com/document/520004381/AU5800-OnlineSpecification-Aug-18-2011v4-1 | Scribd | AU5800 Online Specification v4.1 (18/08/2011) | TB | Đã có bản v5.0 (2015); chỉ cần nếu máy chạy phần mềm cũ hơn v5. |
| 5 | https://www.beckmancoulter.com/download/wsr-231082?type=pdf | Beckman Coulter | DxC 700 AU Reference Manual | Thấp | Có chương cài đặt online/LAN. |

Nhà phân phối Beckman tại Việt Nam thấy trong kết quả: Kim Hưng (kimhung.vn).

## 5. Dirui HC-300 — giao thức truyền (bảng mã ký tự, byte đệm 0xAB)

Không thấy tài liệu giao thức riêng cho HC-300. Gần nhất là phụ lục "Communication with Computer" của dòng H (H-100/H-500/H-800), nhiều khả năng cùng khung khung dữ liệu.

| # | Link | Trang web | Tài liệu | Khả năng đúng | Ghi chú |
|---|---|---|---|---|---|
| 1 | https://anyflip.com/kmfvx/fhin/basic/51-74 | AnyFlip | Dirui H-500 User Manual, trang 51–74, có "Appendix B Communication with Computer" | TB | Xem online, chụp hoặc chép lại phụ lục B. |
| 2 | https://storage.mtender.gov.md/get/1a0fff36-03e9-4a11-8c98-fdefc97c3d38-1688036645655 | Cổng đấu thầu Moldova | HDSD Dirui H Series Urine Analyzer | TB | PDF tải thẳng. Kiểm tra có phụ lục giao tiếp hay không. |
| 3 | https://www.scribd.com/document/586259568/H500-service-manual | Scribd | H-500 Urine Analyzer Training/Service Manual | TB | |
| 4 | https://imda.moh.gov.vn/documents/10182/78286054/upload_00001107_1767149522145.pdf?version=1.0&fileId=78291963 | Cổng TTB y tế Bộ Y tế | HDSD gốc máy nước tiểu Dirui dòng H (bản đăng ký lưu hành tại VN) | TB | Hồ sơ của đơn vị nhập khẩu tại VN; tên đơn vị ghi trong tệp, có thể liên hệ xin protocol. |
| 5 | https://github.com/vilake/DiruiH800Conn | GitHub | Mã nguồn kết nối Dirui H-800 | TB | Đã biết từ trước. Đọc code để biết bảng mã và byte đệm. |

Nếu các link trên vẫn không nói rõ bảng mã và byte 0xAB: **xin nhà phân phối "DIRUI HC-300 Communication Protocol / LIS Interface Specification"**.

## 6. ACON Mission U120 / U500 — LIS/Data Transfer protocol

| # | Link | Trang web | Tài liệu | Khả năng đúng | Ghi chú |
|---|---|---|---|---|---|
| 1 | https://www.scribd.com/document/614656677/Mission-U500-Improvement-Data-Transfer-Package-Insert-02232017 | Scribd | U500 Urine Analyzer Data Transfer Guide (02/2017, 1 tr.): khung ASCII gồm header, kết quả, mã lỗi; cáp null-modem/USB | Cao | Ngắn nhưng đúng định dạng truyền của U500. |
| 2 | https://www.scribd.com/document/789692926/U120-Service-manual-version-2-0 | Scribd | U120 Urine Analyzer Service Manual v2.0 | TB | Service manual thường có định dạng xuất RS-232. |
| 3 | https://imda.moh.gov.vn/documents/10182/79224424/upload_00001097_1769394882650.pdf?version=1.0&fileId=79227288 | Cổng TTB y tế Bộ Y tế | Urine Analyzer for Clinics U120 User's Manual (hồ sơ lưu hành tại VN) | TB | PDF tải thẳng. Trong tệp có tên đơn vị nhập khẩu tại VN. |
| 4 | https://www.aconlabs.com/wp-content/uploads/2020/07/1150977602-Mission-Expert-U120-Smart-Analyzer-min.pdf | ACON Labs | Mission Expert U120 Smart Urine Analyzer (HDSD) | Thấp | Bản U120 Smart. |
| 5 | https://www.connectivitywise.com/instruments/acon-laboratories-mission-u120/ | ConnectivityWise | Trang tích hợp U120 | Thấp | Chỉ để xác nhận U120 xuất qua RS-232C. |

## 7. CYBOW R-50S (DFI Co., Hàn Quốc) — PC interface protocol

Không thấy tài liệu giao thức. Có HDSD của máy cùng hãng, trong đó có cài đặt tốc độ baud khi xuất ra PC.

| # | Link | Trang web | Tài liệu | Khả năng đúng | Ghi chú |
|---|---|---|---|---|---|
| 1 | https://www.nguyenlam.vn/downloads/HDSD_CYBOW_READER_300.pdf | Nguyên Lâm (nhà phân phối VN) | HDSD CYBOW Reader 300 (có mục "Set Bps (PC)") | TB | Máy khác model nhưng cùng hãng. |
| 2 | https://www.scribd.com/document/588339485/Manual-300-V7-201607-CYBOW-2 | Scribd | User Manual CYBOW 300, V7 (07/2016, 29 tr.) | Thấp | Phần trích không có định dạng RS-232. |
| 3 | https://www.uranodiagnostics.com/uploads/imgen/730-manual-uranotest-reader-eng.pdf | Urano Diagnostics | User Manual Uranotest Reader (bản OEM của R-50S) | TB | Bản OEM có thể có phụ lục định dạng dữ liệu. |
| 4 | https://thienlocgroup.com/wp-content/uploads/2020/06/cybow_reader_300.pdf | Thiên Lộc (VN) | Brochure CYBOW Reader 300 (Interface RS232c) | Thấp | |

**Không tìm thấy, xin nhà phân phối: "CYBOW R-50S PC Interface / RS-232C Communication Protocol" (hãng DFI Co., Ltd.).** Nhà phân phối DFI tại VN thấy trong kết quả: Nguyên Lâm (nguyenlam.vn), Thiên Lộc (thienlocgroup.com), Tân Kiều (tankieu.vn).

## 8. Nihon Kohden Celltac G MEK-9100 — Host communication / External output specification

| # | Link | Trang web | Tài liệu | Khả năng đúng | Ghi chú |
|---|---|---|---|---|---|
| 1 | https://www.scribd.com/document/923979559/MEK-9100K-SystemConnection-RS232C | Scribd | MEK-9100K System Connection RS-232C ("RS-232C Communication Overview") | Cao | Đúng tên tài liệu kết nối hệ thống. |
| 2 | https://www.scribd.com/document/640622765/MEK9100-HL7 | Scribd | MEK9100 HL7 | Cao | Giao thức HL7 qua LAN. |
| 3 | https://www.scribd.com/document/829513381/C-G-MEK9100-Data-MGT-Guide | Scribd | Celltac G (MEK-9100) Data Management Guide | TB | |
| 4 | https://github.com/DinhLucent/SimulationLIS-9100 | GitHub | Bộ giả lập host ASTM qua LAN cho MEK-9100 (cổng 50003, ASTM một chiều, có/không khung E1381) | TB | Không phải tài liệu hãng nhưng đọc được ngay: cấu hình "External Output > LAN > ASTM". |
| 5 | https://pdfcoffee.com/0614-907468b-mek9100-om-pdf-free.html | pdfcoffee | MEK-9100 Operator's Manual 0614-907468B | TB | Có chương External Output. Bản Scribd: https://www.scribd.com/document/362986925/0614-907468B-MEK9100-OM |

Nihon Kohden có công ty con tại Việt Nam, có thể xin trực tiếp "MEK-9100 External Output Specification (ASTM/HL7)".

## 9. Biolis 50i (Tokyo Boeki) — Bi-directional Communication Specification

| # | Link | Trang web | Tài liệu | Khả năng đúng | Ghi chú |
|---|---|---|---|---|---|
| 1 | https://pdfcoffee.com/biolis-50i-bi-directional-astm-ver101-pdf-free.html | pdfcoffee | BiOLiS 50i Superior Bi-directional Communication Specification, ASTM Ver.1.01 | Cao | **Đúng tài liệu.** Không cần đăng nhập. |
| 2 | https://www.scribd.com/document/462287388/Biolis-50i-Bi-directional-ASTM-Ver-1-01 | Scribd | Biolis 50i Bi-directional ASTM (Ver.1.01) | Cao | Cùng tài liệu với #1 (dự phòng). |
| 3 | https://www.scribd.com/document/915958492/DataSheet-U783w-3 | Scribd | "BiOLiS 50i Communication Protocol Guide" (7 tr.) | Cao | Tên tệp lạ nhưng tiêu đề và mô tả đều là protocol 50i. |
| 4 | https://www.scribd.com/document/629956900/30i-Communication-Specifications-V1-4 | Scribd | BiOLiS 30i Communication Specifications V1.4 | TB | Máy cùng họ, để đối chiếu. |
| 5 | https://pdfcoffee.com/90-55-3000biolis-50ioperatorx27s-mv57pdf-pdf-free.html | pdfcoffee | BIOLIS 50i Operator's Manual V5.7 | Thấp | Để xem cài đặt host trên máy. |

Biolis 30i lưu hành tại VN qua Amethyst Medical (theo hồ sơ trên vimda.moh.gov.vn).

## 10. "EBA 200" và Erba Chem 7

**EBA 200:** tên này trùng máy ly tâm Hettich EBA 200 (https://www.hettichlab.com/products/centrifuges/small-centrifuges/eba-200-200-s/), loại máy không có LIS. Tuy vậy, trong CSDL LIS cũ máy EBA200 có mã xét nghiệm sinh hóa (AMY2, CHO2, CRE, CRP, GGT2, GLU, …), nên đây là máy sinh hóa, không phải máy ly tâm. Không xác định được hãng: có thể là Erba (XL-200, EM-200) hoặc tên tự đặt. Tìm "Erba XL-200 / EM 200 LIS protocol" không ra tài liệu nào. **Phải xem tem máy trước**, rồi mới xin "Host Interface / LIS Communication Protocol" của đúng model.

**Erba Chem 7:**

| # | Link | Trang web | Tài liệu | Khả năng đúng | Ghi chú |
|---|---|---|---|---|---|
| 1 | https://www.scribd.com/document/498293792/Chem-7-Service-Manual | Scribd | Chem-7 Service Manual | TB | Service manual có thể có phần cổng giao tiếp/định dạng. |
| 2 | http://5.imimg.com/data5/WJ/CE/BW/SELLER-92061780/erbachem7-biochemistry-analyzer.pdf | IndiaMART (imimg) | Biochemistry Analyzer CHEM-7 Operator Manual | TB | PDF tải thẳng. |
| 3 | https://vimda.moh.gov.vn/documents/10182/51368141/H%C6%AF%E1%BB%9ANG%20D%E1%BA%AAN%20S%E1%BB%AC%20D%E1%BB%A4NG%20CHEM% | Cổng TTB y tế Bộ Y tế | HDSD Chem 7 tiếng Việt (Erba Diagnostics Mannheim) | Thấp | Link trong kết quả tìm kiếm bị cắt. Nếu không mở được thì vào vimda.moh.gov.vn tìm "CHEM 7". |
| 4 | https://product.erbamannheim.com/upload/docs/1606434903-Chem%207.pdf | Erba Mannheim | Brochure CHEM-7 (cổng USB-B nối host) | Thấp | Chỉ để biết máy giao tiếp qua USB. |

**Chưa thấy protocol LIS của Chem 7, xin nhà phân phối: "ERBA CHEM-7 LIS/Host Communication Protocol".**

## 11. CON500 — chưa xác định hãng

Tìm "CON500", "CON-500" kèm hematology, đông máu và tiếng Việt đều không ra máy xét nghiệm nào. Theo bộ mã trong LIS cũ (WBC, LY_/MO_/GR_, RDW_SD, P-LCR…), đây là máy huyết học 3 thành phần. Kiểu đặt tên GR (granulocyte) hay gặp ở Boule/Swelab/Medonic, Diatron Abacus 3 và Erba Elite 3, nhưng chưa có căn cứ để chọn.

**Không tìm thấy. Chụp tem máy (hãng, model, số serial), rồi xin nhà phân phối "Host/LIS Communication Protocol" của đúng model đó.**

## 12. ST200 / ST200 Pro — máy điện giải SensaCore (Ấn Độ)

Rất có thể là máy điện giải SensaCore ST-200 Pro: hãng có dòng ST-200 Plus/Pro/CC, khớp bộ mã Na/K/Cl/iCa.

| # | Link | Trang web | Tài liệu | Khả năng đúng | Ghi chú |
|---|---|---|---|---|---|
| 1 | https://www.scribd.com/document/445420305/st-200-pro | Scribd | "ST-200 Pro" (132 tr., có vẻ là HDSD đầy đủ) | TB | HDSD đầy đủ thường có mục RS-232/Host output. |
| 2 | https://www.sensacore.com/product/electrolyte-analyzer-plus | SensaCore | Trang ST-200 Plus, có mục "Download Supporting Documents" | TB | Xem có tệp tải được không. |
| 3 | https://www.sensacore.com/product/blood-gas-analyzer-abge | SensaCore | Trang ST 200 CC | Thấp | Để xác nhận đúng hãng. |

Nếu HDSD không có định dạng truyền: **xin nhà phân phối "SensaCore ST-200 Pro LIS/RS-232 Output Format"**. Trước đó nên xem tem máy để chắc là SensaCore.

---

## Tổng hợp việc xin nhà phân phối

| Máy | Tài liệu cần xin |
|---|---|
| Tosoh HLC-723 G11 | HLC-723G11 Host Computer Connection Specification Manual (nếu G8 Rev5 không khớp) |
| Tosoh AIA-360 | Host Computer Connection Specifications for the AIA-360 System (nếu không tải được từ trang Tosoh) |
| Dirui HC-300 | DIRUI HC-300 Communication Protocol (bảng mã, byte đệm) |
| CYBOW R-50S | CYBOW R-50S PC Interface / RS-232C Communication Protocol (DFI) |
| Erba Chem 7 | ERBA CHEM-7 LIS/Host Communication Protocol |
| EBA200, CON500 | Xem tem máy trước, rồi xin Host/LIS protocol của đúng model |
| ST200/ST200 Pro | SensaCore ST-200 Pro LIS/RS-232 output format (nếu HDSD không có) |
