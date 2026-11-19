# C — Kênh lệnh và điều khiển agent từ web — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agent đã cài giữ một kết nối WebSocket đi ra tới server; quản trị viên trên web ra được mọi lệnh ở spec mục 5.C (nạp lại cấu hình, quét cổng COM, thử cổng, nối lại máy, gửi lại dữ liệu chờ, khởi động lại, cập nhật, thu hồi) và thấy kết quả + lịch sử, không đụng vào máy tính agent.

**Architecture:** Server giữ trong bộ nhớ một `AgentChannelHub` (agentId → WebSocket đang mở; một tiến trình server — spec mục 9). Mỗi lệnh là một dòng `agent_command` ở `lis_master` (ai, lúc nào, trạng thái, kết quả). Agent mở `GET /agent/v1/channel` (WebSocket, header `X-Agent-Key`), gửi `hello`, nhận `command`, chạy tuần tự, trả `started` / `result`. Phong bì JSON dùng chung đặt ở `Lis.Core/Agents`. Web hỏi định kỳ (2 giây khi đang xem, 1 giây khi chờ một lệnh) — đủ trễ ≤ 3 giây.

**Tech Stack:** .NET 10 / C# 14, ASP.NET Core minimal API + `UseWebSockets`, `System.Net.WebSockets.ClientWebSocket`, Dapper + DbUp (master migration 0003), xunit v3 + Shouldly + Testcontainers + FakeTimeProvider; web React + antd + TanStack Query + vitest.

**Spec:** `docs/superpowers/specs/2026-11-16-mot-server-agent-dieu-khien-tu-web-design.md` (mục 5.C, 6, 7, 9 "Tệp cài agent", "Một bản server"). Nền: nhánh B (`docs/superpowers/plans/2026-11-17-b-ghep-noi-agent.md`) đã xong.

## Global Constraints

