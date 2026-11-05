# LIS Connect — Đợt 1: Nền tảng và LIS dùng độc lập — Kế hoạch triển khai

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dựng phần mềm LIS cho Windows chạy được ở chế độ không có HIS. Gồm:
- cài đặt lần đầu (kết nối CSDL, tài khoản quản trị đầu tiên);
- đăng nhập và phân vai trò;
- danh mục dịch vụ (cha–con, khoảng tham chiếu), máy và map kênh;
- nhập chỉ định, cấp barcode theo loại mẫu, in tem;
- nhập/duyệt/in kết quả;
- hàm nhận kết quả từ máy (`IngestService`) để đợt 2 gọi, kèm hàng "Chưa ghép".

**Architecture:**
- **Lõi nghiệp vụ thuần** (`Lis.Core`) không có IO và được test kỹ. Gồm: định dạng giá trị, chọn khoảng tham chiếu, tính cờ, ghép barcode, gom mẫu, phân quyền.
- **Tầng dữ liệu** (`Lis.Data`) dùng PostgreSQL qua Npgsql + Dapper, migration SQL nhúng chạy bằng DbUp. Các service nghiệp vụ dùng CSDL nằm ở đây, test bằng PostgreSQL thật qua Testcontainers.
- **Tầng in** (`Lis.Reports`) sinh PDF cho tem và phiếu kết quả.
- **Giao diện** (`Lis.App`) dùng Avalonia + MVVM (CommunityToolkit.Mvvm), gọi thẳng các service.
- Dịch vụ nền, driver máy và tích hợp HIS thuộc các đợt sau.

**Tech Stack:**
- Nền tảng: .NET 10 (SDK 10.0.403), C# 14.
- Giao diện: Avalonia 12.1.x, CommunityToolkit.Mvvm 8.4.x.
- CSDL: Npgsql 10.0.x, Dapper 2.1.x, dbup-postgresql 7.0.x.
- Mật khẩu: Konscious.Security.Cryptography.Argon2 1.3.x.
- In: PDFsharp-MigraDoc 6.x (giấy phép MIT), ZXing.Net 0.16.x để vẽ Code 128, PDFtoImage để in trên Windows.
- Nhập Excel: ClosedXML 0.105.x.
- Test: xUnit v3, Shouldly 4.3.x, Testcontainers.PostgreSql 4.16.x, Avalonia.Headless.XUnit.

**Spec:** [`docs/superpowers/specs/2026-11-03-lis-windows-design.md`](../specs/2026-11-03-lis-windows-design.md). Tài liệu máy: [`docs/drivers/README.md`](../../drivers/README.md).

**Lộ trình các đợt** (mỗi đợt có kế hoạch riêng, viết khi đợt trước xong):

| Đợt | Nội dung | Kết thúc khi |
|---|---|---|
| **1 (kế hoạch này)** | Nền tảng + LIS dùng độc lập | Nhập chỉ định → in tem → nhập kết quả tay → duyệt → in phiếu chạy được. `IngestService` ghép đúng kết quả giả lập |
| 2 | Dịch vụ nền Windows + khung driver + log dữ liệu thô + bộ giả lập máy + 3 driver ưu tiên (BC-5380, AU480, Access 2) + bản sao chỉ định cục bộ | Bộ giả lập phát bản tin, kết quả hiện trên màn duyệt, log thô xem được |
| 3 | Tích hợp HIS cấu hình được + API phía HIS-PROMAX (`his-be`) + khóa duyệt bên HIS | Chỉ định từ HIS-PROMAX chạy hết vòng và kết quả đã duyệt về lại HIS |
| 4 | Các driver ✅ còn lại | Mỗi driver qua bộ test bản tin mẫu |
| 5 | Bộ cài `.msi`, kèm PostgreSQL tùy chọn, cập nhật | Cài sạch trên Windows 10/11 |

## Global Constraints

- Thư mục mã: `src/` và `tests/` ở gốc repo `lis-connect`. Solution: `LisConnect.slnx`. Mọi project dùng `net10.0`, riêng `Lis.App` dùng `net10.0` + Avalonia (chạy cả Windows lẫn macOS để phát triển).
- `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<ImplicitUsings>enable</ImplicitUsings>`, khai một lần trong `Directory.Build.props`. Phiên bản gói khai một lần trong `Directory.Packages.props` (Central Package Management).
- Chạy dotnet trên máy phát triển: `export PATH=$HOME/.dotnet:$PATH` (SDK cài ở `~/.dotnet`).
- Toàn bộ chữ trên giao diện là **tiếng Việt có dấu**. Comment code viết tiếng Việt, tên lớp/biến viết tiếng Anh.
- Thời gian lưu kiểu `timestamptz` (UTC), hiển thị theo giờ máy. Ngày sinh là `date`, hoặc chỉ năm sinh `int`.
- Giá trị kết quả lưu **chuỗi**, không lưu số thực. So ngưỡng dùng `decimal`.
- Barcode **chỉ gồm chữ số**, **duy nhất** trong CSDL, **so khớp chính xác** sau khi bỏ khoảng trắng hai đầu. Không bao giờ khớp theo đuôi số.
- Một `(instrument_id, channel_code)` chỉ trỏ tới **một** chỉ số (ràng buộc UNIQUE trong CSDL).
- Mật khẩu: tối thiểu **8** ký tự, băm **Argon2id**. Sai **5** lần liên tiếp thì khóa **15 phút**.
- Vai trò (mã cố định): `quan-tri`, `tiep-nhan`, `ky-thuat-vien`, `truong-khoa`.
- Barcode mặc định: tiền tố `9` + `yyMMdd` + số thứ tự trong ngày 3 chữ số (vd `9261103001`, 10 chữ số). Tiền tố, mẫu ngày và số chữ số thứ tự đều cấu hình được. Mặc định mỗi loại mẫu một barcode. Tùy chọn `single` cho cả phiếu một barcode.
- Kết quả chỉ in bản chính thức và trả ra ngoài khi đã **duyệt**. Bản in khi chưa duyệt có dòng chữ "CHƯA DUYỆT".
- Sửa tay kết quả bắt buộc có lý do và lưu giá trị cũ. Hủy duyệt bắt buộc có lý do.
- Dữ liệu demo và test chỉ dùng dữ liệu giả: họ tên chứa "Mẫu" hoặc "Thử", SĐT `09000000xx`. Không đưa dữ liệu người bệnh thật vào repo.
- Commit nhỏ theo từng task, dạng `feat(core): …`, `test: …`. Cuối message thêm các dòng attribution theo quy định phiên.

## Review Focus

Năm tình huống thực tế dễ làm hỏng phần mềm mà spec không nói thẳng. Mỗi dòng có test ở task sở hữu đoạn mã liên quan.

1. **Máy gửi barcode có khoảng trắng hoặc ký tự điều khiển thừa** (`" 9261103001\r"`) → vẫn khớp đúng mẫu. Máy gửi barcode chỉ trùng đuôi với một mẫu có thật (`261103001`) → **không** khớp, đưa vào Chưa ghép. *(Task 11: `Ingest_TrimsBarcode_ButNeverSuffixMatches`)*
2. **Giá trị kết quả có dấu phẩy thập phân, dấu so sánh hoặc ký hiệu lỗi của máy** (`"4,8"`, `">500"`, `"***.*"`, `""`). Lần lượt hiểu thành `4.8`, giá trị kèm dấu `>`, và **không có giá trị** (không phải 0). *(Task 4: `Parse_*`)*
3. **Người bệnh chỉ có năm sinh.** Tuổi tính từ ngày 01/07 của năm đó. Khoảng tham chiếu theo tuổi vẫn chọn đúng. *(Task 4: `Select_BirthYearOnly_UsesJulyFirst`)*
4. **Máy gửi lại kết quả cho mẫu đã duyệt.** Không ghi đè. Kết quả mới thành "chờ thay thế". Mẫu vẫn giữ trạng thái đã duyệt. *(Task 11: `Ingest_ApprovedResult_BecomesPendingReplacement`)*
5. **Hai máy trạm cùng tạo chỉ định trong một giây.** Không trùng barcode, không lỗi ra người dùng. *(Task 8: `CreateOrder_Concurrent_ProducesUniqueBarcodes`)*

---

### Task 1: Khung solution, build và chạy test

