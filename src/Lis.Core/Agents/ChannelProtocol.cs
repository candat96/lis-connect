using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lis.Core.Agents;

/// <summary>Agent báo kết quả lần cập nhật gần nhất (kèm <c>hello</c> khi nối lại sau khi thay bản).</summary>
public sealed record UpdateReport(Guid CommandId, bool Ok, string? Version, string? Error);

/// <summary>
/// Phong bì một khung trên kênh lệnh (WebSocket). <see cref="Type"/> quyết định trường nào có nghĩa:
/// <c>hello</c> (agent→server: <see cref="Version"/>, <see cref="MachineName"/>, <see cref="Update"/>), <c>started</c> / <c>result</c>
/// (agent→server: <see cref="Id"/>, <see cref="Ok"/>, <see cref="Data"/>, <see cref="Error"/>), <c>command</c> (server→agent:
/// <see cref="Id"/>, <see cref="Name"/>, <see cref="Args"/>), <c>config-changed</c> (server→agent, không trường nào).
/// </summary>
public sealed record ChannelEnvelope(
    string Type,
    Guid? Id = null,
    string? Name = null,
    JsonElement? Args = null,
    bool? Ok = null,
    JsonElement? Data = null,
    string? Error = null,
    string? Version = null,
    string? MachineName = null,
    UpdateReport? Update = null);

/// <summary>Mã hoá / giải mã phong bì: JSON camelCase, bỏ trường rỗng.</summary>
public static class ChannelProtocol
{
    public const string Hello = "hello";
    public const string Started = "started";
    public const string Result = "result";
    public const string Command = "command";
    public const string ConfigChanged = "config-changed";

    /// <summary>Một khung tối đa 1 MB; lớn hơn coi như hỏng.</summary>
    public const int MaxMessageBytes = 1_048_576;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ChannelEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);

    /// <summary>Giải mã một khung UTF-8; hỏng, không phải đối tượng JSON, thiếu <c>type</c> hoặc quá <see cref="MaxMessageBytes"/> → null.</summary>
    public static ChannelEnvelope? Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty || utf8.Length > MaxMessageBytes)
        {
            return null;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<ChannelEnvelope>(utf8, Options);
            return envelope is not null && !string.IsNullOrWhiteSpace(envelope.Type) ? envelope : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
