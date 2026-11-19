using System.Text;
using System.Text.Json;
using Dapper;
using Lis.Data.Instruments;
using Lis.Server.Api;
using Lis.Server.Tenancy;

namespace Lis.Server.AgentApi;

/// <summary>Thông tin agent nhận về khi khởi động.</summary>
public sealed record AgentMe(string ClinicCode, string ClinicName, string AgentName, string WorkstationCode);

public sealed record OrderQueryRequest(Guid InstrumentId, string Barcode);

public sealed record OrderQueryResponse(IReadOnlyList<string> ChannelCodes);

/// <summary>Trạng thái kết nối một máy do agent báo về (hiện trên web).</summary>
public sealed record InstrumentStatusReport(
    Guid InstrumentId, string State, string Text, DateTimeOffset? LastResultAt, int SamplesReceived, int PendingToRetry);

public sealed record StatusRequest(IReadOnlyList<InstrumentStatusReport> Instruments, string? AgentVersion = null, string? MachineName = null);

/// <summary>Agent đổi mã ghép nối (hiện trên web, dán vào ô nhập hoặc nằm trong tên tệp cài) lấy khoá.</summary>
public sealed record PairRequest(string? PairingSecret, string? MachineName, string? AgentVersion);

/// <summary>Khoá agent trả về MỘT lần duy nhất, kèm thông tin để agent lưu cấu hình.</summary>
public sealed record PairResponse(Guid AgentId, string AgentKey, Guid ClinicId, string ClinicCode, string ClinicName, string AgentName);

/// <summary>Một dòng log agent đẩy lên: byte đi/về với máy (hex), sự kiện kết nối, hoặc cảnh báo của agent (không có máy).</summary>
public sealed record AgentLogLine(Guid? InstrumentId, DateTimeOffset At, string Direction, string? Session, string? Hex, string? Note);

public sealed record LogsRequest(IReadOnlyList<AgentLogLine> Entries);

