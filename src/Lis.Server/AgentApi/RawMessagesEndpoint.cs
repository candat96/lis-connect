using Dapper;
using Lis.Data.RawMessages;
using Lis.Server.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Lis.Server.AgentApi;

/// <summary>Một bản tin thô agent gửi. Mọi trường nullable để thân thiếu trường chỉ làm hỏng đúng bản tin đó (<c>khong-hop-le</c>), không hỏng cả lô.</summary>
public sealed record RawMessageItem(
    Guid? Id, Guid? InstrumentId, long? Seq, string? Session, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Bytes, bool? Partial);

public sealed record RawMessagesRequest(IReadOnlyList<RawMessageItem?>? Messages);

/// <summary>Kết quả từng bản tin: <see cref="Status"/> ∈ da-luu / trung / khong-thuoc-agent / khong-hop-le; <see cref="DecodeStatus"/> null khi không lưu.</summary>
public sealed record RawMessageResult(Guid? Id, string Status, string? DecodeStatus);

public sealed record RawMessagesResponse(IReadOnlyList<RawMessageResult> Results);

/// <summary>
/// <c>POST /agent/v1/raw-messages</c>: agent đẩy bản tin thô (đã cắt theo lượt truyền). Server lưu rồi giải mã ngay ở đây;
/// giải mã lỗi không ảnh hưởng việc đã lưu — chỉ lỗi CSDL tạm thời khi LƯU mới trả 5xx cho cả lô (lỗi dữ liệu / ràng buộc của một bản tin chỉ làm hỏng bản tin đó) để agent gửi lại (id do agent sinh nên gửi lại không trùng).
/// </summary>
public static class RawMessagesEndpoint
{
    public const int MaxBatchMessages = 50;
    public const int MaxMessageBytes = 4 * 1024 * 1024;
    public const int MaxBatchBytes = 16 * 1024 * 1024;

    /// <summary>Base64 phình thêm 1/3 so với byte gốc, cộng phần JSON bao quanh 50 bản tin.</summary>
    private const long MaxRequestBodyBytes = MaxBatchBytes / 3 * 4 + 1024 * 1024;

    private const int MaxSessionLength = 200;

    public const string Saved = "da-luu";
    public const string Duplicate = "trung";
    public const string NotOwned = "khong-thuoc-agent";
    public const string Invalid = "khong-hop-le";