**Files:**
- Create: `LisConnect.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json` (SDK `10.0.403`, `rollForward: latestFeature`), `.editorconfig`
- Create: `src/Lis.Core/Lis.Core.csproj`, `src/Lis.Data/Lis.Data.csproj`, `src/Lis.Reports/Lis.Reports.csproj`, `src/Lis.App/Lis.App.csproj`
- Create: `tests/Lis.Core.Tests/…csproj`, `tests/Lis.Data.Tests/…csproj`, `tests/Lis.Reports.Tests/…csproj`, `tests/Lis.App.Tests/…csproj`
- Create: `README.md` (thay README cũ: mục đích, cách build/test, trỏ tới spec, kế hoạch và tài liệu driver)
- Modify: `.gitignore` (thêm `bin/`, `obj/`, `*.user`, `.vs/`, `TestResults/`)
- Create: `.github/workflows/build.yml` (chạy `dotnet build` + `dotnet test` trên `windows-latest` và `ubuntu-latest`. Test `Lis.Data.Tests` cần Docker nên chỉ chạy trên ubuntu)

**Interfaces:**
- Produces: phụ thuộc giữa các project là `Lis.Data → Lis.Core`, `Lis.Reports → Lis.Core`, `Lis.App → Lis.Core, Lis.Data, Lis.Reports`. Không được có chiều ngược lại.

- [ ] **Step 1:** Tạo các tệp trên. `tests/Lis.Core.Tests` có một test giữ chỗ `Solution_Builds` để `Assert.True(true)`.
- [ ] **Step 2:** Chạy `dotnet build LisConnect.slnx -warnaserror` rồi `dotnet test tests/Lis.Core.Tests`. Kết quả mong đợi: build thành công, 1 test qua.
- [ ] **Step 3:** Commit `chore: khung solution .NET 10 + CI`.

---

### Task 2: Cấu hình cục bộ và mã hóa bí mật

**Files:**
- Create: `src/Lis.Data/Local/LocalConfig.cs`, `LocalConfigStore.cs`, `ISecretProtector.cs`, `DpapiSecretProtector.cs`, `DevKeySecretProtector.cs`, `SecretProtectors.cs`
- Test: `tests/Lis.Data.Tests/Local/LocalConfigStoreTests.cs`

**Interfaces:**
- Produces:
  - `record DbSettings(string Host, int Port, string Database, string Username, string Password, bool RequireSsl)`. Kèm phương thức `string ToConnectionString()`, trong đó `SSL Mode=Require` khi `RequireSsl`, ngược lại `Prefer`.
  - `record LocalConfig(DbSettings Db, string WorkstationCode)`.
  - `class LocalConfigStore(string directory, ISecretProtector protector)`:
    - `LocalConfig? Load()`;
    - `void Save(LocalConfig cfg)`.
    - Ghi vào `<directory>/config.json`, trường mật khẩu lưu là `passwordProtected` (base64 của bản mã hóa).
  - `interface ISecretProtector { byte[] Protect(byte[] plain); byte[] Unprotect(byte[] cipher); }`.
  - `static class SecretProtectors`:
    - `ISecretProtector ForCurrentOs(string directory)`;
    - Windows trả `DpapiSecretProtector` (phạm vi `LocalMachine`);
    - hệ khác trả `DevKeySecretProtector` (AES-GCM, khóa ngẫu nhiên lưu `<directory>/dev.key`).
  - Thư mục mặc định: `%ProgramData%\LIS Connect` trên Windows, `~/Library/Application Support/LIS Connect` trên macOS. Hằng `LocalPaths.DataDirectory`.

- [ ] **Step 1: Viết test.** `SaveThenLoad_RoundTrips`: lưu rồi đọc ra bằng nhau. `Save_DoesNotWritePlainPassword`: nội dung `config.json` không chứa chuỗi mật khẩu `"Thu@12345"`. `Load_MissingFile_ReturnsNull`.
- [ ] **Step 2:** Chạy `dotnet test tests/Lis.Data.Tests --filter LocalConfigStoreTests`. Mong đợi FAIL vì chưa có lớp.
- [ ] **Step 3:** Cài đặt các lớp trên. `DpapiSecretProtector` gọi `ProtectedData` (gói `System.Security.Cryptography.ProtectedData`) và chặn bằng `OperatingSystem.IsWindows()`.
- [ ] **Step 4:** Chạy lại test. Mong đợi PASS.
- [ ] **Step 5:** Commit `feat(data): cấu hình cục bộ + mã hóa mật khẩu CSDL`.

---

### Task 3: Migration và lược đồ CSDL v1

**Files:**
- Create: `src/Lis.Data/Db.cs`, `src/Lis.Data/Migrator.cs`, `src/Lis.Data/Migrations/0001_khoi_tao.sql` (embedded resource)
- Create: `tests/Lis.Data.Tests/PostgresFixture.cs` (Testcontainers, image `postgres:18-alpine`, mỗi test class một CSDL mới), `tests/Lis.Data.Tests/MigratorTests.cs`

**Interfaces:**
- Produces:
  - `sealed class Db(NpgsqlDataSource source)`:
    - `static Db Create(string connectionString)`;
    - `Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)`;
    - `Task<T> InTransactionAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct = default)`.
    - Dapper đặt `DefaultTypeMap.MatchNamesWithUnderscores = true`.
  - `static class Migrator`:
    - `MigrationResult Upgrade(string connectionString)`, với `record MigrationResult(bool Ok, IReadOnlyList<string> Applied, string? Error)`;
    - `bool IsEmpty(string connectionString)`: true khi chưa có bảng `schemaversions`.
  - `PostgresFixture.NewDatabaseAsync()` trả chuỗi kết nối của một CSDL mới đã migrate.

Lược đồ `0001_khoi_tao.sql` (schema `public`). Mọi khóa chính là `uuid DEFAULT uuidv7()`. Mọi bảng có `created_at timestamptz DEFAULT now()`.

```sql
app_user(id, username citext UNIQUE, full_name, title, password_hash, roles text[], active bool, failed_count int, locked_until timestamptz, last_login_at, his_staff_code)
facility(id int PK CHECK(id=1), name, address, phone, logo bytea, barcode_prefix text DEFAULT '9', barcode_date_pattern text DEFAULT 'yyMMdd', barcode_seq_digits int DEFAULT 3, barcode_mode text DEFAULT 'per-specimen' CHECK IN ('per-specimen','single'))
workstation(id, code UNIQUE, name, last_seen_at)
specimen_type(id, code UNIQUE, name, sort_order)                         -- seed: EDTA, SERUM, CITRAT, NUOC-TIEU, KHAC
service(id, code UNIQUE, name, group_name, specimen_type_id, unit, value_type text CHECK IN ('so','dinh-tinh','chu'), decimals int, print_order int, is_panel bool, auto_approve bool, active bool, his_code)
service_child(parent_id, child_id, sort_order, PRIMARY KEY(parent_id, child_id))
reference_range(id, service_id, sex text NULL CHECK IN ('M','F'), age_from_days int, age_to_days int, instrument_id NULL, low numeric, high numeric, critical_low numeric, critical_high numeric, text_range text)
instrument(id, name, model, protocol_family text, driver_config jsonb, connection jsonb, workstation_code text, active bool)
instrument_channel(id, instrument_id, channel_code text, service_id, decimals int NULL, factor numeric NULL, keep_comparator bool, send_on_query bool, UNIQUE(instrument_id, channel_code))
qualitative_rule(id, instrument_id NULL, service_id, raw_value text, display_value text, flag text NULL, UNIQUE(instrument_id, service_id, raw_value))
patient(id, code UNIQUE, full_name, dob date NULL, birth_year int NULL, sex text CHECK IN ('M','F','O'), address, phone, source text DEFAULT 'lis', his_id text NULL)
lab_order(id, patient_id, source text DEFAULT 'lis', his_order_id text NULL, ordering_doctor, ordering_dept, diagnosis, ordered_at timestamptz, created_by uuid)
sample(id, order_id, barcode text UNIQUE CHECK (barcode ~ '^[0-9]+$'), specimen_type_id, collected_at, received_at, approved_at, approved_by)
barcode_counter(day date, prefix text, last_seq int, PRIMARY KEY(day, prefix))
order_item(id, order_id, sample_id, service_id, parent_item_id NULL)      -- dịch vụ cha và từng chỉ số con đều là một dòng
result(id, order_item_id UNIQUE, value text, unit, ref_low numeric, ref_high numeric, ref_text, flag text, source text CHECK IN ('may','tay'), instrument_id NULL, raw_ref text NULL, status text CHECK IN ('cho-duyet','da-duyet'), pending_value text NULL, pending_instrument_id NULL, pending_raw_ref text NULL, updated_at, updated_by)
result_history(id, result_id, action text CHECK IN ('nhan','sua','duyet','huy-duyet','thay-the'), old_value, new_value, reason, actor_id, at timestamptz DEFAULT now())
unmatched_result(id, instrument_id, barcode, channel_code, value, unit, reason text CHECK IN ('khong-co-barcode','khong-co-chi-dinh','kenh-chua-map'), raw_ref, received_at, resolved_at, resolved_by, resolved_item_id)
qc_result(id, instrument_id, barcode, channel_code, value, unit, raw_ref, received_at)
setting(key text PK, value text)
```

