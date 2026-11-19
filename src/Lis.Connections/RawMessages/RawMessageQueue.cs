using System.Text.Json;

namespace Lis.Connections.RawMessages;

/// <summary>
/// Hàng chờ bản tin thô trên đĩa: <c>{root}/{instrumentId:N}/{Seq:D20}-{Id:N}.json</c>. Ghi nguyên tử (.tmp, flush xuống đĩa, đổi tên);
/// đọc theo <see cref="RawMessage.Seq"/>; tệp hỏng đổi thành <c>.loi</c> và không chặn hàng. Tệp đang bị khoá thì
/// <see cref="Peek"/> dừng tại đó (không nhảy qua), để bản tin sau không đến server trước bản tin trước.
/// </summary>
public class RawMessageQueue : IRawMessageSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly TimeSpan StaleTemp = TimeSpan.FromDays(1);

    private readonly string root;

    public RawMessageQueue(string root)
    {
        this.root = root;
        DeleteStaleTempFiles();
    }

    private string DirectoryOf(Guid instrumentId) => Path.Combine(root, instrumentId.ToString("N"));

    private static string FileName(RawMessage m) => $"{m.Seq:D20}-{m.Id:N}.json";

    public async Task SaveAsync(RawMessage message, CancellationToken ct)
    {
        var dir = DirectoryOf(message.InstrumentId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName(message));
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, message, Json, ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Tối đa <paramref name="max"/> bản tin của máy, theo <c>Seq</c> tăng dần; dừng tại tệp đầu tiên đang bị khoá.</summary>
    public IReadOnlyList<RawMessage> Peek(Guid instrumentId, int max)
    {
        var dir = DirectoryOf(instrumentId);
        var result = new List<RawMessage>();
        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (var file in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            if (result.Count >= max)
            {
                break;
            }

            byte[] content;
            try
            {
                // Chia sẻ đọc/ghi/xoá: tệp đang bị chương trình khác giữ (diệt vi-rút, sao lưu) chỉ là lỗi tạm, không phải tệp hỏng.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                content = buffer.ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dừng tại đây, không nhảy qua: server ingest đường Live theo thứ tự đến, bản tin cũ đến sau bản tin mới
                // sẽ ghi giá trị cũ đè lên kết quả chạy lại của mẫu chưa duyệt. Lần Peek sau đọc lại từ tệp này.
                break;
            }

            try
            {
                var message = JsonSerializer.Deserialize<RawMessage>(content, Json);
                if (message is null || message.Bytes is null || message.InstrumentId != instrumentId)
                {
                    throw new JsonException("Nội dung không hợp lệ");
                }

                result.Add(message);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
            {
                try
                {
                    File.Move(file, file + ".loi", overwrite: true);
                }
                catch (Exception)
                {
                    // không đổi tên được: bỏ qua, lần sau thử lại
                }
            }
        }

        return result;
    }

    public IReadOnlyList<Guid> Instruments()
    {
        DeleteStaleTempFiles();
        try
        {
            return Directory.GetDirectories(root)
                .Select(d => Guid.TryParseExact(Path.GetFileName(d), "N", out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty && HasJson(id))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // thư mục gốc chưa có hoặc bị xoá giữa chừng
        }
    }

    private bool HasJson(Guid instrumentId)
    {
        try
        {
            return Directory.EnumerateFiles(DirectoryOf(instrumentId), "*.json").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // thư mục máy vừa bị xoá
        }
    }

    /// <summary><c>Seq</c> lớn nhất đọc từ tên tệp (<c>.json</c> và <c>.loi</c>); 0 nếu không có.</summary>
    public long MaxSeq(Guid instrumentId)
    {
        long max = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(DirectoryOf(instrumentId)))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(".json", StringComparison.Ordinal) && !name.EndsWith(".json.loi", StringComparison.Ordinal))
                {
                    continue;
                }

                if (name.Length > 20 && long.TryParse(name.AsSpan(0, 20), out var seq) && seq > max)
                {
                    max = seq;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // chưa có thư mục: chưa có bản tin nào
        }

        return max;
    }

    /// <summary>Tệp <c>.tmp</c> mồ côi (tắt máy giữa lúc ghi) quá 1 ngày thì xoá. Không bao giờ ném lỗi.</summary>
    private void DeleteStaleTempFiles()
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var temp in Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(temp) > StaleTemp)
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // đang bị giữ: để lần sau
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // thư mục vừa bị xoá
        }
    }

    public virtual void Delete(RawMessage message)
    {
        var path = Path.Combine(DirectoryOf(message.InstrumentId), FileName(message));
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Đổi tệp của bản tin thành <c>.json.loi</c> (server từ chối vì không hợp lệ): rời khỏi hàng gửi nhưng giữ lại để xem tay và giữ <see cref="MaxSeq"/>.</summary>
    public virtual void MarkInvalid(RawMessage message)
    {
        var path = Path.Combine(DirectoryOf(message.InstrumentId), FileName(message));
        if (File.Exists(path))
        {
            File.Move(path, path + ".loi", overwrite: true);
        }
    }

    /// <summary>Số bản tin còn chờ gửi của một máy.</summary>
    public int Count(Guid instrumentId)
    {
        try
        {
            return Directory.EnumerateFiles(DirectoryOf(instrumentId), "*.json").Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public int Count() =>
        Directory.Exists(root) ? Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length : 0;
}
