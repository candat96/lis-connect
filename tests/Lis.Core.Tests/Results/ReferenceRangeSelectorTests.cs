using Lis.Core.Patients;
using Lis.Core.Results;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Results;

/// <summary>
/// Kiểm tra chọn khoảng tham chiếu đúng theo máy, giới tính, độ tuổi (bao gồm
/// trường hợp sơ sinh tính theo ngày và người bệnh chỉ có năm sinh).
/// </summary>
public class ReferenceRangeSelectorTests
{
    private static readonly Guid ServiceId = Guid.NewGuid();
    private static readonly Guid InstrumentId = Guid.NewGuid();
    private static readonly Guid OtherInstrumentId = Guid.NewGuid();

    [Fact]
    public void Select_PrefersInstrumentThenSexThenNarrowAge()
    {
        var chung = new ReferenceRange(Guid.NewGuid(), ServiceId, null, null, null, null, 1, 10, null, null, null);
        var nam = new ReferenceRange(Guid.NewGuid(), ServiceId, 'M', null, null, null, 2, 9, null, null, null);
        var namDungMay = new ReferenceRange(Guid.NewGuid(), ServiceId, 'M', null, null, InstrumentId, 3, 8, null, null, null);
        var namDungMayTuoiHep = new ReferenceRange(Guid.NewGuid(), ServiceId, 'M', 365 * 18, 365 * 60, InstrumentId, 4, 7, null, null, null);

        var ranges = new[] { chung, nam, namDungMay, namDungMayTuoiHep };
        var patient = new PatientFacts('M', new DateOnly(1990, 1, 1), null);

        var selected = ReferenceRangeSelector.Select(ranges, patient, new DateOnly(2026, 1, 1), InstrumentId);

        selected.ShouldBe(namDungMayTuoiHep);
    }

    [Fact]
    public void Select_NewbornByDays()
    {
        var treSoSinh = new ReferenceRange(Guid.NewGuid(), ServiceId, null, 0, 28, null, 1, 2, null, null, null);
        var treEm = new ReferenceRange(Guid.NewGuid(), ServiceId, null, 29, 365 * 18, null, 3, 4, null, null, null);

        var ranges = new[] { treSoSinh, treEm };
        var ngaySinh = new DateOnly(2026, 1, 1);
        var patient = new PatientFacts('F', ngaySinh, null);

        var selected = ReferenceRangeSelector.Select(ranges, patient, ngaySinh.AddDays(10), null);

        selected.ShouldBe(treSoSinh);
    }

    [Fact]
    public void Select_BirthYearOnly_UsesJulyFirst()
    {
        // Sinh giả định 01/07/2000. Đúng 26 năm (tính cả ngày nhuận) là 9496 ngày,
        // nên mốc "dưới 26 năm" dừng ở ngày 9495.
        var duoi26 = new ReferenceRange(Guid.NewGuid(), ServiceId, null, null, 9495, null, 1, 2, null, null, null);
        var tu26TroLen = new ReferenceRange(Guid.NewGuid(), ServiceId, null, 9496, null, null, 3, 4, null, null, null);

        var ranges = new[] { duoi26, tu26TroLen };
        var patient = new PatientFacts('F', null, 2000);

        // Ngày 2026-06-30 là 9495 ngày sau 01/07/2000 — vẫn còn dưới 26 năm tuổi theo ngày.
        var selected = ReferenceRangeSelector.Select(ranges, patient, new DateOnly(2026, 6, 30), null);

        selected.ShouldBe(duoi26);
    }
}
