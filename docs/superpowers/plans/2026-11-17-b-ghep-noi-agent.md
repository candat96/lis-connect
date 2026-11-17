# Phần B — Ghép nối và tự cài agent — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Từ lúc bấm "Thêm agent" trên web đến lúc web hiện agent "Đang kết nối", không ai phải gõ URL server, mã phòng khám hay sửa tệp nào: web phát tệp cài kèm mã ghép nối dùng một lần, tệp cài tự đổi mã lấy khoá lâu dài và tự cài thành dịch vụ Windows.

**Architecture:** Server thêm bảng `agent_pairing` (lis_master chuyển sang migration đánh số), route `/agent/v1/pair` (đổi mã lấy khoá, không cần khoá agent) và đổi `/api/agents` sang luồng ghép nối; phát `LisAgent.exe` từ `AGENT_DIST_DIR`. Định dạng mã ghép nối nằm ở `Lis.Core` để server sinh và agent đọc dùng chung một chỗ. Agent có các chế độ dòng lệnh `run | pair | install | uninstall` và chế độ bấm-chạy (tự xin UAC → cài / ghép nối lại / gỡ); khoá lưu bằng `ISecretProtector` sẵn có (DPAPI phạm vi máy). Mọi bước đụng Windows (dịch vụ, UAC, quyền tệp) đứng sau interface để kiểm logic trên macOS bằng bản giả.

**Tech Stack:** .NET 10 / C# 14, ASP.NET Core minimal API, Dapper + Npgsql, DbUp, `Microsoft.Extensions.Hosting.WindowsServices`, `sc.exe`, xunit v3 + Shouldly + Testcontainers + `FakeTimeProvider`, React + antd + TanStack Query + vitest.

**Spec:** `docs/superpowers/specs/2026-11-16-mot-server-agent-dieu-khien-tu-web-design.md` — mục 5.B, 6, 7, 9 ("Tệp cài agent", "Khóa chống trùng bản tin thô" — phần cách ly hàng chờ, "Cấu hình agent", "Thử trên Windows thật").

**Các kế hoạch sau (viết khi B xong):** C kênh lệnh · D giải mã ở server · E tiếp nhận tối giản.

## Global Constraints

- Chạy lệnh .NET với `export PATH=$HOME/.dotnet:$PATH`. `TreatWarningsAsErrors` đang bật: `dotnet build LisConnect.slnx` phải **0 warning, 0 error**. API chỉ có trên Windows (DPAPI, `WindowsIdentity`, ACL tệp) gọi trong lớp/hàm gắn `[SupportedOSPlatform("windows")]` và chỉ gọi tới sau `OperatingSystem.IsWindows()` — không tắt CA1416.
- Test CSDL cần Docker. Toàn bộ test hiện có phải vẫn xanh (756 test lúc bắt đầu B), cộng test mới.
- Không sửa hành vi `Lis.App`, `Lis.Connections`, `Lis.Core/Drivers`. Các route `/agent/v1` hiện có (`me`, `instruments`, `readings`, `order-queries`, `logs`, `status`) giữ nguyên hợp đồng; `status` chỉ được **thêm** trường tuỳ chọn.
- Hợp đồng `/api` chỉ đổi ở `/api/agents` đúng như Task 3 mô tả; route khác giữ nguyên. Lỗi `/api` vẫn là `{"statusCode", "message"}`; thân có trường lạ → 400.
- Bí mật: server chỉ lưu **SHA-256** của bí mật ghép nối và của khoá agent. Khoá agent chỉ đi qua mạng một lần (phản hồi của `/agent/v1/pair`). Mã ghép nối không bao giờ nằm trong URL, query string hay log của server; tệp cài tải bằng `fetch` kèm header `Authorization`, tên tệp do web đặt.
- Thời hạn mã ghép nối: **30 phút**, dùng một lần. Mọi so sánh thời gian dùng `TimeProvider` lấy từ DI (test thay được), không dùng `now()` của PostgreSQL.
- Tên dịch vụ Windows: `LIS Agent`. Thư mục cài: `%ProgramFiles%\LIS Agent\LisAgent.exe`. Thư mục dữ liệu giữ như hiện nay: `%ProgramData%\LIS Agent`.
- Biến môi trường `LISAGENT_Server__Url` + `LISAGENT_Server__AgentKey` (đủ cả hai) thắng cấu hình đã ghép nối — chỉ để chạy console lúc phát triển.
- Văn bản hiển thị, thông báo lỗi và chú thích code bằng tiếng Việt có dấu, theo phong cách sẵn có. Dữ liệu test là giả, tên chứa "Thử" hoặc "Mẫu".
- Commit cục bộ sau mỗi task, kết thúc thông điệp bằng hai dòng:
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` và `Claude-Session: https://claude.ai/code/session_01C1E12ztg3hpspNxZbcDmGA`. **Không push.**

## Phân công

- opencode hết token → mọi task giao subagent Claude theo superpowers:subagent-driven-development. Implementer mặc định **haiku**, task 2 và 6 dùng **sonnet** (bảo mật / điều phối nhiều bước); kẹt thì nâng một bậc. Reviewer **sonnet**; lượt rà soát cuối toàn nhánh **opus**.
- **Task 7 (giao diện) Claude tự làm** và kiểm bằng ảnh chụp thật trên Chrome (quy ước dự án).
- **Task 9** là việc của chủ dự án trên máy Windows thật; Claude chuẩn bị tệp và danh sách kiểm.

## Review Focus

1. Hai tiến trình dùng **cùng một mã** cùng lúc → đúng một bên nhận khoá, bên kia 410 "đã được dùng" (test ở task 2).
2. Mã hết hạn (30 phút 1 giây) hoặc đã dùng → agent báo câu tiếng Việt rõ ràng, **không cài nửa chừng**: không dịch vụ mới, bản cài cũ (nếu có) chạy lại như trước (test ở task 2 và task 6).
3. Ghép nối lại sang **phòng khám khác** → hàng chờ `cho-gui` cũ bị chuyển vào `cach-ly`, không bao giờ gửi lên phòng khám mới; cùng phòng khám thì giữ nguyên (test ở task 5).
4. "Ghép nối lại" một agent đang chạy → khoá cũ vẫn dùng được cho tới khi mã mới được đổi, sau đó khoá cũ 401 (test ở task 2).
5. Tên tệp tải về bị trình duyệt thêm `" (1)"` (Chrome/Edge) hoặc `"(1)"` (Firefox) vẫn đọc được mã; tệp bị đổi tên hẳn thì hỏi dán mã (test ở task 2 và task 6).

## Cấu trúc tệp

