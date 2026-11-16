using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Lis.Server.Tenancy;

namespace Lis.Server.Api;

public sealed record AgentBody(string? Name, string? WorkstationCode);

public sealed record AgentInstrumentView(Guid Id, string Name, JsonElement? Status);

public sealed record AgentView(
    Guid Id, string Name, string WorkstationCode, string? LastSeenAt, bool Online, IReadOnlyList<AgentInstrumentView> Instruments);

public sealed record AgentLogView(
    string Id, string At, Guid AgentId, Guid? InstrumentId, string? InstrumentName, string Direction, string? Session, string? Hex, string? Note);

/// <summary>
/// Agent (<c>/api/agents</c>) và log agent đẩy lên (<c>/api/agent-logs</c>). Agent: Quản trị, Kỹ thuật viên, Trưởng khoa xem; chỉ Quản trị ghi.
/// Khoá sinh bằng <see cref="MasterStore"/> nên <c>/agent/v1</c> nhận được. Agent của phòng khám khác không thấy được:
/// mọi thao tác theo id đều gắn với phòng khám của phiên. Kiểm đầu vào chạy trước kiểm quyền, giống pipe chạy trước handler ở NestJS.
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
        agents.MapPost("", (HttpContext http, MasterStore master, AgentBody body) => CreateAsync(http.Ctx(), master, body));
        agents.MapPatch("/{id}", (HttpContext http, MasterStore master, string id, AgentBody body) => UpdateAsync(http.Ctx(), master, id, body));
        agents.MapPost("/{id}/key", (HttpContext http, MasterStore master, string id) => RegenerateKeyAsync(http.Ctx(), master, id));
        agents.MapDelete("/{id}", (HttpContext http, MasterStore master, string id) => DeleteAsync(http.Ctx(), master, id));

        // Ép về Delegate: lambda chỉ nhận HttpContext sẽ bị hiểu là RequestDelegate và bỏ mất kết quả trả về.
        api.MapGroup("/agent-logs").RequireSession().MapGet("", (Delegate)((HttpContext http) => LogsAsync(http.Ctx(), http.Request.Query)));
    }

    private static async Task<IResult> ListAsync(RequestContext ctx, MasterStore master, TimeProvider clock)
    {
        ctx.RequireLabStaff();
        var agents = await master.AgentsOfAsync(ctx.Clinic.Id);
        var instruments = await ctx.Tenant.Instruments.ListAsync();
        var now = clock.GetUtcNow();

        return Results.Ok(agents.Select(a =>
        {
            var online = a.Agent.LastSeenAt is { } seen && now - seen < OnlineWindow;
            var mine = instruments
                .Where(i => string.Equals(i.WorkstationCode ?? "", a.Agent.WorkstationCode, StringComparison.OrdinalIgnoreCase))
                .Select(i => new AgentInstrumentView(i.Id, i.Name, online ? InstrumentsEndpoints.StatusOf(a.LastStatusJson, i.Id) : null))
                .ToList();
            return new AgentView(
                a.Agent.Id, a.Agent.Name, a.Agent.WorkstationCode, a.Agent.LastSeenAt is { } last ? UsersEndpoints.Iso(last) : null, online, mine);
        }));
    }

    /// <summary>Tạo agent. Khoá chỉ trả về MỘT lần ở đây — dán vào appsettings.json của agent.</summary>
    private static async Task<IResult> CreateAsync(RequestContext ctx, MasterStore master, AgentBody body)
    {
        var (name, workstationCode) = Check(body);
        ctx.RequireAdmin();
        await EnsureWorkstationFreeAsync(master, ctx, workstationCode, null);
        try
        {
            var (agent, key) = await master.AddAgentAsync(ctx.Clinic.Id, name, workstationCode);
            return Results.Json(new { id = agent.Id, key }, statusCode: StatusCodes.Status201Created);
        }
        catch (AgentException ex)
        {
            throw Translate(ex);
        }
    }

    private static async Task<IResult> UpdateAsync(RequestContext ctx, MasterStore master, string id, AgentBody body)
    {
        var agentId = ParseId(id);
        var (name, workstationCode) = Check(body);
        ctx.RequireAdmin();
        await FindAsync(master, ctx, agentId);
        await EnsureWorkstationFreeAsync(master, ctx, workstationCode, agentId);
        string previous;
        try
        {
            previous = await master.UpdateAgentAsync(ctx.Clinic.Id, agentId, name, workstationCode);
        }
        catch (AgentException ex)
        {
            throw Translate(ex);
        }

        // Đổi mã máy trạm: các máy đang gán cho agent này đi theo mã mới, không bị "mồ côi".
        if (!string.Equals(workstationCode, previous, StringComparison.OrdinalIgnoreCase))
        {
            await using var connection = await ctx.Tenant.Db.OpenAsync();
            await connection.ExecuteAsync(
                "UPDATE instrument SET workstation_code = @code WHERE lower(workstation_code) = lower(@previous)", new { code = workstationCode, previous });
        }

        return Results.NoContent();
    }

    /// <summary>Cấp khoá mới (khoá cũ hết hiệu lực ngay) — dùng khi lộ khoá hoặc cài lại máy tính agent.</summary>
    private static async Task<IResult> RegenerateKeyAsync(RequestContext ctx, MasterStore master, string id)
    {
        var agentId = ParseId(id);
        ctx.RequireAdmin();
        try
        {
            return Results.Json(new { key = await master.RegenerateAgentKeyAsync(ctx.Clinic.Id, agentId) }, statusCode: StatusCodes.Status201Created);
        }
        catch (AgentException ex)
        {
            throw Translate(ex);
        }
    }

    private static async Task<IResult> DeleteAsync(RequestContext ctx, MasterStore master, string id)
    {
        var agentId = ParseId(id);
        ctx.RequireAdmin();
        var agent = await FindAsync(master, ctx, agentId);
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

    private static Guid ParseId(string id) =>
        Guid.TryParseExact(id, "D", out var guid) ? guid : throw ApiException.BadRequest("Validation failed (uuid is expected)");

    /// <summary>Agent phải thuộc phòng khám của phiên — không đụng được agent của phòng khám khác.</summary>
    private static async Task<AgentInfo> FindAsync(MasterStore master, RequestContext ctx, Guid id) =>
        (await master.AgentsOfAsync(ctx.Clinic.Id)).Select(a => a.Agent).FirstOrDefault(a => a.Id == id) ?? throw ApiException.NotFound(AgentNotFound);

    private static async Task EnsureWorkstationFreeAsync(MasterStore master, RequestContext ctx, string workstationCode, Guid? exceptId)
    {
        var taken = (await master.AgentsOfAsync(ctx.Clinic.Id)).Any(a =>
            a.Agent.Id != exceptId && string.Equals(a.Agent.WorkstationCode, workstationCode, StringComparison.OrdinalIgnoreCase));
        if (taken) throw ApiException.Conflict($"Mã máy trạm {workstationCode} đã có agent khác dùng");
    }

    private static ApiException Translate(AgentException ex) => ex.Failure switch
    {
        AgentFailure.NotFound => ApiException.NotFound(ex.Message),
        _ => ApiException.Conflict(ex.Message),
    };

    /// <summary>Kiểm thân (cùng câu báo lỗi như DTO ở NestJS) và cắt khoảng trắng tên.</summary>
    private static (string Name, string WorkstationCode) Check(AgentBody body)
    {
        var name = body.Name ?? throw ApiException.BadRequest(InvalidBody);
        if (name.Trim().Length == 0) throw ApiException.BadRequest("Nhập tên agent");
        if (name.Length > 100) throw ApiException.BadRequest(InvalidBody);

        var code = body.WorkstationCode ?? throw ApiException.BadRequest(InvalidBody);
        if (code.Length == 0) throw ApiException.BadRequest("Nhập mã máy trạm");
        if (code.Length > 64) throw ApiException.BadRequest(InvalidBody);
        if (!code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw ApiException.BadRequest("Mã máy trạm chỉ gồm chữ không dấu, số, dấu chấm, gạch ngang, gạch dưới");
        }

        return (name.Trim(), code);
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
            r.Id.ToString(CultureInfo.InvariantCulture), UsersEndpoints.Iso(r.At), r.AgentId, r.InstrumentId, r.InstrumentName,
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
