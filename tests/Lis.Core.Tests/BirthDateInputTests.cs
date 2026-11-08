using Lis.Core.Patients;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests;

/// <summary>Một ô ngày sinh: dd/MM/yyyy, 8 chữ số liền, hoặc chỉ năm.</summary>
public class BirthDateInputTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    [Theory]
    [InlineData("15/06/1980", 1980, 6, 15)]
    [InlineData("5/6/1980", 1980, 6, 5)]
    [InlineData("15-06-1980", 1980, 6, 15)]
    [InlineData("15061980", 1980, 6, 15)]
    [InlineData(" 29/02/2024 ", 2024, 2, 29)]
    public void FullDate_Parses(string text, int year, int month, int day)
    {
        BirthDateInput.TryParse(text, Today, out var value, out var error).ShouldBeTrue(error);
        value.ShouldBe(new BirthDate(new DateOnly(year, month, day), null));
    }

    [Fact]
    public void YearOnly_Parses()
    {
        BirthDateInput.TryParse("1980", Today, out var value, out _).ShouldBeTrue();
        value.ShouldBe(new BirthDate(null, 1980));
    }

    [Theory]
    [InlineData("", "Chưa nhập ngày sinh (dd/MM/yyyy hoặc chỉ năm yyyy)")]
    [InlineData("31/02/1980", "Ngày 31/02/1980 không có thật")]
    [InlineData("01/01/2030", "Ngày sinh sau hôm nay")]
    [InlineData("2030", "Năm sinh phải từ 1900 đến 2026")]
    [InlineData("1980/06/15", "Ngày sinh phải dạng dd/MM/yyyy (vd 15/06/1980) hoặc chỉ năm (vd 1980)")]
    [InlineData("abc", "Ngày sinh phải dạng dd/MM/yyyy (vd 15/06/1980) hoặc chỉ năm (vd 1980)")]
    public void Invalid_ReportsReason(string text, string expected)
    {
        BirthDateInput.TryParse(text, Today, out _, out var error).ShouldBeFalse();
        error.ShouldBe(expected);
    }

    [Fact]
    public void Format_EightDigits_InsertsSlashes()
    {
        BirthDateInput.Format("01011990").ShouldBe("01/01/1990");
        BirthDateInput.Format("1990").ShouldBe("1990");
        BirthDateInput.Format("0101").ShouldBe("0101");
    }

    [Fact]
    public void Describe_ShowsAge()
    {
        BirthDateInput.Describe("15/06/1980", Today).ShouldBe("15/06/1980 · 46 tuổi");
        BirthDateInput.Describe("09/11/1980", Today).ShouldBe("09/11/1980 · 45 tuổi");
        BirthDateInput.Describe("1980", Today).ShouldBe("Năm 1980 · 46 tuổi");
        BirthDateInput.Describe("01/06/2026", Today).ShouldBe("01/06/2026 · dưới 1 tuổi");
    }
}
