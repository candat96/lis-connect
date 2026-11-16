using System.Text.Json;
using System.Text.Json.Serialization;
using Lis.Core.Drivers;
using Lis.Core.Instruments;
using Lis.Data.Instruments;
using Lis.Server.Tenancy;

namespace Lis.Server.Api;

public sealed record InstrumentConnectionBody(
    string? Kind, string? PortName, int? BaudRate, int? DataBits, string? Parity, int? StopBits, string? Host, int? TcpPort);

public sealed record InstrumentBody(
    string? Name, string? Model, string? ProtocolFamily, string? WorkstationCode, bool? Active, InstrumentConnectionBody? Connection);

public sealed record InstrumentChannelBody(
    string? ChannelCode, string? ServiceCode, int? Decimals, decimal? Factor, bool? KeepComparator, bool? SendOnQuery);

public sealed record DeclareBody(string? ServiceCode);

/// <summary>Cấu hình kết nối dạng form; trường không có giá trị thì bỏ khỏi JSON (như <c>undefined</c> ở NestJS).</summary>
public sealed record ConnectionView(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PortName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BaudRate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DataBits = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Parity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StopBits = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Host = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TcpPort = null);

public sealed record InstrumentAgentView(Guid Id, string Name, string? LastSeenAt, bool Online);

public sealed record InstrumentView(
    Guid Id, string Name, string? Model, string? ProtocolFamily, string? WorkstationCode, bool Active,
    ConnectionView Connection, string? ConnectionText, int ChannelCount, int PendingServiceCount, int UnmappedCount,
    InstrumentAgentView? Agent, JsonElement? Status);

public sealed record InstrumentChannelView(
    Guid? Id, string? ChannelCode, string ServiceCode, string ServiceName, int? Decimals, decimal? Factor, bool KeepComparator, bool SendOnQuery);

public sealed record UnmappedChannelView(string ChannelCode, int Count, string LastValue, string? LastUnit, string LastAt);

/// <summary>
/// Máy xét nghiệm, map kênh, chỉ số khai báo (<c>/api/instruments</c>): ai đăng nhập cũng xem được; chỉ Quản trị ghi.
/// Ghi qua <see cref="InstrumentStore"/>; cấu hình kết nối đọc / kiểm bằng <see cref="ConnectionSettings"/> (cùng bộ đọc của agent).
/// Kiểm đầu vào chạy trước kiểm quyền, giống pipe chạy trước handler ở NestJS.
/// </summary>
public static class InstrumentsEndpoints
{
    /// <summary>Agent im quá khoảng này thì coi là offline (như <c>ONLINE_WINDOW_MS</c> ở NestJS).</summary>
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    private const string InvalidBody = "Dữ liệu gửi lên không hợp lệ";
    private const string LeafOnly = "Kênh máy chỉ map tới chỉ số lá, không map tới dịch vụ cha (gói)";
    private const string ChannelNotFound = "Không tìm thấy kênh";
    private const string InstrumentNotFound = "Không tìm thấy máy";
    private static readonly int[] BaudRates = [.. ConnectionSettings.BaudRates];
    private static readonly string[] Kinds = ["none", "serial", "tcp-server", "tcp-client"];

    public static void MapInstrumentsApi(this RouteGroupBuilder api)
    {
        var instruments = api.MapGroup("/instruments").RequireSession();
        instruments.MapGet("", (HttpContext http, MasterStore master, TimeProvider clock) => ListAsync(http.Ctx(), master, clock));
        instruments.MapPost("", (HttpContext http, MasterStore master, InstrumentBody body) => CreateAsync(http.Ctx(), master, body));
        instruments.MapPut("/{id}", (HttpContext http, MasterStore master, string id, InstrumentBody body) => UpdateAsync(http.Ctx(), master, id, body));
        instruments.MapDelete("/{id}", (HttpContext http, string id) => DeleteAsync(http.Ctx(), id));
        instruments.MapGet("/{id}/channels", (HttpContext http, string id) => ChannelsAsync(http.Ctx(), id));
        instruments.MapPost("/{id}/channels", (HttpContext http, string id, InstrumentChannelBody body) => AddChannelAsync(http.Ctx(), id, body));
        instruments.MapPut("/{id}/channels/{channelId}",
            (HttpContext http, string id, string channelId, InstrumentChannelBody body) => UpdateChannelAsync(http.Ctx(), id, channelId, body));
        instruments.MapDelete("/{id}/channels/{channelId}", (HttpContext http, string id, string channelId) => RemoveChannelAsync(http.Ctx(), id, channelId));
        instruments.MapPost("/{id}/declared", (HttpContext http, string id, DeclareBody body) => DeclareAsync(http.Ctx(), id, body));
        instruments.MapDelete("/{id}/declared/{serviceCode}", (HttpContext http, string id, string serviceCode) => UndeclareAsync(http.Ctx(), id, serviceCode));
        instruments.MapGet("/{id}/unmapped", (HttpContext http, string id) => UnmappedAsync(http.Ctx(), id));
    }