- Chạy .NET với `export PATH=$HOME/.dotnet:$PATH`; `dotnet build LisConnect.slnx` **0 warning, 0 error** (`TreatWarningsAsErrors`). API chỉ có trên Windows nằm trong lớp/hàm `[SupportedOSPlatform("windows")]`, chỉ gọi sau `OperatingSystem.IsWindows()`; không tắt CA1416.
- Toàn bộ test hiện có xanh (cuối nhánh B) cộng test mới. Test CSDL cần Docker.
- Không đổi hành vi `Lis.App`, `Lis.Core/Drivers`. `Lis.Connections` chỉ được **thêm** API (task 5 chuyển nguyên văn `SerialPortService` từ `Lis.App` sang — chỉ đổi namespace).
- Route `/agent/v1` hiện có giữ hợp đồng; `/api` chỉ thêm route mới và thêm trường vào `AgentView`. Lỗi `/api` vẫn `{"statusCode","message"}`; thân có trường lạ → 400.
- Chỉ vai trò `quan-tri` ra lệnh; Quản trị / Kỹ thuật viên / Trưởng khoa xem lịch sử. Agent phòng khám khác → 404.
- Lệnh gửi cho agent đang mất kết nối **hết hạn sau 60 giây** (trạng thái `expired`), không xếp hàng lâu hơn. "Thu hồi" có hiệu lực ngay ở server dù agent tắt.
- Server khởi động lại: lệnh `queued` / `sent` / `running` → `unknown` ("Máy chủ khởi động lại — không rõ kết quả"), không tự chạy lại.
- Khoá agent / mã ghép nối không bao giờ nằm trong log, URL, query, tham số dòng lệnh, nội dung lệnh hay kết quả lệnh.
- Cập nhật agent: tệp chỉ tải từ chính server đã ghép nối, phải khớp SHA-256 server công bố; "bản mới không chạy được" = không nối lại được server trong **2 phút** → trả tệp cũ, bật lại, báo lỗi lên web. Không tự cập nhật hàng loạt.
- Mất kết nối server: agent tiếp tục chạy máy xét nghiệm với cấu hình đã lưu (như hiện nay) — kênh lệnh đứt không được làm dừng `InstrumentConnectionManager`.
- Nối lại kênh: chờ tăng dần 1, 2, 4, 8, 16, 30, 60 giây (tối đa 60, ±20 % ngẫu nhiên); nối được thì về lại 1 giây.
- Văn bản, lỗi, chú thích tiếng Việt có dấu. Dữ liệu test giả, tên chứa "Thử" hoặc "Mẫu".
- Commit cục bộ mỗi task, thông điệp kết thúc bằng đúng một dòng `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Không push.

## Tên dùng chung (mọi task)

- Tên lệnh (chuỗi trên dây): `reload-config`, `scan-ports`, `test-port`, `reconnect-instrument`, `flush-pending`, `restart`, `update`, `revoke`.
- Trạng thái lệnh: `queued` (agent chưa nối, chờ ≤ 60 s), `sent`, `running`, `done`, `failed`, `expired`, `unknown`.
- Kiểu phong bì (`type`): agent→server `hello`, `started`, `result`; server→agent `command`, `config-changed`.

## Review Focus

1. Agent đứt kênh giữa lúc chạy lệnh → lệnh không kẹt `running` mãi: thành `unknown` "Agent mất kết nối giữa chừng" (trừ `update`, chờ agent nối lại) — test ở task 2.
2. Agent nối lại khi socket cũ chưa chết hẳn (hai kết nối cùng agent) → kết nối mới thắng, cũ bị đóng, lệnh đi vào kết nối mới — test ở task 2.
3. Thu hồi khi agent đang tắt → khoá bị từ chối ngay (401 ở `/agent/v1/me`, bắt tay WebSocket bị từ chối) — test ở task 3.
4. Thử cổng trên cổng một máy xét nghiệm đang dùng → máy đó tự chạy lại sau khi thử xong, **kể cả khi** thử cổng ném lỗi hoặc bị huỷ — test ở task 5.
5. Cập nhật: SHA-256 sai → không đụng tệp đang chạy, lệnh `failed`; bản mới không nối lại server trong 2 phút → bản cũ được trả lại và chạy, lệnh `failed` kèm lý do khi bản cũ nối lại — test ở task 6.

---

### Task 1: Bảng lệnh agent và cổng COM lần quét cuối ở `lis_master`

**Files:**
- Create: `src/Lis.Server/MasterMigrations/0003_kenh_lenh_agent.sql`, `src/Lis.Server/Tenancy/AgentCommandStore.cs`
- Modify: `src/Lis.Server/Tenancy/MasterStore.cs` (thu hồi, lưu cổng), `src/Lis.Server/Program.cs` (đăng ký DI)
- Test: `tests/Lis.Server.Tests/AgentCommandStoreTests.cs`, `tests/Lis.Server.Tests/MasterMigrationTests.cs` (thêm dòng 0003)

**Interfaces:**
- Produces:
  - Bảng `agent_command(id uuid PK, agent_id uuid NOT NULL REFERENCES agent(id) ON DELETE CASCADE, clinic_id uuid NOT NULL, name text NOT NULL, args jsonb NULL, status text NOT NULL, requested_by uuid NULL, requested_by_name text NULL, requested_at timestamptz NOT NULL, started_at timestamptz NULL, finished_at timestamptz NULL, result jsonb NULL, error text NULL)`, chỉ mục `(agent_id, requested_at DESC)` và `(status)`. Cột mới ở `agent`: `last_ports jsonb NULL`, `last_ports_at timestamptz NULL`.
  - `public sealed record AgentCommandRow(Guid Id, Guid AgentId, string Name, JsonElement? Args, string Status, string? RequestedByName, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, JsonElement? Result, string? Error);`
  - `AgentCommandStore(string masterConnection)`:
    - `Task<AgentCommandRow> CreateAsync(Guid clinicId, Guid agentId, string name, string? argsJson, string status, Guid? userId, string? userName, DateTimeOffset now)`
    - `Task<bool> TransitionAsync(Guid id, string[] from, string to, DateTimeOffset now, string? resultJson = null, string? error = null)` — chỉ đổi khi trạng thái hiện tại thuộc `from`; `sent`/`running` ghi `started_at` (nếu null), trạng thái kết thúc ghi `finished_at`. Trả false khi không đổi.
    - `Task<IReadOnlyList<AgentCommandRow>> QueuedForAsync(Guid agentId, DateTimeOffset notBefore)` (cũ nhất trước)
    - `Task<int> ExpireQueuedAsync(DateTimeOffset olderThan, DateTimeOffset now)` → `expired`, error "Agent không kết nối trong 60 giây"
    - `Task<int> TimeoutRunningAsync(DateTimeOffset now)` → `unknown` cho `sent`/`running` quá hạn: `update` 10 phút, `test-port` (args.seconds + 30 s), lệnh khác 2 phút; error "Agent không trả lời"
    - `Task<int> MarkUnfinishedUnknownAsync(DateTimeOffset now)` (lúc server khởi động)
    - `Task<int> MarkDisconnectedAsync(Guid agentId, DateTimeOffset now)` → `sent`/`running` (trừ `update`) thành `unknown` "Agent mất kết nối giữa chừng"
    - `Task<AgentCommandRow?> FindAsync(Guid clinicId, Guid commandId)`, `Task<IReadOnlyList<AgentCommandRow>> ListAsync(Guid clinicId, Guid agentId, int limit)` (mới nhất trước, limit 1..200)
    - `Task<AgentCommandRow?> OpenUpdateAsync(Guid agentId)` — lệnh `update` mới nhất đang `sent`/`running`/`unknown` trong 30 phút qua
  - `MasterStore.RevokeAgentAsync(Guid clinicId, Guid agentId) → Task<bool>`: `key_hash = NULL, paired_at = NULL`, huỷ mã ghép nối chưa dùng; false khi không thấy. `MasterStore.SavePortsAsync(Guid agentId, string portsJson, DateTimeOffset now)`. `AgentWithStatus` thêm `string? LastPortsJson, DateTimeOffset? LastPortsAt`.

- [ ] **Step 1: Test đỏ** — `MasterMigrationTests`: `schemaversions` có 3 dòng, dòng cuối kết thúc bằng `0003_kenh_lenh_agent.sql`. `AgentCommandStoreTests` (Testcontainers, `FakeTimeProvider`):
  - `Tao_roi_chuyen_trang_thai_ghi_moc_thoi_gian`: create `queued` → transition `[queued]→sent` true, `started_at` = now; `[sent]→done` với result `{"ok":1}` → `finished_at`, result đúng; transition `[queued]→sent` lần nữa → false.
  - `Het_han_hang_cho_sau_60_giay`: tạo `queued` lúc t; `ExpireQueuedAsync(t+60s-1ms)` không đổi; `ExpireQueuedAsync` với `olderThan = t+1ms` → 1 dòng `expired`, error "Agent không kết nối trong 60 giây".
  - `Qua_han_chay_thanh_unknown`: `test-port` args `{"seconds":10}` `running` lúc t: t+39 s không đổi, t+41 s → `unknown`; `update` `running` t+9 phút không đổi.
  - `Mat_ket_noi_khong_dong_update`: một `scan-ports` `running` + một `update` `running` → `MarkDisconnectedAsync` đổi đúng 1 (scan-ports).
  - `Khoi_dong_lai_server`: `queued`, `sent`, `running` → `unknown`; `done` giữ nguyên.
  - `Lich_su_chi_cua_phong_kham`: `ListAsync(clinicB, agentA)` rỗng; `FindAsync(clinicB, cmdA)` null.
  - `Thu_hoi_xoa_khoa`: agent có khoá → `RevokeAgentAsync` true → `AuthenticateAsync(khoá cũ)` null; `paired_at` null; mã ghép nối chưa dùng không còn đổi được (`RedeemPairingAsync` → failure).
- [ ] **Step 2: Đỏ** — `dotnet test tests/Lis.Server.Tests --filter "AgentCommandStore|MasterMigration"`.
- [ ] **Step 3: Làm** — migration + `AgentCommandStore` + hai hàm `MasterStore`; mọi mốc thời gian lấy từ tham số `now` (không `now()` của PostgreSQL). Đăng ký `AgentCommandStore` singleton cạnh `MasterStore`.
- [ ] **Step 4: Xanh** — lệnh step 2 + `dotnet build LisConnect.slnx` 0 warning.
- [ ] **Step 5: Commit** — `feat(server): bảng lệnh agent, thu hồi khoá, lưu cổng COM lần quét cuối`.

### Task 2: Phong bì kênh lệnh, `AgentChannelHub` và `/agent/v1/channel`

**Files:**
- Create: `src/Lis.Core/Agents/ChannelProtocol.cs`, `src/Lis.Server/AgentApi/AgentChannelHub.cs`, `src/Lis.Server/AgentApi/AgentChannelEndpoint.cs`, `src/Lis.Server/AgentApi/AgentCommandSweeper.cs`
- Modify: `src/Lis.Server/Program.cs` (`UseWebSockets`, map endpoint, hosted service, `MarkUnfinishedUnknownAsync` lúc khởi động), `src/Lis.Server/AgentApi/AgentEndpoints.cs` (tách hàm xác thực khoá dùng chung)
- Test: `tests/Lis.Server.Tests/AgentChannelTests.cs`, `tests/Lis.Core.Tests/ChannelProtocolTests.cs`

**Interfaces:**
- Consumes: `AgentCommandStore` (task 1), `MasterStore.AuthenticateAsync/TouchAgentAsync`.
- Produces:
  - `Lis.Core.Agents`: `public sealed record ChannelEnvelope(string Type, Guid? Id = null, string? Name = null, JsonElement? Args = null, bool? Ok = null, JsonElement? Data = null, string? Error = null, string? Version = null, string? MachineName = null, UpdateReport? Update = null);` `public sealed record UpdateReport(Guid CommandId, bool Ok, string? Version, string? Error);` `public static class ChannelProtocol { const string Hello="hello", Started="started", Result="result", Command="command", ConfigChanged="config-changed"; string Serialize(ChannelEnvelope); ChannelEnvelope? Parse(ReadOnlySpan<byte> utf8); const int MaxMessageBytes = 1_048_576; }` (JSON web camelCase; `Parse` trả null khi hỏng / thiếu `type` / quá lớn).
  - `AgentChannelHub` (singleton): `bool IsConnected(Guid agentId)`; `Task<AgentCommandRow> SubmitAsync(Guid clinicId, Guid agentId, string name, string? argsJson, Guid? userId, string? userName)` — đang nối → ghi `sent` rồi gửi; không nối → ghi `queued`; `Task NotifyConfigChangedAsync(IEnumerable<Guid> agentIds)` (không ghi lịch sử); `Task DisconnectAsync(Guid agentId)` (đóng kênh, dùng khi thu hồi); `event` hoặc callback `Func<Guid agentId, ChannelEnvelope result, Task>? ResultObserver` cho task 3/6 (vd lưu cổng sau `scan-ports`).
  - Endpoint `GET /agent/v1/channel`: không phải WebSocket → 400; khoá sai/thu hồi → 401 trước khi nâng cấp; một agent tối đa một kênh — kênh mới thay kênh cũ (đóng cũ với lý do "Có kết nối mới hơn"). Sau `hello`: `TouchAgentAsync(version, machineName)`, giao các lệnh `QueuedForAsync(now-60s)` (→ `sent`). `started` → `running`; `result` → `done`/`failed` (data lưu vào `result`, error vào `error`). Kênh đóng → `MarkDisconnectedAsync`. KeepAlive 20 s.
  - `AgentCommandSweeper : BackgroundService` mỗi 5 s: `ExpireQueuedAsync(now-60s)`, `TimeoutRunningAsync(now)` (dùng `TimeProvider` DI).

- [ ] **Step 1: Test đỏ** — `ChannelProtocolTests`: round-trip `command` có args; `Parse` trả null với `{}`, chuỗi rác, 1 MB + 1 byte. `AgentChannelTests` (dùng `WebApplicationFactory` + `Server.CreateWebSocketClient()` với header `X-Agent-Key`, agent tạo bằng `AddAgentAsync`):
  - `Khoa_sai_bi_tu_choi`: bắt tay với khoá sai → lỗi 401 (không mở được socket).
  - `Lenh_den_agent_dang_noi_va_ket_qua_ve_lich_su`: nối, gửi `hello`; `SubmitAsync(scan-ports)` → agent nhận `command` đúng id/tên; trả `started` rồi `result ok data {"ports":[]}` → dòng lệnh `done`, `result` đúng, `IsConnected` true.
  - `Lenh_cho_agent_offline_giao_khi_noi_trong_60_giay`: submit khi chưa nối → `queued`; nối + `hello` sau 30 s (FakeTimeProvider) → nhận lệnh, trạng thái `sent`.
  - `Lenh_cho_qua_60_giay_het_han`: submit, tiến 61 s, chạy sweeper một vòng (gọi hàm quét trực tiếp) → `expired`; nối sau đó không nhận lệnh này.
  - `Dut_giua_chung_thanh_unknown` (Review Focus 1): lệnh `running`, đóng socket phía agent → `unknown` "Agent mất kết nối giữa chừng".
  - `Ket_noi_moi_thang_ket_noi_cu` (Review Focus 2): nối socket A, nối socket B cùng khoá → A bị đóng; lệnh mới đến B, không đến A.
  - `Khoi_dong_lai_server_danh_dau_unknown`: tạo `running` trong DB rồi dựng host mới → `unknown`.
- [ ] **Step 2: Đỏ** — `dotnet test tests/Lis.Server.Tests --filter AgentChannel` và `tests/Lis.Core.Tests --filter ChannelProtocol`.
- [ ] **Step 3: Làm** — hub giữ `ConcurrentDictionary<Guid, ChannelConnection>`; mỗi kết nối một vòng nhận và một khoá gửi (`SemaphoreSlim`) để không gửi chồng khung. Không log nội dung `args`/`data` (chỉ tên lệnh, id, trạng thái).
- [ ] **Step 4: Xanh** — hai lệnh step 2 + build 0 warning.
- [ ] **Step 5: Commit** — `feat(server): kênh lệnh WebSocket cho agent, hết hạn 60 giây, đứt kênh không kẹt lệnh`.

### Task 3: `/api` ra lệnh, thu hồi, tự báo đổi cấu hình, tệp cập nhật cho agent

**Files:**
- Create: `src/Lis.Server/Api/AgentCommandsEndpoints.cs`
- Modify: `src/Lis.Server/Api/AgentsEndpoints.cs` (`AgentView` thêm trường), `src/Lis.Server/Api/InstrumentsEndpoints.cs` (báo đổi cấu hình), `src/Lis.Server/AgentApi/AgentEndpoints.cs` (`GET /agent/v1/installer`)
- Test: `tests/Lis.Server.Tests/Api/AgentCommandsApiTests.cs`

**Interfaces:**
- Consumes: hub + store (task 1, 2), `AgentDistribution` (B task 4).
- Produces:
  - `POST /api/agents/{id}/commands` thân `{name, args?}` → 202 `AgentCommandView`. Chỉ `quan-tri`. Kiểm: `name` thuộc 8 tên; `test-port` args `{portName (COM\d{1,3}), baudRate (300..115200), dataBits (7|8), parity (None|Even|Odd), stopBits (1|2), seconds (1..60)}`; `reconnect-instrument` args `{instrumentId}` phải là máy gán cho agent này; `update` không nhận args từ web — server tự điền `{version, sha256}` từ `AgentDistribution.Current()` (chưa có tệp → 409 "Máy chủ chưa có tệp cài agent"); lệnh khác không có args. Sai → 400 tiếng Việt. Agent chưa ghép nối → 409 "Agent chưa ghép nối".
  - `revoke`: `RevokeAgentAsync` ngay; agent đang nối → gửi lệnh, đóng kênh sau khi có `result` hoặc 5 s; agent không nối → lệnh `done` ngay với result `{"offline":true}`.
  - `GET /api/agents/{id}/commands?limit=50` → `AgentCommandView[]` mới nhất trước; `GET /api/agents/commands/{commandId}` → một. Lab staff xem được.
  - `public sealed record AgentCommandView(Guid Id, string Name, JsonElement? Args, string Status, string? RequestedBy, string RequestedAt, string? StartedAt, string? FinishedAt, JsonElement? Result, string? Error);`
  - `AgentView` thêm cuối: `bool Channel` (đang có kênh), `JsonElement? Ports`, `string? PortsScannedAt`. `Online` = `Channel` hoặc báo về < 90 s (giữ như cũ).
  - Sau `scan-ports` `done`: hub observer → `SavePortsAsync(agentId, data.ports)`.
  - Tạo / sửa / xoá máy (`InstrumentsEndpoints`) → `NotifyConfigChangedAsync` cho agent của mã máy trạm cũ và mới.
  - `GET /agent/v1/installer` (xác thực khoá agent) → tệp như `/api/agents/installer` kèm `X-Agent-Sha256`, `X-Agent-Version`; chưa có tệp → 404.
- [ ] **Step 1: Test đỏ** — `AgentCommandsApiTests`:
  - `Ky_thuat_vien_khong_ra_lenh_duoc` → 403; xem lịch sử được → 200.
  - `Ten_lenh_la_va_args_sai_bi_400`: `{"name":"format-c"}`, `test-port` seconds 61, portName `"/dev/tty"`, trường lạ → 400.
  - `Agent_phong_kham_khac_404` cho POST / GET lịch sử / GET một lệnh.
  - `Ra_lenh_agent_offline_la_queued` → 202 status `queued`; `Update_khi_chua_co_tep_409`; `Update_dien_version_sha_tu_server` (tệp giả trong `AGENT_DIST_DIR`) → args có `version`, `sha256` đúng.
  - `Thu_hoi_khi_agent_tat` (Review Focus 3): POST revoke → 202 `done`; `GET /agent/v1/me` với khoá cũ → 401; bắt tay `/agent/v1/channel` → 401; danh sách agent `paired=false`.
  - `Thu_hoi_khi_agent_dang_noi`: socket thử nhận `command revoke`, trả result → socket bị server đóng trong ≤ 5 s.
  - `Quet_cong_xong_luu_ports`: socket thử trả `result` `{"ports":[{"name":"COM3","description":"USB-SERIAL CH340","usedBy":null,"busy":false}]}` → `GET /api/agents` có `ports`, `portsScannedAt`.
  - `Sua_may_bao_agent_doi_cau_hinh`: socket thử của agent được gán nhận `config-changed` sau `PUT /api/instruments/{id}`.
  - `Agent_tai_tep_cap_nhat`: `GET /agent/v1/installer` với khoá → 200 + header SHA; không khoá → 401.
- [ ] **Step 2: Đỏ** — `dotnet test tests/Lis.Server.Tests --filter AgentCommandsApi`.
- [ ] **Step 3: Làm** — kiểm đầu vào trước kiểm quyền (giống các endpoint hiện có); lỗi theo `ApiException`.
- [ ] **Step 4: Xanh** — cả `tests/Lis.Server.Tests` + build 0 warning.
- [ ] **Step 5: Commit** — `feat(server): API ra lệnh agent, thu hồi tức thì, báo agent khi sửa máy`.

### Task 4: Agent giữ kênh lệnh và chạy các lệnh không đụng cổng

**Files:**
- Create: `src/Lis.Agent/Channel/ChannelClient.cs`, `src/Lis.Agent/Channel/CommandDispatcher.cs`, `src/Lis.Agent/Channel/AgentControl.cs`
- Modify: `src/Lis.Agent/AgentWorker.cs` (chạy kênh song song; `AgentPaths.ConnectedMarker` = `ket-noi.json`, `AgentPaths.UpdateReport` = `cap-nhat.json`), `src/Lis.Agent/Program.cs`, `src/Lis.Agent/Setup/IServiceControl.cs` (`Disable()`), `src/Lis.Connections/InstrumentConnectionManager.cs` (thêm `RetryPendingNowAsync()`), `src/Lis.Connections/InstrumentRunner.cs` (lộ `RetryPendingAsync` cho manager)
- Test: `tests/Lis.Agent.Tests/ChannelClientTests.cs`, `tests/Lis.Agent.Tests/CommandDispatcherTests.cs`, `tests/Lis.Server.Tests/AgentChannelEndToEndTests.cs`

**Interfaces:**
- Consumes: `ChannelProtocol` (task 2), endpoint kênh (task 2/3).
- Produces:
  - `public interface IAgentCommandHandler { string Name { get; } Task<JsonElement?> RunAsync(JsonElement? args, CancellationToken ct); }` — ném `AgentCommandException(string message)` cho lỗi báo lên web.
  - `CommandDispatcher(IEnumerable<IAgentCommandHandler>)`: `Task<ChannelEnvelope> RunAsync(ChannelEnvelope command, Func<ChannelEnvelope, Task> send, CancellationToken ct)` — gửi `started`, chạy **tuần tự** (một lệnh một lúc), trả `result` ok/lỗi; tên lạ → `result ok=false` "Agent chưa hỗ trợ lệnh <tên>"; ngoại lệ không mong đợi → `ok=false` với `ex.Message` (không stack).
  - `ChannelClient(Uri serverUrl, string agentKey, CommandDispatcher dispatcher, AgentPaths paths, TimeProvider clock, ILogger, Func<Uri, string, CancellationToken, Task<WebSocket>>? connect = null)`: `Task RunAsync(CancellationToken)`; `event Action? ConfigChanged`. URL kênh: `http→ws`, `https→wss`, đường `agent/v1/channel`. Sau khi gửi `hello` (kèm `Update` đọc từ `cap-nhat.json` nếu có, xoá tệp sau khi gửi) thì ghi `ket-noi.json` `{version, at}`. Chờ nối lại theo Global Constraints; hàm `static TimeSpan BackoffFor(int attempt, double jitter01)`.
  - Handler trong task này: `reload-config` (gọi `manager.ReloadAsync()`, trả `{instruments: n}`), `reconnect-instrument` (`RestartAsync(id)`; máy không thuộc agent → lỗi "Máy này không chạy trên agent"), `flush-pending` (`RetryPendingNowAsync()` → `{remaining: n}`), `restart` (trả kết quả trước, rồi `IAgentLifetime.RestartSoon()`), `revoke` (trả kết quả, rồi `StopAllAsync`, xoá `pairing.json` + `agent.json` + `instruments.json` + khoá dev, Windows dịch vụ → `IServiceControl.Disable()`, thoát mã 0). Hàng chờ `cho-gui` giữ nguyên.
  - `public interface IAgentLifetime { void RestartSoon(); void StopForRevoke(); }` — thật: `RestartSoon` = sau 1 s `Environment.Exit(1)` (Windows Recovery bật lại dịch vụ — kiểm Windows thật); `StopForRevoke` = `IHostApplicationLifetime.StopApplication()`.
  - `config-changed` → `manager.ReloadAsync()` ngay. Trạng thái máy đổi → gửi `POST /agent/v1/status` sau 1 s gom (debounce), ngoài vòng 30 s sẵn có.
  - `InstrumentConnectionManager.RetryPendingNowAsync() → Task<int>` (tổng bản tin còn chờ sau khi thử).
- [ ] **Step 1: Test đỏ** —
  - `ChannelClientTests.Backoff`: attempt 0..7, jitter 0.5 → 1, 2, 4, 8, 16, 30, 60, 60 s; jitter 0 → ×0.8, jitter 1 → ×1.2 (tối đa 72 s).
  - `ChannelClientTests.Url`: `https://lis.vn/` → `wss://lis.vn/agent/v1/channel`; `http://10.0.0.5:5080` → `ws://10.0.0.5:5080/agent/v1/channel`.
  - `CommandDispatcherTests`: lệnh lạ → `ok=false` đúng câu; handler ném `AgentCommandException("Cổng bận")` → `ok=false`, error "Cổng bận"; hai lệnh gửi cùng lúc chạy tuần tự (handler giả ghi thứ tự bắt đầu/kết thúc không chồng nhau); `started` gửi trước `result`.
  - `AgentChannelEndToEndTests` (server thật + `ChannelClient` thật, `connect` dùng `TestServer.CreateWebSocketClient`): `hello` → web thấy `channel=true`, `version`; `POST reload-config` → `done` ≤ 3 s; sửa máy → handler reload được gọi; dừng server socket → client nối lại, `ket-noi.json` có `version`.
  - `Revoke_xoa_khoa_giu_hang_cho`: thư mục tạm có `pairing.json`, `cho-gui/x.json` → sau handler revoke: `pairing.json` mất, `cho-gui/x.json` còn, `IAgentLifetime.StopForRevoke` được gọi, `IServiceControl.Disable` gọi khi giả lập Windows-service.
  - `Mat_kenh_khong_dung_may`: client không nối được server (connect ném) 3 lần → `InstrumentConnectionManager` giả không bị `StopAllAsync`.
