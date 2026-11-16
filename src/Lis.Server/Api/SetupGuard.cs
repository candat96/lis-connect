using System.Collections.Concurrent;
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
    private const int MaxTrackedAddresses = 10_000;

    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly byte[] _keyHash;

    /// <param name="configuredKey">Mã cấu hình; trống / chỉ khoảng trắng → sinh ngẫu nhiên.</param>
    public SetupGuard(string? configuredKey)
    {
        Generated = string.IsNullOrWhiteSpace(configuredKey);
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

    /// <summary>Chờ theo số lần sai liên tiếp của địa chỉ rồi kiểm mã; sai → 403.</summary>
    public async Task CheckAsync(string address, string? supplied, CancellationToken cancellationToken)
    {
        if (_failures.TryGetValue(address, out var failures) && failures > 0)
        {
            await Delay(TimeSpan.FromSeconds(Math.Min(failures, MaxDelaySeconds)), cancellationToken);
        }

        if (!Matches(supplied))
        {
            if (_failures.Count >= MaxTrackedAddresses)
            {
                _failures.Clear();
            }

            _failures.AddOrUpdate(address, 1, (_, n) => n + 1);
            throw ApiException.Forbidden("Mã thiết lập không đúng");
        }

        _failures.TryRemove(address, out _);
    }

    // So hai băm cùng độ dài bằng FixedTimeEquals: không lộ độ dài hay vị trí ký tự khác nhau qua thời gian xử lý.
    private bool Matches(string? supplied) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? "")), _keyHash);
}
