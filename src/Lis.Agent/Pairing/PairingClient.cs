using System.Net.Http.Json;
using System.Text.Json;
using Lis.Core.Agents;

namespace Lis.Agent;

/// <summary>Phản hồi của <c>POST /agent/v1/pair</c>. <see cref="AgentKey"/> chỉ có đúng một lần này.</summary>
public sealed record PairResponse(Guid AgentId, string AgentKey, Guid ClinicId, string ClinicCode, string ClinicName, string AgentName);

/// <summary>Đổi mã ghép nối không thành công; thông báo là câu tiếng Việt cho người dùng.</summary>
public sealed class PairingException(string message) : Exception(message);

/// <summary>Gọi <c>POST /agent/v1/pair</c> (không cần khoá agent) tới địa chỉ server nằm trong mã.</summary>
public sealed class PairingClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PairResponse> RedeemAsync(PairingCode code, string machineName, string agentVersion, CancellationToken ct)
    {
        // Địa chỉ tuyệt đối lấy từ mã — không đụng BaseAddress của HttpClient được truyền vào.
        var url = new Uri($"{code.ServerUrl}/agent/v1/pair");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(url, new { pairingSecret = code.Secret, machineName, agentVersion }, Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new PairingException($"Không tới được máy chủ {code.ServerUrl}: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new PairingException(await DetailOfAsync(response, ct));
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<PairResponse>(Json, ct)
                       ?? throw new PairingException("Máy chủ trả phản hồi ghép nối rỗng");
            }
            catch (JsonException)
            {
                throw new PairingException($"Máy chủ {code.ServerUrl} trả phản hồi không đúng định dạng — kiểm tra địa chỉ máy chủ");
            }
        }
    }

    private static async Task<string> DetailOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(detail.GetString()))
            {
                return detail.GetString()!;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Không phải JSON — dùng câu chung bên dưới.
        }

        return $"Máy chủ từ chối ghép nối (mã {(int)response.StatusCode})";
    }
}

/// <summary>Kết quả <see cref="PairingFlow.PairAsync"/>: trạng thái đã lưu và cảnh báo (nếu có) để nơi gọi in ra.</summary>
public sealed record PairingResult(PairingState State, string? Warning, string? QuarantinedTo = null);

public static class PairingFlow
{
    public const string HttpWarning =
        "Cảnh báo: địa chỉ máy chủ dùng http:// nên khoá agent đi qua mạng không được mã hoá. Nên dùng https://.";

    /// <summary>Đổi mã lấy khoá rồi lưu. Đổi mã lỗi thì không ghi gì.</summary>
    public static async Task<PairingResult> PairAsync(
        PairingCode code, PairingClient client, PairingStore store, TimeProvider clock, CancellationToken ct)
    {
        var response = await client.RedeemAsync(code, Environment.MachineName, AgentVersion.Current, ct);
        var state = new PairingState(
            code.ServerUrl, response.ClinicId, response.ClinicCode, response.ClinicName,
            response.AgentId, response.AgentName, clock.GetUtcNow());
        try
        {
            store.Save(state, response.AgentKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            throw new PairingException(
                $"Mã ghép nối đã được dùng nhưng không lưu được cấu hình ({ex.Message}). Hãy chạy lại bằng quyền quản trị, " +
                "rồi bấm \"Ghép nối lại\" trên web để lấy mã mới.");
        }

        var warning = code.ServerUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? HttpWarning : null;
        return new PairingResult(state, warning, store.LastQuarantine);
    }
}
