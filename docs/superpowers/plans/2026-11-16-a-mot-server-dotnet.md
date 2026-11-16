# Phần A — Một server .NET, bỏ NestJS — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `LisServer` (.NET) phục vụ toàn bộ `/api` mà NestJS đang có, trang web tĩnh, và tạo phòng khám trên web — để triển khai chỉ cần PostgreSQL + một server; xóa thư mục `server/`.

**Architecture:** Thêm nhóm route `/api` vào `src/Lis.Server` (minimal API, cùng tiến trình với `/agent/v1`). Mỗi request `/api` qua một bộ lọc phiên: đọc JWT → phòng khám → `TenantServices` → tài khoản. Quy tắc nghiệp vụ lấy từ `Lis.Data` / `Lis.Core`; chỉ các câu truy vấn đọc riêng cho web mới chuyển SQL từ TypeScript sang Dapper.

**Tech Stack:** .NET 10 / C# 14, ASP.NET Core minimal API, Dapper + Npgsql, `Microsoft.IdentityModel.JsonWebTokens` (HS256), xunit v3 + Shouldly + Testcontainers (`postgres:18-alpine`), React/Vite (chỉ đổi proxy + thêm trang thiết lập).

**Spec:** `docs/superpowers/specs/2026-11-16-mot-server-agent-dieu-khien-tu-web-design.md` — mục 5.A, 7, 9.

**Các kế hoạch sau (viết khi A xong):** B ghép nối + tự cài agent · C kênh lệnh · D giải mã ở server · E tiếp nhận tối giản.

## Global Constraints

- Chạy lệnh .NET với `export PATH=$HOME/.dotnet:$PATH`. Build phải **0 warning, 0 error**: `dotnet build LisConnect.slnx`.
- Test CSDL cần Docker đang chạy. Toàn bộ test cũ phải vẫn xanh (Core 175, App 120, Data 153, Reports 19, Simulator 17, Server 4).
- **Hợp đồng `/api` giữ nguyên**: đường dẫn, phương thức, tên trường JSON (camelCase), mã trạng thái HTTP, thông báo lỗi nghiệp vụ tiếng Việt. Nguồn đặc tả: `server/src/**/*.controller.ts`, `server/src/**/*.service.ts` và kiểu trong `web/src/services/index.ts`.
- Lỗi `/api` trả JSON `{"statusCode": <mã>, "message": "<câu tiếng Việt>"}` (web đọc trường `message`). `/agent/v1` giữ nguyên dạng lỗi hiện tại.
- Thân request có trường lạ → 400 (NestJS đang `forbidNonWhitelisted`).
- Quy tắc nghiệp vụ (giá trị kết quả, cờ, khoảng tham chiếu, duyệt / hủy duyệt, "chờ thay", băm mật khẩu, khóa đăng nhập) **gọi `Lis.Data` / `Lis.Core`**, không viết lại. Chỗ NestJS khác `Lis.Data` thì `Lis.Data` thắng; ghi khác biệt vào thông điệp commit.
- Không sửa hành vi `Lis.App`, `Lis.Agent`, `/agent/v1`.
- Mọi route `/api` (trừ `auth/login`, `setup/*`) chỉ chạm CSDL của phòng khám trong token.
- Dữ liệu test là giả, tên chứa "Mẫu" hoặc "Thử".
- Văn bản hiển thị và chú thích code bằng tiếng Việt có dấu, theo phong cách sẵn có.
- Commit cục bộ sau mỗi task, kết thúc bằng `Co-Authored-By: Claude <noreply@anthropic.com>`. **Không push.**

## Phân công

- **Claude**: task 1, 6, 7, 8 (nền tảng, phần an toàn người bệnh, giao diện, đóng gói).
- **opencode** (qua Orca; Claude viết brief từ task và tự kiểm chứng kết quả): task 2, 3, 4, 5.
- Task 6 được một lượt rà soát độc lập mức cao nhất trước khi nhận.

## Review Focus