- [ ] **Step 2: Đỏ** — `dotnet test tests/Lis.Agent.Tests --filter "ChannelClient|CommandDispatcher|Revoke|Mat_kenh"` và `tests/Lis.Server.Tests --filter AgentChannelEndToEnd`.
- [ ] **Step 3: Làm** — `AgentWorker` dựng manager như cũ rồi chạy song song `ChannelClient.RunAsync`; kênh lỗi chỉ log cảnh báo + `uploader.AgentNote`, không thoát vòng chính. Khoá agent chỉ đặt vào header `X-Agent-Key` của `ClientWebSocket.Options.SetRequestHeader`. Agent khởi động xoá `LisAgent.new.exe` sót lại cạnh tệp chạy (bỏ qua lỗi).
- [ ] **Step 4: Xanh** — `tests/Lis.Agent.Tests`, `tests/Lis.Server.Tests`, build 0 warning.
- [ ] **Step 5: Commit** — `feat(agent): giữ kênh lệnh, nạp lại / nối lại / gửi bù / khởi động lại / thu hồi theo lệnh web`.

### Task 5: Quét cổng COM và thử cổng (tạm nhường cổng đang dùng)

**Files:**
- Move: `src/Lis.App/Services/SerialPortService.cs` → `src/Lis.Connections/SerialPortService.cs` (namespace `Lis.Connections`; Lis.App + Lis.App.Tests chỉ đổi `using`; thêm `System.Management` vào `Lis.Connections.csproj` nếu Lis.App đang dùng gói đó)
- Create: `src/Lis.Agent/Channel/PortCommands.cs`
- Modify: `src/Lis.Connections/InstrumentConnectionManager.cs` (`BorrowPortAsync`)
- Test: `tests/Lis.Agent.Tests/PortCommandsTests.cs`, `tests/Lis.Core.Tests` hoặc test sẵn có của Connections cho `BorrowPortAsync`

