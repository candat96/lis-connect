# E. Tiếp nhận tối giản trên web — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Từ web tạo được người bệnh + chỉ định + mẫu có barcode, rồi chạy trọn vòng: bộ giả lập gửi kết quả qua agent → kết quả hiện ở màn Kết quả → duyệt được.

**Architecture:**
- Server thêm `/api/reception`, dùng lại nguyên `OrderService` / `PatientStore` của `Lis.Data` (cùng quy tắc mã người bệnh và barcode với `Lis.App`).
- Phòng khám tạo từ web chưa có dòng `facility`, nên khi tạo phòng khám ghi kèm dòng mặc định.
- Web thêm màn "Tiếp nhận": nhập bằng bàn phím, chọn dịch vụ bằng mã + Enter.

**Tech Stack:** .NET 10 minimal API, Dapper; React + antd + TanStack Query; xunit v3 + Testcontainers; vitest.

**Spec:** `docs/superpowers/specs/2026-11-16-mot-server-agent-dieu-khien-tu-web-design.md` mục 5.E.

## Global Constraints

- Không in tem, không thu phí, không nhập Excel.
- Vai trò được tiếp nhận: `tiep-nhan`, `quan-tri`. Vai trò khác: 403 `"Tài khoản không có quyền tiếp nhận"`.
- Mọi chỉ định đi qua `OrderService.CreateAsync`. Không viết đường sinh barcode hay mã người bệnh nào khác.
- Bàn phím:
  - Enter chuyển ô.
  - Ô dịch vụ: gõ mã + Enter để thêm.
  - Ctrl+Enter (hoặc F9) lưu.
  - Sau khi lưu, con trỏ về ô Họ tên cho người kế tiếp.
