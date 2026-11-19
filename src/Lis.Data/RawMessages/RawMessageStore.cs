using System.Text.Json;
using Dapper;

namespace Lis.Data.RawMessages;

/// <summary>Trạng thái giải mã của một bản tin thô (<c>raw_message.decode_status</c>).</summary>
public static class RawMessageStatus
{
    public const string Waiting = "cho-giai-ma";
    public const string Decoding = "dang-giai-ma";
    public const string Decoded = "da-giai-ma";
    public const string NoResult = "khong-co-ket-qua";
    public const string Error = "loi";
    public const string StoredOnly = "chi-luu";
    public const string NeedsReview = "can-xem-lai";

    /// <summary>Các trạng thái đã kết thúc — chỉ những bản tin này mới được xoá sau 90 ngày.</summary>
    public static IReadOnlyList<string> Terminal { get; } = [Decoded, NoResult, Error, StoredOnly];
}

/// <summary>Một bản tin thô agent gửi lên. <see cref="Id"/> do agent sinh, dùng để chống trùng khi gửi lại.</summary>
public sealed record RawMessageInsert(
    Guid Id, Guid InstrumentId, Guid? AgentId, long Seq, string Session,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, byte[] Bytes, bool Partial);

/// <summary>Một dòng <c>raw_message</c>. <see cref="DecodeResult"/> là JSON kết quả lượt giải mã gần nhất.</summary>
public sealed record RawMessageRow(
    Guid Id, Guid InstrumentId, Guid? AgentId, long Seq, string Session,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, DateTimeOffset StoredAt, byte[] Bytes, bool Partial,
    string? ProtocolFamily, string? Model, string? DriverConfigJson,
    string DecodeStatus, DateTimeOffset? DecodeStartedAt, string? DecoderVersion, JsonElement? DecodeResult, DateTimeOffset? DecodedAt);

/// <summary>Bộ lọc danh sách: mới nhất trước, phân trang theo con trỏ (<see cref="BeforeCompletedAt"/>, <see cref="BeforeId"/>).</summary>
public sealed record RawMessageFilter(
    Guid? InstrumentId, IReadOnlyList<string>? Statuses, DateTimeOffset? From, DateTimeOffset? To,
    DateTimeOffset? BeforeCompletedAt, Guid? BeforeId, int Limit = 50);

/// <summary>Truy cập bảng <c>raw_message</c>. Không có logic giải mã ở đây (xem <see cref="RawMessageProcessor"/>).</summary>
public sealed class RawMessageStore(Db db)
{
    internal const string Columns =
        """
        id, instrument_id, agent_id, seq, session, started_at, completed_at, stored_at, bytes, partial,
        protocol_family, model, driver_config::text AS driver_config_json,
        decode_status, decode_started_at, decoder_version, decode_result::text AS decode_result_json, decoded_at
        """;

    /// <summary>Lưu bản tin; trả null khi id đã có (gửi lại) và không đổi dòng cũ.</summary>
    public async Task<RawMessageRow?> InsertIfAbsentAsync(RawMessageInsert m, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var dto = await connection.QuerySingleOrDefaultAsync<Dto>(new CommandDefinition(
            $"""
            INSERT INTO raw_message (id, instrument_id, agent_id, seq, session, started_at, completed_at, bytes, partial, decode_status)
            VALUES (@Id, @InstrumentId, @AgentId, @Seq, @Session, @StartedAt, @CompletedAt, @Bytes, @Partial, 'cho-giai-ma')
            ON CONFLICT (id) DO NOTHING
            RETURNING {Columns}
            """,
            new
            {
                m.Id, m.InstrumentId, m.AgentId, m.Seq, m.Session,
                StartedAt = m.StartedAt.ToUniversalTime(), CompletedAt = m.CompletedAt.ToUniversalTime(), m.Bytes, m.Partial,
            },
            cancellationToken: ct));
        return dto?.ToRow();
    }

    public async Task<RawMessageRow?> FindAsync(Guid id)
    {
        await using var connection = await db.OpenAsync();
        var dto = await connection.QuerySingleOrDefaultAsync<Dto>($"SELECT {Columns} FROM raw_message WHERE id = @id", new { id });
        return dto?.ToRow();
    }