1. Token của phòng khám A dùng với id bản ghi của phòng khám B → phải 404, không lộ dữ liệu (test ở task 1 và mỗi task có route theo id).
2. Tài khoản bị ngừng hoạt động sau khi đã đăng nhập → request kế tiếp 401 "Tài khoản đã ngừng hoạt động" (task 1).
3. Sửa giá trị kết quả của mẫu đã duyệt, hoặc duyệt hai lần cùng lúc → bị từ chối đúng như `ResultService`, không ghi đè (task 6).
4. Hai người bấm "Tạo phòng khám" cùng mã cùng lúc → đúng một cái được tạo, cái kia 409 (task 7).
5. CSDL của một phòng khám nâng cấp lỗi lúc khởi động → phòng khám đó 503 kèm thông báo; phòng khám khác đăng nhập bình thường (task 7).

## Cấu trúc tệp

```
src/Lis.Server/
  ServerOptions.cs            cấu hình: Jwt:Secret, LocalUtcOffsetMinutes, Setup:Key, Web:Root
  Api/ApiErrors.cs            ApiException + chuyển lỗi thành {statusCode, message}
  Api/ApiSession.cs           SessionTokens, RequestContext, bộ lọc phiên, kiểm vai trò
  Api/AuthEndpoints.cs        /api/auth
  Api/UsersEndpoints.cs       /api/users
  Api/CatalogEndpoints.cs     /api/catalog
  Api/InstrumentsEndpoints.cs /api/instruments
  Api/AgentsEndpoints.cs      /api/agents, /api/agent-logs
  Api/SamplesEndpoints.cs     /api/samples, /api/results
  Api/SetupEndpoints.cs       /api/setup
  Tenancy/ClinicProvisioner.cs  tạo CSDL + migrate + ghi master (tách từ AdminCommands)
tests/Lis.Server.Tests/
  ApiHost.cs                  dựng server + phòng khám + đăng nhập cho test
  Api/<Tên>EndpointsTests.cs  một tệp cho mỗi tệp endpoints
web/src/modules/thiet-lap/    trang "Thiết lập lần đầu"
docker-compose.yml, Dockerfile
```

---

### Task 1: Nền tảng `/api` — lỗi, phiên, đăng nhập (Claude)

**Files:**
- Create: `src/Lis.Server/ServerOptions.cs`, `src/Lis.Server/Api/ApiErrors.cs`, `src/Lis.Server/Api/ApiSession.cs`, `src/Lis.Server/Api/AuthEndpoints.cs`
- Modify: `src/Lis.Server/Program.cs`, `src/Lis.Server/Tenancy/TenantRegistry.cs` (thêm store dùng chung vào `TenantServices`), `src/Lis.Server/appsettings.json`, `Directory.Packages.props`, `src/Lis.Server/Lis.Server.csproj`
- Test: `tests/Lis.Server.Tests/ApiHost.cs`, `tests/Lis.Server.Tests/Api/AuthEndpointsTests.cs`

**Interfaces — Produces:**

```csharp
public sealed record ServerOptions(string JwtSecret, int LocalUtcOffsetMinutes, string? SetupKey, string? WebRoot);
// Jwt:Secret thiếu, = "CHANGE_ME" hoặc < 24 ký tự → ném InvalidOperationException lúc khởi động.

public sealed class ApiException(int status, string message) : Exception(message) { public int Status { get; } }
// Tiện dụng: ApiException.BadRequest(msg), .NotFound(msg), .Forbidden(msg), .Conflict(msg)

public sealed class SessionTokens(string secret, TimeProvider clock)   // HS256, hết hạn 12 giờ, claim: sub, clinicId
{ public string Issue(Guid userId, Guid clinicId); public (Guid UserId, Guid ClinicId)? Read(string? bearer); }

public sealed record RequestContext(Clinic Clinic, TenantServices Tenant, UserInfo User);

public static class ApiSession
{
    public static RouteGroupBuilder MapApiGroup(this WebApplication app);            // "/api": bắt ApiException, cấm trường lạ
    public static RouteGroupBuilder RequireSession(this RouteGroupBuilder group);    // gắn RequestContext
    public static RequestContext Ctx(this HttpContext http);
    public static void RequireAdmin(this RequestContext ctx);          // 403 "Chỉ tài khoản Quản trị được thay đổi cài đặt này"
    public static void RequireResultEditor(this RequestContext ctx);   // 403 "Tài khoản không có quyền nhập / duyệt kết quả"
    public static void RequireUnapprover(this RequestContext ctx);     // 403 "Chỉ Trưởng khoa hoặc Quản trị được huỷ duyệt"
    public static void RequireLabStaff(this RequestContext ctx);       // 403 "Tài khoản không có quyền xem mục này"
}

// tests
public sealed class ApiHost : IAsyncDisposable
{
    public static Task<ApiHost> StartAsync(ServerFixture fixture, string? setupKey = null);
    public Task<Clinic> AddClinicAsync(string code, string name);                       // tạo CSDL + migrate + master
    public Task AddUserAsync(Clinic clinic, string username, string password, params string[] roles);
    public HttpClient Anonymous { get; }
    public Task<HttpClient> LoginAsync(string clinicCode, string username, string password);   // client đã gắn Bearer
}
```

