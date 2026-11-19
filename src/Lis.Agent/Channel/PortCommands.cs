using System.IO.Ports;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lis.Connections;
using Lis.Core.Instruments;
using Microsoft.Extensions.Logging;

namespace Lis.Agent;

/// <summary>Thăm dò một cổng COM có đang bị chương trình khác giữ hay không (mở rồi đóng ngay, không gửi gì).</summary>
public interface IPortProbe
{
    bool IsBusy(string portName);
}

/// <summary>Bản thật: mở / đóng <see cref="SerialPort"/> ngay; <see cref="UnauthorizedAccessException"/> hoặc <see cref="IOException"/> → bận.</summary>
public sealed class SerialPortProbe : IPortProbe
{
    public bool IsBusy(string portName)
    {
        try
        {
            using var port = new SerialPort(portName);
            port.Open();
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException and not (FileNotFoundException or DirectoryNotFoundException))
        {
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException)
        {
            // Tên cổng không hợp lệ / cổng không dùng được: không phải do bị chiếm.
            return false;
        }
    }
}

/// <summary>
/// <c>scan-ports</c>: liệt kê cổng COM → <c>{ports:[{name, description, usedBy, busy}]}</c>.
/// <c>usedBy</c> = tên máy xét nghiệm của agent đang dùng cổng (cấu hình COM, đang hoạt động) — cổng đó <b>không</b> bị thử mở (<c>busy=false</c>);
/// cổng khác hỏi <see cref="IPortProbe"/>. <c>description</c>, <c>usedBy</c> là <c>null</c> khi không có.
/// </summary>
public sealed class ScanPortsHandler(AgentRuntime runtime, ISerialPortService serial, IPortProbe probe, ILogger<ScanPortsHandler>? logger = null) : IAgentCommandHandler
{
    public string Name => "scan-ports";

    public Task<JsonElement?> RunAsync(JsonElement? args, CancellationToken ct)
    {
        var usedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in runtime.Owned().Where(i => i.Active))
        {
            var settings = ConnectionSettings.Parse(instrument.ConnectionJson);
            if (settings.Kind == ConnectionKind.Serial && settings.Validate() is null && settings.PortName is { } port)
            {
                usedBy.TryAdd(port, instrument.Name);
            }
        }

        var ports = new List<object>();
        foreach (var entry in serial.ListPorts())
        {
            ct.ThrowIfCancellationRequested();
            var owner = usedBy.GetValueOrDefault(entry.PortName);
            var busy = owner is null && ProbeSafely(entry.PortName);
            ports.Add(new { name = entry.PortName, description = entry.Description, usedBy = owner, busy });
        }

        return Task.FromResult(CommandJson.Of(new { ports }));
    }

    /// <summary>Một cổng thăm dò lỗi lạ không được làm hỏng cả lượt quét: coi như không bận.</summary>
    private bool ProbeSafely(string portName)
    {
        try
        {
            return probe.IsBusy(portName);
        }
        catch (Exception ex)
        {
            logger?.LogDebug("Thăm dò {Port} lỗi: {Message}", portName, ex.Message);
            return false;
        }
    }
}

/// <summary>
/// <c>test-port</c> <c>{portName, baudRate, dataBits, parity, stopBits, seconds}</c>: tạm nhường cổng nếu máy xét nghiệm đang dùng,
/// chỉ nghe (không gửi gì) đúng <c>seconds</c> giây, trả cổng lại trong <c>finally</c> (cả khi lỗi / bị huỷ).
/// Kết quả <c>{opened, error, hex, byteCount, borrowedFrom}</c>: <c>hex</c> = tối đa <see cref="MaxHexBytes"/> byte đầu, <b>chữ HOA</b>, không dấu cách;
/// <c>byteCount</c> = tổng số byte nhận được (có thể lớn hơn phần trong <c>hex</c>); <c>borrowedFrom</c> = tên các máy đã tạm nhường cổng.
/// </summary>
public sealed partial class TestPortHandler(AgentRuntime runtime, ISerialPortService serial) : IAgentCommandHandler
{
    public const int MaxHexBytes = 64 * 1024;