**Interfaces:**
- Consumes: `IAgentCommandHandler` (task 4), `ISerialPortService` (đã chuyển).
- Produces:
  - `InstrumentConnectionManager.BorrowPortAsync(string portName) → Task<IAsyncDisposable>`: dừng các runner COM có `PortName` trùng (không phân biệt hoa thường), trạng thái "Đang nhường cổng cho lệnh thử cổng"; `ReloadAsync` không mở lại máy đó khi còn mượn; `DisposeAsync` → `ReloadAsync`. Trả về kèm danh sách tên máy đã nhường: `public sealed record PortLease(IReadOnlyList<string> InstrumentNames) : IAsyncDisposable`.
  - `IPortProbe { bool IsBusy(string portName); }` (thật: mở/đóng `SerialPort` ngay, `UnauthorizedAccessException`/`IOException` → bận).
  - `scan-ports` → `{ports: [{name, description, usedBy, busy}]}`: `usedBy` = tên máy xét nghiệm agent đang chạy trên cổng đó (khi đó **không** thử mở), cổng khác thử `IsBusy`.
  - `test-port` args như task 3 → `{opened, error, hex, byteCount, borrowedFrom}`; mượn cổng nếu đang dùng, nghe bằng `ISerialPortService.ListenAsync` đúng `seconds`, giữ tối đa 64 KB đầu, trả cổng trong `finally`.
