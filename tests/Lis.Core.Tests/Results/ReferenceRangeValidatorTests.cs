using Lis.Core.Results;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Results;

/// <summary>
/// Kiểm tra từng quy tắc của <see cref="ReferenceRangeValidator"/>: thấp ≤ cao, nguy hiểm thấp
/// ≤ thấp, cao ≤ nguy hiểm cao, tuổi từ ≤ tuổi đến. Thông báo lỗi kèm số dòng (1-based).
/// </summary>
public class ReferenceRangeValidatorTests
{
    private static readonly Guid ServiceId = Guid.NewGuid();

    private static ReferenceRange Range(
        decimal? low = null, decimal? high = null, decimal? criticalLow = null, decimal? criticalHigh = null,
        int? ageFromDays = null, int? ageToDays = null) =>
        new(Guid.NewGuid(), ServiceId, null, ageFromDays, ageToDays, null, low, high, criticalLow, criticalHigh, null);

    [Fact]
    public void Validate_ValidRange_NoErrors()
    {
        var range = Range(low: 10, high: 20, criticalLow: 5, criticalHigh: 25, ageFromDays: 0, ageToDays: 100);

        var errors = ReferenceRangeValidator.Validate([range]);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_LowGreaterThanHigh_Errors()
    {
        var range = Range(low: 20, high: 10);

        var errors = ReferenceRangeValidator.Validate([range]);

        errors.ShouldContain("Dòng 1: giá trị thấp phải nhỏ hơn hoặc bằng giá trị cao");
    }

    [Fact]
    public void Validate_CriticalLowGreaterThanLow_Errors()
    {
        var range = Range(low: 10, criticalLow: 15);

        var errors = ReferenceRangeValidator.Validate([range]);

        errors.ShouldContain("Dòng 1: nguy hiểm thấp phải nhỏ hơn hoặc bằng giá trị thấp");
    }

    [Fact]
    public void Validate_HighGreaterThanCriticalHigh_Errors()
    {
        var range = Range(high: 30, criticalHigh: 25);

        var errors = ReferenceRangeValidator.Validate([range]);

        errors.ShouldContain("Dòng 1: giá trị cao phải nhỏ hơn hoặc bằng nguy hiểm cao");
    }

    [Fact]
    public void Validate_AgeFromGreaterThanAgeTo_Errors()
    {
        var range = Range(ageFromDays: 100, ageToDays: 50);

        var errors = ReferenceRangeValidator.Validate([range]);

        errors.ShouldContain("Dòng 1: tuổi từ phải nhỏ hơn hoặc bằng tuổi đến");
    }

    [Fact]
    public void Validate_OnlyOneBoundSet_NoCriticalErrors()
    {
        // Chỉ đặt nguy hiểm thấp, chưa đặt giá trị thấp — chưa đủ để so sánh, không báo lỗi.
        var range = Range(criticalLow: 5);

        var errors = ReferenceRangeValidator.Validate([range]);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_SecondRowError_UsesCorrectRowNumber()
    {
        var valid = Range(low: 1, high: 2);
        var invalid = Range(low: 20, high: 10);

        var errors = ReferenceRangeValidator.Validate([valid, invalid]);

        errors.ShouldBe(["Dòng 2: giá trị thấp phải nhỏ hơn hoặc bằng giá trị cao"]);
    }
}