```
src/Lis.Core/Agents/PairingCode.cs            mã ghép nối: sinh, tìm trong chuỗi bất kỳ (tên tệp / văn bản dán)
src/Lis.Server/MasterMigrations/0001_khoi_tao.sql      lis_master hiện tại (IF NOT EXISTS — chạy được trên master cũ)
src/Lis.Server/MasterMigrations/0002_ghep_noi_agent.sql agent_pairing + cột mới của agent
src/Lis.Server/Tenancy/MasterMigrator.cs      DbUp cho lis_master
src/Lis.Server/Tenancy/MasterStore.cs         (sửa) tạo agent tự sinh mã máy trạm, mã ghép nối, đổi mã lấy khoá
src/Lis.Server/AgentApi/AgentEndpoints.cs     (sửa) POST /agent/v1/pair, status nhận thêm phiên bản + tên máy
src/Lis.Server/Api/AgentsEndpoints.cs         (sửa) /api/agents theo luồng ghép nối, tải tệp cài
src/Lis.Server/AgentDistribution.cs           đọc LisAgent.exe + version.txt trong AGENT_DIST_DIR, SHA-256
src/Lis.Agent/Program.cs                      (sửa) chọn chế độ: dịch vụ | run | pair | install | uninstall | bấm-chạy
src/Lis.Agent/AgentCommand.cs                 đọc tham số dòng lệnh
src/Lis.Agent/AgentVersion.cs                 phiên bản agent (InformationalVersion)
src/Lis.Agent/Pairing/PairingStore.cs         pairing.json (khoá đã mã hoá), cách ly hàng chờ khi đổi phòng khám
src/Lis.Agent/Pairing/PairingClient.cs        gọi /agent/v1/pair
src/Lis.Agent/Pairing/AgentConnection.cs      chọn URL + khoá: biến môi trường thắng tệp ghép nối
src/Lis.Agent/Setup/IServiceControl.cs        interface dịch vụ Windows + ScServiceControl (sc.exe)
src/Lis.Agent/Setup/AgentInstaller.cs         cài / ghép nối lại / gỡ, hoàn tác khi lỗi
src/Lis.Agent/Setup/SetupConsole.cs           màn hình console bấm-chạy (tiếng Việt), xin UAC
tests/Lis.Agent.Tests/                        dự án test mới (không cần Docker)
web/src/modules/agent/AgentsPage.tsx          (sửa) thêm agent → hộp thoại ghép nối
web/src/modules/agent/PairingDialog.tsx       tải tệp cài, mã để chép, theo dõi tới khi agent kết nối
scripts/publish-agent.sh                      publish LisAgent.exe win-x64 tự chứa + version.txt
```

---

### Task 1: lis_master chạy bằng migration đánh số

**Files:**
- Create: `src/Lis.Server/MasterMigrations/0001_khoi_tao.sql`, `src/Lis.Server/MasterMigrations/0002_ghep_noi_agent.sql`, `src/Lis.Server/Tenancy/MasterMigrator.cs`
- Modify: `src/Lis.Server/Lis.Server.csproj` (`<EmbeddedResource Include="MasterMigrations\*.sql" />`), `src/Lis.Server/Tenancy/MasterStore.cs` (`EnsureSchemaAsync`)
- Test: `tests/Lis.Server.Tests/MasterMigrationTests.cs`

**Interfaces:**
- Produces: `MasterMigrator.Upgrade(string connectionString) → Lis.Data.MigrationResult`; `MasterStore.EnsureSchemaAsync()` giữ tên, giờ = tạo CSDL nếu thiếu + `MasterMigrator.Upgrade`, lỗi → `InvalidOperationException("Nâng cấp CSDL lis_master lỗi: <lỗi>")`.
- Schema sau 0002 (task 2 dùng): `agent.key_hash` cho phép NULL; `agent` thêm `paired_at timestamptz NULL`, `machine_name text NULL`, `agent_version text NULL`; bảng `agent_pairing (id uuid PK, agent_id uuid NOT NULL REFERENCES agent(id) ON DELETE CASCADE, secret_hash text NOT NULL UNIQUE, expires_at timestamptz NOT NULL, used_at timestamptz NULL, created_by uuid NULL, created_at timestamptz NOT NULL DEFAULT now())` + index `agent_id`. 0002 đặt `paired_at = created_at` cho agent cũ đã có khoá.

- [ ] **Step 1: Viết test đỏ** — trong `MasterMigrationTests` (dùng `ServerFixture`, mỗi test một CSDL master tên riêng `lis_master_mig_<guid N>` tạo từ `fixture.MasterConnection`):
  - `Master_trang_chay_du_migration_va_co_bang_agent_pairing`: `EnsureSchemaAsync()` trên tên CSDL chưa có → CSDL được tạo; `schemaversions` có đúng 2 dòng; `information_schema.columns` của `agent` có `paired_at`, `machine_name`, `agent_version`; `agent.key_hash` `is_nullable = 'YES'`; bảng `agent_pairing` tồn tại.
  - `Master_cu_tao_bang_CREATE_IF_NOT_EXISTS_nang_cap_duoc_va_giu_du_lieu`: tạo CSDL, chạy đúng khối SQL cũ (chép từ `EnsureSchemaAsync` hiện tại vào hằng trong test), chèn 1 clinic + 1 agent có `key_hash`; rồi `EnsureSchemaAsync()` → clinic và agent còn nguyên, agent có `paired_at` khác NULL.
  - `Chay_hai_lan_khong_ap_them_gi`: gọi hai lần → lần hai `MasterMigrator.Upgrade` trả `Applied` rỗng.
- [ ] **Step 2: Chạy, xác nhận đỏ** — `dotnet test --project tests/Lis.Server.Tests --filter-class "*.MasterMigrationTests"` → không biên dịch / FAIL.
- [ ] **Step 3: Viết migration + `MasterMigrator`** — DbUp như `Lis.Data.Migrator` (`WithTransactionPerScript`, `LogToNowhere`), script nhúng trong assembly `Lis.Server` lọc theo tên chứa `.MasterMigrations.`. `0001` là nguyên văn ba `CREATE TABLE IF NOT EXISTS` hiện tại. `EnsureSchemaAsync` bỏ khối SQL nội tuyến.
- [ ] **Step 4: Chạy lại test lớp này + toàn bộ `tests/Lis.Server.Tests`** → xanh (các test cũ gọi `EnsureSchemaAsync` không đổi).
- [ ] **Step 5: Commit** — `feat(server): lis_master chạy bằng migration đánh số, thêm bảng agent_pairing`.

### Task 2: Mã ghép nối và đổi mã lấy khoá (`/agent/v1/pair`)

**Files:**
- Create: `src/Lis.Core/Agents/PairingCode.cs`, `tests/Lis.Core.Tests/Agents/PairingCodeTests.cs`, `tests/Lis.Server.Tests/PairingTests.cs`
- Modify: `src/Lis.Server/Tenancy/MasterStore.cs`, `src/Lis.Server/AgentApi/AgentEndpoints.cs`, `tests/Lis.Server.Tests/ApiHost.cs` (helper)