`citext` cần `CREATE EXTENSION IF NOT EXISTS citext`. Seed `specimen_type` ngay trong migration.

- [ ] **Step 1: Viết test.** `Upgrade_EmptyDb_AppliesAllAndIsIdempotent`: chạy hai lần, lần hai `Applied` rỗng. `IsEmpty_TrueBeforeFalseAfter`. `ChannelMap_DuplicateChannel_Rejected`: chèn hai dòng cùng `(instrument_id, channel_code)` thì ném `PostgresException` với `SqlState == "23505"`. `Sample_NonDigitBarcode_Rejected`: chèn `"92611A"` thì vi phạm CHECK.
- [ ] **Step 2:** Chạy `dotnet test tests/Lis.Data.Tests --filter MigratorTests`. Mong đợi FAIL.
- [ ] **Step 3:** Viết `Db`, `Migrator`, `0001_khoi_tao.sql`.
- [ ] **Step 4:** Chạy lại. Mong đợi PASS (cần Docker đang chạy).
- [ ] **Step 5:** Commit `feat(data): lược đồ CSDL v1 + migration`.

---

### Task 4: Lõi kết quả — giá trị, khoảng tham chiếu, cờ, quy đổi định tính

**Files:**
- Create: `src/Lis.Core/Results/ResultValue.cs`, `ValueRounding.cs`, `ReferenceRange.cs`, `ReferenceRangeSelector.cs`, `ResultFlag.cs`, `FlagCalculator.cs`, `QualitativeRule.cs`, `QualitativeMapper.cs`, `src/Lis.Core/Patients/PatientAge.cs`
- Test: `tests/Lis.Core.Tests/Results/*Tests.cs`

**Interfaces:**
- Produces:
  - `sealed record ResultValue(string Raw, decimal? Number, string? Comparator, bool IsEmpty)`:
    - `static ResultValue Parse(string? raw)`.
    - `Comparator` là `"<"`, `">"`, `"<="`, `">="` hoặc null.
    - Hiệu giá `a:b` hoặc `a/b` cho ra `Number = a/b`, `Raw` giữ nguyên.
  - `static class ValueRounding`:
    - `string Round(string raw, int? decimals)`;
    - làm tròn kiểu half-away-from-zero, giữ đúng số chữ số thập phân (`"4.8"` với 2 → `"4.80"`), giữ dấu so sánh, không đụng chuỗi không phải số.
  - `sealed record ReferenceRange(Guid Id, Guid ServiceId, char? Sex, int? AgeFromDays, int? AgeToDays, Guid? InstrumentId, decimal? Low, decimal? High, decimal? CriticalLow, decimal? CriticalHigh, string? TextRange)`.
  - `sealed record PatientFacts(char Sex, DateOnly? Dob, int? BirthYear)`.
  - `static class PatientAge`:
    - `int Days(PatientFacts p, DateOnly at)`;
    - chỉ có năm sinh thì lấy ngày sinh giả định là **01/07** của năm đó;
    - không có cả hai thì trả `-1`.
  - `static class ReferenceRangeSelector`:
    - `ReferenceRange? Select(IEnumerable<ReferenceRange> ranges, PatientFacts p, DateOnly at, Guid? instrumentId)`.
    - Chỉ xét khoảng khớp tuổi và giới (giới null là khoảng chung). Ưu tiên theo thứ tự: đúng máy hơn máy null, rồi có giới hơn chung, rồi khoảng tuổi hẹp hơn.
  - `enum ResultFlag { None, Low, High, CriticalLow, CriticalHigh, Abnormal }`.
  - `static class FlagCalculator`:
    - `ResultFlag Compute(ResultValue v, ReferenceRange? r)`;
    - giá trị rỗng hoặc không có khoảng thì `None`;
    - `">500"` với high 400 thì `High`.
  - `sealed record QualitativeRule(Guid? InstrumentId, Guid ServiceId, string RawValue, string DisplayValue, ResultFlag? Flag)`.
  - `static class QualitativeMapper`:
    - `(string Display, ResultFlag? FlagOverride) Apply(string raw, Guid serviceId, Guid? instrumentId, IEnumerable<QualitativeRule> rules)`;
    - so `RawValue` không phân biệt hoa thường, bỏ khoảng trắng hai đầu;
    - luật đúng máy ưu tiên hơn luật máy null;
    - không có luật thì trả nguyên raw.

- [ ] **Step 1: Viết test.**
  - `Parse_CommaDecimal` (`"4,8"` → `4.8m`).
  - `Parse_Comparator` (`">500"` → `500m`, `">"`).
  - `Parse_Titer` (`"1:160"` → `0.00625m`, Raw `"1:160"`).
  - `Parse_MachineInvalid` (`"***.*"`, `"**"`, `""`, `null` → `IsEmpty`).
  - `Round_KeepsTrailingZeros` (`("4.8",2)` → `"4.80"`, `("0.125",2)` → `"0.13"`, `(">5.55",1)` → `">5.6"`, `("Âm tính",2)` → `"Âm tính"`).
  - `Select_PrefersInstrumentThenSexThenNarrowAge`.
  - `Select_NewbornByDays` (khoảng 0–28 ngày khớp trẻ 10 ngày tuổi).
  - `Select_BirthYearOnly_UsesJulyFirst` (năm sinh 2000, xét ngày 2026-06-30 thì tuổi < 26 năm theo ngày).
  - `Flag_CriticalBeatsHigh`.
  - `Flag_EmptyIsNone`.
  - `Qualitative_InstrumentRuleWins`.
  - `Qualitative_NegativeOverridesFlagToNone`.
- [ ] **Step 2:** Chạy `dotnet test tests/Lis.Core.Tests`. Mong đợi FAIL.
- [ ] **Step 3:** Cài đặt.
- [ ] **Step 4:** Chạy lại. Mong đợi PASS.
- [ ] **Step 5:** Commit `feat(core): giá trị kết quả, khoảng tham chiếu, cờ, quy đổi định tính`.

---

### Task 5: Tài khoản, đăng nhập, vai trò

**Files:**
- Create: `src/Lis.Core/Auth/Roles.cs`, `Permission.cs`, `src/Lis.Data/Auth/PasswordHasher.cs`, `UserStore.cs`, `AuthService.cs`, `CurrentUser.cs`
- Test: `tests/Lis.Core.Tests/Auth/RolesTests.cs`, `tests/Lis.Data.Tests/Auth/AuthServiceTests.cs`

**Interfaces:**
- Produces:
  - Vai trò: `static class Roles { const string QuanTri="quan-tri", TiepNhan="tiep-nhan", KyThuatVien="ky-thuat-vien", TruongKhoa="truong-khoa"; IReadOnlyList<string> All; }`.
  - `enum Permission { QuanTri, TiepNhan, XemKetQua, NhapSuaKetQua, Duyet, HuyDuyet, GhepTay, TheoDoiMay, XemLogTho }`.
  - Quyền theo vai trò:

    | Vai trò | Quyền |
    |---|---|
    | `quan-tri` | tất cả |
    | `tiep-nhan` | `TiepNhan`, `XemKetQua` |
    | `ky-thuat-vien` | `XemKetQua`, `NhapSuaKetQua`, `Duyet`, `GhepTay`, `TheoDoiMay`, `XemLogTho` |
    | `truong-khoa` | như `ky-thuat-vien` + `HuyDuyet` |

  - `static class Permissions { bool Has(IEnumerable<string> roles, Permission p); }`.
  - `sealed class PasswordHasher`:
    - `string Hash(string password)`;
    - `bool Verify(string password, string hash)`;
    - Argon2id, salt 16 byte, 64 MB, 3 lượt, 1 luồng; định dạng lưu `argon2id$<m>$<t>$<p>$<salt b64>$<hash b64>`.
  - `record UserInfo(Guid Id, string Username, string FullName, string? Title, IReadOnlyList<string> Roles, bool Active)`.
  - `class UserStore(Db db)`:
    - `Task<bool> AnyAsync()`;
    - `Task<UserInfo> CreateAsync(string username, string fullName, string? title, string password, IReadOnlyList<string> roles)`;
    - `Task UpdateAsync(Guid id, string fullName, string? title, IReadOnlyList<string> roles, bool active, string? newPassword)`;
    - `Task<IReadOnlyList<UserInfo>> ListAsync()`.
  - `record LoginResult(bool Ok, string Code, string Message, UserInfo? User)`. `Code` nhận một trong các giá trị `ok`, `sai`, `khoa`, `ngung`.
  - `class AuthService(Db db, PasswordHasher hasher, TimeProvider clock)`: `Task<LoginResult> LoginAsync(string username, string password)`.
  - `sealed class CurrentUser`: phiên đăng nhập trong ứng dụng, có `UserInfo User` và `bool Can(Permission p)`.
