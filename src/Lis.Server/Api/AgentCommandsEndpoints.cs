using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lis.Server.AgentApi;
using Lis.Server.Tenancy;

namespace Lis.Server.Api;

/// <summary>Thân <c>POST /api/agents/{id}/commands</c>. Trường lạ ở cấp này → 400 (bộ lọc chung); <c>args</c> do <see cref="AgentCommandsEndpoints"/> kiểm từng trường.</summary>
public sealed record AgentCommandBody(string? Name, JsonElement? Args);

public sealed record AgentCommandView(
    Guid Id, string Name, JsonElement? Args, string Status, string? RequestedBy, string RequestedAt, string? StartedAt, string? FinishedAt,
    JsonElement? Result, string? Error);

/// <summary>
/// Ra lệnh cho agent qua kênh lệnh (<c>/api/agents/{id}/commands</c>): chỉ Quản trị ra lệnh; Quản trị, Kỹ thuật viên, Trưởng khoa xem lịch sử.
/// Kiểm đầu vào chạy trước kiểm quyền (như các endpoint khác). Tham số lệnh được kiểm rồi dựng lại từ giá trị đã kiểm —
/// không chuyển nguyên JSON của web xuống agent. Không ghi log nội dung tham số / kết quả.
/// </summary>
public static partial class AgentCommandsEndpoints
{
    /// <summary>Sau <c>revoke</c> mà agent đang nối: chờ kết quả tối đa chừng này rồi đóng kênh.</summary>
    internal static TimeSpan RevokeCloseGrace { get; set; } = TimeSpan.FromSeconds(5);

    private const string InvalidBody = "Dữ liệu gửi lên không hợp lệ";
    private const string AgentNotFound = "Không tìm thấy agent";
    private const string CommandNotFound = "Không tìm thấy lệnh";
    private const string NotPaired = "Agent chưa ghép nối";
    private const string InstallerMissing = "Máy chủ chưa có tệp cài agent";
    private const int DefaultLimit = 50;

    private static readonly HashSet<string> Names =
        ["reload-config", "scan-ports", "test-port", "reconnect-instrument", "flush-pending", "restart", "update", "revoke"];

    private static readonly string[] Parities = ["None", "Even", "Odd"];

