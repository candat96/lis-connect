using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Lis.Core.Agents;
using Lis.Server.Tenancy;

namespace Lis.Server.Api;

/// <summary>Thân <c>/api/agents</c>: chỉ có tên. Mã máy trạm do server tự sinh, nên thân gửi lên có <c>workstationCode</c> là trường lạ → 400.</summary>
public sealed record AgentBody(string? Name);

public sealed record AgentInstrumentView(Guid Id, string Name, JsonElement? Status);

/// <summary>
/// Một agent trong danh sách. <see cref="Paired"/> = đã đổi mã ghép nối lấy khoá; <see cref="PairingExpiresAt"/> có giá trị khi còn một mã chưa dùng và chưa hết hạn.
/// </summary>
public sealed record AgentView(
    Guid Id, string Name, string WorkstationCode, string? LastSeenAt, bool Online, IReadOnlyList<AgentInstrumentView> Instruments,
    bool Paired, string? PairedAt, string? MachineName, string? Version, string? PairingExpiresAt);

public sealed record AgentLogView(
    string Id, string At, Guid AgentId, Guid? InstrumentId, string? InstrumentName, string Direction, string? Session, string? Hex, string? Note);

/// <summary>
/// Agent (<c>/api/agents</c>) và log agent đẩy lên (<c>/api/agent-logs</c>). Agent: Quản trị, Kỹ thuật viên, Trưởng khoa xem; chỉ Quản trị ghi.
/// Tạo agent xong thì lấy <b>mã ghép nối</b> dùng một lần (30 phút); agent đổi mã lấy khoá qua <c>/agent/v1/pair</c>, khoá không đi qua web.
/// Agent của phòng khám khác không thấy được: mọi thao tác theo id đều gắn với phòng khám của phiên. Kiểm đầu vào chạy trước kiểm quyền, giống pipe chạy trước handler ở NestJS.
/// </summary>
public static class AgentsEndpoints
{
    /// <summary>Agent im quá khoảng này thì coi là offline (agent báo mỗi 30 giây).</summary>
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    private const string InvalidBody = "Dữ liệu gửi lên không hợp lệ";
    private const string AgentNotFound = "Không tìm thấy agent";
    private const int DefaultLogLimit = 300;
    private const int MaxLogLimit = 1000;
    private static readonly string[] Directions = ["in", "out", "event", "agent"];

    public static void MapAgentsApi(this RouteGroupBuilder api)
    {
        var agents = api.MapGroup("/agents").RequireSession();
        agents.MapGet("", (HttpContext http, MasterStore master, TimeProvider clock) => ListAsync(http.Ctx(), master, clock));
        agents.MapPost("", (HttpContext http, MasterStore master, TimeProvider clock, ServerOptions options, AgentBody body) =>
            CreateAsync(http, master, clock, options, body));
        agents.MapPatch("/{id}", (HttpContext http, MasterStore master, string id, AgentBody body) => UpdateAsync(http.Ctx(), master, id, body));
        agents.MapPost("/{id}/pairing", (HttpContext http, MasterStore master, TimeProvider clock, ServerOptions options, string id) =>
            PairingAsync(http, master, clock, options, id));
        agents.MapDelete("/{id}", (HttpContext http, MasterStore master, TimeProvider clock, string id) => DeleteAsync(http.Ctx(), master, clock, id));

        // Ép về Delegate: lambda chỉ nhận HttpContext sẽ bị hiểu là RequestDelegate và bỏ mất kết quả trả về.
        api.MapGroup("/agent-logs").RequireSession().MapGet("", (Delegate)((HttpContext http) => LogsAsync(http.Ctx(), http.Request.Query)));
    }

    private static async Task<IResult> ListAsync(RequestContext ctx, MasterStore master, TimeProvider clock)
    {
        ctx.RequireLabStaff();
        var now = clock.GetUtcNow();
        var agents = await master.AgentsOfAsync(ctx.Clinic.Id, now);
        var instruments = await ctx.Tenant.Instruments.ListAsync();

        return Results.Ok(agents.Select(a =>
        {
            var online = a.Agent.LastSeenAt is { } seen && now - seen < OnlineWindow;
            var mine = instruments
                .Where(i => string.Equals(i.WorkstationCode ?? "", a.Agent.WorkstationCode, StringComparison.OrdinalIgnoreCase))
                .Select(i => new AgentInstrumentView(i.Id, i.Name, online ? InstrumentsEndpoints.StatusOf(a.LastStatusJson, i.Id) : null))
                .ToList();
            return new AgentView(
                a.Agent.Id, a.Agent.Name, a.Agent.WorkstationCode, a.Agent.LastSeenAt is { } last ? ApiFormat.Iso(last) : null, online, mine,
                a.PairedAt is not null, a.PairedAt is { } paired ? ApiFormat.Iso(paired) : null, a.MachineName, a.AgentVersion,
                a.PairingExpiresAt is { } expires ? ApiFormat.Iso(expires) : null);
        }));
    }