- Thông báo lỗi tiếng Việt:
  - `"Sai tên đăng nhập hoặc mật khẩu"`;
  - `"Tài khoản tạm khóa do nhập sai nhiều lần, thử lại sau {phút} phút"`;
  - `"Tài khoản đã ngừng hoạt động"`.
- Mật khẩu ngắn hơn 8 ký tự thì ném `ArgumentException("Mật khẩu tối thiểu 8 ký tự")`.

- [ ] **Step 1: Viết test.**
  - `TruongKhoa_CanUnapprove_KyThuatVienCannot`.
  - `Hash_VerifyRoundTrip_AndRejectsWrong`.
  - `Login_FifthFailureLocksFor15Minutes`: dùng `FakeTimeProvider`; lần 5 sai trả `khoa`; sau 15 phút + 1 giây đăng nhập đúng trả `ok`.
  - `Login_SuccessResetsFailedCount`.
  - `Login_Inactive_ReturnsNgung`.
  - `Create_ShortPassword_Throws`.
- [ ] **Step 2:** Chạy test. Mong đợi FAIL.
- [ ] **Step 3:** Cài đặt.
- [ ] **Step 4:** Chạy lại. Mong đợi PASS.
- [ ] **Step 5:** Commit `feat(auth): tài khoản, Argon2id, khóa sai 5 lần, vai trò`.

---

### Task 6: Cài đặt lần đầu (logic) và khung ứng dụng Avalonia

**Files:**
- Create: `src/Lis.Data/Setup/SetupService.cs`, `src/Lis.Data/Facility/FacilityStore.cs`
- Create: `src/Lis.App/App.axaml(.cs)`, `Program.cs`, `Views/SetupWizardWindow.axaml`, `ViewModels/SetupWizardViewModel.cs`, `Views/LoginWindow.axaml`, `ViewModels/LoginViewModel.cs`, `Views/MainWindow.axaml`, `ViewModels/MainViewModel.cs`, `Services/AppServices.cs` (khởi tạo `Db` và service từ `LocalConfig`), `Assets/` (icon)
- Test: `tests/Lis.Data.Tests/Setup/SetupServiceTests.cs`, `tests/Lis.App.Tests/SetupWizardViewModelTests.cs`

**Interfaces:**
- Consumes: `LocalConfigStore`, `Migrator`, `UserStore` (Task 2, 3, 5).
- Produces:
  - `record ConnectionTest(bool Ok, string Message, bool DatabaseEmpty)`.
  - `class SetupService`:
    - `Task<ConnectionTest> TestAsync(DbSettings s)`. Lỗi kết nối được dịch sang tiếng Việt: sai mật khẩu → `"Sai tài khoản hoặc mật khẩu CSDL"`; không tới được máy chủ → `"Không kết nối được máy chủ CSDL {host}:{port}"`; không có CSDL → `"CSDL {tên} chưa được tạo trên máy chủ"`.
    - `MigrationResult Migrate(DbSettings s)`.
    - `Task<bool> NeedsFirstAdminAsync(DbSettings s)`.
    - `Task CreateFirstAdminAsync(DbSettings s, string username, string fullName, string password)`: luôn gán vai trò `quan-tri`; ném lỗi nếu đã có tài khoản.
  - `record FacilityInfo(string Name, string? Address, string? Phone, byte[]? Logo, string BarcodePrefix, string BarcodeDatePattern, int BarcodeSeqDigits, string BarcodeMode)`.
  - `class FacilityStore(Db db)`: `Task<FacilityInfo?> GetAsync()`, `Task SaveAsync(FacilityInfo f)`.
  - Luồng khởi động trong `App`:
    1. Chưa có `config.json` → mở `SetupWizardWindow`, đi 3 bước theo spec mục 5.
       - Bước 2 chỉ hiện khi `NeedsFirstAdmin`.
       - Bước 3 chỉ hiện khi CSDL vừa được tạo mới.
    2. Có config → mở `LoginWindow`.
    3. Đăng nhập xong → mở `MainWindow`.
  - Đăng nhập xong thì upsert `workstation(code = LocalConfig.WorkstationCode, last_seen_at = now())`.
  - `MainWindow` có thanh điều hướng trái với các mục: Tiếp nhận, Kết quả, Chưa ghép, Danh mục, Máy & map, Tài khoản, Cài đặt. Mục hiện/ẩn theo `Permission`.
  - Có `TrayIcon`. Đóng cửa sổ thì ẩn xuống khay. Menu khay có Mở / Thoát.
  - Nút "Tiếp theo" của wizard bị khóa cho tới khi Thử kết nối thành công.

- [ ] **Step 1: Viết test.**
  - `Test_WrongPassword_VietnameseMessage`.
  - `FirstAdmin_OnlyWhenNoUsers`.
  - `FirstAdmin_GetsQuanTriRole`.
  - `Wizard_NextDisabledUntilConnectionOk` (test ViewModel, giả lập `SetupService` qua interface `ISetupService`).
- [ ] **Step 2:** Chạy test. Mong đợi FAIL.
- [ ] **Step 3:** Cài đặt service, ViewModel và view.
- [ ] **Step 4:** Chạy test. Mong đợi PASS. Chạy `dotnet run --project src/Lis.App`, đi hết wizard với PostgreSQL dev (`docker run -d --name lis-dev-pg -e POSTGRES_PASSWORD=lis_dev_pw -p 5442:5432 postgres:18-alpine`), chụp ảnh 3 bước wizard và màn đăng nhập để kiểm.
- [ ] **Step 5:** Commit `feat(app): cài đặt lần đầu, đăng nhập, khung màn chính + khay`.

---

### Task 7: Danh mục dịch vụ, khoảng tham chiếu, định tính

**Files:**
- Create: `src/Lis.Data/Catalog/CatalogStore.cs`, `src/Lis.App/Views/Catalog/*.axaml`, `ViewModels/Catalog/*.cs`
- Test: `tests/Lis.Data.Tests/Catalog/CatalogStoreTests.cs`

**Interfaces:**
- Produces:
  - `record ServiceInfo(Guid Id, string Code, string Name, string? GroupName, Guid SpecimenTypeId, string? Unit, string ValueType, int? Decimals, int PrintOrder, bool IsPanel, bool AutoApprove, bool Active, string? HisCode, IReadOnlyList<Guid> ChildIds)`.
  - `class CatalogStore(Db db)`:
    - `Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(bool activeOnly)`;
    - `Task<ServiceInfo?> FindByCodeAsync(string code)` (không phân biệt hoa thường);
    - `Task<Guid> SaveServiceAsync(ServiceInfo s)`;
    - `Task SetChildrenAsync(Guid panelId, IReadOnlyList<Guid> childIds)`. Ném `InvalidOperationException("Dịch vụ không thể là con của chính nó")` khi có vòng. Con phải là dịch vụ không phải panel;
    - `Task<IReadOnlyList<ReferenceRange>> RangesAsync(Guid serviceId)`, `Task SaveRangesAsync(Guid serviceId, IReadOnlyList<ReferenceRange> ranges)`;
    - `Task<IReadOnlyList<QualitativeRule>> QualitativeRulesAsync(Guid serviceId)`, `Task SaveQualitativeRulesAsync(...)`;
    - `Task<IReadOnlyList<(Guid Id,string Code,string Name)>> SpecimenTypesAsync()`.
  - Màn Danh mục:
    - bảng dịch vụ có ô lọc;
    - form sửa dịch vụ;
    - tab "Chỉ số con" (thêm bằng mã + Enter, kéo hoặc nút để đổi thứ tự);
    - tab "Khoảng tham chiếu": bảng giới / tuổi từ–đến (nhập theo ngày, tháng hoặc năm, lưu quy ra ngày) / máy / thấp / cao / nguy hiểm thấp / nguy hiểm cao / chữ;
    - tab "Quy đổi định tính".

