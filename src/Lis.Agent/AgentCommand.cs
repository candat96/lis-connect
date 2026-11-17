namespace Lis.Agent;

/// <summary>Lệnh dòng lệnh của <c>LisAgent.exe</c>.</summary>
public abstract record AgentCommand
{
    private const string Usage =
        "Cách dùng: LisAgent [run | pair <mã> | install [--code <mã>] | uninstall]";

    /// <summary>Chạy như dịch vụ Windows.</summary>
    public sealed record Service : AgentCommand;

    /// <summary>Chạy ngay trong cửa sổ dòng lệnh.</summary>
    public sealed record Run : AgentCommand;

    /// <summary>Đổi mã ghép nối lấy khoá rồi lưu (không cài dịch vụ).</summary>
    public sealed record Pair(string? CodeText) : AgentCommand;

    /// <summary>Cài dịch vụ Windows; <see cref="CodeText"/> null thì lấy mã từ tên tệp hoặc hỏi người dùng.</summary>
    public sealed record Install(string? CodeText) : AgentCommand;

    public sealed record Uninstall : AgentCommand;

    /// <summary>Bấm đúp tệp cài trên Windows (không tham số).</summary>
    public sealed record Interactive : AgentCommand;

    /// <exception cref="ArgumentException">Tham số không hiểu được; thông báo là câu hướng dẫn tiếng Việt.</exception>
    public static AgentCommand Parse(string[] args, bool isWindowsService, bool isWindows)
    {
        if (isWindowsService)
        {
            return new Service();
        }

        if (args.Length == 0)
        {
            return isWindows ? new Interactive() : new Run();
        }

        switch (args[0].ToLowerInvariant())
        {
            case "run" when args.Length == 1:
                return new Run();
            case "uninstall" when args.Length == 1:
                return new Uninstall();
            case "pair" when args.Length == 1:
                throw new ArgumentException($"Thiếu mã ghép nối. Dùng: LisAgent pair <mã>");
            case "pair" when args.Length == 2:
                return new Pair(args[1]);
            case "install" when args.Length == 1:
                return new Install(null);
            case "install" when args.Length == 3 && args[1].Equals("--code", StringComparison.OrdinalIgnoreCase):
                return new Install(args[2]);
            case "install" when args.Length == 2 && args[1].Equals("--code", StringComparison.OrdinalIgnoreCase):
                throw new ArgumentException("Thiếu mã sau --code. Dùng: LisAgent install --code <mã>");
        }

        throw new ArgumentException($"Lệnh không hợp lệ: {string.Join(' ', args)}. {Usage}");
    }
}
