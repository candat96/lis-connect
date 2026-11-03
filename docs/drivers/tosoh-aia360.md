# Tosoh AIA-360 — miễn dịch (enzyme miễn dịch huỳnh quang)

> Trạng thái tài liệu: ❌ chưa có đặc tả giao thức của hãng — chỉ có bố cục vị trí trường do LIS cũ ghi lại
> Kiểm chứng: chưa đối chiếu máy thật (LIS cũ nhận được 6 kết quả với bố cục này)
> Driver: `tosoh-aia360` — họ giao thức: fixed-offset (LIS cũ: protocol `AIA360`)

## 1. Tóm tắt

Máy miễn dịch để bàn, đo marker ung thư, nội tiết, tim mạch (TSH, FT4, AFP, CEA, PSA, Troponin I, BNP…). Ở BVĐK 10 nối RS-232 **COM1 trạm DK10.3, 9600 8N1** (InsID 12), 20 dòng map / 19 mã. LIS cũ dùng driver riêng `AIA360` đọc theo **vị trí byte cố định** trong một dòng. Khung truyền (ký tự đầu/cuối, checksum, ACK) **không được ghi lại ở đâu** — đây là điểm khó nhất. **Chỉ một chiều**: theo thông tin đặc tính máy thu thập được, AIA-360 chỉ **xuất** kết quả qua RS-232C, không nhận chỉ định; máy lưu tối đa **800 kết quả** trong bộ nhớ (gửi lại thủ công được *(suy luận)*).

## 2. Kết nối vật lý

| Mục | Giá trị | Nguồn |
|---|---|---|
| Cổng | RS-232, COM1 trạm DK10.3 | `InterfacePara` (HostID DK10.3, InstrumentID 12) |
| Tham số | 9600, 8, N, 1 | như trên |
| Chiều | Một chiều (máy → LIS), RS-232C | thông tin đặc tính máy |
| Bộ nhớ | 800 kết quả | như trên |
| Bắt tay phần cứng | Tài liệu không nêu — cần đo trên máy thật | |
| TCP | Không dùng ở BVĐK 10 | |
| Cài đặt trên máy | Tài liệu không nêu (tên menu) | |

## 3. Khung truyền và bắt tay

Tài liệu không nêu — cần đo trên máy thật.

Driver `tosoh-aia360` hiện khai "kiểu dòng" *(phỏng đoán của người viết agent)*: không STX/ETX, không checksum, không ACK, cắt bản ghi theo khoảng lặng 300 ms giữa các byte, độ dài tối đa 16 384, timeout frame 30 s. Nếu máy thật dùng STX/ETX hoặc chờ ACK thì phải sửa — xem mục 8.

## 4. Bản tin kết quả (máy → LIS)

Bố cục theo `Instrument.json` InsID 12 (cột `*Index` / `*Length`). Vị trí tính **từ 0** *(suy luận — cùng quy ước với AU640 có `TestCodeIndex = 0`)*, tính từ byte đầu của dòng mà LIS cũ nhận được.

| Vị trí | Dài | Trường | Ghi chú |
|---|---|---|---|
| 10 | 17 | **Sample ID** | rộng 17 — đủ cho SID 9 chữ số; phải trim khoảng trắng |
| 38 | 6 | **Mã xét nghiệm** | dạng `#TSH`, `#AFP`, `#19-9` — có dấu `#` đầu, đệm khoảng trắng *(suy luận từ bảng mã)* |
| 52 | 5 | **Giá trị** | chỉ 5 ký tự — xem mục 8 |
| 63 | 11 | **Đơn vị** | |
| — | — | Thời gian đo | LIS cũ không khai (`TestTimeIndex = 0`, dài 0) |
| — | — | Cờ, mẫu QC/chuẩn | LIS cũ không khai — tài liệu không nêu |

Độ dài dòng tối thiểu để đủ các trường: 74 byte. Các byte 0–9, 27–37, 44–51, 57–62 và sau 74 chưa rõ ý nghĩa (có thể là loại bản ghi, số thứ tự, ngày giờ, cờ) — cần bản ghi thô.

## 5. Hỏi chỉ định / gửi worklist

Chỉ một chiều — máy chỉ xuất kết quả qua RS-232C, không nhận worklist. LIS cũ cũng chỉ nhận kết quả.

## 6. Bảng mã chỉ số

InsID 12, 20 dòng map, 19 mã khác nhau (mã đúng như LIS cũ lưu, đã bỏ khoảng trắng):