**Interfaces:**
- Consumes: schema của task 1.
- Produces (task 3, 5 dùng):
  - `Lis.Core.Agents.PairingCode(string ServerUrl, string ClinicCode, string Secret)`:
    - `const string Prefix = "LIS1."`; `string Encode()` = `Prefix + base64url-không-padding(UTF-8 của ServerUrl + "\n" + ClinicCode + "\n" + Secret)`. Constructor chuẩn hoá `ServerUrl` (bỏ `/` cuối) và ném `ArgumentException` nếu không phải URL tuyệt đối `http`/`https` không có query/fragment, `ClinicCode` rỗng, hoặc `Secret` không đúng 32 ký tự base64url.
    - `static bool TryFind(string? text, [NotNullWhen(true)] out PairingCode? code)`: tìm `LIS1\.[A-Za-z0-9_-]+` ở bất kỳ đâu trong chuỗi (tên tệp, văn bản dán có khoảng trắng / xuống dòng), giải mã và kiểm như constructor; sai → false.
    - `static string NewSecret()` = 24 byte ngẫu nhiên → base64url (32 ký tự).
  - `MasterStore`:
    - `static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(30)`.
    - `record PairingTicket(string Secret, DateTimeOffset ExpiresAt)`.
    - `Task<(AgentInfo Agent, PairingTicket Pairing)> CreateAgentAsync(Guid clinicId, string name, Guid? createdBy, DateTimeOffset now)` — mã máy trạm tự sinh = `agentId.ToString("N")`, `key_hash` NULL.
    - `Task<PairingTicket> CreatePairingAsync(Guid clinicId, Guid agentId, Guid? createdBy, DateTimeOffset now)` — xoá mã chưa dùng của agent đó trước khi tạo (mỗi agent tối đa một mã còn hiệu lực); agent không thuộc phòng khám → `AgentException(NotFound)`.
    - `enum PairingFailure { Unknown, Used, Expired }`; `record PairingRedemption(AgentIdentity? Identity, string? AgentKey, PairingFailure? Failure)`.
    - `Task<Clinic?> FindPairingClinicAsync(string secret)` — để kiểm phòng khám nâng cấp lỗi **trước** khi tiêu mã.
    - `Task<PairingRedemption> RedeemPairingAsync(string secret, string? machineName, string? agentVersion, DateTimeOffset now)` — trong một transaction: `UPDATE agent_pairing SET used_at = @now WHERE secret_hash = @hash AND used_at IS NULL AND expires_at > @now RETURNING agent_id`; không có dòng → đọc lại để phân biệt `Unknown` / `Used` / `Expired`; có → sinh khoá mới (`NewKey`), `UPDATE agent SET key_hash, paired_at = @now, machine_name, agent_version`.
    - `TouchAgentAsync(Guid agentId, string? statusJson, string? machineName = null, string? agentVersion = null)` — cột nào null thì giữ giá trị cũ.
    - `AgentWithStatus` thêm `DateTimeOffset? PairedAt, string? MachineName, string? AgentVersion, DateTimeOffset? PairingExpiresAt` (mã chưa dùng, chưa hết hạn so với `now` truyền vào: `AgentsOfAsync(Guid clinicId, DateTimeOffset now)`). Sửa mọi chỗ gọi (`AgentsEndpoints`, `InstrumentsEndpoints`) truyền `TimeProvider.GetUtcNow()` lấy từ DI.
    - Giữ nguyên `RegenerateAgentKeyAsync` và route `/key` ở task này (task 3 bỏ cả hai). Giữ `AddAgentAsync(clinicId, name, workstationCode)` cho CLI và test.
    - `AuthenticateAsync` từ chối rõ ràng agent có `key_hash` NULL.
  - `POST /agent/v1/pair` (map ngoài nhóm có bộ lọc khoá): thân `PairRequest(string? PairingSecret, string? MachineName, string? AgentVersion)` → 200 `PairResponse(Guid AgentId, string AgentKey, Guid ClinicId, string ClinicCode, string ClinicName, string AgentName)`. Lỗi dạng `Results.Problem(detail)` như các route `/agent/v1` khác:
    - thiếu / sai dạng bí mật → 400 "Mã ghép nối không hợp lệ"
    - `Unknown` → 401 "Mã ghép nối không đúng — lấy mã mới ở mục Agent trên web"
    - `Used` → 410 "Mã ghép nối đã được dùng — bấm Ghép nối lại trên web để lấy mã mới"
    - `Expired` → 410 "Mã ghép nối đã hết hạn (chỉ dùng trong 30 phút) — bấm Ghép nối lại trên web để lấy mã mới"
    - phòng khám nâng cấp lỗi → 503 `ClinicUpgradeFailure.Message`, **mã chưa bị tiêu**.
    - `machineName` cắt 100 ký tự, `agentVersion` cắt 50.
  - `StatusRequest` thêm `string? AgentVersion = null, string? MachineName = null`, truyền vào `TouchAgentAsync`.

- [ ] **Step 1: Test đỏ cho `PairingCode`** (`PairingCodeTests`):
  - `Encode_roi_TryFind_ra_dung_ba_truong` với `new PairingCode("https://lis.thu.vn/", "pkthu", PairingCode.NewSecret())` → `ServerUrl == "https://lis.thu.vn"`, hai trường còn lại bằng nhau.
  - Theory `TryFind_doc_duoc_trong_ten_tep_trinh_duyet_doi`: `$"LisAgent_{code}.exe"`, `$"LisAgent_{code} (1).exe"`, `$"LisAgent_{code}(2).exe"`, `$"  {code}\r\n"` → true, đúng mã.
  - Theory `TryFind_tra_false`: `"LisAgent.exe"`, `null`, `"LIS1.!!!"`, mã bị cắt mất 5 ký tự cuối, payload chỉ 2 dòng, URL `ftp://x`, bí mật 31 ký tự → false.
  - `Constructor_tu_choi_url_tuong_doi_va_query`: `"lis.thu.vn"`, `"https://a.vn/?x=1"` → `ArgumentException`.
  - `Ma_cho_url_40_ky_tu_ngan_hon_160_ky_tu` (để tên tệp không quá dài).
- [ ] **Step 2: Test đỏ cho server** (`PairingTests`, `ApiHost` có `FakeTimeProvider`; helper mới trong `ApiHost`: `CreateAgentWithPairingAsync(Clinic, string name)` gọi `MasterStore.CreateAgentAsync` với đồng hồ của host):
  - `Doi_ma_lay_khoa_roi_khoa_goi_duoc_me`: tạo agent → `POST /agent/v1/pair` với bí mật → 200, `ClinicCode`/`AgentName` đúng; dùng `AgentKey` gọi `/agent/v1/me` qua `ServerClient` của agent → 200 và `WorkstationCode == agentId.ToString("N")`; `master.AgentsOfAsync` có `PairedAt`, `MachineName`, `AgentVersion` đã gửi.
  - `Ma_dung_lan_hai_thi_410_da_duoc_dung`.
  - `Hai_yeu_cau_cung_ma_cung_luc_chi_mot_ben_nhan_khoa`: 8 request song song → đúng 1 cái 200, 7 cái 410.
  - `Ma_qua_30_phut_thi_410_het_han`: tiến đồng hồ `30 phút + 1 giây` → 410 "hết hạn"; ở `29 phút 59 giây` (agent khác) → 200.
  - `Ma_sai_thi_401_va_than_thieu_thi_400`.
  - `Ghep_noi_lai_khoa_cu_van_chay_toi_khi_ma_moi_duoc_doi`: agent đã ghép nối (khoá K1) → `CreatePairingAsync` → K1 vẫn gọi `/me` được → đổi mã mới (K2) → K1 401, K2 200; mã cũ chưa dùng tạo trước đó → 401 (đã bị xoá).
  - `Phong_kham_nang_cap_loi_thi_503_va_ma_chua_bi_tieu`: dùng `RegisterUnmigratableClinicAsync` + tạo agent/mã trực tiếp qua `MasterStore` → 503; sau đó đọc bảng `agent_pairing` thấy `used_at` NULL.
  - `Xoa_agent_thi_ma_cua_no_thanh_401`.
  - `Status_luu_phien_ban_va_ten_may`: `POST /agent/v1/status` với `agentVersion`, `machineName` → `AgentsOfAsync` thấy giá trị mới; gửi lại không có hai trường → giữ giá trị cũ.
