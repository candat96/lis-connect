using System.Globalization;
using System.Text.Json;
using Dapper;
using Lis.Data.RawMessages;
using Lis.Server.Tenancy;

namespace Lis.Server.Api;

/// <summary>Giải mã lại: hoặc <c>ids</c>, hoặc <c>instrumentId</c> + <c>from</c> + <c>to</c> (theo <c>completed_at</c>).</summary>
public sealed record RedecodeBody(IReadOnlyList<Guid>? Ids, Guid? InstrumentId, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>
/// Bản tin thô máy gửi (<c>/api/raw-messages</c>): xem log (Quản trị, Kỹ thuật viên, Trưởng khoa), giải mã thử và giải mã lại
/// (Quản trị, Trưởng khoa). Giải mã lại chạy tuần tự, mới → cũ, qua <see cref="RawMessageProcessor"/> (mọi kết quả đi qua IngestService).
/// </summary>
public static class RawMessagesEndpoints
{
    public const int MaxRedecode = 500;
    private const int HexBytes = 256 * 1024;
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const string NotFound = "Không tìm thấy bản tin";
    private const string Busy = "Bản tin đang được giải mã";
    private const string TooMany = "Tối đa 500 bản tin mỗi lần — thu hẹp khoảng thời gian";
    private const string InvalidBody = "Dữ liệu gửi lên không hợp lệ";

    private static readonly string[] Statuses =
    [
        RawMessageStatus.Waiting, RawMessageStatus.Decoding, RawMessageStatus.Decoded, RawMessageStatus.NoResult,
        RawMessageStatus.Error, RawMessageStatus.StoredOnly, RawMessageStatus.NeedsReview,
    ];

    /// <summary>Các trường của <c>decode_result</c> giữ lại trong <c>summary</c> của danh sách.</summary>
    private static readonly string[] SummaryFields =
        ["readings", "stored", "pendingReplacement", "unmatched", "qc", "unchanged", "conflicts", "olderThanCurrent", "error", "note", "interrupted"];

    public static void MapRawMessagesApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/raw-messages").RequireSession();
        group.MapGet("", (Delegate)((HttpContext http) => ListAsync(http.Ctx(), http.Request.Query)));
        // Đặt trước "/{id}" cho rõ ràng; "redecode" không phải uuid nên không khớp nhầm route kia.
        group.MapPost("/redecode", (HttpContext http, IHostApplicationLifetime lifetime, RedecodeBody body) => RedecodeAsync(http.Ctx(), lifetime, body));
        group.MapGet("/{id}", (HttpContext http, string id) => DetailAsync(http.Ctx(), id));
        group.MapPost("/{id}/preview", (HttpContext http, string id) => PreviewAsync(http.Ctx(), id));
    }

    // Lớp có setter (không phải record vị trí): Dapper đọc timestamptz thành DateTime rồi gán vào DateTimeOffset qua setter.
    private sealed class ListRow
    {
        public Guid Id { get; init; }
        public Guid InstrumentId { get; init; }
        public string InstrumentName { get; init; } = "";
        public long Seq { get; init; }
        public string Session { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset CompletedAt { get; init; }
        public bool Partial { get; init; }
        public int Size { get; init; }
        public string DecodeStatus { get; init; } = "";
        public string? DecoderVersion { get; init; }
        public string? DecodeResultJson { get; init; }
    }

    private static async Task<IResult> ListAsync(RequestContext ctx, IQueryCollection query)
    {
        ctx.RequireLabStaff();
        var parameters = new DynamicParameters();
        var conditions = new List<string>();

        if (Text(query, "instrumentId") is { } instrumentId)
        {
            parameters.Add("instrumentId", ParseGuid(instrumentId, "instrumentId không hợp lệ"));
            conditions.Add("m.instrument_id = @instrumentId");
        }

        if (Text(query, "status") is { } status)
        {
            var wanted = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
            if (wanted.Length == 0 || wanted.Any(s => !Statuses.Contains(s)))
            {
                throw ApiException.BadRequest("Trạng thái giải mã không hợp lệ");
            }

            parameters.Add("statuses", wanted);
            conditions.Add("m.decode_status = ANY(@statuses)");
        }

        if (Text(query, "from") is { } from)
        {
            parameters.Add("from", ParseTime(from, "from không hợp lệ").UtcDateTime);
            conditions.Add("m.completed_at >= @from");
        }

        if (Text(query, "to") is { } to)
        {
            parameters.Add("to", ParseTime(to, "to không hợp lệ").UtcDateTime);
            conditions.Add("m.completed_at < @to");
        }

        if (Text(query, "before") is { } before)
        {
            // Con trỏ: "{completedAt}|{id}" của dòng cuối trang trước; chỉ có thời điểm thì lấy các bản tin hoàn tất sớm hơn.
            var parts = before.Split('|', 2);
            parameters.Add("beforeAt", ParseTime(parts[0], "before không hợp lệ").UtcDateTime);
            if (parts.Length == 2)
            {
                parameters.Add("beforeId", ParseGuid(parts[1], "before không hợp lệ"));
                conditions.Add("(m.completed_at, m.id) < (@beforeAt, @beforeId)");
            }
            else
            {
                conditions.Add("m.completed_at < @beforeAt");
            }
        }

        var limit = DefaultLimit;
        if (Text(query, "limit") is { } limitText)
        {
            if (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1)
            {
                throw ApiException.BadRequest("limit không hợp lệ");
            }

            limit = Math.Min(limit, MaxLimit);
        }

        parameters.Add("limit", limit);
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        await using var connection = await ctx.Tenant.Db.OpenAsync();
        // Không kéo cột bytes (tới 4 MB mỗi dòng): chỉ lấy độ dài.
        var rows = await connection.QueryAsync<ListRow>(
            $"""
            SELECT m.id, m.instrument_id AS InstrumentId, i.name AS InstrumentName, m.seq, m.session,
                   m.started_at AS StartedAt, m.completed_at AS CompletedAt, m.partial, octet_length(m.bytes) AS Size,
                   m.decode_status AS DecodeStatus, m.decoder_version AS DecoderVersion, m.decode_result::text AS DecodeResultJson
            FROM raw_message m
            JOIN instrument i ON i.id = m.instrument_id
            {where}
            ORDER BY m.completed_at DESC, m.id DESC
            LIMIT @limit
            """,
            parameters);

        return Results.Ok(rows.Select(r => new
        {
            r.Id, r.InstrumentId, r.InstrumentName, r.Seq, r.Session,
            StartedAt = ApiFormat.Iso(r.StartedAt), CompletedAt = ApiFormat.Iso(r.CompletedAt),
            r.Partial, r.Size, r.DecodeStatus, r.DecoderVersion, Summary = Summarize(r.DecodeResultJson),
        }));
    }

    private sealed class DetailRow
    {
        public Guid Id { get; init; }
        public Guid InstrumentId { get; init; }
        public string InstrumentName { get; init; } = "";
        public Guid? AgentId { get; init; }
        public long Seq { get; init; }
        public string Session { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset CompletedAt { get; init; }
        public DateTimeOffset StoredAt { get; init; }
        public bool Partial { get; init; }
        public int Size { get; init; }
        public string Hex { get; init; } = "";
        public string? ProtocolFamily { get; init; }
        public string? Model { get; init; }
        public string DecodeStatus { get; init; } = "";
        public DateTimeOffset? DecodeStartedAt { get; init; }
        public string? DecoderVersion { get; init; }
        public string? DecodeResultJson { get; init; }
        public DateTimeOffset? DecodedAt { get; init; }
    }

    private sealed class DecodeRow
    {
        public string Kind { get; init; } = "";
        public string? ActorName { get; init; }
        public DateTimeOffset At { get; init; }
        public string DecoderVersion { get; init; } = "";
        public string Status { get; init; } = "";
        public string ResultJson { get; init; } = "";
    }

    private static async Task<IResult> DetailAsync(RequestContext ctx, string idText)
    {
        ctx.RequireLabStaff();
        var id = ApiFormat.ParseId(idText);
        await using var connection = await ctx.Tenant.Db.OpenAsync();
        var row = await connection.QuerySingleOrDefaultAsync<DetailRow>(
            $"""
            SELECT m.id, m.instrument_id AS InstrumentId, i.name AS InstrumentName, m.agent_id AS AgentId, m.seq, m.session,
                   m.started_at AS StartedAt, m.completed_at AS CompletedAt, m.stored_at AS StoredAt, m.partial,
                   octet_length(m.bytes) AS Size, upper(encode(substring(m.bytes from 1 for {HexBytes}), 'hex')) AS Hex,
                   m.protocol_family AS ProtocolFamily, m.model, m.decode_status AS DecodeStatus, m.decode_started_at AS DecodeStartedAt,
                   m.decoder_version AS DecoderVersion, m.decode_result::text AS DecodeResultJson, m.decoded_at AS DecodedAt
            FROM raw_message m
            JOIN instrument i ON i.id = m.instrument_id
            WHERE m.id = @id
            """,
            new { id }) ?? throw ApiException.NotFound(NotFound);

        var decodes = await connection.QueryAsync<DecodeRow>(
            """
            SELECT d.kind, u.full_name AS ActorName, d.at, d.decoder_version AS DecoderVersion, d.status, d.result::text AS ResultJson
            FROM raw_message_decode d
            LEFT JOIN app_user u ON u.id = d.actor_id
            WHERE d.raw_message_id = @id
            ORDER BY d.at, d.id
            """,
            new { id });

        return Results.Ok(new
        {
            row.Id, row.InstrumentId, row.InstrumentName, row.AgentId, row.Seq, row.Session,
            StartedAt = ApiFormat.Iso(row.StartedAt), CompletedAt = ApiFormat.Iso(row.CompletedAt), StoredAt = ApiFormat.Iso(row.StoredAt),
            row.Partial, row.Size, row.Hex, HexTruncated = row.Size > HexBytes,
            row.ProtocolFamily, row.Model, row.DecodeStatus,
            DecodeStartedAt = row.DecodeStartedAt is { } started ? ApiFormat.Iso(started) : null,
            row.DecoderVersion, DecodeResult = Parse(row.DecodeResultJson),
            DecodedAt = row.DecodedAt is { } decoded ? ApiFormat.Iso(decoded) : null,
            Decodes = decodes.Select(d => new
            {
                d.Kind, d.ActorName, At = ApiFormat.Iso(d.At), d.DecoderVersion, d.Status, Result = Parse(d.ResultJson),
            }).ToList(),
        });
    }

    /// <summary>Giải mã thử bằng cấu hình hiện tại của máy: không ghi gì.</summary>
    private static async Task<IResult> PreviewAsync(RequestContext ctx, string idText)
    {
        ctx.RequireRedecoder();
        var id = ApiFormat.ParseId(idText);
        try
        {
            var result = await ctx.Tenant.RawProcessor.PreviewAsync(id);
            return Results.Ok(new
            {
                Readings = result.Readings.Select(r => new
                {
                    r.Barcode, r.IsQc,
                    Items = r.Items.Select(i => new { i.ChannelCode, i.RawValue, i.Unit, Flag = i.InstrumentFlag }).ToList(),
                }).ToList(),
                Queries = result.QueryBarcodes,
                result.Notes,
                result.Abandoned,
            });
        }
        catch (KeyNotFoundException)
        {
            throw ApiException.NotFound(NotFound);
        }
        catch (InvalidOperationException ex)
        {
            throw ApiException.NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            // Cấu hình driver của máy hỏng.
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, $"Không giải mã thử được: {ex.Message}");
        }
    }

    private sealed class Totals
    {
        public int Count, Stored, PendingReplacement, Unmatched, Qc, Unchanged, Conflicts, OlderThanCurrent, Busy, Errors;

        public void Add(JsonElement? result)
        {
            if (result is not { ValueKind: JsonValueKind.Object } r)
            {
                return;
            }

            Stored += Int(r, "stored");
            PendingReplacement += Int(r, "pendingReplacement");
            Unmatched += Int(r, "unmatched");
            Qc += Int(r, "qc");
            Unchanged += Int(r, "unchanged");
            Conflicts += Int(r, "conflicts");
            OlderThanCurrent += Int(r, "olderThanCurrent");
        }

        private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
    }

    private static async Task<IResult> RedecodeAsync(RequestContext ctx, IHostApplicationLifetime lifetime, RedecodeBody body)
    {
        ctx.RequireRedecoder();
        var ids = await ResolveIdsAsync(ctx, body);
        var totals = new Totals();
        var stopping = lifetime.ApplicationStopping;

        // Tuần tự, mới → cũ (ResolveIdsAsync đã sắp): bản tin cũ không tạo "chờ thay" đè kết quả của bản tin mới hơn.
        // Không dùng RequestAborted: HTTP ngắt giữa chừng sẽ huỷ lượt đang chạy; token dừng của host mới huỷ (OperationCanceledException thoát ra).
        foreach (var id in ids)
        {
            totals.Count++;
            RawMessageRow row;
            try
            {
                row = await ctx.Tenant.RawProcessor.DecodeAsync(id, "giai-ma-lai", ctx.User.Id, stopping);
            }
            catch (KeyNotFoundException)
            {
                totals.Errors++;
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                totals.Errors++;
                continue;
            }

            switch (row.DecodeStatus)
            {
                case RawMessageStatus.Decoding:
                    totals.Busy++;
                    break;
                case RawMessageStatus.Error or RawMessageStatus.NeedsReview:
                    totals.Errors++;
                    break;
                default:
                    totals.Add(row.DecodeResult);
                    break;
            }
        }

        if (ids.Count == 1 && totals.Busy == 1)
        {
            throw ApiException.Conflict(Busy);
        }

        return Results.Ok(new
        {
            totals.Count, totals.Stored, totals.PendingReplacement, totals.Unmatched, totals.Qc,
            totals.Unchanged, totals.Conflicts, totals.OlderThanCurrent, totals.Busy, totals.Errors,
        });
    }

    /// <summary>Id cần giải mã lại, đã sắp mới → cũ (<c>completed_at</c>, <c>seq</c>); quá <see cref="MaxRedecode"/> thì 400.</summary>
    private static async Task<IReadOnlyList<Guid>> ResolveIdsAsync(RequestContext ctx, RedecodeBody body)
    {
        var hasIds = body.Ids is { Count: > 0 };
        var hasRange = body.InstrumentId is not null || body.From is not null || body.To is not null;
        if (hasIds == hasRange || (hasRange && (body.InstrumentId is null || body.From is null || body.To is null)))
        {
            throw ApiException.BadRequest(InvalidBody);
        }

        await using var connection = await ctx.Tenant.Db.OpenAsync();
        if (hasIds)
        {
            var wanted = body.Ids!.Distinct().ToArray();
            if (wanted.Length > MaxRedecode)
            {
                throw ApiException.BadRequest(TooMany);
            }

            var found = (await connection.QueryAsync<Guid>(
                "SELECT id FROM raw_message WHERE id = ANY(@wanted) ORDER BY completed_at DESC, seq DESC, id DESC", new { wanted })).ToList();
            return found.Count == wanted.Length ? found : throw ApiException.NotFound(NotFound);
        }

        var ids = (await connection.QueryAsync<Guid>(
            """
            SELECT id FROM raw_message
            WHERE instrument_id = @instrumentId AND completed_at >= @from AND completed_at < @to
            ORDER BY completed_at DESC, seq DESC, id DESC
            LIMIT @limit
            """,
            new { instrumentId = body.InstrumentId, from = body.From!.Value.UtcDateTime, to = body.To!.Value.UtcDateTime, limit = MaxRedecode + 1 })).ToList();
        return ids.Count <= MaxRedecode ? ids : throw ApiException.BadRequest(TooMany);
    }

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    /// <summary><c>decode_result</c> rút gọn: chỉ các số đếm và lời nhắn, không kèm danh sách dài.</summary>
    private static Dictionary<string, JsonElement>? Summarize(string? json)
    {
        if (Parse(json) is not { ValueKind: JsonValueKind.Object } result)
        {
            return null;
        }

        var summary = new Dictionary<string, JsonElement>();
        foreach (var field in SummaryFields)
        {
            if (result.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                summary[field] = value;
            }
        }

        return summary;
    }

    private static string? Text(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var values) && values.ToString().Trim() is { Length: > 0 } text ? text : null;

    private static Guid ParseGuid(string text, string message) =>
        Guid.TryParseExact(text, "D", out var guid) ? guid : throw ApiException.BadRequest(message);

    private static DateTimeOffset ParseTime(string text, string message) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : throw ApiException.BadRequest(message);
}