/// <summary>
/// API cho Lis.Agent (<c>/agent/v1</c>). Xác thực bằng header <c>X-Agent-Key</c>; khoá xác định
/// luôn phòng khám, nên agent chỉ chạm được CSDL của phòng khám mình và chỉ các máy gán cho
/// máy trạm của nó.
/// </summary>
public static class AgentEndpoints
{
    public const string KeyHeader = "X-Agent-Key";
    private const string IdentityItem = "agent-identity";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapAgentApi(this WebApplication app)
    {
        // Chưa có khoá — nằm ngoài nhóm có bộ lọc khoá bên dưới.
        app.MapPost("/agent/v1/pair", PairAsync);

        var group = app.MapGroup("/agent/v1");

        group.AddEndpointFilter(async (context, next) =>
        {
            var master = context.HttpContext.RequestServices.GetRequiredService<MasterStore>();
            var identity = await master.AuthenticateAsync(context.HttpContext.Request.Headers[KeyHeader].FirstOrDefault());
            if (identity is null)
            {
                return Results.Problem("Khoá agent không hợp lệ", statusCode: StatusCodes.Status401Unauthorized);
            }

            if (ClinicUpgradeFailure.IsFailed(context.HttpContext.RequestServices, identity.Clinic))
            {
                // Không để agent ghi kết quả vào CSDL chưa nâng cấp xong.
                return Results.Problem(ClinicUpgradeFailure.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            context.HttpContext.Items[IdentityItem] = identity;
            return await next(context);
        });

        group.MapAgentChannel();

        // Tệp cài agent cho lệnh update: chỉ tải từ chính server đã ghép nối; agent so SHA-256 với giá trị trong lệnh.
        group.MapGet("/installer", (HttpContext http, AgentDistribution distribution) =>
        {
            var package = distribution.Current();
            if (package is null)
            {
                return Results.Problem("Máy chủ chưa có tệp cài agent", statusCode: StatusCodes.Status404NotFound);
            }

            http.Response.Headers["Content-Disposition"] = "attachment; filename=\"LisAgent.exe\"";
            http.Response.Headers["X-Agent-Sha256"] = package.Sha256;
            if (package.Version is not null) http.Response.Headers["X-Agent-Version"] = package.Version;
            return Results.File(package.Path, "application/vnd.microsoft.portable-executable", enableRangeProcessing: false);
        });

        group.MapGet("/me", (HttpContext http) =>
        {
            var id = Identity(http);
            return new AgentMe(id.Clinic.Code, id.Clinic.Name, id.Agent.Name, id.Agent.WorkstationCode);
        });

        // Các máy gán cho máy trạm của agent này (kèm cấu hình kết nối, driver).
        group.MapGet("/instruments", async (HttpContext http, TenantRegistry tenants) =>
        {
            var id = Identity(http);
            var all = await tenants.For(id.Clinic).Instruments.ListAsync();
            return all.Where(i => IsMine(i, id)).ToList();
        });

        // Bản tin thô máy gửi: lưu rồi giải mã ở server (thay cho /readings cũ).
        group.MapRawMessages();

        group.MapPost("/order-queries", async (OrderQueryRequest request, HttpContext http, TenantRegistry tenants, CancellationToken ct) =>
        {
            var id = Identity(http);
            var tenant = tenants.For(id.Clinic);
            if (!await OwnsInstrumentAsync(tenant, id, request.InstrumentId))
            {
                return Results.Problem("Máy này không gán cho agent", statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(new OrderQueryResponse(await tenant.OrderQueries.ChannelsToRunAsync(request.InstrumentId, request.Barcode, ct)));
        });

        // Log thô (dữ liệu máy gửi/nhận nguyên văn + sự kiện) để xem trên web. Lô tối đa 500 dòng.
        group.MapPost("/logs", async (LogsRequest request, HttpContext http, TenantRegistry tenants) =>
        {
            var id = Identity(http);
            if (request.Entries.Count > MaxLogBatch)
            {
                return Results.Problem($"Mỗi lô tối đa {MaxLogBatch} dòng log", statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var tenant = tenants.For(id.Clinic);
            var mine = (await tenant.Instruments.ListAsync()).Where(i => IsMine(i, id)).Select(i => i.Id).ToHashSet();
            var rows = request.Entries
                .Where(e => Directions.Contains(e.Direction) && (e.InstrumentId is null || mine.Contains(e.InstrumentId.Value)))
                .Select(e => new
                {
                    agentId = id.Agent.Id,
                    instrumentId = e.InstrumentId,
                    at = e.At.ToUniversalTime(),
                    direction = e.Direction,
                    session = Truncate(e.Session, 64),
                    hex = Truncate(e.Hex, MaxHexLength),
                    note = Truncate(e.Note, 2000),
                })
                .ToList();

            await using var connection = await tenant.Db.OpenAsync();
            if (rows.Count > 0)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO agent_log (agent_id, instrument_id, at, direction, session, hex, note)
                    VALUES (@agentId, @instrumentId, @at, @direction, @session, @hex, @note)
                    """,
                    rows);
            }

            // Dọn dòng cũ: mỗi phòng khám tối đa một lần mỗi giờ.
            var now = DateTimeOffset.UtcNow;
            if (LastPurge.GetValueOrDefault(id.Clinic.Id) < now.AddHours(-1))
            {
                LastPurge[id.Clinic.Id] = now;
                await connection.ExecuteAsync($"DELETE FROM agent_log WHERE at < now() - interval '{LogRetentionDays} days'");
            }

            return Results.Ok(new { stored = rows.Count });
        });

        group.MapPost("/status", async (StatusRequest request, HttpContext http, MasterStore master) =>
        {
            await master.TouchAgentAsync(
                Identity(http).Agent.Id, JsonSerializer.Serialize(request.Instruments, Json),
                Clean(request.MachineName, MaxMachineName), Clean(request.AgentVersion, MaxAgentVersion));
            return Results.NoContent();
        });
    }

    private const int MaxMachineName = 100;
    private const int MaxAgentVersion = 50;
    private const int MaxLogBatch = 500;
    private const int MaxHexLength = 64 * 1024;
    private const int LogRetentionDays = 14;
    private static readonly HashSet<string> Directions = ["in", "out", "event", "agent"];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> LastPurge = new();

    private static async Task<IResult> PairAsync(PairRequest request, HttpContext http, MasterStore master, TimeProvider clock)
    {
        var secret = request.PairingSecret?.Trim();
        if (secret is null || secret.Length != 32 || !secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return Results.Problem("Mã ghép nối không hợp lệ", statusCode: StatusCodes.Status400BadRequest);
        }

        // Phòng khám nâng cấp lỗi thì không tiêu mã — agent thử lại sau khi quản trị sửa xong.
        if (await master.FindPairingClinicAsync(secret) is { } clinic && ClinicUpgradeFailure.IsFailed(http.RequestServices, clinic))
        {
            return Results.Problem(ClinicUpgradeFailure.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var result = await master.RedeemPairingAsync(
            secret, Clean(request.MachineName, MaxMachineName), Clean(request.AgentVersion, MaxAgentVersion), clock.GetUtcNow());
        if (result.Failure is { } failure)
        {
            return failure switch
            {
                PairingFailure.Used => Results.Problem(
                    "Mã ghép nối đã được dùng — bấm Ghép nối lại trên web để lấy mã mới", statusCode: StatusCodes.Status410Gone),
                PairingFailure.Expired => Results.Problem(
                    "Mã ghép nối đã hết hạn (chỉ dùng trong 30 phút) — bấm Ghép nối lại trên web để lấy mã mới", statusCode: StatusCodes.Status410Gone),
                _ => Results.Problem(
                    "Mã ghép nối không đúng — lấy mã mới ở mục Agent trên web", statusCode: StatusCodes.Status401Unauthorized),
            };
        }

        var identity = result.Identity!;
        return Results.Ok(new PairResponse(
            identity.Agent.Id, result.AgentKey!, identity.Clinic.Id, identity.Clinic.Code, identity.Clinic.Name, identity.Agent.Name));
    }

    /// <summary>Bỏ khoảng trắng đầu/cuối, rỗng → null (giữ giá trị cũ), cắt tối đa <paramref name="max"/> ký tự.</summary>
    internal static string? Clean(string? text, int max) => string.IsNullOrWhiteSpace(text) ? null : Truncate(text.Trim(), max);

    private static string? Truncate(string? text, int max) => text is null || text.Length <= max ? text : text[..max];

    internal static AgentIdentity Identity(HttpContext http) => (AgentIdentity)http.Items[IdentityItem]!;

    private static bool IsMine(InstrumentInfo instrument, AgentIdentity id) =>
        string.Equals(instrument.WorkstationCode, id.Agent.WorkstationCode, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> OwnsInstrumentAsync(TenantServices tenant, AgentIdentity id, Guid instrumentId) =>
        (await tenant.Instruments.ListAsync()).Any(i => i.Id == instrumentId && IsMine(i, id));
}