- [ ] **Step 3: Chạy, xác nhận đỏ** — `dotnet test --project tests/Lis.Core.Tests --filter-class "*.PairingCodeTests"` và `dotnet test --project tests/Lis.Server.Tests --filter-class "*.PairingTests"`.
- [ ] **Step 4: Cài đặt** theo khối Interfaces. Băm bí mật ghép nối bằng cùng hàm `Hash` của khoá agent. Không ghi bí mật / khoá vào log.
- [ ] **Step 5: Chạy lại** hai lớp test trên, rồi toàn bộ `tests/Lis.Server.Tests` → xanh (`AgentApiTests`, `Api/AgentsEndpointsTests`, `Api/InstrumentsEndpointsTests` không đổi hành vi).
- [ ] **Step 6: Commit** — `feat: mã ghép nối agent dùng một lần (30 phút) và POST /agent/v1/pair`.

### Task 3: `/api/agents` theo luồng ghép nối

**Files:**
- Modify: `src/Lis.Server/Api/AgentsEndpoints.cs`, `src/Lis.Server/ServerOptions.cs`, `src/Lis.Server/AdminCommands.cs`, `src/Lis.Server/Program.cs` (chú thích lệnh), `tests/Lis.Server.Tests/Api/AgentsEndpointsTests.cs`, `tests/Lis.Server.Tests/Api/InstrumentsEndpointsTests.cs` (nếu dùng `/key`)

**Interfaces:**
- Consumes: `MasterStore.CreateAgentAsync / CreatePairingAsync / AgentsOfAsync(clinicId, now)`, `PairingCode` (task 2).
- Produces (web — task 7 dùng):
  - `ServerOptions` thêm `string? PublicUrl` (`PublicUrl` hoặc `PUBLIC_URL`; đặt mà không phải URL tuyệt đối http/https không query → `InvalidOperationException` lúc khởi động, câu tiếng Việt) và `string? AgentDistDir` (`Agent:DistDir` hoặc `AGENT_DIST_DIR`; task 4 dùng).
  - `POST /api/agents` thân `{ name }` → 201 `{ id, pairing: { code, expiresAt } }`. `code = new PairingCode(serverUrl, clinic.Code, ticket.Secret).Encode()`; `serverUrl` = `PublicUrl` nếu đặt, không thì `{Request.Scheme}://{Request.Host}{Request.PathBase}` (đã qua forwarded headers nếu bật). Thân có `workstationCode` → 400 (trường lạ).
  - `POST /api/agents/{id}/pairing` → 201 `{ code, expiresAt }`. Thay cho `POST /{id}/key` (**bỏ** route này cùng `MasterStore.RegenerateAgentKeyAsync`).
  - `PATCH /api/agents/{id}` thân `{ name }` → 204. Bỏ đoạn "máy đi theo mã mới".
  - `GET /api/agents` mỗi phần tử thêm `paired: bool`, `pairedAt: string|null`, `machineName: string|null`, `version: string|null`, `pairingExpiresAt: string|null` (ISO như `ApiFormat.Iso`); giữ `workstationCode` (form máy dùng làm giá trị chọn).
  - Quyền giữ như cũ: xem = Quản trị / Kỹ thuật viên / Trưởng khoa; ghi = Quản trị. Câu lỗi tên agent giữ ("Nhập tên agent", "Dữ liệu gửi lên không hợp lệ").
  - CLI: `agent add --clinic --name [--workstation]` — thiếu `--workstation` thì tự sinh như web; câu in ra hướng dẫn dùng khoá cho `LISAGENT_Server__Url` / `LISAGENT_Server__AgentKey` (cứu hộ / phát triển), không nhắc `appsettings.json`.

- [ ] **Step 1: Sửa / thêm test đỏ** trong `AgentsEndpointsTests`:
  - `Tao_agent_tra_ma_ghep_noi_doi_duoc_lay_khoa` (thay `Khoa_tra_ve_tu_POST_goi_duoc_agent_v1_me_va_luu_dung_ten_ma`): POST `{name:"Agent Thử"}` → 201; `PairingCode.TryFind(code)` true; `ServerUrl` bằng địa chỉ server thử (`http://localhost`); `ClinicCode` đúng; `expiresAt` = giờ server thử + 30 phút; đổi bí mật qua `/agent/v1/pair` → 200.
  - `PublicUrl_dat_thi_ma_dung_dia_chi_do`: host với setting `PublicUrl=https://lis.thu.vn` → `ServerUrl == "https://lis.thu.vn"`.
  - `PublicUrl_sai_thi_server_khong_khoi_dong`: `ServerOptions.From` với `PublicUrl=lis.thu.vn` → `InvalidOperationException`.
  - `Tao_agent_than_co_workstationCode_thi_400` và giữ các ca kiểm tên cũ.
  - `Ghep_noi_lai_201_ma_moi_va_agent_phong_kham_khac_404`.
  - `Danh_sach_co_trang_thai_ghep_noi`: agent vừa tạo → `paired=false`, `pairingExpiresAt` khác null; sau khi đổi mã → `paired=true`, `pairedAt`, `machineName`, `version` có giá trị, `pairingExpiresAt=null`.
  - `Sua_agent_chi_doi_ten` (thay test sửa mã máy trạm); bỏ `Cap_khoa_moi_201_...`; `POST /{id}/key` → 404/405.
  - Giữ nguyên các test log, xoá, quyền, cô lập phòng khám (sửa chỗ tạo agent nếu cần).
- [ ] **Step 2: Đỏ** — `dotnet test --project tests/Lis.Server.Tests --filter-class "*.AgentsEndpointsTests"`.
- [ ] **Step 3: Cài đặt** theo khối Interfaces; lấy `createdBy` từ `RequestContext.User`.
- [ ] **Step 4: Xanh** — lớp trên + toàn bộ `tests/Lis.Server.Tests`.
- [ ] **Step 5: Commit** — `feat(server): /api/agents tạo agent bằng mã ghép nối, bỏ mã máy trạm và cấp khoá tay`.

### Task 4: Server phát tệp cài agent

**Files:**
- Create: `src/Lis.Server/AgentDistribution.cs`, `tests/Lis.Server.Tests/Api/AgentInstallerEndpointsTests.cs`
- Modify: `src/Lis.Server/Api/AgentsEndpoints.cs`, `src/Lis.Server/Program.cs` (đăng ký singleton)

