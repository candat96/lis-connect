using Lis.Core;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests;

/// <summary>
/// Kiểm tra parse ô nhập số thập phân: chấp nhận dấu phẩy hoặc dấu chấm làm dấu thập phân,
/// không bao giờ đoán ý dấu phân cách hàng nghìn (có cả hai dấu là sai).
/// </summary>
public class DecimalInputTests
{
    [Theory]
    [InlineData("130,5", 130.5)]
    [InlineData("130.5", 130.5)]
    [InlineData("-0,25", -0.25)]
    public void TryParse_ValidInput_ReturnsExpectedValue(string text, double expected)
    {
        var ok = DecimalInput.TryParse(text, out var value);

        ok.ShouldBeTrue();
        value.ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("1.234,5")]
    [InlineData("1,234.5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_InvalidInput_ReturnsFalse(string? text)
    {
        var ok = DecimalInput.TryParse(text, out var value);

        ok.ShouldBeFalse();
        value.ShouldBe(0m);
    }
}
