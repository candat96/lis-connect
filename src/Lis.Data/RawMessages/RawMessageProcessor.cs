using System.Text.Json;
using Dapper;
using Lis.Core.Drivers;
using Lis.Core.Drivers.ListenOnly;
using Lis.Core.Ingest;
using Lis.Data.Ingest;
using Npgsql;

namespace Lis.Data.RawMessages;

/// <summary>
/// Giải mã bản tin thô đã lưu ở server. Mọi kết quả đi qua <see cref="IngestService"/> (khớp barcode, map kênh,
/// cờ, "chờ thay" khi mẫu đã duyệt) — không có đường ghi nào khác. Một bản tin chạy trong MỘT giao dịch gồm mọi
/// reading, <c>decode_result</c> và dòng vết; lỗi ở bất kỳ reading nào thì rollback hết rồi ghi trạng thái
/// <c>loi</c> trong giao dịch riêng.
/// </summary>
/// <remarks>
/// <c>dang-giai-ma</c> được commit TRƯỚC giao dịch chính để tiến trình chết giữa chừng vẫn bị phát hiện
/// (<see cref="RawMessageStore.MarkInterruptedAsync"/> → <c>can-xem-lai</c>, không tự chạy lại). Tắt máy chủ có
/// báo (<see cref="OperationCanceledException"/>) thì giao dịch chính rollback và trạng thái trở về như trước
/// lượt này (<c>cho-giai-ma</c> với lượt tự động) để lần khởi động sau giải mã đúng một lần.
/// </remarks>
public sealed class RawMessageProcessor(Db db, IngestService ingest, IChannelMapLearning? learning, TimeProvider clock, Action<string>? log = null)
{
    /// <summary>Quá thời gian này mà vẫn <c>dang-giai-ma</c> thì coi là tiến trình đã chết.</summary>
    public static readonly TimeSpan InterruptedAfter = TimeSpan.FromMinutes(5);

    private const string KindAuto = "tu-dong";
    private const string KindRedo = "giai-ma-lai";

    private readonly RawMessageStore _store = new(db);

    /// <summary>Chỉ dùng trong kiểm thử: chạy sau khi reading thứ n (đếm từ 1) đã ingest xong, trong giao dịch chính.</summary>
    internal Func<int, Task>? AfterReadingIngested { get; set; }

    /// <summary>
    /// Giải mã (lượt <c>tu-dong</c> đầu tiên hoặc <c>giai-ma-lai</c> do người dùng) và trả dòng bản tin sau khi xử lý.
    /// Trả nguyên dòng, không làm gì, khi lượt khác đang giải mã, hoặc khi lượt <c>tu-dong</c> mà bản tin không còn
    /// <c>cho-giai-ma</c>. Không có bản tin → <see cref="KeyNotFoundException"/>.
    /// </summary>
    public async Task<RawMessageRow> DecodeAsync(Guid id, string kind, Guid? actorId, CancellationToken ct)
    {
        if (kind is not (KindAuto or KindRedo))
        {
            throw new ArgumentException($"Loại lượt giải mã không hợp lệ: {kind}", nameof(kind));
        }

        // Bước 1-2: khoá dòng, đặt dang-giai-ma và commit ngay (giao dịch riêng).
        var claim = await ClaimAsync(id, kind, ct);
        if (claim.Skipped is { } untouched)
        {
            return untouched;
        }

        var message = claim.Row!;
        var previousStatus = claim.PreviousStatus!;
        InstrumentConfig? config = null;
        try
        {
            var summary = await db.InTransactionAsync(async (connection, transaction) =>
            {
                config = await LoadConfigAsync(connection, transaction, message.InstrumentId)
                    ?? throw new InvalidOperationException("Không tìm thấy máy của bản tin.");
                if (kind == KindAuto)
                {
                    // Lượt tự động đầu tiên: cấu hình lúc này cũng là ảnh chụp lưu vào bản tin.
                    await connection.ExecuteAsync(
                        "UPDATE raw_message SET protocol_family = @Family, model = @Model, driver_config = @DriverConfig::jsonb WHERE id = @id",
                        new { id, config.Family, config.Model, config.DriverConfig }, transaction);
                }

                var summary = await DecodeIngestAsync(message, config, connection, transaction);
                await FinishAsync(connection, transaction, message.Id, kind, actorId, config, summary.Status, summary.ResultJson);
                return summary;
            }, ct);

            if (summary.Unmatched > 0 && learning is not null)
            {
                await TryLearnAsync(message.InstrumentId);
            }
        }
        catch (OperationCanceledException)
        {
            await RevertAsync(id, previousStatus);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            await RecordErrorAsync(message, kind, actorId, config, ex);
        }

        return await _store.FindAsync(id) ?? throw new KeyNotFoundException($"Không tìm thấy bản tin {id}.");
    }