- [ ] **Step 1: Viết test.** `SetChildren_PreservesOrder`, `SetChildren_RejectsPanelAsChild`, `FindByCode_CaseInsensitive`, `SaveRanges_ReplacesAll`.
- [ ] **Step 2:** Chạy test. Mong đợi FAIL.
- [ ] **Step 3:** Cài đặt store và các màn.
- [ ] **Step 4:** Chạy test. Mong đợi PASS. Chụp ảnh màn danh mục với 1 panel "Tổng phân tích tế bào máu" có 3 con.
- [ ] **Step 5:** Commit `feat(catalog): dịch vụ cha–con, khoảng tham chiếu, quy đổi định tính`.

---

### Task 8: Người bệnh, chỉ định, cấp barcode, màn tiếp nhận

**Files:**
- Create: `src/Lis.Core/Orders/BarcodeFormat.cs`, `BarcodeComposer.cs`, `SampleGrouper.cs`, `src/Lis.Data/Orders/OrderService.cs`, `PatientStore.cs`, `src/Lis.App/Views/Reception/*`, `ViewModels/Reception/*`
- Test: `tests/Lis.Core.Tests/Orders/*Tests.cs`, `tests/Lis.Data.Tests/Orders/OrderServiceTests.cs`

**Interfaces:**
- Consumes: `CatalogStore.FindByCodeAsync`, `FacilityStore.GetAsync`.
- Produces:
  - `sealed record BarcodeFormat(string Prefix, string DatePattern, int SeqDigits, string Mode)`:
    - `int Length` (tổng độ dài);
    - `int MaxSeq` (= 10^SeqDigits − 1).
  - `static class BarcodeComposer`: `string Compose(BarcodeFormat f, DateOnly day, int seq)`. `seq` vượt `MaxSeq` thì ném `InvalidOperationException("Hết số barcode trong ngày — tăng số chữ số thứ tự trong Cài đặt")`.
  - `sealed record OrderedService(Guid ServiceId, Guid SpecimenTypeId)`, `sealed record SampleGroup(Guid SpecimenTypeId, IReadOnlyList<Guid> ServiceIds)`.
  - `static class SampleGrouper`: `IReadOnlyList<SampleGroup> Group(IEnumerable<OrderedService> items, string mode)`. Mode `per-specimen` gom theo loại mẫu, giữ thứ tự xuất hiện. Mode `single` cho đúng 1 nhóm, loại mẫu lấy của dịch vụ đầu.
  - `record NewPatient(string FullName, DateOnly? Dob, int? BirthYear, char Sex, string? Address, string? Phone)`.
  - `record NewOrder(Guid? PatientId, NewPatient? Patient, IReadOnlyList<Guid> ServiceIds, string? OrderingDoctor, string? OrderingDept, string? Diagnosis)`.
  - `record CreatedSample(Guid SampleId, string Barcode, string SpecimenName, IReadOnlyList<string> ServiceNames)`, `record OrderCreated(Guid OrderId, Guid PatientId, string PatientCode, IReadOnlyList<CreatedSample> Samples)`.
  - `class OrderService(Db db, TimeProvider clock)`:
    - `Task<OrderCreated> CreateAsync(NewOrder o, Guid actorId)`. Trong một giao dịch:
      1. Tạo người bệnh nếu mới. Mã người bệnh có dạng `BN` + `yyMMdd` + 4 số, sinh bằng `barcode_counter` với prefix `BN`.
      2. Tách các panel ra thành con: dòng cha có `parent_item_id = null`, dòng con trỏ về dòng cha.
      3. Gom mẫu, cấp barcode. Số thứ tự lấy bằng `INSERT … ON CONFLICT (day,prefix) DO UPDATE SET last_seq = barcode_counter.last_seq + 1 RETURNING last_seq`.
    - `Task<IReadOnlyList<PatientHit>> FindPatientsAsync(string query)`: tìm theo mã, họ tên (không dấu, không phân biệt hoa thường, dùng `unaccent`) hoặc SĐT; tối đa 20 dòng.
  - Màn Tiếp nhận:
    - form người bệnh (Enter chuyển ô);
    - ô "Mã dịch vụ" (gõ mã + Enter để thêm; gõ chữ thì gợi ý);
    - danh sách đã chọn, hiển thị nhóm theo loại mẫu;
    - nút **Lưu và in tem** (F9).
    - Sau khi lưu hiện bảng barcode đã cấp.

- [ ] **Step 1: Viết test.**
  - `Compose_Default_Is10Digits` (`9`, `yyMMdd`, 3, ngày 2026-11-03, seq 1 → `"9261103001"`).
  - `Compose_SeqOverflow_Throws`.
  - `Group_PerSpecimen_SplitsTubes`.
  - `Group_Single_OneGroup`.
  - `CreateOrder_PanelExpandsChildren` (panel 3 con cho ra 4 `order_item`).
  - `CreateOrder_TwoSpecimens_TwoBarcodes`.
  - `CreateOrder_Concurrent_ProducesUniqueBarcodes`: 20 tác vụ song song, barcode không trùng.
  - `FindPatients_AccentInsensitive` (`"nguyen van mau"` khớp `"Nguyễn Văn Mẫu"`; migration bổ sung `CREATE EXTENSION unaccent` trong `0002_unaccent.sql`).
- [ ] **Step 2:** Chạy test. Mong đợi FAIL.
- [ ] **Step 3:** Cài đặt.
- [ ] **Step 4:** Chạy test. Mong đợi PASS. Chụp ảnh màn tiếp nhận sau khi lưu.
- [ ] **Step 5:** Commit `feat(orders): chỉ định, cấp barcode theo loại mẫu, màn tiếp nhận`.

---

### Task 9: In tem barcode

**Files:**
- Create: `src/Lis.Reports/Barcode/Code128Renderer.cs`, `src/Lis.Reports/Labels/LabelData.cs`, `LabelPdf.cs`, `src/Lis.Reports/Printing/IPrintService.cs`, `PdfPrintService.cs` (Windows: in qua `System.Drawing.Printing` sau khi rasterize bằng PDFtoImage; hệ khác: lưu PDF tạm và mở bằng ứng dụng mặc định), `src/Lis.App/Views/Settings/PrinterSettings*`
- Test: `tests/Lis.Reports.Tests/LabelPdfTests.cs`

**Interfaces:**
- Produces:
  - `record LabelData(string Barcode, string PatientName, int? BirthYear, char Sex, string SpecimenName, DateOnly Day)`.
  - `record LabelSize(double WidthMm, double HeightMm)`: mặc định **50 × 25 mm**, lưu ở `setting` khóa `label.size`.
  - `static class LabelPdf`: `byte[] Render(IReadOnlyList<LabelData> labels, LabelSize size)`, mỗi tem một trang.
  - `interface IPrintService { Task PrintAsync(byte[] pdf, string? printerName, CancellationToken ct = default); IReadOnlyList<string> Printers(); }`.
  - Máy in tem và máy in phiếu chọn riêng trong Cài đặt, lưu ở `setting` với khóa `printer.label` và `printer.report`.

- [ ] **Step 1: Viết test.** `Render_OnePagePerLabel`, `Render_ContainsBarcodeText`: đọc lại PDF bằng UglyToad.PdfPig, chuỗi barcode và họ tên có trong text. `Code128_DecodesBack`: render PNG rồi decode lại bằng ZXing, ra đúng barcode.
- [ ] **Step 2:** Chạy test. Mong đợi FAIL.
- [ ] **Step 3:** Cài đặt. Nối nút "Lưu và in tem" ở Task 8 vào `IPrintService`.
- [ ] **Step 4:** Chạy test. Mong đợi PASS. Mở PDF tem mẫu và chụp ảnh để kiểm bố cục.
- [ ] **Step 5:** Commit `feat(reports): tem barcode Code 128 + dịch vụ in`.

---

### Task 10: Nhập, sửa, duyệt và hủy duyệt kết quả

**Files:**
- Create: `src/Lis.Data/Results/ResultService.cs`, `SampleResultsQuery.cs`, `src/Lis.App/Views/Results/*`, `ViewModels/Results/*`
- Test: `tests/Lis.Data.Tests/Results/ResultServiceTests.cs`