- [ ] **Step 1: Test đỏ** —
  - `Thu_cong_dang_dung_tra_lai_cong_ca_khi_loi` (Review Focus 4): manager có máy "Máy Thử" ở COM3 (link giả); `ListenAsync` giả ném `SerialPortBusyException` → kết quả `opened=false`, error tiếng Việt, `borrowedFrom=["Máy Thử"]`; sau lệnh máy chạy lại (link factory được gọi lần 2). Lặp lại với huỷ (`ct` bị huỷ giữa chừng) → máy vẫn chạy lại.
  - `Reload_khong_mo_may_dang_bi_muon`: trong lúc mượn gọi `ReloadAsync` → runner COM3 không được tạo.
  - `Quet_cong`: cổng COM3 (máy đang chạy) + COM5 (probe bận) + COM7 (rảnh) → `usedBy="Máy Thử"`, `busy` lần lượt false/true/false; probe không được gọi cho COM3.
  - `Thu_cong_tra_hex_toi_da_64KB`: ListenAsync giả trả 70 000 byte → `byteCount=70000`, `hex` dài 131 072 ký tự.
  - Lis.App.Tests hiện có vẫn xanh sau khi chuyển tệp.
- [ ] **Step 2: Đỏ** — `dotnet test tests/Lis.Agent.Tests --filter PortCommands`.
- [ ] **Step 3: Làm** — chuyển tệp bằng `git mv`, không sửa thân lớp.
- [ ] **Step 4: Xanh** — `dotnet test LisConnect.slnx` (toàn bộ) + build 0 warning.
- [ ] **Step 5: Commit** — `feat(agent): quét cổng COM và thử cổng theo lệnh web, tạm nhường cổng máy đang dùng`.

