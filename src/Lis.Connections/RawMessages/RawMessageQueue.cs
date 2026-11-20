using System.Collections.Concurrent;
using System.Text.Json;

namespace Lis.Connections.RawMessages;

/// <summary>
/// Hàng chờ bản tin thô trên đĩa: <c>{root}/{instrumentId:N}/{Seq:D20}-{Id:N}.json</c>. Ghi nguyên tử (.tmp, flush xuống đĩa, đổi tên);
/// đọc theo <see cref="RawMessage.Seq"/>; tệp hỏng đổi thành <c>.loi</c> và không chặn hàng. Tệp đang bị khoá thì
/// <see cref="Peek"/> dừng tại đó (không nhảy qua), để bản tin sau không đến server trước bản tin trước.
/// </summary>
/// <remarks>
/// Mất điện giữa lúc ghi: tệp <c>.json.tmp</c> đọc được trọn bản tin thì được đổi thành <c>.json</c> (lúc khởi tạo, <see cref="Instruments"/>, <see cref="Peek"/>) —
/// xấu nhất là trùng Id, server trả <c>trung</c>, không bao giờ mất bản tin máy đã được ACK. <c>.tmp</c> hỏng và cũ hơn 1 ngày → <c>.json.loi</c> (không xoá).
/// </remarks>
public class RawMessageQueue : IRawMessageSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly TimeSpan StaleTemp = TimeSpan.FromDays(1);

    private readonly string root;

    /// <summary>Tệp <c>.tmp</c> đang được <see cref="SaveAsync"/> ghi trong tiến trình này — việc cứu tệp mồ côi không được đụng vào.</summary>
    private readonly ConcurrentDictionary<string, byte> writing = new(StringComparer.Ordinal);

    public RawMessageQueue(string root)
    {
        this.root = root;
        RecoverTempFiles(root);
    }

    private string DirectoryOf(Guid instrumentId) => Path.Combine(root, instrumentId.ToString("N"));

    private static string FileName(RawMessage m) => $"{m.Seq:D20}-{m.Id:N}.json";

    public async Task SaveAsync(RawMessage message, CancellationToken ct)
    {
        var dir = DirectoryOf(message.InstrumentId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName(message));
        var temp = path + ".tmp";
        writing[temp] = 0;
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, message, Json, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            // Không có API .NET để ghi thẳng thao tác đổi tên xuống đĩa (MoveFileEx WRITE_THROUGH / flush thư mục) nên không làm:
            // nếu mất điện ngay sau đây thì tệp .tmp (đã flush) được cứu lại thành .json ở lần mở sau (RecoverTempFiles).
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            writing.TryRemove(temp, out _);
        }
    }

    /// <summary>Tên tệp <c>.json</c> đầu hàng (theo <c>Seq</c>) của máy; <c>null</c> nếu không có.</summary>
    public string? FirstFileName(Guid instrumentId)
    {
        try
        {
            return Directory.EnumerateFiles(DirectoryOf(instrumentId), "*.json").Order(StringComparer.Ordinal).Select(Path.GetFileName).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

        RecoverTempFiles(dir);

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
        RecoverTempFiles(root);
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

    /// <summary>
    /// Cứu tệp <c>.json.tmp</c> mồ côi dưới <paramref name="folder"/> (tắt máy / mất điện giữa lúc ghi). Đọc được trọn bản tin hợp lệ → đổi thành <c>.json</c>
    /// (đã có <c>.json</c> cùng tên thì tệp tạm chỉ là bản sao, xoá). Hỏng và cũ hơn 1 ngày → <c>.json.loi</c>. Không bao giờ ném lỗi.
    /// </summary>
    private void RecoverTempFiles(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var temp in Directory.EnumerateFiles(folder, "*.json.tmp", SearchOption.AllDirectories).ToList())
            {
                if (writing.ContainsKey(temp))
                {
                    continue;
                }

                try
                {
                    RecoverTempFile(temp);
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

    private static void RecoverTempFile(string temp)
    {
        var target = temp[..^".tmp".Length];
        var valid = false;
        try
        {
            using var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var message = JsonSerializer.Deserialize<RawMessage>(stream, Json);
            valid = message is { Bytes: not null }
                && string.Equals(Path.GetFileName(target), FileName(message), StringComparison.Ordinal)
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(target)), message.InstrumentId.ToString("N"), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            // hỏng / dở dang
        }

        if (valid)
        {
            if (File.Exists(target))
            {
                File.Delete(temp);
            }
            else
            {
                File.Move(temp, target, overwrite: false);
            }
        }
        else if (DateTime.UtcNow - File.GetLastWriteTimeUtc(temp) > StaleTemp)
        {
            File.Move(temp, target + ".loi", overwrite: true);
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