Quy tắc đăng nhập: gọi `Lis.Data.Auth.AuthService` (đã có khóa 15 phút sau 5 lần sai). Thông báo và mã trạng thái theo `server/src/auth/auth.service.ts`: sai bất kỳ thứ gì → 401 "Sai mã phòng khám, tên đăng nhập hoặc mật khẩu"; đang khóa → 429 "Tài khoản tạm khoá do nhập sai nhiều lần — thử lại sau N phút"; ngừng hoạt động → 401 "Tài khoản đã ngừng hoạt động". Phiên: token hỏng/hết hạn → 401 "Phiên đăng nhập hết hạn — đăng nhập lại"; phòng khám không còn → 401 "Phiên đăng nhập không hợp lệ". Mã phòng khám so khớp sau `Trim().ToLowerInvariant()`.

- [ ] **Step 1: Viết `ApiHost` và test thất bại** trong `AuthEndpointsTests`:
  - `Login_dung_tra_token_va_thong_tin`: POST `/api/auth/login` `{clinicCode:"pkthu", username:"admin", password}` → 200, có `token`, `clinic.code == "pkthu"`, `user.roles == ["quan-tri"]`.
  - `Login_sai_mat_khau_401_cung_thong_bao_voi_sai_phong_kham`.
  - `Login_sai_5_lan_thi_429`.
  - `Login_co_truong_la_400`.
  - `Me_khong_token_401`, `Me_token_hop_le_tra_user_va_clinic`.
  - `Tai_khoan_bi_ngung_sau_khi_dang_nhap_thi_401` (Review Focus 2).
  - `Token_ky_bang_khoa_khac_401`.
- [ ] **Step 2: Chạy, xác nhận đỏ** — `dotnet test tests/Lis.Server.Tests --filter AuthEndpointsTests` → FAIL (404 / không biên dịch).
- [ ] **Step 3: Hiện thực** các kiểu ở khối Interfaces; `Program.cs` đọc `ServerOptions`, đăng ký `SessionTokens`, gọi `app.MapApiGroup()` rồi `MapAuthApi`.
- [ ] **Step 4: Chạy lại** — PASS; `dotnet build LisConnect.slnx` 0 warning.
- [ ] **Step 5: Commit** — `feat(server): nền tảng /api trên .NET — phiên JWT, đăng nhập`.

### Task 2: `/api/users` (opencode)

**Files:** Create `src/Lis.Server/Api/UsersEndpoints.cs`; Test `tests/Lis.Server.Tests/Api/UsersEndpointsTests.cs`; Modify `Program.cs` (một dòng `MapUsersApi`).

**Interfaces — Consumes:** task 1 (`RequireSession`, `Ctx`, `RequireAdmin`, `ApiException`, `ApiHost`). **Produces:** `public static void MapUsersApi(this RouteGroupBuilder api)`.

Đặc tả: `server/src/users/users.controller.ts` (4 route: `GET /users`, `POST /users`, `PUT /users/:id`, `POST /users/me/password`). Ghi qua `Lis.Data.Auth.UserStore` + `PasswordHasher`.

