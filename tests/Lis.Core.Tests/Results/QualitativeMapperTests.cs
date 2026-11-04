using Lis.Core.Results;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Results;

/// <summary>
/// Kiểm tra quy đổi kết quả định tính (ví dụ "Âm tính"/"Dương tính") sang chuỗi
/// hiển thị và cờ ghi đè, theo luật riêng từng máy ưu tiên hơn luật chung.
/// </summary>
public class QualitativeMapperTests
{
    private static readonly Guid ServiceId = Guid.NewGuid();
    private static readonly Guid InstrumentId = Guid.NewGuid();

    [Fact]
    public void Qualitative_InstrumentRuleWins()
    {
        var luatChung = new QualitativeRule(null, ServiceId, "pos", "Dương tính", ResultFlag.Abnormal);
        var luatDungMay = new QualitativeRule(InstrumentId, ServiceId, "pos", "DƯƠNG TÍNH (máy X)", ResultFlag.Abnormal);

        var (display, flag) = QualitativeMapper.Apply(" POS ", ServiceId, InstrumentId, new[] { luatChung, luatDungMay });

        display.ShouldBe("DƯƠNG TÍNH (máy X)");
        flag.ShouldBe(ResultFlag.Abnormal);
    }

    [Fact]
    public void Qualitative_NegativeOverridesFlagToNone()
    {
        var luat = new QualitativeRule(null, ServiceId, "neg", "Âm tính", ResultFlag.None);

        var (display, flag) = QualitativeMapper.Apply("NEG", ServiceId, null, new[] { luat });

        display.ShouldBe("Âm tính");
        flag.ShouldBe(ResultFlag.None);
    }

    [Fact]
    public void Qualitative_NoRule_ReturnsRawUnchanged()
    {
        var (display, flag) = QualitativeMapper.Apply("abc", ServiceId, null, Array.Empty<QualitativeRule>());

        display.ShouldBe("abc");
        flag.ShouldBeNull();
    }
}
