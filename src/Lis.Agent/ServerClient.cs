using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lis.Connections;
using Lis.Connections.RawMessages;
using Lis.Core.Ingest;
using Lis.Data.Ingest;
using Lis.Data.Instruments;

namespace Lis.Agent;

public sealed record AgentMe(string ClinicCode, string ClinicName, string AgentName, string WorkstationCode);

/// <summary>Kết quả từng bản tin: <see cref="Status"/> ∈ da-luu / trung / khong-thuoc-agent / khong-hop-le.</summary>
public sealed record RawMessageResult(Guid? Id, string Status, string? DecodeStatus);

public sealed record RawMessagesResponse(IReadOnlyList<RawMessageResult> Results);

public sealed record OrderQueryResponse(IReadOnlyList<string> ChannelCodes);

/// <summary>Một dòng log đẩy lên server: byte đi/về với máy (hex), sự kiện kết nối, hoặc cảnh báo của agent.</summary>
public sealed record AgentLogLine(Guid? InstrumentId, DateTimeOffset At, string Direction, string? Session, string? Hex, string? Note);

public sealed record InstrumentStatusReport(
    Guid InstrumentId, string State, string Text, DateTimeOffset? LastResultAt, int SamplesReceived, int PendingToRetry);

/// <summary>Gọi API <c>/agent/v1</c> của Lis.Server. Mọi lỗi mạng / server ném ngoại lệ — nơi gọi quyết định giữ lại và thử sau.</summary>
public sealed class ServerClient(HttpClient http)
{
    public const string KeyHeader = "X-Agent-Key";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Tải thân tệp cài (HttpClient.Timeout chỉ tính tới khi có tiêu đề) tối đa chừng này.</summary>
    internal static readonly TimeSpan BodyTimeout = TimeSpan.FromMinutes(5);

    public static HttpClient CreateHttp(string serverUrl, string agentKey)
    {
        var client = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add(KeyHeader, agentKey);
        return client;
    }

    public async Task<AgentMe> MeAsync(CancellationToken ct = default) =>
        await GetAsync<AgentMe>("agent/v1/me", ct);

    public async Task<IReadOnlyList<InstrumentInfo>> InstrumentsAsync(CancellationToken ct = default) =>
        await GetAsync<List<InstrumentInfo>>("agent/v1/instruments", ct);

    /// <summary>Đẩy một lô bản tin thô (<c>POST agent/v1/raw-messages</c>); byte đi dưới dạng base64.</summary>
    public async Task<RawMessagesResponse> SendRawMessagesAsync(IReadOnlyList<RawMessage> messages, CancellationToken ct = default) =>
        await PostAsync<RawMessagesResponse>("agent/v1/raw-messages", new { messages }, ct);

    public async Task<IReadOnlyList<string>> OrderQueryAsync(Guid instrumentId, string barcode, CancellationToken ct = default) =>
        (await PostAsync<OrderQueryResponse>("agent/v1/order-queries", new { instrumentId, barcode }, ct)).ChannelCodes;

    public async Task SendLogsAsync(IReadOnlyList<AgentLogLine> entries, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("agent/v1/logs", new { entries }, Json, ct);
        await EnsureOkAsync(response, ct);
    }

    public async Task SendStatusAsync(IReadOnlyList<InstrumentStatusReport> instruments, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("agent/v1/status", new { instruments, agentVersion = AgentVersion.Current, machineName = Environment.MachineName }, Json, ct);
        await EnsureOkAsync(response, ct);
    }

    /// <summary>
    /// Tải tệp cài agent (<c>GET agent/v1/installer</c>) ghi vào <paramref name="destination"/>; SHA-256 (hex chữ thường) tính ngay trên
    /// byte đã ghi, không tin tiêu đề server. Lỗi giữa chừng thì xoá tệp dở rồi ném lại.
    /// </summary>
    public async Task<(string Sha256, string? Version)> DownloadInstallerAsync(string destination, CancellationToken ct = default)
    {
        using var response = await http.GetAsync("agent/v1/installer", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOkAsync(response, ct);
        var version = response.Headers.TryGetValues("X-Agent-Version", out var values) ? values.FirstOrDefault() : null;
        try
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyTimeout.CancelAfter(BodyTimeout);
            ct = bodyTimeout.Token;
            await using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var body = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            return (Convert.ToHexStringLower(hash.GetHashAndReset()), version);
        }
        catch
        {
            try
            {
                File.Delete(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        await EnsureOkAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, Json, ct);
        await EnsureOkAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = response.StatusCode == HttpStatusCode.Unauthorized
            ? "khoá agent không hợp lệ"
            : (await response.Content.ReadAsStringAsync(ct)) is { Length: > 0 } text ? text[..Math.Min(text.Length, 200)] : "";
        throw new HttpRequestException($"Server trả {(int)response.StatusCode}: {detail}", null, response.StatusCode);
    }
}

/// <summary>
/// Agent chạy chế độ bản tin thô: kết quả do server giải mã, agent không bao giờ ghi kết quả tại chỗ.
/// Runner không gọi sink này khi có <see cref="IRawMessageSink"/>; gọi nhầm thì báo lỗi to thay vì nuốt kết quả.
/// </summary>
public sealed class RawModeReadingSink : IReadingSink
{
    public Task<IngestOutcome> IngestAsync(InstrumentReading reading) =>
        throw new InvalidOperationException("Agent chạy chế độ bản tin thô: kết quả do server giải mã, không ghi tại chỗ");
}

/// <summary>Máy hỏi chỉ định → hỏi server.</summary>
public sealed class HttpOrderQueries(ServerClient server) : IOrderQueryService
{
    public Task<IReadOnlyList<string>> ChannelsToRunAsync(Guid instrumentId, string barcode, CancellationToken ct = default) =>
        server.OrderQueryAsync(instrumentId, barcode, ct);
}
