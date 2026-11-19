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
/// <b>Hàng rào lượt chạy.</b> <c>dang-giai-ma</c> + <c>decode_started_at</c> được commit TRƯỚC giao dịch chính
/// (<see cref="ClaimAsync"/>); giao dịch chính mở đầu bằng <c>SELECT … FOR UPDATE</c> đúng dòng đó với đúng dấu thời gian
/// đã claim và giữ khoá tới lúc commit. Mọi <c>UPDATE</c> trạng thái ngoài giao dịch chính cũng kèm điều kiện đó, nên một
/// lượt cũ (đã bị phục hồi cướp, hoặc đã xong) không thể ghi đè trạng thái của lượt sau — kể cả khi commit mập mờ.
/// <para>
/// <b>Phục hồi.</b> <see cref="RawMessageStore.RecoverAsync"/> tìm dòng <c>dang-giai-ma</c> đã quá hạn ngắn bằng
/// <c>FOR UPDATE SKIP LOCKED</c>: lấy được khoá nghĩa là không còn lượt sống nào giữ dòng. Không đoán theo thời gian chạy dài.
/// </para>
/// <para>
/// <b>Khoá mẫu theo thứ tự.</b> Ngay sau khoá dòng bản tin, giao dịch khoá trước mọi mẫu của bản tin theo thứ tự <c>id</c>,
/// nên hai bản tin chứa cùng các mẫu theo thứ tự ngược nhau không deadlock; deadlock / lỗi tạm thời còn lại được thử lại.
/// </para>
/// Tắt máy chủ có báo (<see cref="OperationCanceledException"/> do token bị huỷ) thì giao dịch chính rollback và trạng thái
/// trở về như trước lượt này để lần khởi động sau giải mã đúng một lần.
/// </remarks>
public sealed class RawMessageProcessor(Db db, IngestService ingest, IChannelMapLearning? learning, TimeProvider clock, Action<string>? log = null)
{
    /// <summary>Quá thời gian này mà vẫn <c>dang-giai-ma</c> và không ai giữ khoá dòng thì coi là lượt đã chết.</summary>
    public static readonly TimeSpan RecoverGrace = RawMessageStore.DefaultRecoverGrace;

    private const string KindAuto = "tu-dong";
    private const string KindRedo = "giai-ma-lai";

    private const string RedecodeEmptyNote = "Giải mã lại không ra kết quả nào — giữ nguyên dòng Chưa ghép/QC cũ. Kiểm cấu hình máy.";

    /// <summary>Số lần thử lại tối đa khi gặp deadlock / lỗi tạm thời, nghỉ <see cref="RetryStep"/> × lần thử.</summary>
    private const int MaxTransientRetries = 3;

    private static readonly TimeSpan RetryStep = TimeSpan.FromMilliseconds(100);

    private readonly RawMessageStore _store = new(db, clock);

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
        var state = new RunState();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var summary = await db.InTransactionAsync(
                        (connection, transaction) => RunAsync(message, claim.PreviousStatus, claim.ClaimedAt, kind, actorId, state, connection, transaction), ct);
                    if (kind == KindAuto && summary.Unmatched > 0 && learning is not null)
                    {
                        await TryLearnAsync(message.InstrumentId);
                    }

