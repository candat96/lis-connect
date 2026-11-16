using Lis.Core.Results;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Results;

/// <summary>
/// Kiểm tra phân tích chuỗi kết quả thô từ máy xét nghiệm thành giá trị dùng được
/// (số, dấu so sánh, hiệu giá) hoặc đánh dấu rỗng khi máy trả ký hiệu lỗi.
/// </summary>
public class ResultValueTests
{
    [Fact]
    public void Parse_CommaDecimal()
    {
        var value = ResultValue.Parse("4,8");

        value.IsEmpty.ShouldBeFalse();
        value.Number.ShouldBe(4.8m);
        value.Comparator.ShouldBeNull();
        value.Raw.ShouldBe("4,8");
    }

    [Fact]
    public void Parse_Comparator()
    {
        var value = ResultValue.Parse(">500");

        value.IsEmpty.ShouldBeFalse();
        value.Number.ShouldBe(500m);
        value.Comparator.ShouldBe(">");
    }

    [Fact]
    public void Parse_Titer()
    {
        var value = ResultValue.Parse("1:160");

        value.IsEmpty.ShouldBeFalse();
        value.Number.ShouldBe(0.00625m);
        value.Raw.ShouldBe("1:160");
        value.Comparator.ShouldBeNull();
    }

    [Fact]
    public void Parse_NegativeNumber()
    {
        var value = ResultValue.Parse("-4.8");

        value.IsEmpty.ShouldBeFalse();
        value.Number.ShouldBe(-4.8m);
        value.Comparator.ShouldBeNull();
    }

    [Theory]
    [InlineData("***.*")]
    [InlineData("**")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_MachineInvalid(string? raw)
    {
        var value = ResultValue.Parse(raw);

        value.IsEmpty.ShouldBeTrue();
        value.Number.ShouldBeNull();
    }

    /// <summary>
    /// Dạng giống hiệu giá nhưng không đọc được thành số (chữ số Ả Rập-Ấn, số quá dài) phải ra rỗng,
    /// không được ném ngoại lệ — trước đây trả 500 khi kỹ thuật viên gõ trên web.
    /// </summary>
    [Theory]
    [InlineData("\u0661:\u0668")]
    [InlineData("1:\u0668")]
    [InlineData("1:999999999999999999999999999999999")]
    [InlineData("999999999999999999999999999999999/2")]
    public void Parse_TiterKhongDocDuocThanhSo_RaRong(string raw)
    {
        var value = ResultValue.Parse(raw);

        value.IsEmpty.ShouldBeTrue();
        value.Number.ShouldBeNull();
        value.Raw.ShouldBe(raw);
    }
}
