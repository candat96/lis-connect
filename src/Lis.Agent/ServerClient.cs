using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lis.Connections;
using Lis.Core.Ingest;
using Lis.Data.Ingest;
using Lis.Data.Instruments;

namespace Lis.Agent;

public sealed record AgentMe(string ClinicCode, string ClinicName, string AgentName, string WorkstationCode);

public sealed record ReadingResponse(int Stored, int PendingReplacement, int Unmatched, int Qc, IReadOnlyList<string> Notes, bool Duplicate = false);

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

    public async Task<ReadingResponse> SendReadingAsync(InstrumentReading reading, CancellationToken ct = default) =>
        await PostAsync<ReadingResponse>("agent/v1/readings", reading, ct);

    public async Task<IReadOnlyList<string>> OrderQueryAsync(Guid instrumentId, string barcode, CancellationToken ct = default) =>
        (await PostAsync<OrderQueryResponse>("agent/v1/order-queries", new { instrumentId, barcode }, ct)).ChannelCodes;

    public async Task SendLogsAsync(IReadOnlyList<AgentLogLine> entries, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("agent/v1/logs", new { entries }, Json, ct);
        await EnsureOkAsync(response, ct);
    }

    public async Task SendStatusAsync(IReadOnlyList<InstrumentStatusReport> instruments, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("agent/v1/status", new { instruments }, Json, ct);
        await EnsureOkAsync(response, ct);
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
/// Nơi nhận kết quả của agent: đẩy lên server. Lỗi (mất mạng, server lỗi) ném ra để
/// <see cref="InstrumentRunner"/> lưu bản tin xuống đĩa và gửi lại sau. Server tự dò map kênh
/// ngay khi nhận và trả kèm ghi chú — giữ lại để runner ghi vào log thô.
/// </summary>
public sealed class HttpReadingSink(ServerClient server) : IReadingSink
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> _notes = new();

    public async Task<IngestOutcome> IngestAsync(InstrumentReading reading)
    {
        var response = await server.SendReadingAsync(reading);
        _notes[reading.InstrumentId] = response.Notes;
        return new IngestOutcome(response.Stored, response.PendingReplacement, response.Unmatched, response.Qc);
    }

    public Task<IReadOnlyList<string>> LearnChannelsAsync(Guid instrumentId) =>
        Task.FromResult(_notes.TryRemove(instrumentId, out var notes) ? notes : []);
}

/// <summary>Máy hỏi chỉ định → hỏi server.</summary>
public sealed class HttpOrderQueries(ServerClient server) : IOrderQueryService
{
    public Task<IReadOnlyList<string>> ChannelsToRunAsync(Guid instrumentId, string barcode, CancellationToken ct = default) =>
        server.OrderQueryAsync(instrumentId, barcode, ct);
}