    private static async Task<IResult> ListAsync(RequestContext ctx, MasterStore master, TimeProvider clock)
    {
        var store = ctx.Tenant.Instruments;
        var instruments = await store.ListAsync();
        var agents = await master.AgentsOfAsync(ctx.Clinic.Id);
        var now = clock.GetUtcNow();

        var views = new List<InstrumentView>();
        foreach (var instrument in instruments)
        {
            var channelCount = (await store.ChannelsAsync(instrument.Id)).Count;
            var pending = (await store.DeclaredServicesAsync(instrument.Id)).Count(d => d.ChannelCode is null);
            var unmapped = (await store.UnmappedChannelsAsync(instrument.Id)).Count;

            var agent = agents.FirstOrDefault(a => string.Equals(a.Agent.WorkstationCode, instrument.WorkstationCode ?? "", StringComparison.OrdinalIgnoreCase));
            var online = agent?.Agent.LastSeenAt is { } seen && now - seen < OnlineWindow;
            var connection = ConnectionSettings.Parse(instrument.ConnectionJson);
            views.Add(new InstrumentView(
                instrument.Id, instrument.Name, instrument.Model, instrument.ProtocolFamily, instrument.WorkstationCode, instrument.Active,
                ViewOf(connection), DescribeConnection(connection), channelCount, pending, unmapped,
                agent is null ? null : new InstrumentAgentView(
                    agent.Agent.Id, agent.Agent.Name, agent.Agent.LastSeenAt is { } last ? UsersEndpoints.Iso(last) : null, online),
                // Trạng thái chỉ đáng tin khi agent còn đang báo về.
                online ? StatusOf(agent!.LastStatusJson, instrument.Id) : null));
        }

        return Results.Ok(views);
    }

