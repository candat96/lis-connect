using Lis.Data.RawMessages;
using Lis.Server.Tenancy;
using Microsoft.AspNetCore.Mvc;

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
/// giải mã lỗi không ảnh hưởng việc đã lưu — chỉ lỗi CSDL khi LƯU mới trả 5xx cho cả lô để agent gửi lại (id do agent sinh nên gửi lại không trùng).
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

        var results = new List<RawMessageResult>(messages.Count);
        long totalBytes = 0;
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

            totalBytes += bytes.Length;
            if (totalBytes > MaxBatchBytes)
            {
                return Results.Problem($"Mỗi lô tối đa {MaxBatchBytes / 1024 / 1024} MB", statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            RawMessageRow? inserted;
            try
            {
                inserted = await tenant.RawMessages.InsertIfAbsentAsync(
                    new RawMessageInsert(
                        messageId, instrumentId, id.Agent.Id, item.Seq!.Value, item.Session!, item.StartedAt!.Value, item.CompletedAt!.Value, bytes,
                        item.Partial ?? false),
                    http.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Lưu thất bại: không có xác nhận nào cho bản tin này — cả lô trả 5xx, agent giữ trên đĩa và gửi lại sau.
                log.LogError(ex, "Không lưu được bản tin thô {MessageId}", messageId);
                return Results.Problem("Không lưu được bản tin — gửi lại sau", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (inserted is null)
            {
                var existing = await tenant.RawMessages.FindAsync(messageId);
                // Cùng id nhưng của máy khác: không để lộ trạng thái, coi như không hợp lệ.
                results.Add(existing is null || existing.InstrumentId != instrumentId
                    ? new RawMessageResult(messageId, Invalid, null)
                    : new RawMessageResult(messageId, Duplicate, existing.DecodeStatus));
                continue;
            }

            results.Add(new RawMessageResult(messageId, Saved, await DecodeAsync(tenant, messageId, log, stopping)));
        }

        return Results.Ok(new RawMessagesResponse(results));
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
            || string.IsNullOrEmpty(item.Bytes))
        {
            return null;
        }

        // Bộ đệm đủ chứa byte giải mã của chuỗi, nhưng không quá 4 MB: lớn hơn thì TryFromBase64String trả false → không hợp lệ.
        var buffer = new byte[Math.Min(MaxMessageBytes, (item.Bytes.Length / 4 + 1) * 3)];
        return Convert.TryFromBase64String(item.Bytes, buffer, out var written) && written > 0 ? buffer[..written] : null;
    }
}
