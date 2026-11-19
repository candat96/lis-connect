using System.Text.Json;

namespace Lis.Connections.RawMessages;

/// <summary>
/// Hàng chờ bản tin thô trên đĩa: <c>{root}/{instrumentId:N}/{Seq:D20}-{Id:N}.json</c>. Ghi nguyên tử (.tmp, flush xuống đĩa, đổi tên);
/// đọc theo <see cref="RawMessage.Seq"/>; tệp hỏng đổi thành <c>.loi</c> và không chặn hàng.
/// </summary>
public sealed class RawMessageQueue(string root) : IRawMessageSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

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

    /// <summary>Tối đa <paramref name="max"/> bản tin của máy, theo <c>Seq</c> tăng dần.</summary>
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

            try
            {
                var message = JsonSerializer.Deserialize<RawMessage>(File.ReadAllText(file), Json);
                if (message is null || message.Bytes is null || message.InstrumentId != instrumentId)
                {
                    throw new JsonException("Nội dung không hợp lệ");
                }

                result.Add(message);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
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

    public IReadOnlyList<Guid> Instruments() =>
        !Directory.Exists(root)
            ? []
            : Directory.GetDirectories(root)
                .Select(d => Guid.TryParseExact(Path.GetFileName(d), "N", out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Where(id => Directory.EnumerateFiles(DirectoryOf(id), "*.json").Any())
                .ToList();

    public void Delete(RawMessage message)
    {
        var path = Path.Combine(DirectoryOf(message.InstrumentId), FileName(message));
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public int Count() =>
        Directory.Exists(root) ? Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length : 0;
}
