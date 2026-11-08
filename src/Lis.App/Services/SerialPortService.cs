using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using Lis.Core.Instruments;

namespace Lis.App.Services;

/// <summary>Một cổng COM thấy trên máy trạm.</summary>
/// <param name="PortName">Vd <c>COM5</c>.</param>
/// <param name="Description">Tên thiết bị Windows báo (vd "USB-SERIAL CH340"), null nếu không lấy được.</param>
public sealed record SerialPortEntry(string PortName, string? Description);

/// <summary>Cổng đang bị chương trình khác mở (hoặc không có quyền).</summary>
public sealed class SerialPortBusyException(string portName, Exception inner)
    : Exception($"{portName} đang bị chương trình khác mở (LIS cũ, phần mềm của hãng, terminal…)", inner);

/// <summary>Liệt kê và nghe thử cổng COM — dùng ở màn dò cổng. Kiểm thử ViewModel dùng bản giả.</summary>
public interface ISerialPortService
{
    IReadOnlyList<SerialPortEntry> ListPorts();

    /// <summary>
    /// Mở cổng với tham số của <paramref name="settings"/>, chỉ đọc (không gửi gì ra máy) trong
    /// <paramref name="duration"/> hoặc tới khi hủy. Mỗi lần nhận byte gọi
    /// <paramref name="onBytes"/> (trên luồng nền). Trả về toàn bộ byte đã nhận.
    /// </summary>
    /// <exception cref="SerialPortBusyException">Cổng đang bị chiếm.</exception>
    Task<byte[]> ListenAsync(ConnectionSettings settings, TimeSpan duration, Action<byte[]>? onBytes, CancellationToken ct);
}

/// <summary>Cài đặt thật bằng <see cref="SerialPort"/>.</summary>
public sealed partial class SerialPortService : ISerialPortService
{
    /// <summary>Giới hạn byte giữ lại cho một lần nghe — đủ vài bản tin, không ăn bộ nhớ.</summary>
    private const int MaxBytes = 256 * 1024;

    public IReadOnlyList<SerialPortEntry> ListPorts()
    {
        var descriptions = ReadDescriptions();
        return SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PortNumber)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new SerialPortEntry(name, descriptions.GetValueOrDefault(name)))
            .ToList();
    }

    public async Task<byte[]> ListenAsync(ConnectionSettings settings, TimeSpan duration, Action<byte[]>? onBytes, CancellationToken ct)
    {
        using var port = new SerialPort(settings.PortName!, settings.BaudRate, ToParity(settings.Parity), settings.DataBits, settings.StopBits == 2 ? StopBits.Two : StopBits.One)
        {
            Handshake = Handshake.None,

            // Nhiều máy (và bộ chuyển USB) chỉ gửi khi thấy DTR/RTS bật.
            DtrEnable = true,
            RtsEnable = true,
        };

        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new SerialPortBusyException(settings.PortName!, ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(duration);

        var received = new List<byte>();
        var buffer = new byte[4096];
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var count = await port.BaseStream.ReadAsync(buffer, timeout.Token);
                if (count == 0)
                {
                    continue;
                }

                var chunk = buffer[..count];
                if (received.Count < MaxBytes)
                {
                    received.AddRange(chunk);
                }

                onBytes?.Invoke(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            // Hết giờ nghe hoặc người dùng bấm dừng — trả về những gì đã nhận.
        }
        catch (IOException) when (timeout.IsCancellationRequested)
        {
            // Một số driver USB-serial ném IOException thay vì OperationCanceledException khi hủy.
        }

        return [.. received];
    }

    private static Parity ToParity(string parity) => parity switch
    {
        "Even" => Parity.Even,
        "Odd" => Parity.Odd,
        _ => Parity.None,
    };

    private static int PortNumber(string name) =>
        int.TryParse(name.AsSpan(name.TakeWhile(c => !char.IsDigit(c)).Count()), out var n) ? n : int.MaxValue;

    /// <summary>Tên thiết bị theo cổng, từ WMI <c>Win32_PnPEntity</c> ("USB-SERIAL CH340 (COM5)"). Không phải Windows → rỗng.</summary>
    private static Dictionary<string, string> ReadDescriptions()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    if (item["Name"] is not string name)
                    {
                        continue;
                    }

                    var match = ComInName().Match(name);
                    if (match.Success)
                    {
                        result[match.Groups[1].Value] = name[..match.Index].Trim();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Không lấy được tên thiết bị thì vẫn liệt kê cổng, chỉ thiếu mô tả.
        }

        return result;
    }

    [GeneratedRegex(@"\((COM\d+)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ComInName();
}