    /// <summary>Dư thêm ngoài <c>seconds</c> trước khi bỏ chờ cổng không phản hồi, để lệnh không giữ hàng đợi mãi.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    /// <summary>Chờ tối đa chừng này cho cổng đóng sau khi huỷ, trước khi trả cổng cho máy.</summary>
    public static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);

    public string Name => "test-port";

    public async Task<JsonElement?> RunAsync(JsonElement? args, CancellationToken ct)
    {
        var manager = runtime.RequireManager();
        var (settings, seconds) = ParseArgs(args);
        var limit = TimeSpan.FromSeconds(seconds);

        var lease = await manager.BorrowPortAsync(settings.PortName!, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit + Margin);
        Task<byte[]>? listening = null;
        try
        {
            try
            {
                listening = serial.ListenAsync(settings, limit, null, timeout.Token);
                var bytes = await listening.WaitAsync(limit + Margin, ct);
                var kept = bytes.Length > MaxHexBytes ? bytes.AsSpan(0, MaxHexBytes) : bytes;
                return Result(true, null, Convert.ToHexString(kept), bytes.Length, lease);
            }
            catch (SerialPortBusyException ex)
            {
                return Result(false, ex.Message, "", 0, lease);
            }
            catch (TimeoutException)
            {
                return Result(false, "Cổng không phản hồi trong thời gian thử", "", 0, lease);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Result(false, $"Không mở được {settings.PortName}: {ex.Message}", "", 0, lease);
            }
        }
        finally
        {
            // Đóng cổng thật xong (tối đa vài giây) rồi mới trả cho máy xét nghiệm, kẻo máy mở lại gặp cổng còn bận.
            if (listening is { IsCompleted: false })
            {
                await timeout.CancelAsync();
                await Task.WhenAny(listening, Task.Delay(CloseWait));
            }

            await lease.DisposeAsync();
        }
    }

    private static JsonElement? Result(bool opened, string? error, string hex, int byteCount, PortLease lease) =>
        CommandJson.Of(new { opened, error, hex, byteCount, borrowedFrom = lease.InstrumentNames });

    /// <summary>Kiểm lại args như server (phòng lệnh gửi sai khuôn): lỗi → <see cref="AgentCommandException"/> tiếng Việt.</summary>
    internal static (ConnectionSettings Settings, int Seconds) ParseArgs(JsonElement? args)
    {
        if (args is not { ValueKind: JsonValueKind.Object } a)
        {
            throw new AgentCommandException("Thiếu tham số thử cổng");
        }

        var port = a.TryGetProperty("portName", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (port is null || !PortName().IsMatch(port))
        {
            throw new AgentCommandException("Tên cổng COM không hợp lệ (ví dụ COM3)");
        }

        var baud = Int(a, "baudRate", 300, 115200);
        var dataBits = Int(a, "dataBits", 7, 8);
        var stopBits = Int(a, "stopBits", 1, 2);
        var seconds = Int(a, "seconds", 1, 60);
        var parity = a.TryGetProperty("parity", out var par) && par.ValueKind == JsonValueKind.String
            ? ConnectionSettings.ParityOptions.FirstOrDefault(o => string.Equals(o, par.GetString(), StringComparison.OrdinalIgnoreCase))
            : null;
        if (parity is null)
        {
            throw new AgentCommandException("Parity phải là None, Even hoặc Odd");
        }

        var settings = new ConnectionSettings
        {
            Kind = ConnectionKind.Serial,
            PortName = port.ToUpperInvariant(),
            BaudRate = baud,
            DataBits = dataBits,
            Parity = parity,
            StopBits = stopBits,
        };
        return (settings, seconds);
    }

    private static int Int(JsonElement a, string name, int min, int max)
    {
        if (!a.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < min || n > max)
        {
            throw new AgentCommandException($"{name} phải là số nguyên từ {min} đến {max}");
        }

        return n;
    }

    [GeneratedRegex(@"^COM\d{1,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex PortName();
}