    public static void MapRawMessages(this RouteGroupBuilder group) =>
        group.MapPost("/raw-messages", ReceiveAsync).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));

    private static async Task<IResult> ReceiveAsync(
        RawMessagesRequest request, HttpContext http, TenantRegistry tenants, IHostApplicationLifetime lifetime, ILoggerFactory loggers)
    {
        var messages = request.Messages;
        if (messages is null)
        {
            return Results.Problem("Thiếu danh sách bản tin", statusCode: StatusCodes.Status400BadRequest);
        }

        if (messages.Count > MaxBatchMessages)
        {
            return Results.Problem($"Mỗi lô tối đa {MaxBatchMessages} bản tin", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var log = loggers.CreateLogger("Lis.Server.RawMessages");
        var id = AgentEndpoints.Identity(http);
        var tenant = tenants.For(id.Clinic);
        var mine = (await tenant.Instruments.ListAsync())
            .Where(i => string.Equals(i.WorkstationCode, id.Agent.WorkstationCode, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Id)
            .ToHashSet();
        // Giải mã dùng token dừng của host, không dùng RequestAborted: HTTP ngắt giữa chừng không được huỷ lượt giải mã đang chạy.
        var stopping = lifetime.ApplicationStopping;

        // Kiểm tổng kích thước TRƯỚC khi lưu bất kỳ bản nào: 413 sau khi đã lưu một phần sẽ khiến agent gửi lại cả lô vô ích.
        // Ước từ độ dài base64 (3 byte / 4 ký tự) — không cần giải mã trước.
        long estimatedBytes = 0;
        foreach (var item in messages)
        {
            estimatedBytes += (item?.Bytes?.Length ?? 0) / 4L * 3;
        }

        if (estimatedBytes > MaxBatchBytes)
        {
            return Results.Problem($"Mỗi lô tối đa {MaxBatchBytes / 1024 / 1024} MB", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var results = new List<RawMessageResult>(messages.Count);
        foreach (var item in messages)
        {
            if (item is null || item.Id is not { } messageId || messageId == Guid.Empty)
            {
                results.Add(new RawMessageResult(item?.Id, Invalid, null));
                continue;
            }

            if (item.InstrumentId is not { } instrumentId || !mine.Contains(instrumentId))
            {
                results.Add(new RawMessageResult(messageId, NotOwned, null));
                continue;
            }

            var bytes = Validate(item);
            if (bytes is null)
            {
                results.Add(new RawMessageResult(messageId, Invalid, null));
                continue;
            }

            Stored stored;
            try
            {
                stored = await StoreAsync(tenant, id.Agent.Id, item, messageId, instrumentId, bytes, http.RequestAborted, log);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Lưu thất bại tạm thời: không có xác nhận nào cho bản tin này — cả lô trả 5xx, agent giữ trên đĩa và gửi lại sau.
                // Chỉ ghi mã lỗi + thông điệp (không ex.ToString(), không Detail: có thể chứa dữ liệu người bệnh).
                log.LogError("Không lưu được bản tin thô {MessageId}: {Error}", messageId, Describe(ex));
                return Results.Problem("Không lưu được bản tin — gửi lại sau", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (stored.Inserted)
            {
                results.Add(new RawMessageResult(messageId, Saved, await DecodeAsync(tenant, messageId, log, stopping)));
            }
            else if (stored.Result is { Status: Duplicate, DecodeStatus: RawMessageStatus.Waiting })
            {
                // Bản tin trùng mà còn cho-giai-ma (server chết giữa lưu và giải mã): giải mã ngay tại chỗ, đúng thứ tự trong lô, để bản
                // tin cũ này không bị worker chạy SAU các bản mới hơn của cùng mẫu. dang-giai-ma thì lượt khác đang giữ: bỏ qua.
                results.Add(new RawMessageResult(messageId, Duplicate, await DecodeAsync(tenant, messageId, log, stopping)));
            }
            else
            {
                results.Add(stored.Result!);
            }
        }

        return Results.Ok(new RawMessagesResponse(results));
    }

    /// <summary>Kết quả lưu một bản tin: vừa chèn (cần giải mã) hoặc đã có kết quả báo ngay (trùng / không hợp lệ).</summary>
    private readonly record struct Stored(bool Inserted, RawMessageResult? Result);

    private sealed class ExistingRow
    {
        public Guid InstrumentId { get; init; }
        public string DecodeStatus { get; init; } = "";
    }

    private static string Describe(Exception ex) =>
        ex is PostgresException pg ? $"{pg.SqlState} {pg.MessageText}" : $"{ex.GetType().Name} {ex.Message}";

    /// <summary>Lỗi dữ liệu (lớp 22) hoặc ràng buộc (lớp 23) là vĩnh viễn: gửi lại vẫn lỗi, nên chỉ bản tin đó khong-hop-le.</summary>
    private static bool IsPermanent(PostgresException ex) => ex.SqlState.StartsWith("22", StringComparison.Ordinal) || ex.SqlState.StartsWith("23", StringComparison.Ordinal);

    private static async Task<Stored> StoreAsync(
        TenantServices tenant, Guid agentId, RawMessageItem item, Guid messageId, Guid instrumentId, byte[] bytes, CancellationToken ct, ILogger log)
    {
        var insert = new RawMessageInsert(
            messageId, instrumentId, agentId, item.Seq!.Value, item.Session!, item.StartedAt!.Value, item.CompletedAt!.Value, bytes, item.Partial ?? false);
        // Lần 2 chỉ khi dòng trùng vừa bị dọn giữa hai lệnh.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (await tenant.RawMessages.InsertIfAbsentAsync(insert, ct) is not null)
                {
                    return new Stored(true, null);
                }
            }
            catch (PostgresException ex) when (IsPermanent(ex))
            {
                log.LogWarning("Bản tin thô {MessageId} bị CSDL từ chối vĩnh viễn: {Error}", messageId, Describe(ex));
                return new Stored(false, new RawMessageResult(messageId, Invalid, null));
            }

            // Truy vấn nhẹ: không kéo cột bytes (tới 4 MB).
            await using var connection = await tenant.Db.OpenAsync(ct);
            var existing = await connection.QuerySingleOrDefaultAsync<ExistingRow>(
                "SELECT instrument_id AS InstrumentId, decode_status AS DecodeStatus FROM raw_message WHERE id = @messageId", new { messageId });
            if (existing is not null)
            {
                // Cùng id nhưng của máy khác: không để lộ trạng thái, coi như không hợp lệ.
                return new Stored(false, existing.InstrumentId != instrumentId
                    ? new RawMessageResult(messageId, Invalid, null)
                    : new RawMessageResult(messageId, Duplicate, existing.DecodeStatus));
            }
        }

        return new Stored(false, new RawMessageResult(messageId, Invalid, null));
    }

    /// <summary>Giải mã ngay; lỗi nào cũng không làm mất việc đã lưu (bản tin còn <c>cho-giai-ma</c> thì worker vớt lại).</summary>
    private static async Task<string> DecodeAsync(TenantServices tenant, Guid id, ILogger log, CancellationToken stopping)
    {
        try
        {
            return (await tenant.RawProcessor.DecodeAsync(id, "tu-dong", null, stopping)).DecodeStatus;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Giải mã bản tin thô {MessageId} lỗi; bản tin vẫn được lưu", id);
            return RawMessageStatus.Waiting;
        }
    }

    /// <summary>Byte đã giải base64 nếu bản tin hợp lệ, ngược lại null.</summary>
    private static byte[]? Validate(RawMessageItem item)
    {
        if (item.Seq is null || item.StartedAt is null || item.CompletedAt is null
            || string.IsNullOrWhiteSpace(item.Session) || item.Session.Length > MaxSessionLength
            // PostgreSQL không lưu được NUL trong text (22021): từ chối ngay, khỏi để lỗi này kẹt cả lô. Bytes là base64 nên NUL tự hỏng ở dưới.
            || item.Session.Contains('\0')
            || string.IsNullOrEmpty(item.Bytes))
        {
            return null;
        }

        // Bộ đệm đủ chứa byte giải mã của chuỗi, nhưng không quá 4 MB: lớn hơn thì TryFromBase64String trả false → không hợp lệ.
        var buffer = new byte[Math.Min(MaxMessageBytes, (item.Bytes.Length / 4 + 1) * 3)];
        return Convert.TryFromBase64String(item.Bytes, buffer, out var written) && written > 0 ? buffer[..written] : null;
    }
}