**Interfaces:**
- Consumes: `ResultValue`, `ValueRounding`, `ReferenceRangeSelector`, `FlagCalculator`, `QualitativeMapper` (Task 4); `Permissions` (Task 5).
- Produces:
  - `record ResultRow(Guid OrderItemId, Guid? ResultId, Guid ServiceId, string ServiceCode, string ServiceName, bool IsPanelHeader, int Depth, string? Value, string? Unit, string? RefText, ResultFlag Flag, string? Status, string? PendingValue, string? Source)`.
  - `record SampleResults(Guid SampleId, string Barcode, string PatientName, string PatientCode, PatientFacts Patient, DateTimeOffset? ApprovedAt, string? ApprovedBy, IReadOnlyList<ResultRow> Rows)`.
  - `record SampleListItem(Guid SampleId, string Barcode, string PatientName, DateTimeOffset OrderedAt, string SpecimenName, int Total, int WithValue, bool Approved, bool HasAbnormal, bool HasPending)`.
  - `class ResultService(Db db, TimeProvider clock)`:
    - `Task<IReadOnlyList<SampleListItem>> ListAsync(DateOnly from, DateOnly to, string? status, string? query)`. `status` nhận `cho-ket-qua`, `cho-duyet`, `da-duyet` hoặc null.
    - `Task<SampleResults> GetAsync(Guid sampleId)`.
    - `Task SetManualAsync(Guid orderItemId, string value, Guid actorId, string? reason)`. Ô đã có giá trị thì bắt buộc `reason`, nếu không ném `ArgumentException("Nhập lý do sửa kết quả")`. Ghi `result_history` action `sua`. Ô trống ghi action `nhan`.
    - `Task ApproveAsync(Guid sampleId, Guid actorId)`: đặt mọi kết quả có giá trị sang `da-duyet`, ghi `sample.approved_at/by`. Không có kết quả nào thì ném `InvalidOperationException("Chưa có kết quả để duyệt")`.
    - `Task UnapproveAsync(Guid sampleId, Guid actorId, string reason)`: lý do bắt buộc.
    - `Task AcceptPendingAsync(Guid resultId, Guid actorId)`: dùng `pending_value` làm giá trị mới, đưa mẫu về chưa duyệt và ghi `thay-the`.
    - `Task RejectPendingAsync(Guid resultId, Guid actorId)`.
    - `Task<bool> TryAutoApproveAsync(Guid sampleId)`: duyệt khi mọi dịch vụ trên mẫu đều `auto_approve`, mọi kết quả có giá trị, cờ đều `None` và máy không gắn cờ. Người duyệt ghi là "Tự duyệt", `approved_by` để null.
  - Mỗi lần ghi giá trị đều: áp `ValueRounding` theo `decimals` của dịch vụ; chọn khoảng tham chiếu theo người bệnh, ngày chỉ định và máy; tính cờ; lưu `ref_low/ref_high/ref_text` đã áp vào `result`.
  - Màn Kết quả, chia ba vùng:
    - **Trái:** danh sách mẫu, có bộ lọc ngày (chọn nhanh: hôm nay, hôm qua, 7 ngày) và trạng thái.
    - **Phải:** bảng chỉ số. Chỉ số con thụt vào. Ô giá trị sửa được bằng bàn phím: Enter xuống dòng, Esc hủy. Cờ hiển thị bằng chữ "↑ Cao" / "↓ Thấp" / "‼ Nguy hiểm" kèm màu.
    - **Nút:** Duyệt (F10), Hủy duyệt (chỉ hiện khi có quyền `HuyDuyet`), In phiếu (F11). Hai nút Chấp nhận / Bỏ bản thay thế hiện khi có `pending`.

- [ ] **Step 1: Viết test.**
  - `SetManual_RoundsAndFlagsWithPatientRange`.
  - `SetManual_EditRequiresReason_AndKeepsHistory`.
  - `Approve_NoValues_Throws`.
  - `Unapprove_RequiresReason`.
  - `AutoApprove_OnlyWhenAllNormalAndAllowed`.
  - `AcceptPending_ReplacesValue_AndUnapproves`.
- [ ] **Step 2:** Chạy test. Mong đợi FAIL.
- [ ] **Step 3:** Cài đặt service và màn.
- [ ] **Step 4:** Chạy test. Mong đợi PASS. Chụp ảnh màn kết quả có một chỉ số cao và một chỉ số thấp.
- [ ] **Step 5:** Commit `feat(results): nhập/sửa có lý do, duyệt, hủy duyệt, tự duyệt, bản thay thế`.

---

### Task 11: Nhận kết quả từ máy (cho đợt 2) và màn Chưa ghép

**Files:**
- Create: `src/Lis.Core/Ingest/InstrumentReading.cs`, `src/Lis.Data/Ingest/IngestService.cs`, `UnmatchedService.cs`, `src/Lis.App/Views/Unmatched/*`, `ViewModels/Unmatched/*`
- Create: `src/Lis.Data/Instruments/InstrumentStore.cs`, `src/Lis.App/Views/Instruments/*` (danh mục máy + bảng map kênh. Trường `protocol_family`, `driver_config`, `connection` ở đợt 1 chỉ là ô chọn/JSON thô; đợt 2 thay bằng form theo họ giao thức)
- Test: `tests/Lis.Data.Tests/Ingest/IngestServiceTests.cs`, `tests/Lis.Data.Tests/Instruments/InstrumentStoreTests.cs`

**Interfaces:**
- Consumes: `ResultService` (logic ghi giá trị và tự duyệt), Task 4.
- Produces (đợt 2 gọi các hàm này):
  - `sealed record ReadingItem(string ChannelCode, string RawValue, string? Unit, string? InstrumentFlag)`.
  - `sealed record InstrumentReading(Guid InstrumentId, string Barcode, bool IsQc, DateTimeOffset ReceivedAt, string? RawRef, IReadOnlyList<ReadingItem> Items)`. `RawRef` trỏ tới bản tin thô (định dạng do đợt 2 định nghĩa, ở đây chỉ lưu chuỗi).
  - `record IngestOutcome(int Stored, int PendingReplacement, int Unmatched, int Qc)`.
  - `class IngestService(Db db, TimeProvider clock)`: `Task<IngestOutcome> IngestAsync(InstrumentReading r)`. Thứ tự xử lý:
    1. `IsQc` → ghi toàn bộ vào `qc_result` và dừng.
    2. Barcode lấy `Trim()` và bỏ ký tự điều khiển, rồi tìm `sample` bằng `barcode =` chính xác. Không có → mọi dòng vào `unmatched_result` với lý do `khong-co-barcode`.
    3. Với từng dòng, tra `instrument_channel` theo `(instrument_id, channel_code)`. Không có → `kenh-chua-map`.
    4. Có map nhưng mẫu không có `order_item` của chỉ số đó → `khong-co-chi-dinh`.
    5. Áp `factor`, rồi định tính (`QualitativeMapper`) hoặc làm tròn (`decimals` của map, nếu null thì theo dịch vụ). Bỏ dấu so sánh khi `keep_comparator = false`.
    6. Kết quả đang `da-duyet` → ghi `pending_value`, `pending_instrument_id`, `pending_raw_ref`, history `thay-the`, đếm `PendingReplacement`. Ngược lại ghi giá trị, nguồn `may`, history `nhan`.
    7. Cuối cùng gọi `TryAutoApproveAsync` cho mẫu.
  - `class UnmatchedService(Db db)`:
    - `Task<IReadOnlyList<UnmatchedItem>> ListOpenAsync(DateOnly from, DateOnly to)`;
    - `Task AssignAsync(Guid unmatchedId, Guid orderItemId, Guid actorId)`: ghi giá trị như bước 5–6, đánh dấu đã xử lý;
    - `Task DismissAsync(Guid unmatchedId, Guid actorId, string reason)`.
  - `class InstrumentStore(Db db)`: `ListAsync`, `SaveAsync(InstrumentInfo)`, `ChannelsAsync(Guid instrumentId)`, `SaveChannelAsync(ChannelMap)`. Trùng kênh thì ném `InvalidOperationException("Mã kênh {code} đã map cho chỉ số {tên}")`.