### Task 6: Cập nhật agent từ web, tự quay về bản cũ

**Files:**
- Create: `src/Lis.Agent/Update/UpdateCommand.cs` (handler `update`), `src/Lis.Agent/Update/UpdateApplier.cs`
- Modify: `src/Lis.Agent/AgentCommand.cs` (chế độ `apply-update <commandId>`), `src/Lis.Agent/Program.cs`, `src/Lis.Agent/ServerClient.cs` (`DownloadInstallerAsync`), `src/Lis.Server/AgentApi/AgentChannelHub.cs` (chốt lệnh update khi `hello`)
- Test: `tests/Lis.Agent.Tests/UpdateTests.cs`, `tests/Lis.Server.Tests/AgentChannelTests.cs` (thêm)

**Interfaces:**
- Consumes: `GET /agent/v1/installer` (task 3), `IServiceControl` (B task 6), `AgentPaths.ConnectedMarker/UpdateReport` (task 4).
- Produces:
  - `ServerClient.DownloadInstallerAsync(string destination, CancellationToken) → Task<(string Sha256, string? Version)>` (sha256 tự tính trên tệp đã ghi, chữ thường).
  - Handler `update` (args `{version, sha256}`): đã đúng phiên bản → `ok` `{alreadyCurrent:true}`; tải về `LisAgent.new.exe` cạnh tệp chạy; SHA tính được ≠ `args.sha256` → xoá tệp, lỗi "Tệp cập nhật sai mã kiểm tra — không cài"; không chạy dạng dịch vụ Windows → lỗi "Chỉ cập nhật được agent cài dạng dịch vụ"; ổn → chạy tách rời `LisAgent.new.exe apply-update <commandId>` (`WorkingDirectory = Path.GetTempPath()`), trả `ok` `{handedOver:true}`.
  - `UpdateApplier(string installedExe, string newExe, AgentPaths paths, IServiceControl service, TimeProvider clock, TimeSpan connectTimeout /* 2 phút */)`: `Task<UpdateReport> ApplyAsync(Guid commandId, string newVersion, CancellationToken)` — `Stop` → đổi `LisAgent.exe` → `LisAgent.old.exe` → chép bản mới → `Start` → chờ `ket-noi.json` có `version == newVersion` và `at` sau lúc bắt đầu, tối đa 2 phút → thành công: xoá `.old`, ghi `cap-nhat.json` `{commandId, ok:true, version}`; thất bại (bất kỳ bước nào): `Stop` (bỏ qua lỗi) → trả `LisAgent.old.exe` về → `Start` → ghi `cap-nhat.json` `{ok:false, error}`.
  - Server: `hello` có `Update` → lệnh đó `done`/`failed` (cả khi đang `unknown`); `hello` có `version` bằng `args.version` của `OpenUpdateAsync(agentId)` → `done`.