    /// <summary>Giải mã thử bằng cấu hình hiện tại của máy: không ghi gì (không CSDL, không trạng thái).</summary>
    public async Task<RawMessageDecodeResult> PreviewAsync(Guid id)
    {
        var message = await _store.FindAsync(id) ?? throw new KeyNotFoundException($"Không tìm thấy bản tin {id}.");
        InstrumentConfig? config;
        await using (var connection = await db.OpenAsync())
        {
            config = await LoadConfigAsync(connection, null, message.InstrumentId);
        }

        if (config is null)
        {
            throw new InvalidOperationException("Không tìm thấy máy của bản tin.");
        }

        var driver = CreateDriver(config);
        return driver is null
            ? new RawMessageDecodeResult([], [], ["Máy chỉ nghe hoặc chưa chọn giao thức: bản tin chỉ được lưu, không giải mã."], false)
            : RawMessageDecoder.Decode(driver, message.Bytes, message.CompletedAt);
    }

    /// <summary>
    /// Lúc khởi động (và định kỳ): bản tin <c>dang-giai-ma</c> quá 5 phút → <c>can-xem-lai</c> (KHÔNG tự chạy lại), rồi
    /// giải mã các bản tin <c>cho-giai-ma</c> (tối đa 500 cái cũ nhất mỗi lần gọi). Một bản tin lỗi không chặn các bản
    /// tin sau; lỗi hạ tầng được gom và ném sau cùng.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        await _store.MarkInterruptedAsync(clock.GetUtcNow() - InterruptedAfter);

