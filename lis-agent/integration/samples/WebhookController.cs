// Mẫu nhận kết quả LIS Agent — ASP.NET Core (C#). Địa chỉ khai báo ở Agent: https://<HIS>/lis/results
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

[ApiController]
public class LisWebhookController : ControllerBase
{
    private const string Secret = "<khóa ký cấu hình ở Agent>";

    [HttpPost("/lis/results")]
    public async Task<IActionResult> Receive()
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var body = ms.ToArray(); // ký trên NGUYÊN VĂN body
        if (!string.IsNullOrEmpty(Secret))
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
            var want = "sha256=" + Convert.ToHexString(h.ComputeHash(body)).ToLowerInvariant();
            var got = Request.Headers["X-Signature"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(got), Encoding.ASCII.GetBytes(want)))
                return Unauthorized("sai chữ ký");
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("type", out var t) && t.GetString() == "ping") return Ok();
        var sid = root.GetProperty("sid").GetString();
        foreach (var r in root.GetProperty("results").EnumerateArray())
        {
            var code = r.GetProperty("testCode").GetString();
            var value = r.GetProperty("value").GetString();
            // TODO: chống trùng theo resultId; ghi kết quả theo sid + code, trạng thái CHỜ DUYỆT
        }
        return Ok(); // 2xx = đã nhận
    }
}