- [ ] **Step 1: Test đỏ** (Review Focus 5) —
  - `Sha_sai_khong_dung_tep_dang_chay`: server giả trả tệp có SHA khác `args.sha256` → lỗi đúng câu, `LisAgent.new.exe` không còn, không tiến trình nào được chạy (bộ chạy tiến trình giả).
  - `Ap_cap_nhat_thanh_cong`: thư mục tạm có `LisAgent.exe` ("cũ") + `LisAgent.new.exe` ("mới"); `IServiceControl` giả; một tác vụ giả ghi `ket-noi.json` version mới sau `Start` → `LisAgent.exe` = "mới", `.old` không còn, `cap-nhat.json` ok.
  - `Ban_moi_khong_noi_duoc_quay_ve`: không ai ghi marker; `FakeTimeProvider` tiến 2 phút 1 giây → `LisAgent.exe` = "cũ", service `Start` gọi 2 lần, `cap-nhat.json` `{ok:false, error:"Bản mới không kết nối được máy chủ trong 2 phút — đã quay về bản cũ"}`.
  - `Chep_ban_moi_loi_quay_ve`: chép ném `IOException` → tệp cũ còn nguyên, service chạy lại, báo lỗi.
  - Server `Hello_bao_cap_nhat_chot_lenh`: lệnh `update` `unknown` + `hello` có `Update{ok:false,error}` → `failed` với error đó; lệnh `running` + `hello` version mới → `done`.
- [ ] **Step 2: Đỏ** — `dotnet test tests/Lis.Agent.Tests --filter Update` và `tests/Lis.Server.Tests --filter Hello_bao_cap_nhat`.
- [ ] **Step 3: Làm** — `apply-update` chỉ Windows (`[SupportedOSPlatform]`), log ra `%ProgramData%\LIS Agent\cap-nhat.log`. Mã lệnh là GUID — tham số dòng lệnh duy nhất, không chứa bí mật.
- [ ] **Step 4: Xanh** — `tests/Lis.Agent.Tests`, `tests/Lis.Server.Tests`, build 0 warning; `bash scripts/publish-agent.sh <scratch>` vẫn ra tệp.
- [ ] **Step 5: Commit** — `feat(agent): cập nhật agent từ web, kiểm SHA-256, không nối lại được trong 2 phút thì quay về bản cũ`.

### Task 7: Giao diện điều khiển agent (Claude tự làm)

**Files:**
- Create: `web/src/modules/agent/AgentDrawer.tsx`, `web/src/modules/agent/commands.ts` (+ `commands.test.ts`), `web/src/modules/agent/AgentDrawer.test.tsx`
- Modify: `web/src/services/index.ts`, `web/src/modules/agent/AgentsPage.tsx`, `web/src/modules/may/InstrumentModal.tsx`, `web/src/shared/ui/StatusTag.tsx`