**Interfaces:**
- Consumes: `ServerOptions.AgentDistDir` (task 3).
- Produces (web — task 7):
  - `AgentDistribution(string? distDir)`: `record AgentPackage(string Path, string? Version, string Sha256, long Size)`; `AgentPackage? Current()` — `null` khi chưa cấu hình hoặc thiếu `LisAgent.exe`; `Version` = nội dung `version.txt` (trim) nếu có; SHA-256 hex chữ thường, tính lại chỉ khi kích thước hoặc thời điểm sửa tệp đổi.
  - `GET /api/agents/installer/info` (Quản trị) → `{ available, version, sha256, size }` (`available=false` thì ba trường kia null).
  - `GET /api/agents/installer` (Quản trị) → tệp, `Content-Type: application/vnd.microsoft.portable-executable`, `Content-Disposition: attachment; filename="LisAgent.exe"`, header `X-Agent-Version`, `X-Agent-Sha256`; chưa có tệp → 404 `{"statusCode":404,"message":"Máy chủ chưa có tệp cài agent — xem mục AGENT_DIST_DIR trong hướng dẫn cài đặt"}`.

- [ ] **Step 1: Test đỏ** (`AgentInstallerEndpointsTests`, thư mục tạm chứa `LisAgent.exe` = vài byte `"MZ-thu"` và `version.txt` = `"0.2.0\n"`):
  - `Info_tra_phien_ban_sha256_va_kich_thuoc` → khớp SHA-256 tự tính.
  - `Tai_tep_dung_noi_dung_va_header`.
  - `Chua_cau_hinh_thi_info_available_false_va_tai_404_json`.
  - `Ky_thuat_vien_403_chua_dang_nhap_401`.
  - `Doi_tep_thi_sha256_doi_theo` (ghi đè nội dung khác độ dài → info mới).
- [ ] **Step 2: Đỏ** — `dotnet test --project tests/Lis.Server.Tests --filter-class "*.AgentInstallerEndpointsTests"`.
- [ ] **Step 3: Cài đặt** (`Results.File` với đường dẫn vật lý, `enableRangeProcessing: false`). Route `/installer` không va với `/{id}` vì chỉ có `PATCH/DELETE /{id}`.
- [ ] **Step 4: Xanh** — lớp trên + toàn bộ `tests/Lis.Server.Tests`.
- [ ] **Step 5: Commit** — `feat(server): phát tệp cài agent từ AGENT_DIST_DIR kèm phiên bản và SHA-256`.

### Task 5: Agent ghép nối được và chạy bằng cấu hình đã ghép nối

**Files:**
- Create: `src/Lis.Agent/AgentCommand.cs`, `src/Lis.Agent/AgentVersion.cs`, `src/Lis.Agent/Pairing/PairingStore.cs`, `src/Lis.Agent/Pairing/PairingClient.cs`, `src/Lis.Agent/Pairing/AgentConnection.cs`, `tests/Lis.Agent.Tests/Lis.Agent.Tests.csproj` (+ thêm vào `LisConnect.slnx`), `tests/Lis.Agent.Tests/{AgentCommandTests,PairingStoreTests,AgentConnectionTests}.cs`, `tests/Lis.Server.Tests/AgentPairingEndToEndTests.cs`
- Modify: `src/Lis.Agent/Program.cs`, `src/Lis.Agent/AgentWorker.cs` (`AgentPaths` thêm đường dẫn; gửi phiên bản + tên máy trong status), `src/Lis.Agent/ServerClient.cs`, `src/Lis.Agent/Lis.Agent.csproj` (`<Version>0.2.0</Version>`, `InternalsVisibleTo Lis.Agent.Tests`), `src/Lis.Agent/appsettings.json` (bỏ mục `Server`)

**Interfaces:**
- Consumes: `PairingCode` (task 2), `POST /agent/v1/pair` (task 2), `Lis.Data.Local.ISecretProtector` / `SecretProtectors.ForCurrentOs(dir)` (sẵn có).
- Produces (task 6 dùng):
  - `AgentPaths` thêm `Pairing` = `<Root>/pairing.json`, `Quarantine` = `<Root>/cach-ly`.
  - `AgentCommand` — `abstract record` với các ca `Service`, `Run`, `Pair(string? CodeText)`, `Install(string? CodeText)`, `Uninstall`, `Interactive`; `static AgentCommand Parse(string[] args, bool isWindowsService, bool isWindows)`: dịch vụ → `Service`; `run` → `Run`; `pair <mã>` → `Pair`; `install [--code <mã>]` → `Install`; `uninstall` → `Uninstall`; không tham số → `Interactive` trên Windows, `Run` nơi khác; tham số lạ → `ArgumentException` với câu hướng dẫn tiếng Việt.
  - `AgentVersion.Current` → chuỗi `InformationalVersion` (bỏ phần `+<hash>`).
  - `record PairingState(string ServerUrl, Guid ClinicId, string ClinicCode, string ClinicName, Guid AgentId, string AgentName, DateTimeOffset PairedAt)`.
  - `PairingStore(AgentPaths paths, ISecretProtector protector, TimeProvider clock)`:
    - `(PairingState State, string AgentKey)? Load()` — tệp hỏng / giải mã lỗi → null.
    - `void Save(PairingState state, string agentKey)` — ghi tệp tạm rồi đổi tên (không để tệp dở); nếu đã có ghép nối với `ClinicId` **khác** → chuyển `cho-gui` sang `cach-ly/<yyyyMMdd-HHmmss>-<mã phòng khám cũ>/cho-gui` trước khi ghi. Luôn xoá bộ đệm `agent.json`, `instruments.json` (của agent / phòng khám cũ).
    - `void Delete()`.
    - Trên Windows sau khi ghi: đặt ACL tệp chỉ `SYSTEM` + `Administrators` (hàm `[SupportedOSPlatform("windows")]`, gọi sau `OperatingSystem.IsWindows()`).
  - `PairingClient(HttpClient http)` — `Task<PairResponse> RedeemAsync(PairingCode code, string machineName, string agentVersion, CancellationToken ct)`; `BaseAddress` lấy từ `code.ServerUrl`; lỗi HTTP → `PairingException` mang `detail` tiếng Việt của server; lỗi mạng → `PairingException("Không tới được máy chủ <url>: <lỗi>")`.
  - `static class PairingFlow` — `Task<PairingState> PairAsync(PairingCode code, PairingClient client, PairingStore store, TimeProvider clock, CancellationToken ct)`: đổi mã → `store.Save` → trả state. URL là `http://` → trả kèm cảnh báo (task 6 in ra; `pair` in ra console).
  - `AgentConnection(string ServerUrl, string AgentKey, string Source)`; `static AgentConnection? Resolve(IConfiguration config, PairingStore store, out string? problem)`: đủ hai biến `Server:Url` + `Server:AgentKey` (từ `LISAGENT_`) → nguồn "biến môi trường"; không thì `store.Load()`; không có gì → null, `problem` = "Agent chưa được ghép nối — chạy tệp cài tải từ mục Agent trên web".
  - `ServerClient.SendStatusAsync` gửi thêm `agentVersion = AgentVersion.Current`, `machineName = Environment.MachineName`.
  - `Program`: `Service`/`Run` → `AgentConnection.Resolve` → thiếu thì ghi lỗi + thoát mã 2; `Pair` → `PairingFlow` rồi in "Đã ghép nối với phòng khám …"; `Install`/`Uninstall`/`Interactive` → task 6 (ở task này in "Chỉ hỗ trợ trên Windows" và thoát mã 2).