| Mã máy | Chỉ số | Đơn vị (LIS) | Ghi chú |
|---|---|---|---|
| `#TSH` | TSH | µUI/mL | |
| `#FT4` | Free T4 | ng/dL | |
| `#iFT3` | Free T3 | pg/mL | chữ `i` thường |
| `#AFP` | AFP định lượng | ng/mL | |
| `#CEA` | CEA | ng/mL | |
| `#125` | CA 125 | U/mL | |
| `#15-3` | CA 15-3 | U/mL | |
| `#19-9` | CA 19-9 | UI/mL | |
| `#PSA2` | PSA toàn phần | ng/mL | |
| `#FER` | Ferritin | ng/mL | |
| `#BCG2` | Beta-hCG | mIU/mL | |
| `#FSH` | FSH | mIU/mL | |
| `#LH2` | LH | mIU/mL | |
| `#TES` | Testosterone | ng/dL | |
| `#CORT` | Cortisol sáng **và** Cortisol chiều | µg/dL | **trùng mã**: 2 dịch vụ cùng một mã máy |
| `#sAg2` | HBsAg | COI | |
| `#BsAb` | Anti-HBs | IU/L | |
| `#TnI3` | Troponin I | ng/mL | |
| `BNP` | BNP | pg/mL | **không có `#`** — khác các mã còn lại, cần kiểm trên máy |

## 7. Bản tin mẫu

Chưa có bản ghi thô. Dòng dưới **chỉ minh họa vị trí trường** (dữ liệu giả, các byte chưa rõ nghĩa để `.`), không phải bản tin thật:

```
0         1         2         3         4         5         6         7
01234567890123456789012345678901234567890123456789012345678901234567890123
..........260915001␣␣␣␣␣␣␣␣...........#TSH␣␣........1.23␣......uIU/mL␣␣␣␣␣
```

(`␣` = khoảng trắng. SID `260915001` tại 10–26, mã `#TSH` tại 38–43, giá trị `1.23` tại 52–56, đơn vị tại 63–73. Đơn vị viết `uIU/mL` cho đúng 1 byte/ký tự; máy thật có thể gửi `µ`.)

## 8. Bẫy và lưu ý khi viết driver

1. **Khung truyền chưa biết.** Nếu dòng thật có `<STX>` ở đầu mà LIS cũ tính vị trí sau khi bỏ STX (hoặc ngược lại) thì mọi vị trí lệch 1 byte. Kiểm bằng bản ghi thô trước khi bật.
2. **Giá trị chỉ 5 ký tự** — kết quả kiểu `>1000.0`, `<0.005`, `12345.6` sẽ bị cắt. Có thể hệ cũ đã cắt sai mà không ai biết.
3. **Mã trùng `#CORT`** → 2 dịch vụ (sáng/chiều). LIS cũ gỡ bằng "BN có chỉ định nào thì lấy cái đó, hòa thì theo alphabet" — LIS mới nên từ chối đoán khi BN có cả hai hoặc không có cái nào, và cảnh báo.
4. Mã có `#` và có thể có khoảng trắng đệm — trim nhưng giữ `#` khi so khớp (BNP lại không có `#`).
5. Ký tự `µ` trong đơn vị không phải ASCII — mã hóa phải đo trên máy.
6. Chỉ 6 kết quả trong lịch sử LIS cũ → bố cục mới được dùng rất ít, độ tin thấp.
7. Có một repo mã nguồn mở `AIA_360` (https://github.com/hmislk/AIA_360) — **độ tin thấp** (không rõ đã chạy với máy thật); chỉ dùng để gợi ý, không dùng làm chuẩn bố cục.

## 9. Kiểm thử

- Bản ghi thô (đã khử định danh) cho: một mẫu nhiều xét nghiệm, một kết quả `<`/`>`, một mẫu QC, một mẫu lỗi; đo khoảng lặng giữa các dòng để chỉnh `interByteTimeoutMs`.
- Mô phỏng: phát lại byte thô qua cặp COM ảo.
- Tình huống: hai mẫu gửi liền nhau không khoảng lặng; dòng ngắn hơn 74 byte; mã chưa map.

## 10. Nguồn

- `lis-agent/src/drivers.ts` — driver `tosoh-aia360` (bố cục và khung "kiểu dòng").
- `khao-sat/seed/du-lieu/Instrument.json` InsID 12 (`TestCodeIndex 38/6`, `SIDIndex 10/17`, `ValueIndex 52/5`, `UnitIndex 63/11`); `InterfacePara.json`; `khao-sat/lis-hien-huu.md` mục 3, 9.2; `khao-sat/seed/anh-xa-ma-may.json`.
- Thông tin đặc tính máy: AIA-360 chỉ xuất một chiều RS-232C, lưu 800 kết quả (thu thập từ tài liệu giới thiệu máy, chưa có tài liệu gốc).
- Mã nguồn mở https://github.com/hmislk/AIA_360 — độ tin thấp.
- **Cần xin hãng Tosoh / nhà phân phối:** tài liệu đặc tả giao tiếp máy chủ (host interface / host communication specification) của **AIA-360** đúng phiên bản phần mềm đang chạy — gồm tham số cổng, khung truyền (STX/ETX, checksum, ACK), bố cục bản ghi kết quả, có hỗ trợ nhận chỉ định hay không. Tên tài liệu chính xác cần hỏi hãng.
