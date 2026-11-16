namespace Lis.Server.Tenancy;

/// <summary>
/// Đồng hồ của phòng khám: thời điểm hiện tại lấy từ đồng hồ gốc, còn "giờ địa phương" là múi giờ cố định của
/// phòng khám (<see cref="ServerOptions.LocalUtcOffsetMinutes"/>) chứ không phải múi giờ của máy chủ. Lis.Data
/// chia "ngày" (lọc danh sách mẫu theo ngày) và tính ngày chỉ định để chọn khoảng tham chiếu theo tuổi bằng
/// <see cref="TimeProvider.LocalTimeZone"/> — app máy trạm chạy đúng giờ máy, server web cần giờ phòng khám.
/// </summary>
public sealed class ClinicTimeProvider : TimeProvider
{
    private readonly TimeProvider _inner;
    private readonly TimeZoneInfo _zone;

    /// <exception cref="ArgumentOutOfRangeException">Độ lệch ngoài ±14 giờ.</exception>
    public ClinicTimeProvider(TimeProvider inner, int utcOffsetMinutes)
    {
        _inner = inner;
        var offset = TimeSpan.FromMinutes(utcOffsetMinutes);
        if (offset > TimeSpan.FromHours(14) || offset < TimeSpan.FromHours(-14))
        {
            throw new ArgumentOutOfRangeException(nameof(utcOffsetMinutes), "LocalUtcOffsetMinutes phải trong khoảng -840 đến 840 phút");
        }

        _zone = TimeZoneInfo.CreateCustomTimeZone($"Phòng khám UTC{utcOffsetMinutes:+0;-0;+0} phút", offset, "Giờ phòng khám", "Giờ phòng khám");
    }

    public override TimeZoneInfo LocalTimeZone => _zone;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _inner.CreateTimer(callback, state, dueTime, period);
}