- [ ] **Step 1: Tạo `tests/Lis.Agent.Tests`** (giống `Lis.Core.Tests.csproj`, tham chiếu `Lis.Agent`) và test đỏ:
  - `AgentCommandTests`: bảng ca cho `Parse` (đủ 6 ca + tham số lạ + `pair` thiếu mã).
  - `PairingStoreTests` (thư mục tạm, `DevKeySecretProtector`): `Luu_roi_doc_lai_dung_va_tep_khong_chua_khoa_dang_ro` (đọc thô `pairing.json` không thấy chuỗi khoá); `Tep_hong_thi_Load_null`; `Doi_phong_kham_thi_cho_gui_cu_vao_cach_ly` (có tệp trong `cho-gui` → sau `Save` với `ClinicId` khác, `cho-gui` rỗng / không có, tệp nằm trong `cach-ly/<…>-<mã cũ>/cho-gui`); `Cung_phong_kham_thi_giu_cho_gui`; `Luon_xoa_bo_dem_agent_va_danh_sach_may`.
  - `AgentConnectionTests`: biến môi trường đủ hai → thắng tệp; thiếu một → dùng tệp; không có gì → null + câu `problem`.
- [ ] **Step 2: Test đầu-cuối đỏ** `AgentPairingEndToEndTests` (Lis.Server.Tests): tạo phòng khám + quản trị + máy gán cho agent; `POST /api/agents` → mã; `PairingFlow.PairAsync` với `PairingClient` dùng `HttpClient` của `WebApplicationFactory` (`BaseAddress` đặt theo server thử) → `PairingStore` trong thư mục tạm có state đúng phòng khám; `AgentConnection.Resolve` → dùng khoá gọi `/agent/v1/instruments` thấy đúng máy đã gán.
- [ ] **Step 3: Đỏ** — `dotnet test --project tests/Lis.Agent.Tests` và `dotnet test --project tests/Lis.Server.Tests --filter-class "*.AgentPairingEndToEndTests"`.
- [ ] **Step 4: Cài đặt** theo khối Interfaces.
- [ ] **Step 5: Xanh** — hai lệnh trên + `tests/Lis.Server.Tests` (`AgentApiTests` vẫn xanh) + build 0 warning.
- [ ] **Step 6: Thử tay trên macOS** — chạy server dev, tạo agent qua `curl` `/api/agents`, rồi `dotnet run --project src/Lis.Agent -- pair "<mã>"` → in "Đã ghép nối…"; `dotnet run --project src/Lis.Agent -- run` (biến `LISAGENT_DataDirectory` trỏ thư mục tạm) → `/api/agents` thấy `online=true`, `version="0.2.0"`. Ghi kết quả vào report.
- [ ] **Step 7: Commit** — `feat(agent): ghép nối bằng mã, lưu khoá đã mã hoá, cách ly hàng chờ khi đổi phòng khám`.

### Task 6: Tệp cài tự cài dịch vụ Windows (cài / ghép nối lại / gỡ)

**Files:**
- Create: `src/Lis.Agent/Setup/IServiceControl.cs` (interface + `ScServiceControl`), `src/Lis.Agent/Setup/IElevation.cs` (interface + `WindowsElevation`), `src/Lis.Agent/Setup/AgentInstaller.cs`, `src/Lis.Agent/Setup/SetupConsole.cs`, `tests/Lis.Agent.Tests/{AgentInstallerTests,SetupConsoleTests}.cs`
- Modify: `src/Lis.Agent/Program.cs`

**Interfaces:**
- Consumes: `PairingCode`, `PairingFlow`, `PairingStore`, `AgentPaths`, `AgentCommand` (task 2, 5).
- Produces:
  - `IServiceControl`: `bool Exists()`, `bool IsRunning()`, `void Create(string exePath)` (tự chạy khi bật máy; khôi phục khi lỗi: khởi động lại sau 5 s, 5 s, 30 s, đặt lại sau 1 ngày; bật cả khi thoát có mã lỗi; mô tả tiếng Việt), `void Start()`, `void Stop()`, `void Delete()`, `bool WaitRunning(TimeSpan timeout)`. `ScServiceControl` gọi `sc.exe` (`create … start= auto`, `failure … reset= 86400 actions= restart/5000/restart/5000/restart/30000`, `failureflag … 1`, `description`), lỗi → `InvalidOperationException` kèm đầu ra của `sc`.
  - `IElevation`: `bool IsElevated()`; `ElevationResult RelaunchElevated(string exePath, string arguments)` — `enum ElevationResult { Started, Cancelled }` (`Win32Exception` mã 1223 → `Cancelled`); tiến trình gốc chờ tiến trình nâng quyền thoát.
  - `AgentInstaller(AgentPaths paths, string installDir, IServiceControl service, Func<PairingCode, CancellationToken, Task<PairingState>> pair, TextWriter output)`:
    - `Task<InstallOutcome> InstallOrRepairAsync(string sourceExe, PairingCode code, CancellationToken ct)` — thứ tự cố định:
      1. ghi nhớ trạng thái cũ (dịch vụ có không, đang chạy không); dừng dịch vụ nếu đang chạy;
      2. nếu `sourceExe` khác tệp cài: sao `LisAgent.exe` cũ thành `LisAgent.exe.bak` rồi chép tệp mới vào `installDir`;
      3. dịch vụ chưa có → `Create`;
      4. `pair(code)` (đổi mã + lưu khoá);
      5. `Start` + `WaitRunning(30 s)`.
      Lỗi ở bước 1–4 → **hoàn tác** theo thứ tự ngược: xoá dịch vụ nếu bước 3 vừa tạo, trả `.bak` về, bật lại dịch vụ cũ nếu trước đó đang chạy; trả `InstallOutcome.Failed(câu lỗi)`. Lỗi ở bước 5 (mã đã tiêu, khoá đã lưu) → không hoàn tác, trả `Failed` kèm hướng dẫn xem log trong `%ProgramData%\LIS Agent` và "chạy lại tệp cài, chọn Ghép nối lại với mã mới". Thành công → xoá `.bak`, trả `Succeeded(state)`.
    - `InstallOutcome Uninstall()` — dừng + xoá dịch vụ, `PairingStore.Delete()`, xoá `installDir` (đang chạy từ chính thư mục đó thì hẹn xoá bằng `cmd /c timeout 3 & rmdir`), **giữ** thư mục dữ liệu và in đường dẫn của nó.
  - `SetupConsole(TextReader input, TextWriter output, IElevation elevation, AgentInstaller installer, PairingStore store, string exePath)` — `Task<int> RunAsync(AgentCommand command, CancellationToken ct)`:
    - `Interactive` chưa nâng quyền → `RelaunchElevated(exePath, "")`; `Cancelled` → in "Cần quyền quản trị Windows để cài agent. Nhờ người có tài khoản quản trị bấm chạy lại tệp này." → thoát mã 5, không đụng gì.
    - Đã nâng quyền, chưa cài (`!service.Exists() && store.Load() is null`) → lấy mã từ tên tệp (`PairingCode.TryFind(Path.GetFileName(exePath))`), không có thì hỏi "Dán mã ghép nối (lấy ở mục Agent trên web) rồi Enter:" (tối đa 3 lần, mỗi lần sai in lý do) → cài.
    - Đã cài → in "Agent đã cài trên máy này: phòng khám {ClinicName}, agent {AgentName}." và menu `1. Ghép nối lại  2. Gỡ agent  0. Thoát`.
    - Cuối mọi nhánh: in kết quả (thành công: "Xong. Agent đang chạy và đã kết nối tới {ServerUrl}. Có thể đóng cửa sổ này."), cảnh báo nếu URL là `http://`, rồi "Nhấn Enter để đóng".
    - `Install(code)` / `Uninstall` (từ dòng lệnh) chạy thẳng, không menu.
    - `Console.OutputEncoding` / `InputEncoding` = UTF-8 đặt ở `Program`, không đặt trong `SetupConsole` (để test dùng `StringReader`/`StringWriter`).

