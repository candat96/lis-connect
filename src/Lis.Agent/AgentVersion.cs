using System.Reflection;

namespace Lis.Agent;

/// <summary>Phiên bản agent đang chạy (báo lên server cùng trạng thái).</summary>
public static class AgentVersion
{
    /// <summary><c>InformationalVersion</c> của tệp chạy, bỏ phần <c>+&lt;hash&gt;</c> do trình biên dịch thêm.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AgentVersion).Assembly;
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString()
                   ?? "0.0.0";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? text[..plus] : text;
    }
}