    /// <summary>
    /// Tạo agent chưa có khoá kèm mã ghép nối dùng một lần. Mã chỉ trả về MỘT lần ở đây; quá 30 phút hoặc đã dùng thì bấm ghép nối lại.
    /// </summary>
    private static async Task<IResult> CreateAsync(HttpContext http, MasterStore master, TimeProvider clock, ServerOptions options, AgentBody body)
    {
        var ctx = http.Ctx();
        var name = CheckName(body);
        ctx.RequireAdmin();
        var (agent, pairing) = await master.CreateAgentAsync(ctx.Clinic.Id, name, ctx.User.Id, clock.GetUtcNow());
        return Results.Json(new { id = agent.Id, pairing = PairingView(http, options, ctx, pairing) }, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(RequestContext ctx, MasterStore master, string id, AgentBody body)
    {
        var agentId = ApiFormat.ParseId(id);
        var name = CheckName(body);
        ctx.RequireAdmin();
        try
        {
            await master.RenameAgentAsync(ctx.Clinic.Id, agentId, name);
        }
        catch (AgentException ex)
        {
            throw Translate(ex);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Cấp mã ghép nối mới cho agent (mã chưa dùng trước đó bị huỷ). Khoá đang chạy vẫn dùng được tới khi agent đổi mã mới.
    /// </summary>
    private static async Task<IResult> PairingAsync(HttpContext http, MasterStore master, TimeProvider clock, ServerOptions options, string id)
    {
        var agentId = ApiFormat.ParseId(id);
        var ctx = http.Ctx();
        ctx.RequireAdmin();
        try
        {
            var ticket = await master.CreatePairingAsync(ctx.Clinic.Id, agentId, ctx.User.Id, clock.GetUtcNow());
            return Results.Json(PairingView(http, options, ctx, ticket), statusCode: StatusCodes.Status201Created);
        }
        catch (AgentException ex)
        {
            throw Translate(ex);
        }
    }

    private static async Task<IResult> DeleteAsync(RequestContext ctx, MasterStore master, TimeProvider clock, string id)
    {
        var agentId = ApiFormat.ParseId(id);
        ctx.RequireAdmin();
        var agent = await FindAsync(master, clock, ctx, agentId);
        await using (var connection = await ctx.Tenant.Db.OpenAsync())
        {
            var assigned = await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM instrument WHERE lower(workstation_code) = lower(@code)", new { code = agent.WorkstationCode });
            if (assigned > 0)
            {
                throw ApiException.Conflict($"Agent đang phụ trách {assigned} máy xét nghiệm — chuyển các máy đó sang agent khác trước khi xoá");
            }
        }

        if (!await master.DeleteAgentAsync(ctx.Clinic.Id, agentId)) throw ApiException.NotFound(AgentNotFound);
        return Results.NoContent();
    }

    // ---- dùng chung ----

    /// <summary>Agent phải thuộc phòng khám của phiên — không đụng được agent của phòng khám khác.</summary>
    private static async Task<AgentInfo> FindAsync(MasterStore master, TimeProvider clock, RequestContext ctx, Guid id) =>
        (await master.AgentsOfAsync(ctx.Clinic.Id, clock.GetUtcNow())).Select(a => a.Agent).FirstOrDefault(a => a.Id == id) ?? throw ApiException.NotFound(AgentNotFound);

    /// <summary>
    /// Địa chỉ server đưa vào mã ghép nối: <c>PublicUrl</c> nếu đặt, không thì địa chỉ request đang đến
    /// (đã qua forwarded headers nếu bật <c>Proxy:TrustForwardedHeaders</c>).
    /// </summary>
    private static string ServerUrl(HttpContext http, ServerOptions options) =>
        options.PublicUrl ?? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";

    private static object PairingView(HttpContext http, ServerOptions options, RequestContext ctx, PairingTicket ticket) => new
    {
        code = new PairingCode(ServerUrl(http, options), ctx.Clinic.Code, ticket.Secret).Encode(),
        expiresAt = ApiFormat.Iso(ticket.ExpiresAt),
    };

    private static ApiException Translate(AgentException ex) => ex.Failure switch
    {
        AgentFailure.NotFound => ApiException.NotFound(ex.Message),
        _ => ApiException.Conflict(ex.Message),
    };

    /// <summary>Kiểm tên (cùng câu báo lỗi như DTO ở NestJS) và cắt khoảng trắng.</summary>
    private static string CheckName(AgentBody body)
    {
        var name = body.Name ?? throw ApiException.BadRequest(InvalidBody);
        if (name.Trim().Length == 0) throw ApiException.BadRequest("Nhập tên agent");
        if (name.Length > 100) throw ApiException.BadRequest(InvalidBody);
        return name.Trim();
    }

    // ---- log ----

    private sealed class LogRow
    {
        public long Id { get; init; }
        public DateTimeOffset At { get; init; }
        public Guid AgentId { get; init; }
        public Guid? InstrumentId { get; init; }
        public string? InstrumentName { get; init; }
        public string Direction { get; init; } = "";
        public string? Session { get; init; }
        public string? Hex { get; init; }
        public string? Note { get; init; }
    }

    /// <summary>
    /// Mới nhất trước, tối đa <c>limit</c> dòng (mặc định 300, tối đa 1000). <c>beforeId</c> xem tiếp dòng cũ hơn;
    /// <c>afterId</c> chỉ lấy dòng mới hơn dòng đã có (tự làm mới).
    /// </summary>
    private static async Task<IResult> LogsAsync(RequestContext ctx, IQueryCollection query)
    {
        ctx.RequireLabStaff();
        var parameters = new DynamicParameters();
        var conditions = new List<string>();

        if (Text(query, "agentId") is { Length: > 0 } agentId)
        {
            if (!Guid.TryParseExact(agentId, "D", out var agent)) throw ApiException.BadRequest("agentId không hợp lệ");
            parameters.Add("agentId", agent);
            conditions.Add("l.agent_id = @agentId");
        }

        if (Text(query, "instrumentId") is { Length: > 0 } instrumentId)
        {
            if (!Guid.TryParseExact(instrumentId, "D", out var instrument)) throw ApiException.BadRequest("instrumentId không hợp lệ");
            parameters.Add("instrumentId", instrument);
            conditions.Add("l.instrument_id = @instrumentId");
        }

        if (Text(query, "direction") is { Length: > 0 } direction)
        {
            var wanted = direction.Split(',').Where(Directions.Contains).ToArray();
            if (wanted.Length == 0) throw ApiException.BadRequest("Chiều log không hợp lệ");
            parameters.Add("directions", wanted);
            conditions.Add("l.direction = ANY(@directions)");
        }

        if (Text(query, "q") is { } q && q.Trim().Length > 0)
        {
            // Tìm trong ghi chú và trong nội dung byte (so trên chuỗi hex của từ khoá, vd barcode).
            var needle = q.Trim();
            parameters.Add("noteLike", "%" + EscapeLike(needle) + "%");
            parameters.Add("hexLike", "%" + Convert.ToHexString(Encoding.UTF8.GetBytes(needle)) + "%");
            conditions.Add("(l.note ILIKE @noteLike ESCAPE '\\' OR l.hex LIKE @hexLike)");
        }

        if (Text(query, "beforeId") is { Length: > 0 } beforeId)
        {
            parameters.Add("beforeId", LogId(beforeId, "beforeId"));
            conditions.Add("l.id < @beforeId");
        }

        if (Text(query, "afterId") is { Length: > 0 } afterId)
        {
            parameters.Add("afterId", LogId(afterId, "afterId"));
            conditions.Add("l.id > @afterId");
        }

        parameters.Add("limit", Limit(Text(query, "limit")));
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        await using var connection = await ctx.Tenant.Db.OpenAsync();
        var rows = await connection.QueryAsync<LogRow>(
            $"""
            SELECT l.id, l.at, l.agent_id AS AgentId, l.instrument_id AS InstrumentId, i.name AS InstrumentName,
                   l.direction, l.session, l.hex, l.note
            FROM agent_log l LEFT JOIN instrument i ON i.id = l.instrument_id
            {where}
            ORDER BY l.id DESC LIMIT @limit
            """,
            parameters);

        return Results.Ok(rows.Select(r => new AgentLogView(
            r.Id.ToString(CultureInfo.InvariantCulture), ApiFormat.Iso(r.At), r.AgentId, r.InstrumentId, r.InstrumentName,
            r.Direction, r.Session, r.Hex, r.Note)));
    }

    private static string? Text(IQueryCollection query, string key) => query.TryGetValue(key, out var values) ? values.FirstOrDefault() : null;

    /// <summary>Chỉ chữ số; số quá lớn so với cột id (bigint) cũng là không hợp lệ.</summary>
    private static long LogId(string text, string field) =>
        text.All(char.IsAsciiDigit) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw ApiException.BadRequest($"{field} không hợp lệ");

    /// <summary>Như <c>Math.min(1000, Math.max(1, Number(text) || 300))</c>: không đọc được hoặc 0 → mặc định 300.</summary>
    private static int Limit(string? text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number == 0 || double.IsNaN(number))
        {
            return DefaultLogLimit;
        }

        return (int)Math.Round(Math.Min(MaxLogLimit, Math.Max(1, number)), MidpointRounding.AwayFromZero);
    }

    /// <summary>Thoát ký tự đại diện của LIKE để chuỗi người dùng gõ được so theo nghĩa đen.</summary>
    private static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
