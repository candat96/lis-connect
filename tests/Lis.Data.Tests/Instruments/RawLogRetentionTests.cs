using Lis.Core.Instruments;
using Lis.Data.Instruments;
using Lis.Data.Settings;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Instruments;

/// <summary>Dọn log thô cũ trên đĩa — không cần CSDL.</summary>
public sealed class RawLogRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-rawlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void DeleteOlderThan_KeepsTodayAndLastNDays()
    {
        var store = new RawLogStore(_root);
        var instrument = Guid.NewGuid();
        var today = new DateOnly(2026, 11, 10);
        foreach (var daysAgo in new[] { 0, 1, 29, 30, 45 })
        {
            var day = today.AddDays(-daysAgo);
            var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(9, 0))));
            store.Append(instrument, new RawLogEntry(at, RawLogDirection.In, "phien-1", [0x06]));
        }

        // Một tệp lạ không đúng mẫu tên — không được đụng vào.
        File.WriteAllText(Path.Combine(_root, instrument.ToString("N"), "ghi-chu.listrace"), "x");

        store.DeleteOlderThan(today, 30).ShouldBe(2);

        store.ListDates(instrument).ShouldBe([today, today.AddDays(-1), today.AddDays(-29)]);
        File.Exists(Path.Combine(_root, instrument.ToString("N"), "ghi-chu.listrace")).ShouldBeTrue();
    }

    [Fact]
    public void DeleteOlderThan_MissingRootOrBadDays_DoesNothing()
    {
        new RawLogStore(_root).DeleteOlderThan(new DateOnly(2026, 11, 10), 30).ShouldBe(0);
        new RawLogStore(_root).DeleteOlderThan(new DateOnly(2026, 11, 10), 0).ShouldBe(0);
    }

    [Fact]
    public void TotalBytes_SumsAllInstruments()
    {
        var store = new RawLogStore(_root);
        store.TotalBytes().ShouldBe(0);
        var at = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.FromHours(7));
        store.Append(Guid.NewGuid(), new RawLogEntry(at, RawLogDirection.In, "a", [1, 2, 3]));
        store.Append(Guid.NewGuid(), new RawLogEntry(at, RawLogDirection.Out, "b", [4]));
        store.TotalBytes().ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("abc", 30)]
    [InlineData("0", 30)]
    [InlineData("-5", 30)]
    [InlineData(" 7 ", 7)]
    public void ParseSetting_FallsBackOnInvalid(string? value, int expected) =>
        RawLogDefaults.Parse(value, RawLogDefaults.KeepDays).ShouldBe(expected);
}