- [ ] **Step 1: Test đỏ `AgentInstallerTests`** (thư mục tạm làm `installDir` + `AgentPaths`; `FakeServiceControl` ghi lại chuỗi lệnh; `pair` giả trả state hoặc ném `PairingException`):
  - `Cai_moi_dung_thu_tu_va_tep_vao_thu_muc_cai`: chuỗi lệnh `[Exists, Create, Start, WaitRunning]`, `LisAgent.exe` có trong `installDir`, không còn `.bak`.
  - `Ma_het_han_thi_khong_con_dich_vu_moi_va_khong_con_tep`: `pair` ném `PairingException("…hết hạn…")` → dịch vụ vừa tạo bị `Delete`, `installDir` không có `LisAgent.exe`, outcome `Failed` chứa câu của server.
  - `Ghep_noi_lai_loi_thi_tra_tep_cu_va_bat_lai_dich_vu_cu`: đã có dịch vụ đang chạy + tệp cũ nội dung "cu" → `pair` lỗi → tệp nội dung "cu", lệnh cuối là `Start`, không có `Delete`.
  - `Chay_tu_chinh_thu_muc_cai_thi_khong_chep_de`.
  - `Dich_vu_khong_len_trong_30_giay_thi_khong_hoan_tac_va_bao_xem_log`.
  - `Go_xoa_dich_vu_va_khoa_giu_du_lieu`: sau `Uninstall` → `Delete` đã gọi, `pairing.json` không còn, `cho-gui` còn nguyên.
- [ ] **Step 2: Test đỏ `SetupConsoleTests`** (`FakeElevation`, `StringReader`/`StringWriter`):
  - `Huy_UAC_thi_bao_can_quyen_quan_tri_va_khong_cai` → mã 5, `FakeServiceControl` không nhận lệnh nào.
  - `Ten_tep_co_ma_thi_cai_khong_hoi_gi` (`exePath` = `…/LisAgent_<mã> (1).exe`, input rỗng).
  - `Ten_tep_khong_co_ma_thi_hoi_dan_ma_sai_hai_lan_dung_lan_ba`.
  - `Da_cai_thi_hien_menu_chon_2_la_go`.
  - `Url_http_thi_in_canh_bao`.
- [ ] **Step 3: Đỏ** — `dotnet test --project tests/Lis.Agent.Tests`.
- [ ] **Step 4: Cài đặt** + nối vào `Program` (`Interactive`/`Install`/`Uninstall` chỉ trên Windows; nơi khác in "Chỉ hỗ trợ trên Windows" mã 2). `ScServiceControl`, `WindowsElevation` và ACL tệp không có test tự động — để task 9.
- [ ] **Step 5: Xanh** — `dotnet test --project tests/Lis.Agent.Tests` + build 0 warning + `dotnet publish src/Lis.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o <thư mục tạm>` thành công (biên dịch chéo từ macOS).
- [ ] **Step 6: Commit** — `feat(agent): tệp cài tự xin quyền quản trị, cài dịch vụ, ghép nối lại, gỡ — hoàn tác khi lỗi`.

### Task 7: Trang Agent trên web theo luồng ghép nối (Claude tự làm)

**Files:**
- Create: `web/src/modules/agent/PairingDialog.tsx`, `web/src/modules/agent/pairing.ts` (+ `pairing.test.ts`), `web/src/modules/agent/AgentsPage.test.tsx`
- Modify: `web/src/modules/agent/AgentsPage.tsx`, `web/src/services/index.ts`, `web/src/services/http.ts` (tải tệp nhị phân kèm phiên), `web/src/shared/ui/StatusTag.tsx`, `web/src/modules/may/InstrumentModal.tsx`, `web/src/modules/may/InstrumentsPage.tsx`, `web/vite.config.ts`

**Interfaces:**
- Consumes: hợp đồng `/api/agents*` của task 3, 4.
- Produces:
  - `agentService.create({ name }) → { id, pairing: { code, expiresAt } }`; `agentService.pair(id) → { code, expiresAt }`; `agentService.update(id, { name })`; `agentService.installerInfo()`; `agentService.downloadInstaller() → Blob` (bỏ `regenerateKey`). Kiểu `Agent` thêm `paired, pairedAt, machineName, version, pairingExpiresAt`.
  - `pairing.ts`: `installerFileName(code: string) = \`LisAgent_${code}.exe\``; `pairingState(agent: Agent, now: Date): 'online' | 'offline' | 'never' | 'pairing' | 'unpaired'` (chưa ghép nối + còn mã → `pairing`; chưa ghép nối + hết mã → `unpaired`; đã ghép nối → như cũ theo `online` / `lastSeenAt`).
  - `StatusTag` domain `agent`: `online` "Đang kết nối", `offline` "Mất kết nối", `never` "Đã ghép nối, chưa chạy", `pairing` "Chờ ghép nối", `unpaired` "Chưa ghép nối".
  - `vite.config.ts`: thêm proxy `'/agent/v1'` (giữ nguyên `/agent` là trang web) để agent chạy dev qua cổng Vite được.

- [ ] **Step 1: Test đỏ** — `pairing.test.ts`: `installerFileName`, đủ 5 nhánh `pairingState` (mã hết hạn đúng 1 ms trước `now` → `unpaired`). `AgentsPage.test.tsx` (mock `agentService`): bấm "Thêm agent" → form chỉ có ô "Tên agent" (không có "Mã máy trạm") → Lưu → hộp thoại hiện mã và nút "Tải tệp cài"; khi `list` trả agent `online=true` → hộp thoại hiện "Agent đã kết nối".
- [ ] **Step 2: Đỏ** — `cd web && npx vitest run src/modules/agent`.
- [ ] **Step 3: Làm giao diện**:
  - Bảng agent: bỏ cột "Mã máy trạm"; cột "Máy tính" (`machineName` · `version`); trạng thái theo `pairingState`; nút "Ghép nối lại" thay "Khoá mới" (xác nhận: "Khoá đang dùng vẫn chạy cho tới khi mã mới được dùng").
  - Form thêm/sửa: chỉ "Tên agent".
  - `PairingDialog` (mở sau khi tạo hoặc ghép nối lại; không đóng bằng click nền): bước 1 nút "Tải tệp cài" (`downloadInstaller` → Blob → `<a download={installerFileName(code)}>`; hiện phiên bản; server chưa có tệp → Alert hướng dẫn `AGENT_DIST_DIR`); bước 2 hướng dẫn chạy tệp trên máy tính cạnh máy xét nghiệm, câu SmartScreen "Windows protected your PC → More info → Run anyway", "Cho phép" ở hộp thoại quyền quản trị; ô mã chỉ đọc + nút "Chép mã" (cho trường hợp chép tệp qua USB rồi bị đổi tên); "Mã hết hạn lúc HH:mm"; trạng thái sống: hỏi `/api/agents` mỗi 2 giây khi hộp thoại mở → "Đang chờ agent…" / "Agent đã kết nối ✓ {machineName}" / "Mã đã hết hạn — bấm Tạo mã mới".
  - `InstrumentModal`: nhãn lựa chọn agent chỉ là tên; `InstrumentsPage`: bỏ "(mã máy trạm)".
