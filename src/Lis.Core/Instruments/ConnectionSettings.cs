using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lis.Core.Instruments;

/// <summary>Cách LIS nối với một máy xét nghiệm.</summary>
public enum ConnectionKind
{
    /// <summary>Chưa cấu hình (hoặc JSON cũ không đọc được).</summary>
    None,

    /// <summary>Cổng COM — RS-232 trực tiếp hoặc USB-serial (COM ảo).</summary>
    Serial,

    /// <summary>TCP, LIS mở cổng chờ máy nối vào (máy là client).</summary>
    TcpServer,

    /// <summary>TCP, LIS nối tới địa chỉ của máy (máy là server).</summary>
    TcpClient,
}

/// <summary>
/// Cấu hình kết nối của một máy — dạng có cấu trúc của cột <c>instrument.connection</c> (jsonb).
/// </summary>
/// <remarks>
/// Khuôn JSON:
/// <list type="bullet">
/// <item>COM: <c>{"type":"serial","port":"COM6","baudRate":9600,"dataBits":8,"parity":"None","stopBits":1}</c></item>
/// <item>TCP LIS chờ: <c>{"type":"tcp","role":"server","port":5100}</c></item>
/// <item>TCP LIS nối tới: <c>{"type":"tcp","role":"client","host":"192.168.1.20","port":50001}</c></item>
/// </list>
/// Các khóa lạ trong JSON cũ được giữ nguyên khi ghi lại (<see cref="ToJson"/>), để không làm mất
/// cấu hình do người khác thêm tay.
/// </remarks>
public sealed record ConnectionSettings
{
    public static readonly IReadOnlyList<int> BaudRates = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];
    public static readonly IReadOnlyList<int> DataBitsOptions = [7, 8];
    public static readonly IReadOnlyList<string> ParityOptions = ["None", "Even", "Odd"];
    public static readonly IReadOnlyList<int> StopBitsOptions = [1, 2];

    private static readonly HashSet<string> KnownKeys = ["type", "role", "port", "host", "baudRate", "dataBits", "parity", "stopBits"];

    public ConnectionKind Kind { get; init; }

    /// <summary>Tên cổng COM (vd <c>COM5</c>) khi <see cref="Kind"/> là <see cref="ConnectionKind.Serial"/>.</summary>
    public string? PortName { get; init; }

    public int BaudRate { get; init; } = 9600;

    public int DataBits { get; init; } = 8;

    /// <summary><c>None</c> / <c>Even</c> / <c>Odd</c>.</summary>
    public string Parity { get; init; } = "None";

    public int StopBits { get; init; } = 1;

    /// <summary>Địa chỉ máy khi <see cref="Kind"/> là <see cref="ConnectionKind.TcpClient"/>.</summary>
    public string? Host { get; init; }

    public int? TcpPort { get; init; }

    /// <summary>JSON gốc khi không đọc được (giữ để hiện cho người dùng, không ghi đè im lặng).</summary>
    public string? UnreadableJson { get; init; }

    private JsonObject? Extra { get; init; }

    public static ConnectionSettings None { get; } = new();

    /// <summary>Đọc JSON trong CSDL. Rỗng → <see cref="None"/>; hỏng → <see cref="None"/> kèm <see cref="UnreadableJson"/>.</summary>
    public static ConnectionSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return None;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                return new ConnectionSettings { UnreadableJson = json };
            }

            var type = GetString(obj, "type")?.ToLowerInvariant();
            var extra = new JsonObject();
            foreach (var (key, value) in obj)
            {
                if (!KnownKeys.Contains(key))
                {
                    extra[key] = value?.DeepClone();
                }
            }

            switch (type)
            {
                case "serial":
                    return new ConnectionSettings
                    {
                        Kind = ConnectionKind.Serial,
                        PortName = GetString(obj, "port"),
                        BaudRate = GetInt(obj, "baudRate") ?? 9600,
                        DataBits = GetInt(obj, "dataBits") ?? 8,
                        Parity = NormalizeParity(GetString(obj, "parity")),
                        StopBits = GetInt(obj, "stopBits") ?? 1,
                        Extra = extra,
                    };

                case "tcp":
                    var role = GetString(obj, "role")?.ToLowerInvariant();
                    return new ConnectionSettings
                    {
                        Kind = role == "client" ? ConnectionKind.TcpClient : ConnectionKind.TcpServer,
                        Host = GetString(obj, "host"),
                        TcpPort = GetInt(obj, "port"),
                        Extra = extra,
                    };

                default:
                    return new ConnectionSettings { UnreadableJson = json };
            }
        }
        catch (JsonException)
        {
            return new ConnectionSettings { UnreadableJson = json };
        }
    }

    /// <summary>Ghi ra JSON cho cột <c>instrument.connection</c>. <see cref="ConnectionKind.None"/> → null.</summary>
    public string? ToJson()
    {
        JsonObject obj;
        switch (Kind)
        {
            case ConnectionKind.Serial:
                obj = new JsonObject
                {
                    ["type"] = "serial",
                    ["port"] = PortName,
                    ["baudRate"] = BaudRate,
                    ["dataBits"] = DataBits,
                    ["parity"] = Parity,
                    ["stopBits"] = StopBits,
                };
                break;

            case ConnectionKind.TcpServer:
                obj = new JsonObject { ["type"] = "tcp", ["role"] = "server", ["port"] = TcpPort };
                break;

            case ConnectionKind.TcpClient:
                obj = new JsonObject { ["type"] = "tcp", ["role"] = "client", ["host"] = Host, ["port"] = TcpPort };
                break;

            default:
                return null;
        }

        if (Extra is not null)
        {
            foreach (var (key, value) in Extra)
            {
                obj[key] = value?.DeepClone();
            }
        }

        return obj.ToJsonString();
    }

    /// <summary>Lỗi nhập liệu đầu tiên, hoặc null nếu hợp lệ.</summary>
    public string? Validate() => Kind switch
    {
        ConnectionKind.Serial when string.IsNullOrWhiteSpace(PortName) => "Chọn cổng COM",
        ConnectionKind.Serial when BaudRate <= 0 => "Tốc độ baud phải lớn hơn 0",
        ConnectionKind.Serial when DataBits is < 5 or > 8 => "Số bit dữ liệu phải từ 5 đến 8",
        ConnectionKind.Serial when StopBits is < 1 or > 2 => "Stop bit phải là 1 hoặc 2",
        ConnectionKind.TcpServer or ConnectionKind.TcpClient when TcpPort is null or < 1 or > 65535 => "Cổng TCP phải từ 1 đến 65535",
        ConnectionKind.TcpClient when string.IsNullOrWhiteSpace(Host) => "Nhập địa chỉ IP của máy",
        _ => null,
    };

    /// <summary>Mô tả ngắn để hiện trong danh sách, vd <c>COM5 · 9600 8N1</c>.</summary>
    public string Describe() => Kind switch
    {
        ConnectionKind.Serial => $"{PortName} · {BaudRate} {DataBits}{Parity[0]}{StopBits}",
        ConnectionKind.TcpServer => $"TCP chờ cổng {TcpPort}",
        ConnectionKind.TcpClient => $"TCP → {Host}:{TcpPort}",
        _ => UnreadableJson is null ? "Chưa cấu hình kết nối" : "Kết nối: JSON không đọc được",
    };

    private static string NormalizeParity(string? value) =>
        ParityOptions.FirstOrDefault(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)) ?? "None";

    private static string? GetString(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? GetInt(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : null;
    }
}