- [ ] **Step 1: Viết test.**
  - `Ingest_MapsChannel_RoundsAndFlags`.
  - `Ingest_TrimsBarcode_ButNeverSuffixMatches`: mẫu `9261103001`. Đọc vào `" 9261103001\r"` thì ghép được. Đọc vào `"261103001"` thì ra Unmatched `khong-co-barcode`.
  - `Ingest_UnknownChannel_Unmatched`.
  - `Ingest_NotOrdered_Unmatched`.
  - `Ingest_ApprovedResult_BecomesPendingReplacement`.
  - `Ingest_Qc_GoesToQcTable_NotResults`.
  - `Ingest_FactorAndQualitative` (`0.405` với factor 100 → `"40.5"`; `"neg"` → `"Âm tính"`, cờ `None`).
  - `Assign_FromUnmatched_WritesResult`.
  - `SaveChannel_Duplicate_FriendlyError`.
- [ ] **Step 2:** Chạy test. Mong đợi FAIL.
- [ ] **Step 3:** Cài đặt service, màn Chưa ghép (bảng + nút "Gán vào mẫu…" mở hộp chọn barcode → chỉ số) và màn Máy & map.
- [ ] **Step 4:** Chạy test. Mong đợi PASS. Chụp ảnh màn Chưa ghép có 3 dòng thuộc 3 lý do khác nhau, dữ liệu sinh bằng test seed.
- [ ] **Step 5:** Commit `feat(ingest): nhận kết quả máy, ghép chính xác, chưa ghép, QC, bản thay thế`.

---

### Task 12: Phiếu kết quả

**Files:**
- Create: `src/Lis.Reports/ResultReport/ReportData.cs`, `ResultReportPdf.cs`
- Test: `tests/Lis.Reports.Tests/ResultReportPdfTests.cs`

**Interfaces:**
- Consumes: `SampleResults` (Task 10), `FacilityInfo` (Task 6), `IPrintService` (Task 9).
- Produces:
  - `record ReportData(FacilityInfo Facility, SampleResults Sample, IReadOnlyList<SampleResults> OtherSamplesSameOrder, bool Approved, string? ApproverName, DateTimeOffset PrintedAt)`.
  - `static class ResultReportPdf`: `byte[] Render(ReportData d)`. Khổ A4 dọc. Bố cục:
    - **Phần đầu:** logo + tên cơ sở + địa chỉ.
    - **Tiêu đề:** "PHIẾU KẾT QUẢ XÉT NGHIỆM".
    - **Thông tin người bệnh:** họ tên, năm sinh, giới, địa chỉ, mã BN, barcode, bác sĩ chỉ định, chẩn đoán.
    - **Bảng kết quả:** cột Xét nghiệm | Kết quả | Đơn vị | Khoảng tham chiếu. Nhóm theo dịch vụ cha, chỉ số con thụt vào. Giá trị bất thường in **đậm** kèm ↑/↓.
    - **Cuối phiếu:** "Người duyệt", thời điểm duyệt.
    - **Khi chưa duyệt:** dòng chữ "CHƯA DUYỆT — KHÔNG CÓ GIÁ TRỊ TRẢ KẾT QUẢ" ở đầu và cuối.
  - In từ màn Kết quả: gộp mọi mẫu của cùng một phiếu chỉ định vào một phiếu in.

- [ ] **Step 1: Viết test.** `Render_ContainsPatientAndValues`. `Render_Unapproved_HasWatermarkText`. `Render_AbnormalHasArrow` (text chứa `"↑"` cạnh giá trị cao). `Render_PanelChildrenIndented` (thứ tự dòng trong text: tên panel đứng trước các con).
- [ ] **Step 2:** Chạy test. Mong đợi FAIL.
- [ ] **Step 3:** Cài đặt và nối nút In phiếu (F11).
- [ ] **Step 4:** Chạy test. Mong đợi PASS. Chụp ảnh trang PDF mẫu để kiểm bố cục.
- [ ] **Step 5:** Commit `feat(reports): phiếu kết quả xét nghiệm`.

---

### Task 13: Quản trị tài khoản, thông tin cơ sở, nhập danh mục Excel, dữ liệu demo

**Files:**
- Create: `src/Lis.App/Views/Admin/Users*`, `Views/Settings/FacilitySettings*`, `src/Lis.Data/Catalog/CatalogExcelImporter.cs`, `tools/seed/mau-danh-muc.xlsx` (sinh bằng `tools/seed/make-seed.csx` hoặc project console `tools/Lis.SeedTool`), `tools/Lis.SeedTool/` (lệnh `seed-demo` tạo người bệnh giả + chỉ định + kết quả)
- Test: `tests/Lis.Data.Tests/Catalog/CatalogExcelImporterTests.cs`

**Interfaces:**
- Consumes: `UserStore`, `FacilityStore`, `CatalogStore`, `InstrumentStore`, `OrderService`, `IngestService`.
- Produces:
  - `record ImportReport(int Created, int Updated, IReadOnlyList<string> Errors)`.
  - `class CatalogExcelImporter(Db db)`: `Task<ImportReport> ImportAsync(Stream xlsx, bool dryRun)`. Workbook gồm các sheet:
    - `DichVu`: `Ma`, `Ten`, `Nhom`, `LoaiMau`, `DonVi`, `KieuKetQua`, `SoLe`, `ThuTuIn`, `LaPanel`, `TuDuyet`, `MaHIS`.
    - `ChiSoCon`: `MaCha`, `MaCon`, `ThuTu`.
    - `KhoangThamChieu`: `Ma`, `Gioi`, `TuoiTu`, `TuoiDen`, `DonViTuoi` (`ngay`/`thang`/`nam`), `Thap`, `Cao`, `NguyHiemThap`, `NguyHiemCao`, `Chu`.
    - `MapMay`: `TenMay`, `MaKenh`, `MaChiSo`, `SoLe`, `HeSo`, `GiuDauSoSanh`, `GuiKhiMayHoi`.
    - Lỗi ghi theo dạng `"Sheet DichVu dòng 12: thiếu Mã"`. Có lỗi thì không ghi gì (một giao dịch).
  - `mau-danh-muc.xlsx`: dựng từ `~/Downloads/lis-tham-khao/lis-server/seed/danhmuc-dichvu.sql` và `khao-sat/seed/anh-xa-ma-may.json`. Đây chỉ là dữ liệu danh mục. Bỏ các dòng map trùng hoặc sai đã liệt kê trong `docs/drivers/cac-may-astm-bvdk10.md` và `khao-sat/lis-hien-huu.md` mục 4 (ví dụ kênh 19 của AU640). Ghi lại các dòng đã bỏ trong sheet `GhiChu`.
  - `Lis.SeedTool seed-demo --conn <chuỗi> --days 3`:
    - tạo khoảng 30 người bệnh có tên chứa "Mẫu"/"Thử";
    - tạo chỉ định ngẫu nhiên;
    - với một nửa số mẫu, gọi `IngestService` bằng kết quả giả.
  - Màn Tài khoản: danh sách, thêm/sửa (chọn nhiều vai trò, đặt lại mật khẩu, khóa/mở). Màn Cài đặt cơ sở: thông tin cơ sở + định dạng barcode (xem trước barcode mẫu) + khổ tem + máy in. Tab **Kết nối CSDL** (chỉ `quan-tri`): sửa `DbSettings` + mã máy trạm, bắt buộc Thử kết nối (`SetupService.TestAsync`) trước khi lưu `LocalConfig`, lưu xong đăng xuất và mở lại màn đăng nhập.

- [ ] **Step 1: Viết test.** `Import_ValidWorkbook_CreatesPanelsAndRanges`. `Import_Error_RollsBackAll_WithRowNumbers`. `Import_DryRun_WritesNothing`. `Import_DuplicateChannel_ReportsError`.
- [ ] **Step 2:** Chạy test. Mong đợi FAIL.
- [ ] **Step 3:** Cài đặt. Sinh `mau-danh-muc.xlsx`.
- [ ] **Step 4:** Chạy toàn bộ `dotnet test LisConnect.slnx`. Mong đợi PASS hết. Chạy app với CSDL dev, nhập `mau-danh-muc.xlsx`, chạy `seed-demo`, rồi đi hết luồng: tiếp nhận → in tem → nhập/duyệt → in phiếu → gán một kết quả Chưa ghép. Chụp ảnh từng màn.
- [ ] **Step 5:** Commit `feat(admin): tài khoản, cài đặt cơ sở, nhập danh mục Excel, dữ liệu demo`.

---

---

### Task 14: Cấu hình kết nối HIS (màn cài đặt, chưa đồng bộ) — bổ sung 05/11 theo yêu cầu người dùng

