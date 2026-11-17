using Microsoft.Extensions.Configuration;

namespace Lis.Agent;

/// <summary>Địa chỉ server và khoá agent đang dùng, kèm nguồn lấy (để ghi log — không bao giờ ghi khoá).</summary>
public sealed record AgentConnection(string ServerUrl, string AgentKey, string Source)
{
    public const string NotPairedProblem = "Agent chưa được ghép nối — chạy tệp cài tải từ mục Agent trên web";

    /// <summary>
    /// Chỉ các biến <c>LISAGENT_…</c> (<c>__</c> thay cho <c>:</c>). Dùng riêng cho <see cref="Resolve"/> để tệp
    /// <c>appsettings.json</c> (kể cả bản cũ còn <c>Server:*</c>) không bao giờ thắng cấu hình đã ghép nối.
    /// </summary>
    public static IConfiguration EnvironmentConfiguration(IEnumerable<KeyValuePair<string, string?>> variables)
    {
        const string prefix = "LISAGENT_";
        var values = variables
            .Where(v => v.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(v => KeyValuePair.Create(v.Key[prefix.Length..].Replace("__", ":", StringComparison.Ordinal), v.Value));
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

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