**Interfaces:**
- Consumes: API task 3.
- Produces: `agentService.command(id, name, args?)`, `agentService.commands(id)`, `agentService.commandById(cmdId)`; `Agent` thêm `channel, ports, portsScannedAt`; `commands.ts`: `commandLabel(name)`, `commandStatus` (StatusTag domain `command`: queued "Chờ agent", sent "Đã gửi", running "Đang chạy", done "Xong", failed "Lỗi", expired "Hết hạn", unknown "Không rõ kết quả"), `summarizeResult(cmd)` (một dòng tiếng Việt cho từng lệnh).

- [ ] **Step 1: Test đỏ** — `commands.test.ts`: nhãn đủ 8 lệnh; `summarizeResult` cho scan-ports ("3 cổng: COM3 (Máy Thử), COM5 bận, COM7"), test-port ("Mở được, nhận 42 byte" / lỗi), update (`handedOver` → "Đã giao bản mới, chờ agent nối lại"). `AgentDrawer.test.tsx` (mock service): mở agent → nút lệnh tắt khi `channel=false` (tooltip "Agent đang mất kết nối"); bấm "Quét cổng COM" → gọi `command(id,'scan-ports')`, hiện "Đang chạy" rồi bảng cổng khi lệnh `done`; "Thu hồi" cần gõ tên agent để xác nhận; "Thử cổng" trên cổng đang được máy dùng → cảnh báo "Máy … sẽ tạm ngắt trong N giây" trước khi gửi.
- [ ] **Step 2: Đỏ** — `cd web && npx vitest run src/modules/agent`.
- [ ] **Step 3: Làm** — bảng agent: bấm dòng / nút "Điều khiển" mở `AgentDrawer` (rộng 720): đầu trang tên, trạng thái, máy tính, phiên bản (+ nút "Cập nhật lên x" khi `installerInfo.version` khác), các nút lệnh; mục "Máy xét nghiệm" (trạng thái đường truyền, lần cuối nhận, đã nhận, đang chờ gửi, nút "Nối lại", "Thử cổng"); mục "Cổng COM" (lần quét cuối + nút quét); mục "Lịch sử lệnh" (bảng: lệnh, người ra lệnh, lúc, trạng thái, kết quả tóm tắt; bấm xem JSON). Đang có lệnh chưa xong → hỏi `commandById` mỗi 1 s; drawer mở → danh sách agent + lịch sử mỗi 2 s. `InstrumentModal`: ô cổng COM thành `AutoComplete` gợi ý từ `ports` của agent được chọn (nhãn "COM3 — USB-SERIAL CH340 · đang dùng bởi Máy Thử"), vẫn gõ tay được.
- [ ] **Step 4: Xanh** — `npx vitest run`, `npm run typecheck`, `npm run build`.
- [ ] **Step 5: Kiểm bằng ảnh chụp thật** — server dev + Postgres thử + agent console (`pair` + `run`, `DOTNET_ROOT` đặt) như lần kiểm B: chụp drawer khi online, kết quả quét cổng (macOS trả danh sách rỗng / cổng giả — ghi chú), nạp lại cấu hình xong ≤ 3 s, khởi động lại (agent console thoát → kênh mất → hiện "Mất kết nối"), thu hồi (agent console dừng, bảng "Chưa ghép nối"), lịch sử lệnh. Sửa tới khi đúng.
- [ ] **Step 6: Commit** — `feat(web): điều khiển agent từ web — lệnh, kết quả, lịch sử, chọn cổng COM từ lần quét`.

### Task 8: Tài liệu và danh sách kiểm Windows thật cho phần C

**Files:**
- Modify: `docs/server-agent.md` (kênh lệnh, bảng lệnh, reverse proxy phải cho WebSocket qua — ví dụ nginx `proxy_set_header Upgrade/Connection`), `docs/HUONG-DAN-CAI-DAT.md` + `docs/HUONG-DAN/02-cau-hinh.md`, `docs/HUONG-DAN/04-xu-ly-su-co.md` (agent "Mất kết nối", lệnh "Hết hạn", cập nhật quay về bản cũ)
- Create: `docs/superpowers/checklists/2026-11-19-c-windows-that.md`

- [ ] **Step 1: Viết tài liệu** — mục "Điều khiển agent từ web" cho nhân viên (mỗi nút làm gì, khi nào dùng), mục reverse proxy WebSocket cho IT.
- [ ] **Step 2: Danh sách kiểm Windows thật** (chủ dự án làm cùng lượt task 9 của B): (1) quét cổng thấy đúng COM + tên thiết bị, cổng do phần mềm khác giữ hiện "bận"; (2) thử cổng với máy thật / bộ giả lập COM ảo nhận byte; thử trên cổng máy đang dùng → máy tự nối lại; (3) nối lại một máy; (4) khởi động lại → dịch vụ tự bật lại trong ≤ 1 phút (Recovery), web lại "Đang kết nối"; (5) cập nhật lên bản mới (đổi `<Version>` rồi publish) → web hiện phiên bản mới; (6) cập nhật với bản cố tình hỏng (không chạy lên) → sau ~2 phút quay về bản cũ, lịch sử lệnh "Lỗi" kèm lý do; (7) thu hồi → dịch vụ dừng, kiểu khởi động Disabled, `cho-gui` còn; (8) rút mạng 5 phút trong lúc máy gửi kết quả → kết quả vẫn lên đủ khi có mạng, kênh tự nối lại; (9) server sau nginx HTTPS: kênh `wss://` nối được.
- [ ] **Step 3: Commit** — `docs: điều khiển agent từ web, reverse proxy WebSocket, danh sách kiểm Windows cho phần C`.
