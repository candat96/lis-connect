namespace Lis.Data;

/// <summary>
/// Đổi một khoảng ngày theo giờ máy (múi giờ của <see cref="TimeProvider.LocalTimeZone"/>) sang
/// khoảng UTC nửa mở [from, to) để lọc cột <c>timestamptz</c>.
/// </summary>
internal static class LocalDay
{
    public static (DateTime FromUtc, DateTime ToUtcExclusive) UtcRange(TimeProvider clock, DateOnly from, DateOnly to)
    {
        var timeZone = clock.LocalTimeZone;
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), timeZone);
        var toUtcExclusive = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), timeZone);
        return (fromUtc, toUtcExclusive);
    }
}