                    break;
                }
                catch (ClaimLostException)
                {
                    // Lượt khác đã lấy lại dòng (hoặc lượt này đã bị phục hồi cướp): giao dịch đã rollback, không ghi gì thêm.
                    log?.Invoke($"Bản tin {id}: lượt {kind} mất quyền chạy, bỏ lượt này.");
                    break;
                }
                catch (Exception ex) when (attempt < MaxTransientRetries && !ct.IsCancellationRequested && IsTransient(ex))
                {
                    log?.Invoke($"Bản tin {id}: lỗi tạm thời ({ex.GetType().Name}), thử lại lần {attempt + 1}: {ex.Message}");
                    await Task.Delay(RetryStep * (attempt + 1), clock, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Kể cả OperationCanceledException mà token KHÔNG bị huỷ (hết giờ nội bộ…): là lỗi, không phải thành công.
                    await RecordErrorAsync(message, claim.ClaimedAt, kind, actorId, state.Config, ex);
                    break;
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Token đã huỷ (tắt máy chủ): lỗi nào kéo theo (kể cả không phải OperationCanceledException, vd kết nối đứt) cũng xử lý như
            // tắt máy — trả trạng thái như trước (có hàng rào), không ghi loi.
            await RevertAsync(id, claim.PreviousStatus!, claim.ClaimedAt);
            ct.ThrowIfCancellationRequested();
            throw;
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
    /// Lúc khởi động (và định kỳ): phục hồi bản tin <c>dang-giai-ma</c> không còn lượt sống giữ
    /// (<see cref="RawMessageStore.RecoverAsync"/>: lượt <c>tu-dong</c> chạy lại, các lượt khác <c>can-xem-lai</c>), rồi
    /// giải mã các bản tin <c>cho-giai-ma</c> (tối đa 500 cái mỗi lần gọi, từng máy theo thứ tự truyền). Một bản tin lỗi
    /// không chặn các bản tin sau; lỗi hạ tầng được gom và ném sau cùng.
    /// </summary>
    public async Task<RawMessageRecovery> RecoverAsync(CancellationToken ct)
    {
        var recovery = await _store.RecoverAsync(RecoverGrace, ct);
        if (recovery.Requeued > 0 || recovery.NeedsReview > 0)
        {
            log?.Invoke($"Phục hồi bản tin thô: {recovery.Requeued} bản tin về cho-giai-ma, {recovery.NeedsReview} bản tin sang can-xem-lai.");
        }

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

        return recovery;
    }

    /// <summary>Kết quả claim: dòng + trạng thái trước + dấu thời gian claim; hoặc <see cref="Skipped"/> khi không được chạy.</summary>
    internal sealed record ClaimResult(RawMessageRow? Row, string? PreviousStatus, DateTime ClaimedAt, RawMessageRow? Skipped);

    /// <summary>
    /// Bước 1-2: khoá dòng, kiểm tra được phép chạy, đặt <c>dang-giai-ma</c> + <c>decode_started_at</c> + loại lượt, tăng
    /// <c>decode_attempts</c>, commit ngay. <c>decode_started_at</c> đọc lại từ CSDL (độ chính xác micro giây) để làm hàng rào.
    /// </summary>
    internal async Task<ClaimResult> ClaimAsync(Guid id, string kind, CancellationToken ct)
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
            return new ClaimResult(null, null, default, row);
        }

        var claimedAt = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            """
            UPDATE raw_message
            SET decode_status = 'dang-giai-ma', decode_started_at = @now, decode_kind = @kind, decode_attempts = decode_attempts + 1
            WHERE id = @id
            RETURNING decode_started_at
            """,
            new { id, kind, now = clock.GetUtcNow().UtcDateTime }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return new ClaimResult(row, row.DecodeStatus, DateTime.SpecifyKind(claimedAt, DateTimeKind.Utc), null);
    }

    /// <summary>Một lượt thử của giao dịch chính: hàng rào → khoá mẫu theo thứ tự → (giải mã lại: thay dòng QC/Chưa ghép) → ingest → kết thúc.</summary>
    private async Task<Summary> RunAsync(
        RawMessageRow message, string? previousStatus, DateTime claimedAt, string kind, Guid? actorId, RunState state, NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var locked = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM raw_message WHERE id = @id AND decode_status = 'dang-giai-ma' AND decode_started_at = @claimedAt FOR UPDATE",
            new { id = message.Id, claimedAt }, transaction);
        if (locked is null)
        {
            throw new ClaimLostException();
        }

        var config = await LoadConfigAsync(connection, transaction, message.InstrumentId)
            ?? throw new InvalidOperationException("Không tìm thấy máy của bản tin.");
        state.Config = config;
        await SnapshotConfigAsync(connection, transaction, message.Id, config);

        var driver = CreateDriver(config);
        if (driver is null)
        {
            var stored = new Totals();
            await FinishAsync(connection, transaction, message.Id, claimedAt, kind, actorId, config, RawMessageStatus.StoredOnly,
                Json(0, stored, [], [], false, [], null));
            return new Summary(RawMessageStatus.StoredOnly, 0);
        }

        var decoded = RawMessageDecoder.Decode(driver, message.Bytes, message.CompletedAt);
        var rawRef = $"ban-tin:{message.Id}";
        var totals = new Totals();

        if (kind == KindRedo && decoded.Readings.Sum(r => r.Items.Count) == 0 && await HasSomethingToKeepAsync(
                connection, transaction, message.InstrumentId, rawRef, previousStatus))
        {
            // Giải mã lại không ra kết quả nào mà có thứ cần giữ (dòng Chưa ghép chưa xử lý / QC của lượt trước, hoặc lượt trước đã
            // ra kết quả) — cấu hình máy sai, bộ giải mã lỗi…: không xoá gì, không ingest, báo loi. Bản tin vốn không có kết quả
            // (chỉ câu hỏi chỉ định, byte rời) thì đi đường tính trạng thái thường bên dưới.
            var emptyJson = Json(0, totals, decoded.QueryBarcodes, decoded.Notes, decoded.Abandoned, decoded.Discarded, RedecodeEmptyNote);
            await FinishAsync(connection, transaction, message.Id, claimedAt, kind, actorId, config, RawMessageStatus.Error, emptyJson);
            return new Summary(RawMessageStatus.Error, 0);
        }

        if (kind == KindRedo)
        {
            // Dòng QC / Chưa ghép chưa xử lý do bản tin này sinh ra ở lượt trước bị thay bằng kết quả của bộ giải mã hiện tại.
            // Dòng Chưa ghép đã xử lý giữ nguyên (QC không có thao tác của người nên thay hết). Xoá TRƯỚC khoá mẫu: cùng thứ
            // tự khoá với UnmatchedService.AssignAsync (dòng Chưa ghép rồi mới tới mẫu).
            totals.SupersededUnmatched = await connection.ExecuteAsync(
                "DELETE FROM unmatched_result WHERE instrument_id = @instrumentId AND raw_ref = @rawRef AND resolved_at IS NULL",
                new { instrumentId = message.InstrumentId, rawRef }, transaction);
            totals.SupersededQc = await connection.ExecuteAsync(
                "DELETE FROM qc_result WHERE instrument_id = @instrumentId AND raw_ref = @rawRef",
                new { instrumentId = message.InstrumentId, rawRef }, transaction);
        }

        // Khoá trước mọi mẫu của bản tin theo thứ tự cố định để hai bản tin không khoá chéo nhau.
        var barcodes = decoded.Readings
            .Where(r => !r.IsQc)
            .Select(r => ReadingNormalizer.CleanBarcode(r.Barcode))
            .Where(b => b.Length > 0)
            .Distinct()
            .ToArray();
        if (barcodes.Length > 0)
        {
            await connection.QueryAsync<Guid>(
                "SELECT id FROM sample WHERE barcode = ANY(@barcodes) ORDER BY id FOR UPDATE", new { barcodes }, transaction);
        }

        var mode = kind == KindRedo ? ValueWriteMode.Redecode : ValueWriteMode.Live;
        var usedResolved = new HashSet<Guid>();
        var index = 0;
        foreach (var r in decoded.Readings)
        {
            var outcome = await ingest.IngestDetailedAsync(
                new InstrumentReading(message.InstrumentId, r.Barcode, r.IsQc, message.CompletedAt, rawRef, r.Items), connection, transaction, mode, usedResolved);
            totals.Add(outcome);

            index++;
            if (AfterReadingIngested is { } hook)
            {
                await hook(index);
            }
        }

        string status;
        string? error = null;
        if (decoded.Readings.Count > 0)
        {
            // Dữ liệu bị bỏ ở đâu đó trong bản tin nhưng vẫn ra kết quả: giữ làm cảnh báo (Discarded), không phải lỗi.
            status = RawMessageStatus.Decoded;
        }
        else if (OnlyLooseBytes(message.Bytes) || !decoded.Abandoned)
        {
            // Không có kết quả: chỉ câu hỏi chỉ định, byte rời (CR sau FS, khoảng trắng, ENQ/EOT lẻ), khung Beckman AU DB/DE…
            // là bình thường, không phải lỗi.
            status = RawMessageStatus.NoResult;
        }
        else
        {
            // Bản tin dở dang (hết giờ, bắt đầu lại giữa chừng…) mà có nội dung thật nhưng không ra kết quả nào.
            status = RawMessageStatus.Error;
            error = "Bản tin dở dang, không ra kết quả nào.";
        }

        // Dòng Chưa ghép đã xử lý mà giải mã lại ra giá trị khác: giữ nguyên dòng đó, báo bằng resolvedMismatch + cảnh báo.
        var warnings = decoded.Discarded.Concat(totals.ResolvedMismatches.Select(m =>
            $"Dòng Chưa ghép đã xử lý (mẫu {m.Barcode}, kênh {m.Channel}) có giá trị {m.OldValue}, giải mã lại ra {m.NewValue} — kiểm tra lại")).ToList();
        var resultJson = Json(decoded.Readings.Count, totals, decoded.QueryBarcodes, decoded.Notes, decoded.Abandoned, warnings, error);
        await FinishAsync(connection, transaction, message.Id, claimedAt, kind, actorId, config, status, resultJson);
        return new Summary(status, totals.Unmatched);
    }

    /// <summary>Có dòng <c>unmatched_result</c> chưa xử lý hoặc <c>qc_result</c> cùng (máy, raw_ref), hoặc lượt trước là <c>da-giai-ma</c>.</summary>
    private static async Task<bool> HasSomethingToKeepAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid instrumentId, string rawRef, string? previousStatus) =>
        previousStatus == RawMessageStatus.Decoded
        || await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM unmatched_result WHERE instrument_id = @instrumentId AND raw_ref = @rawRef AND resolved_at IS NULL)
                OR EXISTS (SELECT 1 FROM qc_result WHERE instrument_id = @instrumentId AND raw_ref = @rawRef)
            """,
            new { instrumentId, rawRef }, transaction);

    /// <summary>Bản tin chỉ gồm khoảng trắng / ký tự điều khiển (kể cả FS, CR, LF, ENQ, EOT…) — không có nội dung nào để giải mã.</summary>
    private static bool OnlyLooseBytes(byte[] bytes) => bytes.All(b => b <= 0x20 || b == 0x7F);

    /// <summary>Ảnh chụp cấu hình máy lúc giải mã: lượt nào ghi trước thì giữ (<c>COALESCE</c>), không phụ thuộc loại lượt.</summary>
    private static async Task SnapshotConfigAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, InstrumentConfig config) =>
        await connection.ExecuteAsync(
            """
            UPDATE raw_message
            SET protocol_family = COALESCE(protocol_family, @Family), model = COALESCE(model, @Model),
                driver_config = COALESCE(driver_config, @DriverConfig::jsonb)
            WHERE id = @id
            """,
            new { id, config.Family, config.Model, config.DriverConfig }, transaction);

    /// <summary>Ghi trạng thái cuối + dòng vết. Kèm hàng rào lượt chạy: không ghi được (lượt đã mất quyền) thì ném <see cref="ClaimLostException"/>.</summary>
    private static async Task FinishAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, DateTime claimedAt, string kind, Guid? actorId,
        InstrumentConfig config, string status, string resultJson)
    {
        if (!await TryFinishAsync(connection, transaction, id, claimedAt, kind, actorId, config, status, resultJson))
        {
            throw new ClaimLostException();
        }
    }

    private static async Task<bool> TryFinishAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, DateTime claimedAt, string kind, Guid? actorId,
        InstrumentConfig config, string status, string resultJson)
    {
        var updated = await connection.ExecuteAsync(
            """
            UPDATE raw_message
            SET decode_status = @status, decode_result = @resultJson::jsonb, decoder_version = @version, decoded_at = now(), decode_attempts = 0
            WHERE id = @id AND decode_status = 'dang-giai-ma' AND decode_started_at = @claimedAt
            """,
            new { id, status, resultJson, version = RawMessageDecoder.Version, claimedAt }, transaction);
        if (updated == 0)
        {
            return false;
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO raw_message_decode (raw_message_id, kind, actor_id, decoder_version, protocol_family, model, driver_config, status, result)
            VALUES (@id, @kind, @actorId, @version, @Family, @Model, @DriverConfig::jsonb, @status, @resultJson::jsonb)
            """,
            new { id, kind, actorId, version = RawMessageDecoder.Version, config.Family, config.Model, config.DriverConfig, status, resultJson },
            transaction);
        return true;
    }

    /// <summary>
    /// Giao dịch riêng sau khi giao dịch chính đã rollback: ghi <c>loi</c> + vết, KÈM hàng rào lượt chạy. Không có reading nào
    /// được giữ. Lượt đã mất quyền (đã xong, đã bị phục hồi, hoặc lượt khác đã claim) thì chỉ ghi log, không ghi gì.
    /// </summary>
    internal async Task RecordErrorAsync(RawMessageRow message, DateTime claimedAt, string kind, Guid? actorId, InstrumentConfig? config, Exception ex)
    {
        var used = config ?? new InstrumentConfig(null, null, null);
        var resultJson = Json(0, new Totals(), [], [], false, [], ex.Message);
        var wrote = await db.InTransactionAsync(async (connection, transaction) =>
        {
            if (!await TryFinishAsync(connection, transaction, message.Id, claimedAt, kind, actorId, used, RawMessageStatus.Error, resultJson))
            {
                return false;
            }

            if (config is not null)
            {
                await SnapshotConfigAsync(connection, transaction, message.Id, config);
            }

            return true;
        });
        if (!wrote)
        {
            log?.Invoke($"Bản tin {message.Id}: lượt {kind} lỗi nhưng đã mất quyền chạy — không ghi trạng thái lỗi ({ex.Message}).");
        }
    }

    /// <summary>Tắt máy chủ giữa lượt: trả trạng thái về như trước lượt này (chỉ khi vẫn đúng lượt đã claim) và không tính là một lần gián đoạn.</summary>
    private async Task RevertAsync(Guid id, string previousStatus, DateTime claimedAt)
    {
        await using var connection = await db.OpenAsync();
        var reverted = await connection.ExecuteAsync(
            """
            UPDATE raw_message SET decode_status = @previousStatus, decode_attempts = GREATEST(decode_attempts - 1, 0)
            WHERE id = @id AND decode_status = 'dang-giai-ma' AND decode_started_at = @claimedAt
            """,
            new { id, previousStatus, claimedAt });
        if (reverted == 0)
        {
            log?.Invoke($"Bản tin {id}: tắt máy chủ giữa lượt nhưng đã mất quyền chạy — không trả trạng thái.");
        }
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

    /// <summary>Deadlock (40P01), lỗi tuần tự hoá (40001) hoặc lỗi Npgsql đánh dấu tạm thời (mạng, hết giờ…): thử lại được.</summary>
    private static bool IsTransient(Exception ex) =>
        ex is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure }
        || ex is NpgsqlException { IsTransient: true };

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

    private static string Json(
        int readings, Totals t, IReadOnlyList<string> queries, IReadOnlyList<string> notes, bool abandoned, IReadOnlyList<string> warnings, string? error) =>
        JsonSerializer.Serialize(new
        {
            readings,
            stored = t.Stored,
            pendingReplacement = t.PendingReplacement,
            unmatched = t.Unmatched,
            qc = t.Qc,
            unchanged = t.Unchanged,
            conflicts = t.Conflicts,
            pendingCleared = t.PendingCleared,
            supersededUnmatched = t.SupersededUnmatched,
            supersededQc = t.SupersededQc,
            olderThanCurrent = t.OlderThanCurrent,
            queries,
            notes,
            warnings,
            resolvedMismatch = t.ResolvedMismatches.Select(m => new { unmatchedId = m.UnmatchedId, barcode = m.Barcode, channel = m.Channel, oldValue = m.OldValue, newValue = m.NewValue }),
            abandoned,
            error,
        });

    private sealed record Summary(string Status, int Unmatched);

    internal sealed record InstrumentConfig(string? Family, string? Model, string? DriverConfig);

    /// <summary>Cấu hình máy lấy được trong lượt thử gần nhất (để ghi vết khi lỗi).</summary>
    private sealed class RunState
    {
        public InstrumentConfig? Config { get; set; }
    }

    private sealed class Totals
    {
        public int Stored { get; private set; }
        public int PendingReplacement { get; private set; }
        public int Unmatched { get; private set; }
        public int Qc { get; private set; }
        public int Unchanged { get; private set; }
        public int Conflicts { get; private set; }
        public int PendingCleared { get; private set; }
        public int SupersededUnmatched { get; set; }
        public int SupersededQc { get; set; }
        public int OlderThanCurrent { get; private set; }
        public List<ResolvedMismatch> ResolvedMismatches { get; } = [];

        public void Add(IngestDetail d)
        {
            Stored += d.Stored;
            PendingReplacement += d.PendingReplacement;
            Unmatched += d.Unmatched;
            Qc += d.Qc;
            Unchanged += d.Unchanged;
            Conflicts += d.Conflicts;
            PendingCleared += d.PendingCleared;
            OlderThanCurrent += d.OlderThanCurrent;
            if (d.ResolvedMismatches is { Count: > 0 } mismatches)
            {
                ResolvedMismatches.AddRange(mismatches);
            }
        }
    }

    /// <summary>Lượt này không còn là lượt đang chạy của bản tin (hàng rào không thấy dòng): huỷ, không ghi gì.</summary>
    private sealed class ClaimLostException : Exception;
}