    public async Task<IReadOnlyList<RawMessageRow>> ListAsync(RawMessageFilter f)
    {
        var limit = Math.Clamp(f.Limit, 1, 200);
        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<Dto>(
            $"""
            SELECT {Columns} FROM raw_message
            WHERE (@instrumentId::uuid IS NULL OR instrument_id = @instrumentId)
              AND (@statuses::text[] IS NULL OR decode_status = ANY(@statuses))
              AND (@from::timestamptz IS NULL OR completed_at >= @from)
              AND (@to::timestamptz IS NULL OR completed_at < @to)
              AND (@beforeCompletedAt::timestamptz IS NULL OR (completed_at, id) < (@beforeCompletedAt, @beforeId::uuid))
            ORDER BY completed_at DESC, id DESC
            LIMIT @limit
            """,
            new
            {
                instrumentId = f.InstrumentId,
                statuses = f.Statuses is { Count: > 0 } ? f.Statuses.ToArray() : null,
                from = f.From?.ToUniversalTime(),
                to = f.To?.ToUniversalTime(),
                beforeCompletedAt = f.BeforeCompletedAt?.ToUniversalTime(),
                beforeId = f.BeforeId ?? Guid.Empty,
                limit,
            });
        return rows.Select(r => r.ToRow()).ToList();
    }

    /// <summary>Bản tin <c>dang-giai-ma</c> bắt đầu trước <paramref name="olderThan"/> (tiến trình đã chết) → <c>can-xem-lai</c>, không tự chạy lại.</summary>
    public async Task<int> MarkInterruptedAsync(DateTimeOffset olderThan)
    {
        await using var connection = await db.OpenAsync();
        return await connection.ExecuteAsync(
            "UPDATE raw_message SET decode_status = 'can-xem-lai' WHERE decode_status = 'dang-giai-ma' AND decode_started_at < @olderThan",
            new { olderThan = olderThan.ToUniversalTime() });
    }

    /// <summary>Id các bản tin <c>cho-giai-ma</c>, cũ nhất trước.</summary>
    public async Task<IReadOnlyList<Guid>> PendingAsync(int max)
    {
        await using var connection = await db.OpenAsync();
        var ids = await connection.QueryAsync<Guid>(
            "SELECT id FROM raw_message WHERE decode_status = 'cho-giai-ma' ORDER BY stored_at, id LIMIT @max",
            new { max = Math.Max(1, max) });
        return ids.ToList();
    }

    /// <summary>Xoá bản tin lưu trước <paramref name="storedBefore"/> — chỉ các trạng thái đã kết thúc; không bao giờ xoá bản tin chưa xong.</summary>
    public async Task<int> DeleteOlderThanAsync(DateTimeOffset storedBefore)
    {
        await using var connection = await db.OpenAsync();
        return await connection.ExecuteAsync(
            "DELETE FROM raw_message WHERE stored_at < @storedBefore AND decode_status = ANY(@terminal)",
            new { storedBefore = storedBefore.ToUniversalTime(), terminal = RawMessageStatus.Terminal.ToArray() });
    }

    internal sealed record Dto
    {
        public Guid Id { get; init; }
        public Guid InstrumentId { get; init; }
        public Guid? AgentId { get; init; }
        public long Seq { get; init; }
        public string Session { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset CompletedAt { get; init; }
        public DateTimeOffset StoredAt { get; init; }
        public byte[] Bytes { get; init; } = [];
        public bool Partial { get; init; }
        public string? ProtocolFamily { get; init; }
        public string? Model { get; init; }
        public string? DriverConfigJson { get; init; }
        public string DecodeStatus { get; init; } = "";
        public DateTimeOffset? DecodeStartedAt { get; init; }
        public string? DecoderVersion { get; init; }
        public string? DecodeResultJson { get; init; }
        public DateTimeOffset? DecodedAt { get; init; }

        public RawMessageRow ToRow() => new(
            Id, InstrumentId, AgentId, Seq, Session, StartedAt, CompletedAt, StoredAt, Bytes, Partial,
            ProtocolFamily, Model, DriverConfigJson, DecodeStatus, DecodeStartedAt, DecoderVersion,
            DecodeResultJson is null ? null : JsonDocument.Parse(DecodeResultJson).RootElement.Clone(), DecodedAt);
    }
}