    private static async Task<IResult> CreateAsync(RequestContext ctx, MasterStore master, InstrumentBody body)
    {
        var input = Check(body);
        ctx.RequireAdmin();
        var info = await BuildAsync(ctx, master, Guid.Empty, null, input);
        var id = await ctx.Tenant.Instruments.SaveAsync(info);
        return Results.Json(new { id }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>Sửa máy. Agent tự nhận cấu hình mới trong vòng 30 giây và mở lại kết nối nếu cấu hình đổi.</summary>
    private static async Task<IResult> UpdateAsync(RequestContext ctx, MasterStore master, string id, InstrumentBody body)
    {
        var instrumentId = ParseId(id);
        var input = Check(body);
        ctx.RequireAdmin();
        var existing = await FindAsync(ctx, instrumentId);
        var info = await BuildAsync(ctx, master, instrumentId, existing.DriverConfigJson, input);
        try
        {
            await ctx.Tenant.Instruments.SaveAsync(info);
        }
        catch (InstrumentException ex)
        {
            throw Translate(ex);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(RequestContext ctx, string id)
    {
        var instrumentId = ParseId(id);
        ctx.RequireAdmin();
        return await ctx.Tenant.Instruments.DeleteAsync(instrumentId) switch
        {
            InstrumentDeleteResult.Deleted => Results.NoContent(),
            InstrumentDeleteResult.NotFound => throw ApiException.NotFound(InstrumentNotFound),
            _ => throw ApiException.Conflict("Máy đã có kết quả xét nghiệm gắn với nó nên không xoá được — hãy chuyển sang \"Ngừng dùng\""),
        };
    }

    /// <summary>Bảng map kênh của một máy: kênh đã map trước, rồi dịch vụ đã khai đang chờ dò mã kênh.</summary>
    private static async Task<IResult> ChannelsAsync(RequestContext ctx, string id)
    {
        var instrumentId = ParseId(id);
        await FindAsync(ctx, instrumentId);
        var store = ctx.Tenant.Instruments;
        var mapped = (await store.ChannelsAsync(instrumentId)).Select(c => new InstrumentChannelView(
            c.Map.Id, c.Map.ChannelCode, c.ServiceCode, c.ServiceName, c.Map.Decimals, c.Map.Factor, c.Map.KeepComparator, c.Map.SendOnQuery));
        var pending = (await store.DeclaredServicesAsync(instrumentId)).Where(d => d.ChannelCode is null)
            .Select(d => new InstrumentChannelView(null, null, d.Code, d.Name, null, null, false, true));
        return Results.Ok(mapped.Concat(pending));
    }

    /// <summary>Thêm một kênh (mã kênh máy → chỉ số). Trùng mã kênh trên cùng máy → báo đang trỏ tới chỉ số nào.</summary>
    private static async Task<IResult> AddChannelAsync(RequestContext ctx, string id, InstrumentChannelBody body)
    {
        var instrumentId = ParseId(id);
        var input = CheckChannel(body);
        ctx.RequireAdmin();
        await FindAsync(ctx, instrumentId);
        var serviceId = await LeafServiceIdAsync(ctx, input.ServiceCode);
        CheckFactor(input.Factor);
        var created = await SaveChannelAsync(ctx, new ChannelMap(
            Guid.Empty, instrumentId, input.ChannelCode, serviceId, input.Decimals, input.Factor, input.KeepComparator, input.SendOnQuery));
        return Results.Json(new { id = created }, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateChannelAsync(RequestContext ctx, string id, string channelId, InstrumentChannelBody body)
    {
        var instrumentId = ParseId(id);
        var channelGuid = ParseId(channelId);
        var input = CheckChannel(body);
        ctx.RequireAdmin();
        var serviceId = await LeafServiceIdAsync(ctx, input.ServiceCode);
        CheckFactor(input.Factor);
        // Kênh phải thuộc đúng máy này (InstrumentStore sửa theo id kênh, không biết máy).
        if (!await HasChannelAsync(ctx, instrumentId, channelGuid)) throw ApiException.NotFound(ChannelNotFound);
        await SaveChannelAsync(ctx, new ChannelMap(
            channelGuid, instrumentId, input.ChannelCode, serviceId, input.Decimals, input.Factor, input.KeepComparator, input.SendOnQuery));
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveChannelAsync(RequestContext ctx, string id, string channelId)
    {
        var instrumentId = ParseId(id);
        var channelGuid = ParseId(channelId);
        ctx.RequireAdmin();
        if (await HasChannelAsync(ctx, instrumentId, channelGuid))
        {
            await ctx.Tenant.Instruments.DeleteChannelAsync(channelGuid);
        }

        return Results.NoContent();
    }

    /// <summary>Khai một chỉ số là máy này chạy được khi chưa biết mã kênh — hệ thống tự dò mã kênh theo barcode.</summary>
    private static async Task<IResult> DeclareAsync(RequestContext ctx, string id, DeclareBody body)
    {
        var instrumentId = ParseId(id);
        var serviceCode = CheckServiceCode(body.ServiceCode);
        ctx.RequireAdmin();
        await FindAsync(ctx, instrumentId);
        var serviceId = await LeafServiceIdAsync(ctx, serviceCode);
        await ctx.Tenant.Instruments.SetDeclaredAsync(instrumentId, serviceId, declared: true);
        return Results.NoContent();
    }

    private static async Task<IResult> UndeclareAsync(RequestContext ctx, string id, string serviceCode)
    {
        var instrumentId = ParseId(id);
        ctx.RequireAdmin();
        if (await ctx.Tenant.Catalog.FindByCodeAsync(serviceCode) is { } service)
        {
            await ctx.Tenant.Instruments.SetDeclaredAsync(instrumentId, service.Id, declared: false);
        }

        return Results.NoContent();
    }

    /// <summary>Mã kênh máy đã gửi mà chưa map (mới nhất trước).</summary>
    private static async Task<IResult> UnmappedAsync(RequestContext ctx, string id)
    {
        var rows = await ctx.Tenant.Instruments.UnmappedChannelsAsync(ParseId(id));
        return Results.Ok(rows.Select(r => new UnmappedChannelView(r.ChannelCode, r.Count, r.LastValue, r.LastUnit, UsersEndpoints.Iso(r.LastAt))));
    }

    // ---- dùng chung ----

    private static Guid ParseId(string id) =>
        Guid.TryParseExact(id, "D", out var guid) ? guid : throw ApiException.BadRequest("Validation failed (uuid is expected)");

    private static async Task<InstrumentInfo> FindAsync(RequestContext ctx, Guid id) =>
        (await ctx.Tenant.Instruments.ListAsync()).FirstOrDefault(i => i.Id == id) ?? throw ApiException.NotFound(InstrumentNotFound);

    private static async Task<bool> HasChannelAsync(RequestContext ctx, Guid instrumentId, Guid channelId) =>
        (await ctx.Tenant.Instruments.ChannelsAsync(instrumentId)).Any(c => c.Map.Id == channelId);

    /// <summary>Chỉ số lá theo mã (không phân biệt hoa thường). Không có / là gói → 400.</summary>
    private static async Task<Guid> LeafServiceIdAsync(RequestContext ctx, string code)
    {
        var trimmed = code.Trim();
        var service = await ctx.Tenant.Catalog.FindByCodeAsync(trimmed) ?? throw ApiException.BadRequest($"Không có chỉ số mã {trimmed}");
        return service.IsPanel ? throw ApiException.BadRequest(LeafOnly) : service.Id;
    }

    private static void CheckFactor(decimal? factor)
    {
        if (factor is <= 0) throw ApiException.BadRequest("Hệ số phải lớn hơn 0");
    }

    private static async Task<Guid> SaveChannelAsync(RequestContext ctx, ChannelMap channel)
    {
        try
        {
            return await ctx.Tenant.Instruments.SaveChannelAsync(channel);
        }
        catch (InstrumentException ex)
        {
            throw Translate(ex);
        }
    }

    private static ApiException Translate(InstrumentException ex) => ex.Failure switch
    {
        InstrumentFailure.NotFound => ApiException.NotFound(ex.Message),
        InstrumentFailure.DuplicateChannel => ApiException.Conflict(ex.Message),
        _ => ApiException.BadRequest(ex.Message),
    };

    /// <summary>Thân đã kiểm đầu vào (cùng câu báo lỗi như DTO ở NestJS), cắt khoảng trắng.</summary>
    private sealed record InstrumentInput(
        string Name, string? Model, string? ProtocolFamily, string? WorkstationCode, bool Active, ConnectionSettings Connection);

    private static InstrumentInput Check(InstrumentBody body)
    {
        var name = body.Name ?? throw ApiException.BadRequest(InvalidBody);
        if (name.Length == 0 || name.Trim().Length == 0) throw ApiException.BadRequest("Nhập tên máy");
        if (name.Length > 100) throw ApiException.BadRequest(InvalidBody);
        if (body.Model is { Length: > 100 }) throw ApiException.BadRequest(InvalidBody);
        if (body.ProtocolFamily is not null && !DriverRegistry.SupportedFamilies.Contains(body.ProtocolFamily))
        {
            throw ApiException.BadRequest("Giao thức chưa được hỗ trợ");
        }

        if (body.WorkstationCode is { Length: > 64 }) throw ApiException.BadRequest(InvalidBody);
        var active = body.Active ?? throw ApiException.BadRequest(InvalidBody);
        var connection = CheckConnection(body.Connection ?? throw ApiException.BadRequest(InvalidBody));
        return new InstrumentInput(name.Trim(), body.Model, body.ProtocolFamily, body.WorkstationCode, active, connection);
    }

    private static ConnectionSettings CheckConnection(InstrumentConnectionBody c)
    {
        if (c.Kind is null || !Kinds.Contains(c.Kind)) throw ApiException.BadRequest("Kiểu kết nối không hợp lệ");
        if (c.PortName is { Length: > 32 }) throw ApiException.BadRequest(InvalidBody);
        if (c.BaudRate is { } baud && !BaudRates.Contains(baud)) throw ApiException.BadRequest("Tốc độ baud không hợp lệ");
        if (c.DataBits is { } bits && !ConnectionSettings.DataBitsOptions.Contains(bits)) throw ApiException.BadRequest("Số bit dữ liệu phải là 7 hoặc 8");
        if (c.Parity is not null && !ConnectionSettings.ParityOptions.Contains(c.Parity)) throw ApiException.BadRequest("Parity không hợp lệ");
        if (c.StopBits is { } stop && !ConnectionSettings.StopBitsOptions.Contains(stop)) throw ApiException.BadRequest("Stop bit phải là 1 hoặc 2");
        if (c.Host is { Length: > 255 }) throw ApiException.BadRequest(InvalidBody);
        if (c.TcpPort is < 1 or > 65535) throw ApiException.BadRequest("Cổng TCP phải từ 1 đến 65535");

        return c.Kind switch
        {
            "serial" => new ConnectionSettings
            {
                Kind = ConnectionKind.Serial, PortName = c.PortName?.Trim().ToUpperInvariant(), BaudRate = c.BaudRate ?? 9600,
                DataBits = c.DataBits ?? 8, Parity = c.Parity ?? "None", StopBits = c.StopBits ?? 1,
            },
            "tcp-server" => new ConnectionSettings { Kind = ConnectionKind.TcpServer, TcpPort = c.TcpPort },
            "tcp-client" => new ConnectionSettings { Kind = ConnectionKind.TcpClient, Host = c.Host?.Trim(), TcpPort = c.TcpPort },
            _ => ConnectionSettings.None,
        };
    }

    /// <summary>Kiểm cấu hình kết nối (<see cref="ConnectionSettings.Validate"/>) và mã máy trạm, rồi dựng bản ghi máy.</summary>
    private static async Task<InstrumentInfo> BuildAsync(RequestContext ctx, MasterStore master, Guid id, string? driverConfigJson, InstrumentInput input)
    {
        if (input.Connection.Validate() is { } problem) throw ApiException.BadRequest(problem);

        var workstationCode = string.IsNullOrEmpty(input.WorkstationCode?.Trim()) ? null : input.WorkstationCode.Trim();
        if (workstationCode is not null
            && !(await master.AgentsOfAsync(ctx.Clinic.Id)).Any(a => string.Equals(a.Agent.WorkstationCode, workstationCode, StringComparison.OrdinalIgnoreCase)))
        {
            throw ApiException.BadRequest($"Không có agent nào dùng mã máy trạm {workstationCode}");
        }

        // driver_config không sửa được từ web: giữ nguyên cái đang có.
        return new InstrumentInfo(
            id, input.Name, input.Model, input.ProtocolFamily, driverConfigJson, input.Connection.ToJson(), workstationCode, input.Active);
    }

    private sealed record ChannelInput(
        string ChannelCode, string ServiceCode, int? Decimals, decimal? Factor, bool KeepComparator, bool SendOnQuery);

    private static ChannelInput CheckChannel(InstrumentChannelBody body)
    {
        var code = body.ChannelCode ?? throw ApiException.BadRequest(InvalidBody);
        if (code.Trim().Length == 0) throw ApiException.BadRequest("Nhập mã kênh máy");
        if (code.Length > 64) throw ApiException.BadRequest(InvalidBody);
        var serviceCode = CheckServiceCode(body.ServiceCode);
        if (body.Decimals is < 0 or > 6) throw ApiException.BadRequest(InvalidBody);
        var keep = body.KeepComparator ?? throw ApiException.BadRequest(InvalidBody);
        var send = body.SendOnQuery ?? throw ApiException.BadRequest(InvalidBody);
        return new ChannelInput(code.Trim(), serviceCode, body.Decimals, body.Factor, keep, send);
    }

    private static string CheckServiceCode(string? code)
    {
        if (code is null) throw ApiException.BadRequest(InvalidBody);
        if (code.Length == 0) throw ApiException.BadRequest("Nhập mã chỉ số");
        if (code.Length > 64) throw ApiException.BadRequest(InvalidBody);
        return code;
    }

    private static ConnectionView ViewOf(ConnectionSettings c) => c.Kind switch
    {
        ConnectionKind.Serial => new ConnectionView("serial", PortName: c.PortName, BaudRate: c.BaudRate, DataBits: c.DataBits, Parity: c.Parity, StopBits: c.StopBits),
        ConnectionKind.TcpServer => new ConnectionView("tcp-server", TcpPort: c.TcpPort),
        ConnectionKind.TcpClient => new ConnectionView("tcp-client", Host: c.Host, TcpPort: c.TcpPort),
        _ => new ConnectionView("none"),
    };

    /// <summary>Mô tả ngắn kết nối cho danh sách; null khi chưa cấu hình.</summary>
    private static string? DescribeConnection(ConnectionSettings c) => c.Kind switch
    {
        ConnectionKind.Serial => $"{c.PortName ?? "?"} · {c.BaudRate} {c.DataBits}{c.Parity[0]}{c.StopBits}",
        ConnectionKind.TcpServer => $"TCP chờ máy nối vào cổng {c.TcpPort?.ToString() ?? "?"}",
        ConnectionKind.TcpClient => $"TCP nối tới {c.Host ?? "?"}:{c.TcpPort?.ToString() ?? "?"}",
        _ => null,
    };

    /// <summary>Báo cáo trạng thái của một máy trong JSON agent đã đẩy lên; không có → null.</summary>
    private static JsonElement? StatusOf(string? statusJson, Guid instrumentId)
    {
        if (string.IsNullOrEmpty(statusJson)) return null;
        using var document = JsonDocument.Parse(statusJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var report in document.RootElement.EnumerateArray())
        {
            if (report.ValueKind == JsonValueKind.Object
                && report.TryGetProperty("instrumentId", out var reported)
                && reported.ValueKind == JsonValueKind.String
                && Guid.TryParse(reported.GetString(), out var reportedId) && reportedId == instrumentId)
            {
                return report.Clone();
            }
        }

        return null;
    }
}