        List<Exception>? failures = null;
        foreach (var id in await _store.PendingAsync(500))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DecodeAsync(id, KindAuto, null, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Một số bản tin thô không giải mã được do lỗi hạ tầng.", failures);
        }
    }

    private async Task<(RawMessageRow? Row, string? PreviousStatus, RawMessageRow? Skipped)> ClaimAsync(Guid id, string kind, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var dto = await connection.QuerySingleOrDefaultAsync<RawMessageStore.Dto>(new CommandDefinition(
            $"SELECT {RawMessageStore.Columns} FROM raw_message WHERE id = @id FOR UPDATE", new { id }, transaction, cancellationToken: ct))
            ?? throw new KeyNotFoundException($"Không tìm thấy bản tin {id}.");
        var row = dto.ToRow();

        if (row.DecodeStatus == RawMessageStatus.Decoding || (kind == KindAuto && row.DecodeStatus != RawMessageStatus.Waiting))
        {
            await transaction.RollbackAsync(ct);
            return (null, null, row);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE raw_message SET decode_status = 'dang-giai-ma', decode_started_at = @now WHERE id = @id",
            new { id, now = clock.GetUtcNow().ToUniversalTime() }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return (row, row.DecodeStatus, null);
    }

    private async Task<Summary> DecodeIngestAsync(RawMessageRow message, InstrumentConfig config, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var driver = CreateDriver(config);
        if (driver is null)
        {
            return new Summary(RawMessageStatus.StoredOnly, 0, Json(0, new IngestOutcome(0, 0, 0, 0), [], [], false, null));
        }

        var decoded = RawMessageDecoder.Decode(driver, message.Bytes, message.CompletedAt);
        var rawRef = $"ban-tin:{message.Id}";
        int stored = 0, pending = 0, unmatched = 0, qc = 0, index = 0;
        foreach (var r in decoded.Readings)
        {
            var outcome = await ingest.IngestAsync(
                new InstrumentReading(message.InstrumentId, r.Barcode, r.IsQc, message.CompletedAt, rawRef, r.Items), connection, transaction);
            stored += outcome.Stored;
            pending += outcome.PendingReplacement;
            unmatched += outcome.Unmatched;
            qc += outcome.Qc;

            index++;
            if (AfterReadingIngested is { } hook)
            {
                await hook(index);
            }
        }

        var total = new IngestOutcome(stored, pending, unmatched, qc);
        if (decoded.Readings.Count > 0)
        {
            return new Summary(RawMessageStatus.Decoded, unmatched, Json(decoded.Readings.Count, total, decoded.QueryBarcodes, decoded.Notes, decoded.Abandoned, null));
        }

        // Không ra kết quả: bản tin dở dang (hết giờ, bắt đầu lại giữa chừng…) là lỗi; còn lại là bình thường
        // (chỉ câu hỏi chỉ định, byte rời, khung Beckman AU DB/DE…).
        return decoded.Abandoned
            ? new Summary(RawMessageStatus.Error, 0, Json(0, total, decoded.QueryBarcodes, decoded.Notes, true, "Bản tin dở dang, không ra kết quả nào."))
            : new Summary(RawMessageStatus.NoResult, 0, Json(0, total, decoded.QueryBarcodes, decoded.Notes, false, null));
    }

    private static async Task FinishAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string kind, Guid? actorId, InstrumentConfig config, string status, string resultJson)
    {
        await connection.ExecuteAsync(
            """
            UPDATE raw_message
            SET decode_status = @status, decode_result = @resultJson::jsonb, decoder_version = @version, decoded_at = now()
            WHERE id = @id
            """,
            new { id, status, resultJson, version = RawMessageDecoder.Version }, transaction);
        await connection.ExecuteAsync(
            """
            INSERT INTO raw_message_decode (raw_message_id, kind, actor_id, decoder_version, protocol_family, model, driver_config, status, result)
            VALUES (@id, @kind, @actorId, @version, @Family, @Model, @DriverConfig::jsonb, @status, @resultJson::jsonb)
            """,
            new { id, kind, actorId, version = RawMessageDecoder.Version, config.Family, config.Model, config.DriverConfig, status, resultJson },
            transaction);
    }

    /// <summary>Giao dịch riêng sau khi giao dịch chính đã rollback: ghi <c>loi</c> + vết. Không có reading nào được giữ.</summary>
    private async Task RecordErrorAsync(RawMessageRow message, string kind, Guid? actorId, InstrumentConfig? config, Exception ex)
    {
        var resultJson = Json(0, new IngestOutcome(0, 0, 0, 0), [], [], false, ex.Message);
        await db.InTransactionAsync(async (connection, transaction) =>
        {
            var used = config ?? new InstrumentConfig(null, null, null);
            await FinishAsync(connection, transaction, message.Id, kind, actorId, used, RawMessageStatus.Error, resultJson);
            return 0;
        });
    }

    /// <summary>Tắt máy chủ giữa lượt: trả trạng thái về như trước lượt này (chỉ khi vẫn đang <c>dang-giai-ma</c>).</summary>
    private async Task RevertAsync(Guid id, string previousStatus)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE raw_message SET decode_status = @previousStatus WHERE id = @id AND decode_status = 'dang-giai-ma'",
            new { id, previousStatus });
    }

    private async Task TryLearnAsync(Guid instrumentId)
    {
        try
        {
            await learning!.AutoLearnAsync(instrumentId);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Tự học map kênh lỗi cho máy {instrumentId}: {ex.Message}");
        }
    }

    private static IInstrumentDriver? CreateDriver(InstrumentConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Family) || string.Equals(config.Family.Trim(), ListenOnlyDriver.FamilyCode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return DriverRegistry.Create(config.Family, config.Model, config.DriverConfig);
    }

    private static Task<InstrumentConfig?> LoadConfigAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid instrumentId) =>
        connection.QuerySingleOrDefaultAsync<InstrumentConfig>(
            "SELECT protocol_family AS Family, model AS Model, driver_config::text AS DriverConfig FROM instrument WHERE id = @instrumentId",
            new { instrumentId }, transaction);

    private static string Json(int readings, IngestOutcome o, IReadOnlyList<string> queries, IReadOnlyList<string> notes, bool abandoned, string? error) =>
        JsonSerializer.Serialize(new
        {
            readings,
            stored = o.Stored,
            pendingReplacement = o.PendingReplacement,
            unmatched = o.Unmatched,
            qc = o.Qc,
            queries,
            notes,
            abandoned,
            error,
        });

    private sealed record Summary(string Status, int Unmatched, string ResultJson);

    private sealed record InstrumentConfig(string? Family, string? Model, string? DriverConfig);
}