- [ ] **Step 4: Xanh** — `npx vitest run`, `npm run build`, `npx tsc --noEmit` (nếu có script `typecheck` thì dùng nó).
- [ ] **Step 5: Kiểm bằng ảnh chụp thật** — server dev + `npm run dev`, Chrome: tạo agent → hộp thoại; tải tệp (dùng `dist/agent` của task 8 nếu đã có, không thì thư mục tệp giả) → tên tệp đúng `LisAgent_LIS1.….exe`; chạy agent console bằng `pair` + `run` → hộp thoại chuyển "Agent đã kết nối" trong ≤ 3 giây; bảng hiện "Đang kết nối", máy tính, phiên bản. Chụp ảnh từng trạng thái, sửa tới khi đúng.
- [ ] **Step 6: Commit** — `feat(web): thêm agent bằng mã ghép nối, tải tệp cài, theo dõi tới khi agent kết nối`.

### Task 8: Đóng gói tệp cài và tài liệu

**Files:**
- Create: `scripts/publish-agent.sh`
- Modify: `Dockerfile`, `docker-compose.yml`, `.env.example`, `.gitignore` (`dist/`), `docs/server-agent.md`, `docs/HUONG-DAN-CAI-DAT.md`, `README.md`

**Interfaces:**
- Consumes: `<Version>` của `Lis.Agent.csproj` (task 5), `AGENT_DIST_DIR` / `PUBLIC_URL` (task 3, 4).
- Produces: `scripts/publish-agent.sh [thư-mục-ra]` (mặc định `dist/agent`) → `LisAgent.exe` (win-x64, tự chứa, một tệp, nén) + `version.txt` (lấy bằng `dotnet msbuild src/Lis.Agent -getProperty:Version`); image Docker có `/app/agent/LisAgent.exe` và `ENV AGENT_DIST_DIR=/app/agent`.

- [ ] **Step 1: Script + Dockerfile** — Dockerfile thêm stage `agent` (SDK) chép `src/Lis.Core`, `src/Lis.Data`, `src/Lis.Connections`, `src/Lis.Agent` rồi publish win-x64 như script; stage chạy chép vào `/app/agent`. `.env.example` + `docker-compose.yml` thêm `PUBLIC_URL` (chú thích: địa chỉ công khai agent dùng để nối về, vd `https://lis.phongkham.vn`; để trống thì lấy theo địa chỉ trình duyệt đang mở web).
- [ ] **Step 2: Kiểm** — `bash scripts/publish-agent.sh /tmp/…` ra `LisAgent.exe` (in kích thước) + `version.txt` = `0.2.0`; `docker build -t lis-server-thu .` thành công và `docker run --rm --entrypoint ls lis-server-thu /app/agent` thấy hai tệp.
- [ ] **Step 3: Tài liệu** — `docs/server-agent.md`: mục Agent viết lại theo luồng ghép nối (bảng cấu hình thêm `PUBLIC_URL`, `AGENT_DIST_DIR`; chế độ dòng lệnh `run | pair | install | uninstall`; biến `LISAGENT_*` chỉ cho phát triển; `agent add` là đường cứu hộ). `docs/HUONG-DAN-CAI-DAT.md`: mục "Cài agent" từng bước cho nhân viên (Thêm agent → Tải tệp cài → chép sang máy tính cạnh máy xét nghiệm nếu cần → bấm chạy → SmartScreen "More info → Run anyway" → "Yes" ở hộp thoại quyền quản trị → chờ chữ "Xong"), chỗ đặt ảnh chụp để trống kèm ghi chú "ảnh bổ sung sau lần thử Windows thật (task 9)"; mục gỡ / ghép nối lại; lưu ý HTTPS khi ra khỏi mạng nội bộ. Xoá mọi hướng dẫn sửa `appsettings.json` của agent.
- [ ] **Step 4: Commit** — `build: đóng gói LisAgent.exe vào image server; docs: cài agent bằng mã ghép nối`.

### Task 9: Thử trên Windows thật (chủ dự án làm, Claude chuẩn bị)

Claude chuẩn bị: `dist/agent/LisAgent.exe` mới nhất, server chạy trên máy phát triển có địa chỉ LAN mà máy Windows tới được (`PUBLIC_URL=http://<ip-lan>:5080`), và danh sách dưới. Chủ dự án làm trên một máy Windows 10/11 sạch, báo lại từng dòng đạt / không đạt (kèm ảnh nếu lỗi):

1. Web → Agent → Thêm agent → Tải tệp cài: tên tệp có `LIS1.`; SmartScreen hiện → "More info → Run anyway" chạy được (chụp ảnh hai bước này cho tài liệu).
2. Hộp thoại quyền quản trị hiện; bấm "No" → cửa sổ báo cần quyền quản trị, `sc query "LIS Agent"` không có dịch vụ.
3. Chạy lại, bấm "Yes" → không phải gõ gì; cửa sổ báo "Xong"; web chuyển "Đang kết nối" trong vòng ~3 giây.
4. `services.msc`: "LIS Agent" Automatic, tab Recovery là Restart service; khởi động lại máy → web lại "Đang kết nối" mà không ai đăng nhập.
5. `%ProgramData%\LIS Agent\pairing.json`: tài khoản thường không mở được; mở bằng quản trị không thấy khoá dạng rõ.
6. Tải tệp hai lần để thành `… (1).exe` → vẫn cài không hỏi mã. Đổi tên tệp thành `LisAgent.exe` → hỏi dán mã, dán mã chép từ web → cài được.
7. Đợi mã quá 30 phút rồi chạy → báo "đã hết hạn", không có dịch vụ mới; bản cài cũ (nếu có) vẫn chạy.
8. Chạy tệp lần hai → menu; "Ghép nối lại" với mã mới → web vẫn "Đang kết nối"; "Gỡ agent" → dịch vụ và thư mục `Program Files\LIS Agent` biến mất, `ProgramData\LIS Agent\cho-gui` còn.
9. Gán một máy xét nghiệm (hoặc bộ giả lập trên cổng COM ảo / TCP) cho agent → kết quả về web như trước.

Khi đủ 9 dòng đạt: Claude chèn ảnh SmartScreen vào `docs/HUONG-DAN-CAI-DAT.md`, ghi "Đã thử Windows thật <ngày>" vào ledger, commit `docs: ảnh cài agent trên Windows thật`. Phần B chỉ coi là xong sau bước này (spec mục 9).
