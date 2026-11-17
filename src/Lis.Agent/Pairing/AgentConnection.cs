using Microsoft.Extensions.Configuration;

namespace Lis.Agent;

/// <summary>Địa chỉ server và khoá agent đang dùng, kèm nguồn lấy (để ghi log — không bao giờ ghi khoá).</summary>
public sealed record AgentConnection(string ServerUrl, string AgentKey, string Source)
{
    public const string NotPairedProblem = "Agent chưa được ghép nối — chạy tệp cài tải từ mục Agent trên web";

    /// <summary>
    /// Đủ cả <c>Server:Url</c> và <c>Server:AgentKey</c> (biến <c>LISAGENT_Server__…</c>, dùng lúc phát triển) thì thắng;
    /// không thì dùng cấu hình đã ghép nối; không có gì → null và <paramref name="problem"/> nói lý do.
    /// </summary>
    public static AgentConnection? Resolve(IConfiguration config, PairingStore store, out string? problem)
    {
        var url = config["Server:Url"];
        var key = config["Server:AgentKey"];
        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key))
        {
            problem = null;
            return new AgentConnection(url, key, "biến môi trường");
        }

        if (store.Load() is { } paired)
        {
            problem = null;
            return new AgentConnection(paired.State.ServerUrl, paired.AgentKey, "cấu hình đã ghép nối");
        }

        problem = NotPairedProblem;
        return null;
    }
}
