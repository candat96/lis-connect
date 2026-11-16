namespace Lis.Server;

/// <summary>
/// Chế độ <c>LisServer --healthcheck</c> cho <c>HEALTHCHECK</c> của Docker: image aspnet không có curl, nên chính tệp chạy
/// của server gọi <c>/healthz</c> trên máy mình rồi thoát 0 (khoẻ) hoặc 1.
/// </summary>
public static class HealthCheck
{
    public const string Flag = "--healthcheck";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == Flag;

    /// <summary>Địa chỉ gọi vào chính server này: cổng đầu tiên trong <c>ASPNETCORE_URLS</c> (mặc định 5080), luôn qua 127.0.0.1.</summary>
    public static string LocalUrl(string? aspNetCoreUrls)
    {
        var first = (aspNetCoreUrls ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var port = 5080;
        var scheme = "http";
        if (first is not null)
        {
            // Địa chỉ dạng http://+:5080 hoặc http://0.0.0.0:5080 không phải Uri hợp lệ để gọi — chỉ lấy cổng và giao thức.
            var normalized = first.Replace("+", "localhost", StringComparison.Ordinal).Replace("*", "localhost", StringComparison.Ordinal);
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            {
                port = uri.Port;
                scheme = uri.Scheme;
            }
        }

        return $"{scheme}://127.0.0.1:{port}";
    }

    /// <summary>0 khi <c>/healthz</c> trả 200; 1 khi lỗi kết nối, quá 3 giây hoặc mã khác 200.</summary>
    public static async Task<int> RunAsync(HttpClient http)
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            using var response = await http.GetAsync("/healthz", cts.Token);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return 1;
        }
    }
}
