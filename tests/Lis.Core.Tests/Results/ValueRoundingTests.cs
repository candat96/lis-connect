using Lis.Core.Results;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Results;

/// <summary>
/// Kiểm tra làm tròn chuỗi giá trị kết quả, giữ đúng số chữ số thập phân
/// (kể cả số 0 ở cuối) và giữ dấu so sánh, không đụng vào chuỗi không phải số.
/// </summary>
public class ValueRoundingTests
{
    [Theory]
    [InlineData("4.8", 2, "4.80")]
    [InlineData("0.125", 2, "0.13")]
    [InlineData(">5.55", 1, ">5.6")]
    [InlineData("Âm tính", 2, "Âm tính")]
    public void Round_KeepsTrailingZeros(string raw, int decimals, string expected)
    {
        var result = ValueRounding.Round(raw, decimals);

        result.ShouldBe(expected);
    }
}