    [GeneratedRegex(@"^COM\d{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex PortNamePattern();

    public static void MapAgentCommandsApi(this RouteGroupBuilder agents)
    {
        agents.MapPost("/{id}/commands",
            (HttpContext http, MasterStore master, TimeProvider clock, AgentChannelHub hub, AgentCommandStore commands, AgentDistribution distribution, ILoggerFactory loggers, string id, AgentCommandBody body) =>
                SubmitAsync(http.Ctx(), master, clock, hub, commands, distribution, loggers, id, body));
        agents.MapGet("/{id}/commands",
            (HttpContext http, MasterStore master, TimeProvider clock, AgentCommandStore commands, string id) =>
                ListAsync(http.Ctx(), master, clock, commands, id, http.Request.Query));
        agents.MapGet("/commands/{commandId}",
            (HttpContext http, AgentCommandStore commands, string commandId) => GetAsync(http.Ctx(), commands, commandId));
    }

    private static async Task<IResult> SubmitAsync(
        RequestContext ctx, MasterStore master, TimeProvider clock, AgentChannelHub hub, AgentCommandStore commands, AgentDistribution distribution, ILoggerFactory loggers, string id, AgentCommandBody body)
    {
        var agentId = ApiFormat.ParseId(id);
        var name = body.Name ?? throw ApiException.BadRequest(InvalidBody);
        if (!Names.Contains(name)) throw ApiException.BadRequest("Tên lệnh không hợp lệ");
        var args = body.Args is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } given ? given : (JsonElement?)null;
        var shape = CheckArgs(name, args);
        ctx.RequireAdmin();

        var agent = (await master.AgentsOfAsync(ctx.Clinic.Id, clock.GetUtcNow())).FirstOrDefault(a => a.Agent.Id == agentId)
            ?? throw ApiException.NotFound(AgentNotFound);
        if (agent.PairedAt is null) throw ApiException.Conflict(NotPaired);

        string? argsJson;
        switch (name)
        {
            case "reconnect-instrument":
                var instrumentId = (Guid)shape!;
                var mine = (await ctx.Tenant.Instruments.ListAsync())
                    .Any(i => i.Id == instrumentId && string.Equals(i.WorkstationCode ?? "", agent.Agent.WorkstationCode, StringComparison.OrdinalIgnoreCase));
                if (!mine) throw ApiException.BadRequest("Máy này không gán cho agent");
                argsJson = JsonSerializer.Serialize(new { instrumentId });
                break;
            case "update":
                var package = distribution.Current() ?? throw ApiException.Conflict(InstallerMissing);
                if (package.Version is null) throw ApiException.Conflict("Tệp cài trên máy chủ thiếu version.txt — không cập nhật được");
                argsJson = JsonSerializer.Serialize(new { version = package.Version, sha256 = package.Sha256 }, ArgsJson);
                break;
            default:
                argsJson = shape as string;
                break;
        }

        if (name == "revoke")
        {
            return Results.Json(await RevokeAsync(ctx, master, hub, commands, clock, loggers.CreateLogger("Lis.Server.Api.AgentCommands"), agent.Agent), statusCode: StatusCodes.Status202Accepted);
        }

        var row = await hub.SubmitAsync(ctx.Clinic.Id, agentId, name, argsJson, ctx.User.Id, ctx.User.FullName);
        return Results.Json(ViewOf(row), statusCode: StatusCodes.Status202Accepted);
    }

    private static readonly JsonSerializerOptions ArgsJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Thu hồi: khoá bị từ chối NGAY ở server (kể cả agent đang tắt). Agent không nối → lệnh <c>done</c> ngay với <c>{"offline":true}</c>;
    /// đang nối → gửi lệnh để agent tự dọn, đóng kênh khi có kết quả hoặc sau <see cref="RevokeCloseGrace"/>.
    /// </summary>
    private static async Task<AgentCommandView> RevokeAsync(RequestContext ctx, MasterStore master, AgentChannelHub hub, AgentCommandStore commands, TimeProvider clock, ILogger logger, AgentInfo agent)
    {
        // Chụp kênh đang nối trước khi thu hồi: chỉ đóng đúng kênh này, không đóng kênh mới nếu agent ghép nối lại ngay.
        var connection = hub.CurrentConnection(agent.Id);
        if (!await master.RevokeAgentAsync(ctx.Clinic.Id, agent.Id)) throw ApiException.NotFound(AgentNotFound);

        var row = await hub.SubmitAsync(ctx.Clinic.Id, agent.Id, "revoke", null, ctx.User.Id, ctx.User.FullName);
        if (row.Status == "queued")
        {
            // Không có kênh: không ai nhận lệnh, khoá đã vô hiệu nên đánh dấu xong luôn.
            if (await commands.TransitionAsync(row.Id, ["queued"], "done", clock.GetUtcNow(), """{"offline":true}"""))
            {
                row = await commands.FindAsync(ctx.Clinic.Id, row.Id) ?? row;
            }
        }

        if (connection is not null && row.Status is "sent" or "running")
        {
            _ = CloseAfterResultAsync(commands, hub, logger, ctx.Clinic.Id, agent.Id, row.Id, connection);
        }

        return ViewOf(row);
    }

    /// <summary>Đóng đúng kênh <paramref name="connection"/> khi lệnh <c>revoke</c> có kết quả hoặc hết <see cref="RevokeCloseGrace"/> (đồng hồ thật, hỏi CSDL mỗi 250 ms).</summary>
    private static async Task CloseAfterResultAsync(
        AgentCommandStore commands, AgentChannelHub hub, ILogger logger, Guid clinicId, Guid agentId, Guid commandId, object connection)
    {
        try
        {
            var deadline = DateTime.UtcNow + RevokeCloseGrace;
            while (DateTime.UtcNow < deadline)
            {
                if (await commands.FindAsync(clinicId, commandId) is not { Status: "sent" or "running" or "queued" })
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(10, RevokeCloseGrace.TotalMilliseconds / 4))));
            }

