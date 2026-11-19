using System.Text.Json;
using Dapper;
using Lis.Core.Drivers;

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

/// <summary>Kết quả một lần phục hồi: số bản tin được trả về <c>cho-giai-ma</c> và số bản tin chuyển <c>can-xem-lai</c>.</summary>
public sealed record RawMessageRecovery(int Requeued, int NeedsReview)
{
    public int Total => Requeued + NeedsReview;
}

/// <summary>Truy cập bảng <c>raw_message</c>. Không có logic giải mã ở đây (xem <see cref="RawMessageProcessor"/>).</summary>
public sealed class RawMessageStore(Db db, TimeProvider? clock = null)
{
    /// <summary>Lượt <c>dang-giai-ma</c> bắt đầu quá thời gian này mà không còn ai giữ khoá dòng thì coi là đã chết.</summary>
    public static readonly TimeSpan DefaultRecoverGrace = TimeSpan.FromMinutes(1);

    /// <summary>Lượt <c>tu-dong</c> bị gián đoạn tới số lần claim này thì không tự chạy lại nữa mà chuyển <c>can-xem-lai</c>.</summary>
    public const int MaxAutoAttempts = 3;

    private const int DeleteBatchSize = 500;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

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

    /// <summary>
    /// Phục hồi các bản tin <c>dang-giai-ma</c> bắt đầu quá <paramref name="grace"/> trước (mặc định 1 phút) mà không còn lượt
    /// nào sống giữ dòng đó: tìm bằng <c>FOR UPDATE SKIP LOCKED</c> — lấy được khoá nghĩa là giao dịch chính của lượt đã
    /// kết thúc (commit, rollback hoặc tiến trình chết), còn dòng đang bị khoá thì lượt đó còn sống nên bỏ qua. Lượt
    /// <c>tu-dong</c> mới claim dưới <see cref="MaxAutoAttempts"/> lần thì về <c>cho-giai-ma</c> để tự chạy lại; các trường hợp
    /// còn lại (lượt <c>giai-ma-lai</c>, hoặc đã thử đủ số lần) thì <c>can-xem-lai</c> kèm ghi chú "Giải mã bị gián đoạn {n} lần".
    /// </summary>
    public async Task<RawMessageRecovery> RecoverAsync(TimeSpan? grace = null, CancellationToken ct = default)
    {
        var cutoff = (_clock.GetUtcNow() - (grace ?? DefaultRecoverGrace)).UtcDateTime;
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var stuck = (await connection.QueryAsync<StuckRow>(new CommandDefinition(
            """
            SELECT id, decode_kind, decode_attempts FROM raw_message
            WHERE decode_status = 'dang-giai-ma' AND decode_started_at < @cutoff
            ORDER BY stored_at, id
            FOR UPDATE SKIP LOCKED
            """,
            new { cutoff }, transaction, cancellationToken: ct))).ToList();

        int requeued = 0, needsReview = 0;
        foreach (var row in stuck)
        {
            if (row.DecodeKind == "tu-dong" && row.DecodeAttempts < MaxAutoAttempts)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE raw_message SET decode_status = 'cho-giai-ma' WHERE id = @id", new { row.Id }, transaction, cancellationToken: ct));
                requeued++;
                continue;
            }

            var result = JsonSerializer.Serialize(new
            {
                interrupted = true,
                attempts = row.DecodeAttempts,
                note = $"Giải mã bị gián đoạn {row.DecodeAttempts} lần",
            });
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE raw_message SET decode_status = 'can-xem-lai', decode_result = @result::jsonb WHERE id = @id",
                new { row.Id, result }, transaction, cancellationToken: ct));
            if (row.DecodeKind is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO raw_message_decode (raw_message_id, kind, actor_id, decoder_version, status, result)
                    VALUES (@Id, @DecodeKind, NULL, @version, 'can-xem-lai', @result::jsonb)
                    """,
                    new { row.Id, row.DecodeKind, version = RawMessageDecoder.Version, result }, transaction, cancellationToken: ct));
            }

            needsReview++;
        }

        await transaction.CommitAsync(ct);
        return new RawMessageRecovery(requeued, needsReview);
    }

    /// <summary>
    /// Id các bản tin <c>cho-giai-ma</c>: từng máy theo đúng thứ tự máy đã truyền (<c>completed_at</c>, <c>seq</c>) để kết quả
    /// của bản tin sau không bị bản tin trước đè lên khi giải mã lại hàng loạt.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> PendingAsync(int max)
    {
        await using var connection = await db.OpenAsync();
        var ids = await connection.QueryAsync<Guid>(
            "SELECT id FROM raw_message WHERE decode_status = 'cho-giai-ma' ORDER BY instrument_id, completed_at, seq, stored_at, id LIMIT @max",
            new { max = Math.Max(1, max) });
        return ids.ToList();
    }

    /// <summary>
    /// Xoá bản tin lưu trước <paramref name="storedBefore"/> — chỉ các trạng thái đã kết thúc; không bao giờ xoá bản tin chưa xong.
    /// Xoá từng lô <see cref="DeleteBatchSize"/> dòng mỗi lệnh (giao dịch ngắn, không giữ khoá lâu) tới khi hết.
    /// </summary>
    public async Task<int> DeleteOlderThanAsync(DateTimeOffset storedBefore)
    {
        await using var connection = await db.OpenAsync();
        var total = 0;
        while (true)
        {
            var deleted = await connection.ExecuteAsync(
                """
                DELETE FROM raw_message WHERE id IN (
                    SELECT id FROM raw_message
                    WHERE stored_at < @storedBefore AND decode_status = ANY(@terminal)
                    ORDER BY stored_at, id
                    LIMIT @batch
                    FOR UPDATE SKIP LOCKED)
                """,
                new { storedBefore = storedBefore.ToUniversalTime(), terminal = RawMessageStatus.Terminal.ToArray(), batch = DeleteBatchSize });
            total += deleted;
            if (deleted < DeleteBatchSize)
            {
                return total;
            }
        }
    }

    internal sealed record StuckRow
    {
        public Guid Id { get; init; }
        public string? DecodeKind { get; init; }
        public int DecodeAttempts { get; init; }
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