- [ ] **Step 1: Test thất bại** — mỗi route: ca đúng (so tên trường với kiểu `User` trong `web/src/services/index.ts`); không phải `quan-tri` → 403; thân sai theo từng `@Is…` trong DTO → 400 với đúng `message` khai trong DTO; trùng tên đăng nhập → đúng mã + câu của controller; `PUT` id của phòng khám khác → 404; đổi mật khẩu sai mật khẩu hiện tại → đúng mã + câu; mọi quy tắc "không tự khóa mình / phải còn một quản trị" có trong controller đều có test.
- [ ] **Step 2: Đỏ** — `dotnet test tests/Lis.Server.Tests --filter UsersEndpointsTests`.
- [ ] **Step 3: Hiện thực.**
- [ ] **Step 4: Xanh + build 0 warning.**
- [ ] **Step 5: Commit** — `feat(server): /api/users trên .NET`.

### Task 3: `/api/catalog` (opencode)

**Files:** Create `src/Lis.Server/Api/CatalogEndpoints.cs`; Test `tests/Lis.Server.Tests/Api/CatalogEndpointsTests.cs`; Modify `Program.cs`.

**Produces:** `public static void MapCatalogApi(this RouteGroupBuilder api)`.

Đặc tả: `server/src/catalog/catalog.controller.ts` (`GET /catalog/services`) và `catalog-admin.controller.ts` (`GET /catalog/meta`, `GET|POST /catalog/services[/:id]`, `PUT|DELETE /catalog/services/:id`). Đọc/ghi qua `Lis.Data.Catalog.CatalogStore` nơi đã có phương thức cùng nghĩa; còn lại chuyển nguyên SQL sang Dapper. Khoảng tham chiếu kiểm bằng logic `Lis.Core` (đối chiếu các ca trong `server/src/common/reference-range.spec.ts` — chuyển các ca đó thành test C# nếu `Lis.Core.Tests` chưa có).

- [ ] **Step 1: Test thất bại** — mỗi route ca đúng (tên trường theo `CatalogService`, `CatalogMeta`, `ServiceDetail`, `ServiceInput`); quyền; từng lỗi kiểm tra đầu vào và lỗi nghiệp vụ (trùng mã, xóa dịch vụ đang được dùng…) có trong controller; id phòng khám khác → 404.
- [ ] **Step 2–5:** đỏ → hiện thực → xanh + build → commit `feat(server): /api/catalog trên .NET`.

### Task 4: `/api/instruments` (opencode)

**Files:** Create `src/Lis.Server/Api/InstrumentsEndpoints.cs`; Test `tests/Lis.Server.Tests/Api/InstrumentsEndpointsTests.cs`; Modify `Program.cs`.

**Produces:** `public static void MapInstrumentsApi(this RouteGroupBuilder api)`.

Đặc tả: `server/src/instruments/instruments.controller.ts` (11 route: máy, `channels`, `declared`, `unmapped`). `PROTOCOL_FAMILIES` lấy từ `Lis.Core.Drivers.DriverRegistry.SupportedFamilies`, không chép danh sách. Ghi qua `Lis.Data.Instruments.InstrumentStore`; trạng thái agent trong `GET /instruments` đọc từ `MasterStore` như controller đang làm.

- [ ] **Step 1: Test thất bại** — như task 3, cộng: mỗi kiểu kết nối (`none`, `serial`, `tcp-server`, `tcp-client`) lưu rồi đọc lại đúng; giao thức lạ → 400; `chi-nghe` được chấp nhận; trùng mã kênh → đúng mã + câu.
- [ ] **Step 2–5:** đỏ → hiện thực → xanh + build → commit `feat(server): /api/instruments trên .NET`.

### Task 5: `/api/agents`, `/api/agent-logs` (opencode)

**Files:** Create `src/Lis.Server/Api/AgentsEndpoints.cs`; Test `tests/Lis.Server.Tests/Api/AgentsEndpointsTests.cs`; Modify `Program.cs`, `src/Lis.Server/Tenancy/MasterStore.cs` (thêm phương thức sửa / đổi khóa / xóa agent nếu thiếu).

**Produces:** `public static void MapAgentsApi(this RouteGroupBuilder api)`.

Đặc tả: `server/src/agents/agents.controller.ts` (5 route) và `server/src/logs/logs.controller.ts` (1 route, phân trang bằng `beforeId`, lọc `agentId`, `instrumentId`, `direction`, `q`, `limit`). Khóa agent sinh bằng đúng `MasterStore.AddAgentAsync` để `/agent/v1` nhận được.

- [ ] **Step 1: Test thất bại** — ca đúng mỗi route; quyền (`RequireAdmin` cho agent, `RequireLabStaff` cho log); khóa trả về từ `POST /agents` gọi được `GET /agent/v1/me`; sau `POST /agents/:id/key` khóa cũ bị 401; agent phòng khám khác → 404; log phân trang không lặp, không sót; `limit` vượt trần bị giới hạn như controller.
- [ ] **Step 2–5:** đỏ → hiện thực → xanh + build → commit `feat(server): /api/agents và /api/agent-logs trên .NET`.

### Task 6: `/api/samples`, `/api/results` (Claude — an toàn người bệnh)

**Files:** Create `src/Lis.Server/Api/SamplesEndpoints.cs`; Test `tests/Lis.Server.Tests/Api/SamplesEndpointsTests.cs`; Modify `Program.cs`, `TenantRegistry.cs` (thêm `ResultService`).

**Produces:** `public static void MapSamplesApi(this RouteGroupBuilder api)`.

Đặc tả route: `server/src/results/results.controller.ts` (9 route). **Mọi thao tác ghi gọi `Lis.Data.Results.ResultService`** — không chuyển `results-write.service.ts`. Đọc danh sách / chi tiết: dùng `ResultService` rồi ánh xạ sang dạng `SampleDetail` của web; trường web cần mà `ResultService` chưa trả thì bổ sung truy vấn đọc, không đụng quy tắc ghi. Lọc "theo ngày" dùng `ServerOptions.LocalUtcOffsetMinutes`.

- [ ] **Step 1: Liệt kê khác biệt** giữa `results-write.service.ts` + `result-value.ts` và `ResultService` (làm tròn, chuẩn hóa giá trị, cờ, điều kiện duyệt, lý do bắt buộc). Chuyển các ca trong `result-value.spec.ts` thành test C# chạy trên logic `Lis.Core`; ca nào lệch thì ghi lại và báo chủ dự án trước khi chốt.
- [ ] **Step 2: Test thất bại** — ca đúng 9 route (tên trường theo `SampleDetail`, `ResultHistory`); `tiep-nhan` nhập kết quả → 403; `ky-thuat-vien` hủy duyệt → 403; sửa kết quả mẫu đã duyệt → bị từ chối; hủy duyệt không lý do → 400; duyệt mẫu còn thiếu kết quả → đúng hành vi `ResultService`; hai request duyệt đồng thời → một thành công (Review Focus 3); chấp nhận / bỏ "chờ thay" ghi lịch sử; id phòng khám khác → 404; lọc ngày ở ranh giới nửa đêm giờ Việt Nam.
- [ ] **Step 3: Đỏ → hiện thực → xanh + build.**
- [ ] **Step 4: Rà soát độc lập** phần này (mức cao nhất), sửa theo nhận xét.
- [ ] **Step 5: Commit** — `feat(server): /api/samples và /api/results trên .NET, dùng ResultService`.

### Task 7: Tạo phòng khám trên web + cô lập lỗi nâng cấp (Claude)

**Files:**
- Create: `src/Lis.Server/Tenancy/ClinicProvisioner.cs`, `src/Lis.Server/Api/SetupEndpoints.cs`, `web/src/modules/thiet-lap/SetupPage.tsx` (+ test), `tests/Lis.Server.Tests/Api/SetupEndpointsTests.cs`
- Modify: `src/Lis.Server/AdminCommands.cs` (gọi `ClinicProvisioner`), `Program.cs`, `Api/ApiSession.cs`, `web/src/services/index.ts`, định tuyến của web, trang đăng nhập (liên kết "Thiết lập lần đầu" khi chưa có phòng khám)

**Interfaces — Produces:**

```csharp
public sealed class ClinicProvisioner(MasterStore master, TenantRegistry tenants)
{
    // Khóa pg_advisory_lock trên master trong suốt quá trình. Trùng mã → ApiException 409.
    public Task<Clinic> CreateAsync(string code, string name, string adminUsername, string adminFullName, string adminPassword);
    public IReadOnlyDictionary<Guid, string> FailedUpgrades { get; }   // phòng khám nâng cấp lỗi lúc khởi động → thông báo
    public Task UpgradeAllAsync();
}
```

- `GET /api/setup/status` → `{"hasClinic": bool}` (không cần đăng nhập).
- `POST /api/setup/clinic` `{setupKey, code, name, adminUsername, adminFullName, adminPassword}` → 201 `{"code": "..."}`. Sai mã thiết lập → 403 "Mã thiết lập không đúng"; mỗi lần sai liên tiếp từ cùng địa chỉ làm request kế tiếp chậm thêm 1 giây, tối đa 10 giây. Mã phòng khám: chữ thường không dấu, số, gạch ngang, 2–32 ký tự. Tên CSDL = `lis_` + mã với `-` thành `_`. Mật khẩu ≥ 8 ký tự.
- Mã thiết lập: `Setup:Key` hoặc biến môi trường `SETUP_KEY`; không có thì sinh 24 ký tự ngẫu nhiên và ghi ra log lúc khởi động (mỗi lần chạy một mã).
- Phòng khám có trong `FailedUpgrades`: đăng nhập và mọi route có phiên trả 503 "Cơ sở dữ liệu của phòng khám chưa nâng cấp được — báo quản trị hệ thống".

- [ ] **Step 1: Test thất bại** — tạo phòng khám rồi đăng nhập được bằng tài khoản vừa tạo; sai mã → 403 và CSDL không được tạo; mã phòng khám sai dạng → 400; trùng mã → 409; hai request cùng mã đồng thời → đúng một 201 (Review Focus 4); `status` đổi từ `false` sang `true`; phòng khám có CSDL hỏng → 503 riêng nó, phòng khám khác 200 (Review Focus 5).
- [ ] **Step 2: Đỏ → hiện thực server → xanh.**
- [ ] **Step 3: Trang web "Thiết lập lần đầu"** — một form, Enter chuyển ô, lỗi server hiện ngay tại form; xong thì chuyển về đăng nhập với mã phòng khám điền sẵn. Test vitest cho gửi form và hiện lỗi. Kiểm bằng ảnh chụp thật.
- [ ] **Step 4: Commit** — `feat: tạo phòng khám và tài khoản quản trị đầu tiên trên web`.

### Task 8: Phục vụ web, đóng gói, đối chiếu, xóa NestJS (Claude)

**Files:**
- Create: `Dockerfile`, `docker-compose.yml`, `scripts/so-sanh-api.sh`
- Modify: `src/Lis.Server/Program.cs` (tệp tĩnh + fallback `index.html` cho đường dẫn không bắt đầu bằng `/api`, `/agent`, `/healthz`), `web/vite.config.ts` (proxy mặc định `http://localhost:5080`), `docs/HUONG-DAN-CAI-DAT.md`, `docs/server-agent.md`, `README.md`
- Delete: `server/`

- [ ] **Step 1: Test thất bại** — `Web:Root` trỏ thư mục tạm có `index.html`: `GET /` và `GET /ket-qua/abc` trả tệp đó; `GET /api/khong-co` trả 404 JSON, không trả `index.html`; không cấu hình `Web:Root` thì server vẫn chạy.
- [ ] **Step 2: Hiện thực → xanh.**
- [ ] **Step 3: Đối chiếu với NestJS** — `scripts/so-sanh-api.sh <url-nest> <url-dotnet> <clinic> <user> <pass>`: đăng nhập cả hai, gọi mọi route `GET`, so JSON đã sắp khóa (`jq -S`). Chạy trên CSDL dev có dữ liệu mẫu; khác biệt nào còn lại phải giải thích được (ghi vào thông điệp commit).
- [ ] **Step 4: Đi hết các màn hình web bằng tay** với server .NET (đăng nhập, nhân sự, danh mục, máy + kênh, agent, log, kết quả: nhập / duyệt / hủy duyệt); `npm test` trong `web/` xanh.
- [ ] **Step 5: `docker compose up`** trên máy trắng (PostgreSQL 18 + server đã kèm `web/dist`) → tạo phòng khám trên web → đăng nhập. Viết lại hướng dẫn cài đặt theo luồng mới.
- [ ] **Step 6: Xóa `server/`**, chạy lại toàn bộ `dotnet test LisConnect.slnx` và `npm test`.
- [ ] **Step 7: Commit** — `feat: một server .NET phục vụ web + api + agent; bỏ NestJS`.
