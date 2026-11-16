using System.Security.Cryptography;
using System.Text;

namespace Lis.Server.Api;

/// <summary>
/// Mã thiết lập bảo vệ việc tạo phòng khám từ web. Không cấu hình thì sinh 24 ký tự ngẫu nhiên mỗi lần chạy (ghi ra log).
/// So sánh mã không phụ thuộc thời gian; nhập sai liên tiếp từ một địa chỉ thì request kế tiếp bị làm chậm thêm 1 giây
/// mỗi lần, tối đa 10 giây, nhập đúng thì đặt lại. Chặn này chỉ làm chậm dò mã, không thay cho việc giấu cổng thiết lập.
/// </summary>
public sealed class SetupGuard
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int GeneratedLength = 24;
    private const int MaxDelaySeconds = 10;
    public const string Placeholder = "CHANGE_ME";
    public const int DefaultMaxTrackedAddresses = 10_000;

    // Số lần thử liên tiếp (chưa đúng) theo địa chỉ, xếp theo lần dùng gần nhất (đầu = mới nhất) để chỉ đuổi địa chỉ lâu không dùng.
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Address, int Attempts)>> _attempts = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Address, int Attempts)> _recent = new();
    private readonly int _maxTrackedAddresses;
    private readonly byte[] _keyHash;

    /// <param name="configuredKey">Mã cấu hình; trống / chỉ khoảng trắng / "CHANGE_ME" → sinh ngẫu nhiên.</param>
    /// <param name="maxTrackedAddresses">Số địa chỉ tối đa theo dõi; đầy thì đuổi địa chỉ lâu không dùng nhất.</param>
    public SetupGuard(string? configuredKey, int maxTrackedAddresses = DefaultMaxTrackedAddresses)
    {
        _maxTrackedAddresses = maxTrackedAddresses;
        // Giá trị mẫu "CHANGE_ME" coi như chưa cấu hình, kẻo người cài quên sửa tệp .env mà để lộ việc tạo phòng khám.
        Generated = string.IsNullOrWhiteSpace(configuredKey) || configuredKey == Placeholder;
        Key = Generated ? RandomNumberGenerator.GetString(Alphabet, GeneratedLength) : configuredKey!;
        _keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(Key));
    }

    public string Key { get; }

    /// <summary>Mã do server sinh (không có cấu hình).</summary>
    public bool Generated { get; }

    /// <summary>Cách chờ; test thay bằng hàm không ngủ thật.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Ghi mã sinh ra ở mức Warning (chỉ khi mã do server sinh) — gọi một lần lúc khởi động.</summary>
    public void LogIfGenerated(ILogger logger)
    {
        if (Generated)
        {
            logger.LogWarning(
                "Chưa cấu hình Setup:Key — mã thiết lập phòng khám lần chạy này là {SetupKey} (đổi mỗi lần khởi động lại; đặt Setup:Key hoặc SETUP_KEY để cố định)",
                Key);
        }
    }

    /// <summary>
    /// Chờ theo số lần thử liên tiếp của địa chỉ rồi kiểm mã; sai → 403. Lần thử được ghi nhận NGAY lúc vào (trước khi chờ và
    /// kiểm), nên các request song song từ một địa chỉ mỗi cái thấy một số đếm khác nhau và chậm dần, không thể cùng lọt qua
    /// với độ trễ 0. Nhập đúng thì xoá số đếm của địa chỉ.
    /// </summary>
    public async Task CheckAsync(string address, string? supplied, CancellationToken cancellationToken)
    {
        var previous = Reserve(address);
        if (previous > 0)
        {
            await Delay(TimeSpan.FromSeconds(Math.Min(previous, MaxDelaySeconds)), cancellationToken);
        }

        if (!Matches(supplied))
        {
            throw ApiException.Forbidden("Mã thiết lập không đúng");
        }

        Forget(address);
    }

    /// <summary>Tăng số lần thử của địa chỉ, trả số lần trước đó; địa chỉ vừa dùng được đưa lên đầu, bảng đầy thì đuổi địa chỉ cuối.</summary>
    private int Reserve(string address)
    {
        lock (_gate)
        {
            var previous = 0;
            if (_attempts.Remove(address, out var node))
            {
                previous = node.Value.Attempts;
                _recent.Remove(node);
            }
            else if (_attempts.Count >= _maxTrackedAddresses && _recent.Last is { } oldest)
            {
                _attempts.Remove(oldest.Value.Address);
                _recent.RemoveLast();
            }

            _attempts[address] = _recent.AddFirst((address, previous + 1));
            return previous;
        }
    }

    private void Forget(string address)
    {
        lock (_gate)
        {
            if (_attempts.Remove(address, out var node))
            {
                _recent.Remove(node);
            }
        }
    }

    // So hai băm cùng độ dài bằng FixedTimeEquals: không lộ độ dài hay vị trí ký tự khác nhau qua thời gian xử lý.
    private bool Matches(string? supplied) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? "")), _keyHash);
}
