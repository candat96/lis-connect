using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Lis.Data.His;

/// <summary>Kết quả "Thử kết nối" tới HIS.</summary>
/// <param name="Ok">true nếu kết nối/xác thực thành công.</param>
/// <param name="Message">Thông báo tiếng Việt hiển thị cho người dùng (kèm mã HTTP khi thành công).</param>
public sealed record HisTestResult(bool Ok, string Message);

/// <summary>
/// Thử kết nối tới HIS theo cấu hình <see cref="HisProfile"/>. Đợt 1 chỉ kiểm tra kết nối/xác
/// thực được không — kéo chỉ định/đẩy kết quả thuộc đợt 3. Với <c>auth_type = bearer-login</c>,
/// đăng nhập lấy token trước rồi mới gọi <c>orders_path</c> để xác nhận token dùng được; các
/// kiểu khác (<c>none</c>, <c>api-key</c>, <c>basic</c>) gọi <c>orders_path</c> trực tiếp.
/// </summary>
public sealed class HisConnectionTester(HttpClient http)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<HisTestResult> TestAsync(HisProfile profile, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        try
        {
            if (profile.AuthType == "api-key" && string.IsNullOrWhiteSpace(profile.ApiKeyHeader))
            {
                return new HisTestResult(false, "Chưa nhập tên header chứa khóa API");
            }

            string? bearerToken = null;

            if (profile.AuthType == "bearer-login")
            {
                var loginResult = await LoginAsync(profile, cts.Token);
                if (loginResult.Token is null)
                {
                    return new HisTestResult(false, loginResult.Error ?? "Đăng nhập HIS thất bại");
                }

                bearerToken = loginResult.Token;
            }

            return await CheckReachableAsync(profile, bearerToken, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HisTestResult(false, $"Không kết nối được máy chủ HIS ({profile.BaseUrl}) — hết thời gian chờ");
        }
        catch (HttpRequestException)
        {
            return new HisTestResult(false, $"Không kết nối được máy chủ HIS ({profile.BaseUrl})");
        }
    }

    private async Task<(string? Token, string? Error)> LoginAsync(HisProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.LoginPath))
        {
            return (null, "Chưa cấu hình đường đăng nhập (login_path)");
        }

        var body = (profile.LoginBodyTemplate ?? "{}")
            .Replace("{username}", profile.Username ?? "")
            .Replace("{password}", profile.Secret ?? "");

        using var request = new HttpRequestMessage(HttpMethod.Post, CombineUrl(profile.BaseUrl, profile.LoginPath))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return (null, "Sai tài khoản hoặc mật khẩu đăng nhập HIS");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, $"Sai đường đăng nhập HIS ({profile.LoginPath})");
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, $"Đăng nhập HIS thất bại (mã HTTP {(int)response.StatusCode})");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var token = ExtractToken(json, profile.TokenJsonPath);

        if (token is null)
        {
            return (null, $"Đăng nhập được nhưng không lấy được token ở đường dẫn \"{profile.TokenJsonPath}\" trong phản hồi");
        }

        return (token, null);
    }

    private async Task<HisTestResult> CheckReachableAsync(HisProfile profile, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.OrdersPath))
        {
            return new HisTestResult(false, "Chưa cấu hình đường kiểm tra kết nối");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, CombineUrl(profile.BaseUrl, profile.OrdersPath));
        ApplyAuth(request, profile, bearerToken);

        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new HisTestResult(false, $"Sai xác thực với HIS (mã HTTP {(int)response.StatusCode})");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new HisTestResult(false, $"Sai đường dẫn tới HIS (mã HTTP 404, {profile.OrdersPath})");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new HisTestResult(false, $"HIS trả lỗi (mã HTTP {(int)response.StatusCode})");
        }

        return new HisTestResult(true, $"Kết nối HIS thành công (mã HTTP {(int)response.StatusCode})");
    }

    private static void ApplyAuth(HttpRequestMessage request, HisProfile profile, string? bearerToken)
    {
        switch (profile.AuthType)
        {
            case "bearer-login" when bearerToken is not null:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                break;

            case "api-key" when !string.IsNullOrWhiteSpace(profile.ApiKeyHeader):
                request.Headers.TryAddWithoutValidation(profile.ApiKeyHeader, profile.Secret ?? "");
                break;

            case "basic":
                var raw = $"{profile.Username}:{profile.Secret}";
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
                break;
        }
    }

    /// <summary>Lấy token từ JSON phản hồi theo đường dẫn chấm phân cấp (vd <c>"data.token"</c>, hoặc <c>"token"</c>).</summary>
    private static string? ExtractToken(string json, string? tokenJsonPath)
    {
        if (string.IsNullOrWhiteSpace(tokenJsonPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var current = document.RootElement;

            foreach (var segment in tokenJsonPath.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CombineUrl(string baseUrl, string path) =>
        baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}