            await hub.DisconnectAsync(agentId, connection);
        }
        catch (Exception ex)
        {
            // Đóng kênh là việc phụ: khoá đã bị thu hồi nên agent không nối lại được. Không ghi nội dung lệnh.
            logger.LogDebug(ex, "Không đóng được kênh của agent {AgentId} sau thu hồi", agentId);
        }
    }

    private static async Task<IResult> ListAsync(
        RequestContext ctx, MasterStore master, TimeProvider clock, AgentCommandStore commands, string id, IQueryCollection query)
    {
        var agentId = ApiFormat.ParseId(id);
        ctx.RequireLabStaff();
        if (!(await master.AgentsOfAsync(ctx.Clinic.Id, clock.GetUtcNow())).Any(a => a.Agent.Id == agentId)) throw ApiException.NotFound(AgentNotFound);
        var limit = query.TryGetValue("limit", out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : DefaultLimit;
        return Results.Ok((await commands.ListAsync(ctx.Clinic.Id, agentId, limit)).Select(ViewOf));
    }

    private static async Task<IResult> GetAsync(RequestContext ctx, AgentCommandStore commands, string commandId)
    {
        var id = ApiFormat.ParseId(commandId);
        ctx.RequireLabStaff();
        return Results.Ok(ViewOf(await commands.FindAsync(ctx.Clinic.Id, id) ?? throw ApiException.NotFound(CommandNotFound)));
    }

    internal static AgentCommandView ViewOf(AgentCommandRow row) => new(
        row.Id, row.Name, row.Args, row.Status, row.RequestedByName, ApiFormat.Iso(row.RequestedAt),
        row.StartedAt is { } started ? ApiFormat.Iso(started) : null, row.FinishedAt is { } finished ? ApiFormat.Iso(finished) : null,
        row.Result, row.Error);

    // ---- kiểm tham số ----

    /// <summary>
    /// Kiểm <c>args</c> theo tên lệnh (chưa cần biết agent). Trả giá trị đã kiểm: JSON dựng lại cho <c>test-port</c>, <c>Guid</c> cho
    /// <c>reconnect-instrument</c>, null cho lệnh không có tham số (và <c>update</c>, do server tự điền).
    /// </summary>
    private static object? CheckArgs(string name, JsonElement? args)
    {
        switch (name)
        {
            case "test-port":
                return CheckTestPort(args ?? throw ApiException.BadRequest("Thiếu tham số thử cổng"));
            case "reconnect-instrument":
                var element = args ?? throw ApiException.BadRequest("Thiếu mã máy");
                OnlyKeys(element, "instrumentId");
                return element.TryGetProperty("instrumentId", out var value) && value.ValueKind == JsonValueKind.String
                    && Guid.TryParseExact(value.GetString(), "D", out var instrumentId)
                    ? instrumentId
                    : throw ApiException.BadRequest("Mã máy không hợp lệ");
            default:
                if (args is not null) throw ApiException.BadRequest("Lệnh này không nhận tham số");
                return null;
        }
    }

    private static string CheckTestPort(JsonElement args)
    {
        OnlyKeys(args, "portName", "baudRate", "dataBits", "parity", "stopBits", "seconds");
        var portName = args.TryGetProperty("portName", out var port) && port.ValueKind == JsonValueKind.String ? port.GetString()!.Trim().ToUpperInvariant() : null;
        if (portName is null || !PortNamePattern().IsMatch(portName)) throw ApiException.BadRequest("Tên cổng COM không hợp lệ");
        var baudRate = Int(args, "baudRate");
        if (baudRate is not (>= 300 and <= 115200)) throw ApiException.BadRequest("Tốc độ baud phải từ 300 đến 115200");
        var dataBits = Int(args, "dataBits");
        if (dataBits is not (7 or 8)) throw ApiException.BadRequest("Số bit dữ liệu phải là 7 hoặc 8");
        var parity = args.TryGetProperty("parity", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (parity is null || !Parities.Contains(parity)) throw ApiException.BadRequest("Parity không hợp lệ");
        var stopBits = Int(args, "stopBits");
        if (stopBits is not (1 or 2)) throw ApiException.BadRequest("Stop bit phải là 1 hoặc 2");
        var seconds = Int(args, "seconds");
        if (seconds is not (>= 1 and <= 60)) throw ApiException.BadRequest("Thời gian nghe phải từ 1 đến 60 giây");
        return JsonSerializer.Serialize(new { portName, baudRate, dataBits, parity, stopBits, seconds }, ArgsJson);
    }

    private static int? Int(JsonElement args, string key) =>
        args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary><c>args</c> phải là đối tượng và chỉ có các trường cho phép (trường lạ → 400, như thân các endpoint khác).</summary>
    private static void OnlyKeys(JsonElement args, params string[] allowed)
    {
        if (args.ValueKind != JsonValueKind.Object) throw ApiException.BadRequest(InvalidBody);
        foreach (var property in args.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)) throw ApiException.BadRequest(InvalidBody);
        }
    }
}
