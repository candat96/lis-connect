using Lis.Core.Ingest;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Ingest;

/// <summary>Barcode máy gửi: bỏ khoảng trắng/ký tự điều khiển, không bao giờ cắt số. Giá trị: áp hệ số và dấu so sánh theo map kênh.</summary>
public class ReadingNormalizerTests
{
    [Theory]
    [InlineData(" 9261103001\r", "9261103001")]
    [InlineData("\u00029261103001\u0003\r\n", "9261103001")]
    [InlineData("9261103001", "9261103001")]
    [InlineData("261103001", "261103001")]
    [InlineData(" \r\n", "")]
    public void CleanBarcode_StripsWhitespaceAndControlCharacters(string raw, string expected)
    {
        ReadingNormalizer.CleanBarcode(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData("0.405", "100", false, "40.5")]
    [InlineData("4,8", "1", false, "4.8")]
    [InlineData("1250", "0.001", false, "1.25")]
    [InlineData(">500", null, true, ">500")]
    [InlineData("> 500", null, false, "500")]
    [InlineData("<=0.5", "10", true, "<=5")]
    [InlineData("neg", "100", false, "neg")]
    [InlineData("***.*", null, false, "***.*")]
    [InlineData("", "100", false, "")]
    public void AdjustValue_AppliesFactorAndComparatorRule(string raw, string? factorText, bool keepComparator, string expected)
    {
        decimal? factor = factorText is null ? null : decimal.Parse(factorText, System.Globalization.CultureInfo.InvariantCulture);

        ReadingNormalizer.AdjustValue(raw, factor, keepComparator).ShouldBe(expected);
    }
}