- Văn bản tiếng Việt có dấu. Dữ liệu test là dữ liệu giả, tên chứa "Thử" hoặc "Mẫu".
- `dotnet build LisConnect.slnx` 0 warning. Web: `npx vitest run`, `npm run typecheck`, `npm run build` xanh.
- Giao diện do Claude tự làm và kiểm bằng ảnh chụp thật (memory `ui-claude-does-it`).
- Commit cục bộ, không push. Mỗi commit kết thúc bằng đúng một dòng `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. Hai người tiếp nhận cùng lúc không được trùng barcode hay mã người bệnh. `OrderService` đã khoá bằng `barcode_counter`; test API song song 10 yêu cầu.
2. Dịch vụ ngừng dùng, hoặc mã không có: báo ngay tại ô mã, không lưu được.
3. Panel được tách thành chỉ số con, và mẫu được gom theo loại bệnh phẩm. Màn hình hiện đủ mọi barcode sinh ra, không chỉ cái đầu.
4. Người bệnh đã có: chọn từ gợi ý, không tạo bản trùng.
5. Phòng khám có sẵn từ trước (đã có `facility` do `Lis.App` cài) không bị ghi đè cấu hình barcode.

---

### Task E1: Server — `/api/reception` và dòng `facility` mặc định

**Files:**
- Create: `src/Lis.Server/Api/ReceptionEndpoints.cs`.
- Modify:
  - `src/Lis.Server/Api/ApiSession.cs`: thêm `RequireReception()`.
  - `src/Lis.Server/Tenancy/TenantRegistry.cs`: `TenantServices` thêm `Orders` (`OrderService`, dùng `clinicClock`).
  - Chỗ map route `/api`, nơi gọi `MapRawMessagesApi`.
  - Đường tạo phòng khám (`ClinicProvisioner` / `SetupEndpoints` / `AdminCommands.AddClinicAsync`): sau migrate, gọi `INSERT INTO facility (id, name) VALUES (1, @clinicName) ON CONFLICT (id) DO NOTHING`.
- Test: `tests/Lis.Server.Tests/ReceptionApiTests.cs`.

**Interfaces:**
- `GET /api/reception/patients?q=` trả `[{id, code, fullName, dob, birthYear, sex, phone}]` (`PatientStore.FindAsync`). Bỏ khoảng trắng hai đầu `q`; `q` dưới 2 ký tự thì trả `[]`.
- `POST /api/reception/orders`
  - Thân: `{patientId?, patient?: {fullName, dob?, birthYear?, sex: "M"|"F"|"O", phone?, address?}, serviceIds: [], orderingDoctor?, orderingDept?, diagnosis?}`.
  - Trả 201 `{orderId, patientId, patientCode, samples: [{sampleId, barcode, specimenName, serviceNames}]}`.
  - Kiểm dữ liệu, mọi lỗi trả 400 kèm câu tiếng Việt:
    - có đúng một trong `patientId` / `patient`;
    - `fullName` không rỗng, tối đa 200 ký tự;
    - có `dob` hoặc `birthYear`;
    - `birthYear` từ 1900 tới năm hiện tại;
    - `serviceIds` có từ 1 tới 50 phần tử, mọi id tồn tại và đang dùng.
  - `InvalidOperationException` của `OrderService` → 400 kèm message.
- Dịch vụ cho ô mã: dùng `GET /api/catalog/services` có sẵn. Web lọc `active` và `code`.

- [ ] **Step 1: Test đỏ**
  - `Tiep_nhan_nguoi_benh_moi_sinh_ma_va_barcode`: hai dịch vụ khác loại mẫu → 2 barcode khác nhau; chế độ mặc định `per-specimen`.
  - `Tiep_nhan_panel_tach_chi_so_con`: chi tiết mẫu `/api/samples/{id}` có đủ chỉ số con.
  - `Tiep_nhan_nguoi_benh_co_san_khong_tao_moi`.
  - `Muoi_yeu_cau_song_song_khong_trung_barcode`.
  - `Ky_thuat_vien_bi_403`, `Tiep_nhan_va_quan_tri_duoc`.
  - `Dich_vu_ngung_dung_hoac_khong_co_bao_400`.
  - `Phong_kham_moi_tao_tu_web_co_facility_mac_dinh` và `Facility_san_co_khong_bi_ghi_de`.
- [ ] **Step 2: Chạy, xác nhận đỏ.**
- [ ] **Step 3: Làm.**
- [ ] **Step 4: Xanh.** Chạy đủ `dotnet test tests/Lis.Server.Tests`.
- [ ] **Step 5: Commit** `feat(server): tiếp nhận tối giản — tìm người bệnh, tạo chỉ định và mẫu có barcode`.

### Task E2: Web — màn "Tiếp nhận" (Claude tự làm)

**Files:**
- Create: `web/src/modules/tiep-nhan/ReceptionPage.tsx`, `reception.ts` (+ `.test.ts`), `ReceptionPage.test.tsx`.
- Modify:
  - `web/src/services/index.ts`: `receptionService.findPatients/createOrder`, kiểu dữ liệu.
  - `App.tsx` + `MainLayout.tsx`: mục "Tiếp nhận" đứng đầu menu. Với vai trò `tiep-nhan`, sau đăng nhập vào thẳng `/tiep-nhan`.
  - `stores/sessionStore.ts`: `useCanReceive`.

**Bố cục:**
- Trái: thẻ "Người bệnh".
  - Ô tìm người bệnh có sẵn (AutoComplete, gõ ≥ 2 ký tự). Chọn một người thì khoá form người bệnh, có nút "Người bệnh mới".
  - Các ô: Họ tên, Giới (phím tắt N = Nam, U = Nữ, K = Khác), Ngày sinh **hoặc** Năm sinh (gõ 4 số thì hiểu là năm), Số điện thoại, Bác sĩ chỉ định, Chẩn đoán.
- Phải: thẻ "Dịch vụ".
  - Ô "Mã dịch vụ" + Enter để thêm. Mã không có thì báo đỏ ngay dưới ô và chọn lại chữ.
  - Bảng các dịch vụ đã chọn: mã, tên, loại mẫu, nút xoá. Delete xoá dòng đang chọn.
- Dưới cùng:
  - Nút "Lưu tiếp nhận (Ctrl+Enter)".
  - Sau khi lưu: khung kết quả nổi bật, mỗi barcode chữ to đơn cách, kèm loại mẫu và tên dịch vụ, có nút "Chép" từng barcode. Form xoá trắng, con trỏ về Họ tên.
- Ghi chú nhỏ: "Bản web chưa in tem — chép barcode để dán vào máy hoặc bộ giả lập."

- [ ] **Step 1: Test đỏ**
  - `reception.test.ts`:
    - `parseBirth('1985')` → `{birthYear: 1985}`;
    - `parseBirth('12/03/1985')` → `{dob: '1985-03-12'}`;
    - ngày không hợp lệ → `null`;
    - `findServiceByCode` không phân biệt hoa thường, bỏ dịch vụ ngừng dùng.
  - `ReceptionPage.test.tsx`:
    - gõ mã + Enter thì thêm dịch vụ;
    - mã sai thì hiện lỗi;
    - Ctrl+Enter gọi `createOrder` với đúng thân;
    - hiện đủ mọi barcode trả về;
    - `ky-thuat-vien` không thấy mục menu, vào thẳng đường dẫn thì báo không có quyền.
- [ ] **Step 2: Làm.**
- [ ] **Step 3: Xanh.** Chạy `npx vitest run`, `npm run typecheck`, `npm run build`.
- [ ] **Step 4: Kiểm trọn vòng bằng ảnh chụp thật.**
  - Server dev + agent console + `Lis.Simulator send --family hl7-mllp --connect …` với barcode vừa tạo.
  - Chụp: màn tiếp nhận, kết quả tiếp nhận (barcode), màn Kết quả có giá trị máy gửi, sau khi duyệt.
- [ ] **Step 5: Commit** `feat(web): màn tiếp nhận tối giản — nhập người bệnh, chọn dịch vụ bằng mã, hiện barcode`.

### Task E3: Tài liệu và checklist chạy thử trọn vòng

- Thêm mục "Chạy thử trọn vòng" vào `docs/HUONG-DAN/` (tạo mẫu → máy/giả lập gửi → xem → duyệt) và một mục vào checklist Windows của D.
- Commit `docs: chạy thử trọn vòng từ tiếp nhận tới duyệt`.
