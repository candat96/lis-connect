using Lis.Core.Results;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Results;

/// <summary>
/// Kiểm tra tính cờ cảnh báo từ giá trị số và khoảng tham chiếu: cờ nguy kịch
/// phải thắng cờ cao/thấp thường, giá trị rỗng hoặc không có khoảng luôn là None.
/// </summary>
public class FlagCalculatorTests
{
    private static readonly Guid ServiceId = Guid.NewGuid();

    [Fact]
    public void Flag_CriticalBeatsHigh()
    {
        var range = new ReferenceRange(Guid.NewGuid(), ServiceId, null, null, null, null, 10, 100, 1, 150, null);
        var value = ResultValue.Parse("200");

        var flag = FlagCalculator.Compute(value, range);

        flag.ShouldBe(ResultFlag.CriticalHigh);
    }

    [Fact]
    public void Flag_EmptyIsNone()
    {
        var range = new ReferenceRange(Guid.NewGuid(), ServiceId, null, null, null, null, 10, 100, 1, 150, null);
        var value = ResultValue.Parse("***.*");

        var flag = FlagCalculator.Compute(value, range);

        flag.ShouldBe(ResultFlag.None);
    }

    [Fact]
    public void Flag_NoRangeIsNone()
    {
        var value = ResultValue.Parse("500");

        var flag = FlagCalculator.Compute(value, null);

        flag.ShouldBe(ResultFlag.None);
    }

    [Fact]
    public void Flag_ComparatorHighBeatsThreshold()
    {
        var range = new ReferenceRange(Guid.NewGuid(), ServiceId, null, null, null, null, 10, 400, null, null, null);
        var value = ResultValue.Parse(">500");

        var flag = FlagCalculator.Compute(value, range);

        flag.ShouldBe(ResultFlag.High);
    }
}
