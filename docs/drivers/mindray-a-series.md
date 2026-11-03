# Mindray A-series (A3 / A5 / A7…) — **máy gây mê**, không phải máy xét nghiệm — KHÔNG THUỘC PHẠM VI LIS

> **Không thuộc phạm vi LIS.** File giữ lại chỉ để ghi nhận ghi chú cũ "huyết học Mindray A-series" là sai; không đưa vào danh sách driver.
>
> Trạng thái tài liệu: ❌ không áp dụng cho LIS — tài liệu đầy đủ nhưng là của **hệ thống gây mê**, không phải máy huyết học
> Kiểm chứng: chưa đối chiếu máy thật (và không cần cho LIS)
> Driver: không có — họ giao thức: hl7-mllp (HL7 v2.6, hồ sơ IHE PCD DEC)

## 1. Tóm tắt

Tệp `tai-lieu-hang/KHONG-DUNG_Mindray_A-series_may-gay-me_HL7.pdf` (tên cũ `Mindray_A-series_Communication_Protocol_Interface_Guide.pdf`) được ghi chú (README) là "huyết học Mindray A-series" — **sai**. Trang bìa và mục 1.1 ghi rõ: *A Series Communication Protocol Interface Guide — **Anesthesia System***, dành cho máy gây mê Mindray A Series có gói phần mềm 02.02.00 trở lên (Mindray DS USA, 2013). Máy gửi định kỳ (mặc định 10 giây/lần) số đo thông khí, khí mê, cài đặt máy thở theo HL7 v2.6 hồ sơ IHE PCD-01 (bản tin `ORU^R01`). Một chiều, không có khái niệm mẫu/barcode/chỉ định xét nghiệm.

BVĐK 10 không có máy nào thuộc dòng này trong danh sách máy xét nghiệm (20 máy, `Instrument.json`). **Không viết driver LIS từ tài liệu này.** Ghi lại tóm tắt để khỏi ai mất công đọc lại, và vì cách đóng khung MLLP + CRC trên serial có thể gặp lại ở thiết bị Mindray khác.

## 2. Kết nối vật lý

| Mục | Giá trị (theo tài liệu) |
|---|---|
| Cổng | Ethernet 10/100Base-T và RS-232; dùng đồng thời được, mỗi cổng nói với một bên nhận |
| Serial | Mặc định 115200, 8 data, 1 stop, không parity, không điều khiển luồng; tối thiểu 57600; data bits luôn 8 |
| TCP | **Máy là client**: tự nối tới IP/cổng server cài trên máy, đứt thì tự nối lại |
| Đồng bộ giờ | SNTP (UDP 123), máy là Time Client — chỉ trên Ethernet |
| Cài đặt | Menu cấu hình mạng/serial của máy gây mê; Serial Protocol = HL7 (MR-Link) / MR-WATO (nội bộ Mindray) / None |

## 3. Khung truyền và bắt tay

| Đường | Khung |
|---|---|
| Ethernet | MLLP: `<SB 0x0B>` + bản tin HL7 + `<EB 0x1C>` + `<CR 0x0D>` |
| Serial | `<SB>` + bản tin HL7 (rút gọn) + **CRC 16 bit dạng 4 ký tự hex ASCII** + `<EB>` + `<CR>`; CRC tính trên phần bản tin, không gồm SB/EB/CR, theo thuật toán FCS-16 của HDLC (IETF RFC 1171; tài liệu có bảng tra 256 phần tử ở phụ lục A) |

ACK: trên mạng bên nhận có thể trả `ACK^R01` nhưng máy **vẫn gửi theo chu kỳ dù không nhận ACK**; trên serial không chờ ACK.

## 4. Bản tin kết quả (máy → LIS)

`ORU^R01` gồm MSH, PID (mã BN, họ tên, ngày sinh, cân nặng nhập trên máy gây mê), PV1 (giường/phòng), OBR, nhiều OBX theo cây đo lường ISO/IEEE 11073-10101 (mã "99MNDRY" riêng nếu chưa có mã chuẩn). Không có mã mẫu xét nghiệm, không có QC.

## 5. Hỏi chỉ định / gửi worklist

Không áp dụng — chỉ một chiều.

## 6. Bảng mã chỉ số

Không áp dụng cho LIS (mã đo thông khí/khí mê ở phụ lục B của tài liệu).

## 7. Bản tin mẫu

Không đưa vào — không dùng cho LIS. Tài liệu có ví dụ ở mục 4.1.3.

## 8. Bẫy và lưu ý

1. **Sửa ghi chú trong `tai-lieu-may/README.md`** của kho tham khảo: dòng "huyết học Mindray A-series" là nhầm.
2. Nếu cần máy **huyết học** Mindray: BVĐK 10 có **BC-3200** (xem `mindray-bc3200.md`); các dòng BC-5xxx/BC-6xxx dùng tài liệu *Mindray PatientDataShare HL7 Programmer's Guide* hoặc tài liệu host interface riêng của dòng đó (file `mindray-bc5380*` do người khác phụ trách).
3. Mẫu "MLLP + CRC hex 4 ký tự trước `<EB>`" trên serial là biến thể không chuẩn — nếu gặp ở thiết bị Mindray khác thì tài liệu này là chỗ tham chiếu thuật toán CRC.

## 9. Kiểm thử

Không áp dụng.

## 10. Nguồn

- `tai-lieu-hang/KHONG-DUNG_Mindray_A-series_may-gay-me_HL7.pdf` — *A Series Communication Protocol Interface Guide, Anesthesia System*, Mindray DS USA, 2013 (mã in MRSZR01N01.05.01(2.0)-01). Mục 1.1 (phạm vi: anesthesia systems, phần mềm ≥ 02.02.00), 2.1.1 (khung serial/Ethernet), 3.1 (cài đặt), 4.1 (DEC/PCD-01), 4.1.4 (máy là client), phụ lục A (CRC). Nguồn tải theo README: mindray.sy.
- Tài liệu cần xin: không có (không thuộc phạm vi LIS). Nếu mục đích ban đầu là máy huyết học Mindray thì cần xin **tài liệu host interface của đúng model huyết học** (vd BC-3200) — xem `cac-may-astm-bvdk10.md`.
