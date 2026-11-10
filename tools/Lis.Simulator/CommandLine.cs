using System.Globalization;

namespace Lis.Simulator;

/// <summary>Tách dòng lệnh: lệnh đầu tiên, rồi các tuỳ chọn <c>--tên giá trị</c> (lặp được) và cờ <c>--tên</c>.</summary>
public sealed class CommandLine
{
    private static readonly HashSet<string> Flags = ["class-b", "bcc", "qc", "help"];

    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string? command)
    {
        Command = command;
    }

    public string? Command { get; }

    public static CommandLine Parse(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        var line = new CommandLine(command);
        for (var i = command is null ? 0 : 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Tham số thừa: {args[i]}");
            }

            var name = args[i][2..];
            string value;
            if (Flags.Contains(name))
            {
                value = "true";
            }
            else if (i + 1 < args.Length)
            {
                value = args[++i];
            }
            else
            {
                throw new ArgumentException($"Thiếu giá trị cho --{name}");
            }

            if (!line._values.TryGetValue(name, out var list))
            {
                line._values[name] = list = [];
            }

            list.Add(value);
        }

        return line;
    }

    public string? Get(string name) => _values.TryGetValue(name, out var list) ? list[^1] : null;

    public IReadOnlyList<string> GetAll(string name) => _values.TryGetValue(name, out var list) ? list : [];

    public bool Has(string name) => _values.ContainsKey(name);

    public int GetInt(string name, int fallback)
    {
        var text = Get(name);
        if (text is null)
        {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"--{name} phải là số nguyên, nhận được: {text}");
    }

    public double GetDouble(string name, double fallback)
    {
        var text = Get(name);
        if (text is null)
        {
            return fallback;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"--{name} phải là số, nhận được: {text}");
    }
}