Người dùng cần cài đặt được kết nối HIS ngay ở bản chạy thử. Đợt 1 chỉ làm phần **lưu cấu hình + thử kết nối**. Phần kéo chỉ định / đẩy kết quả / đồng bộ nhân sự vẫn thuộc đợt 3.

**Files:**
- Create: `src/Lis.Data/Migrations/0006_his_profile.sql`, `src/Lis.Data/His/HisProfile.cs`, `HisProfileStore.cs`, `HisConnectionTester.cs`, `src/Lis.App/Views/Settings/HisSettings*`, `ViewModels/Settings/HisSettingsViewModel.cs`
- Test: `tests/Lis.Data.Tests/His/HisProfileStoreTests.cs`, `HisConnectionTesterTests.cs`, `tests/Lis.App.Tests/HisSettingsViewModelTests.cs`

**Interfaces:**
- Produces:
  - Bảng `his_profile`: một dòng, `id int PK CHECK (id=1)`. Các cột:
    - `enabled bool`, `name text`, `base_url text`;
    - `auth_type text CHECK IN ('none','api-key','basic','bearer-login')`;
    - `api_key_header text`, `secret_protected text` (khóa API hoặc mật khẩu, mã hóa như mật khẩu CSDL ở Task 2, dạng base64), `username text`;
    - `login_path text`, `login_body_template text`, `token_json_path text`, `token_ttl_minutes int`;
    - `orders_mode text CHECK IN ('pull','push')`, `orders_path text`, `poll_seconds int`, `results_path text`, `staff_path text`;
    - `push_listen_port int`, `push_api_key_hash text`, `push_workstation_code text`;
    - `updated_at`, `updated_by`.
  - `record HisProfile(...)` có đủ các trường trên. Bí mật được giải mã khi đọc và **không bao giờ in ra trong `ToString`** (che như `DbSettings`).
  - `class HisProfileStore(Db db, ISecretProtector protector)`: `Task<HisProfile?> GetAsync()`, `Task SaveAsync(HisProfile p, Guid actorId)`.
  - `class HisConnectionTester(HttpClient http)`: `Task<HisTestResult> TestAsync(HisProfile p, CancellationToken ct)`, với `record HisTestResult(bool Ok, string Message)`.
    - Gọi đăng nhập nếu `bearer-login`, rồi `GET` tới `orders_path`.
    - Timeout 10 giây.
    - Thông báo tiếng Việt cho các trường hợp: thành công kèm mã HTTP; sai xác thực (401/403); không tới được máy chủ; sai đường dẫn (404); không lấy được token ở `token_json_path`.
  - Hồ sơ dựng sẵn **"HIS-PROMAX"**: nút điền sẵn `auth_type = bearer-login`, `login_path = /api/v1/integration/auth/verify`. Các đường API khác để trống và ghi chú "API phía HIS-PROMAX làm ở đợt 3".
  - Màn **Cài đặt → Kết nối HIS** (chỉ vai trò `quan-tri`):
    - form theo `auth_type`, ẩn/hiện trường theo loại;
    - mật khẩu/khóa API là ô che chữ; để trống nghĩa là giữ giá trị cũ;
    - nút **Thử kết nối** và **Lưu**.

- [ ] **Step 1:** Viết test.
  - `Save_ThenGet_RoundTrips_SecretEncrypted`: cột `secret_protected` không chứa chuỗi bí mật rõ.
  - `ToString_RedactsSecret`.
  - `Test_*`: dựng HTTP server giả trong test (`HttpListener` hoặc `WebApplication` cục bộ) cho các trường hợp 200 / 401 / 404 / không tới được / token thiếu.
  - `HisSettingsViewModel_TestButton_ShowsMessage`.
- [ ] **Step 2:** Chạy test, mong đợi FAIL.
- [ ] **Step 3:** Cài đặt.
- [ ] **Step 4:** Chạy test, mong đợi PASS. Chụp ảnh màn Kết nối HIS.
- [ ] **Step 5:** Commit `feat(his): màn cấu hình kết nối HIS + thử kết nối`.

---

### Task 15: Build và chạy thử trên Windows

**Files:**
- Create: `scripts/build-windows.ps1`, `docs/CHAY-THU-WINDOWS.md`
- Modify: `README.md` (trỏ tới tài liệu chạy thử), `.github/workflows/build.yml` (thêm job `publish-win-x64` đẩy artifact zip)

**Interfaces:**
- `scripts/build-windows.ps1`:
  - chạy `dotnet publish src/Lis.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`;
  - kèm công cụ `tools/Lis.SeedTool`;
  - ra thư mục `dist/LisConnect-win-x64/` và tệp zip;
  - in ra đường dẫn.
- `docs/CHAY-THU-WINDOWS.md` (tiếng Việt có dấu), từng bước:
  1. Cài .NET 10 SDK (chỉ cần khi build từ mã nguồn) hoặc dùng bản zip từ CI.
  2. Cài PostgreSQL 16+ cho Windows (link tải chính thức). Tạo CSDL `lis` và user `lis_user` bằng lệnh `psql` mẫu. Bật extension `citext` và `unaccent`: hai extension này đi kèm bản cài, migration tự `CREATE EXTENSION`, nhưng user cần quyền hoặc là chủ CSDL — ghi rõ cách cấp.
  3. Chạy `LisConnect.exe` → trình cài đặt:
     - nhập kết nối CSDL, **Thử kết nối**;
     - tạo **tài khoản quản trị**;
     - nhập thông tin cơ sở.
  4. Đăng nhập → **Cài đặt → Kết nối CSDL** để sửa kết nối khi cần. **Cài đặt → Kết nối HIS** để cấu hình HIS và thử kết nối.
  5. Nạp danh mục mẫu: **Danh mục → Nhập Excel** với `tools/seed/mau-danh-muc.xlsx`. Tạo dữ liệu demo bằng `Lis.SeedTool seed-demo`.
  6. Cấu hình máy in tem / máy in phiếu.
  7. Gỡ lỗi thường gặp: tường lửa cổng 5432, sai mật khẩu, thiếu quyền tạo extension, font.
  - Nói rõ các phần **chưa có ở bản thử**: dịch vụ nền Windows, kết nối máy xét nghiệm, log thô (đợt 2); đồng bộ HIS thật (đợt 3); bộ cài `.msi` (đợt 5).
- **PostgreSQL 16+ (thay vì 18):** migration hiện dùng `uuidv7()`, chỉ có từ PostgreSQL 18. Phải chọn một trong hai:
  - (a) yêu cầu PostgreSQL 18;
  - (b) thêm migration tạo hàm `uuidv7()` dự phòng khi phiên bản < 18.
  
  Chọn **(b)** để cài được trên bản PostgreSQL phổ biến: viết `0007_uuidv7_compat.sql`, tạo hàm SQL thuần sinh UUIDv7 khi `current_setting('server_version_num')::int < 180000`. Thứ tự migration phải bảo đảm hàm có trước khi bảng dùng: vì 0001 đã dùng `uuidv7()`, đặt khối tạo hàm vào **script chạy trước** 0001 (DbUp pre-deployment script, luôn chạy, idempotent). Test chạy migration trên `postgres:16-alpine`.

- [ ] **Step 1:** Viết test `Upgrade_OnPostgres16_Works` (fixture thêm image 16) và `UuidV7Compat_GeneratesTimeOrderedUuid`.
- [ ] **Step 2:** Chạy test, mong đợi FAIL trên PostgreSQL 16.
- [ ] **Step 3:** Cài đặt script, tài liệu, job CI.
- [ ] **Step 4:** Chạy test, mong đợi PASS. Chạy `pwsh scripts/build-windows.ps1` nếu có PowerShell; nếu không có thì kiểm `dotnet publish -r win-x64` chạy được trên macOS.
- [ ] **Step 5:** Commit `chore: build Windows + hướng dẫn chạy thử`.

## Kết thúc đợt 1

- [ ] `dotnet build LisConnect.slnx -warnaserror` và `dotnet test LisConnect.slnx` xanh. CI xanh trên cả `windows-latest` và `ubuntu-latest`.
- [ ] README cập nhật cách chạy dev (PostgreSQL docker, `dotnet run --project src/Lis.App`, `seed-demo`).
- [ ] Cập nhật bảng lộ trình ở đầu tệp này, đánh dấu đợt 1 xong. Viết kế hoạch đợt 2 (dịch vụ nền, driver, log thô) dựa trên `IngestService.IngestAsync` và `InstrumentStore`.
