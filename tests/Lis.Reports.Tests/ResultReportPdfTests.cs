using Lis.Core.Patients;
using Lis.Core.Results;
using Lis.Data.Facility;
using Lis.Data.Results;
using Lis.Reports.ResultReport;
using Shouldly;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Xunit;

namespace Lis.Reports.Tests;

/// <summary>
/// Kiểm tra phiếu kết quả: thông tin người bệnh và giá trị hiện ra đúng, phiếu chưa duyệt có
/// dòng "CHƯA DUYỆT", giá trị bất thường in kèm mũi tên, và chỉ số con của panel thụt vào sau
/// tên panel (kiểm bằng thứ tự dòng trong text đọc lại từ PDF).
/// </summary>
public class ResultReportPdfTests
{
    private static readonly Guid SampleId = Guid.NewGuid();
    private static readonly DateTimeOffset OrderedAt = new(2026, 11, 4, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ApprovedAt = new(2026, 11, 4, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PrintedAt = new(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);

    private static readonly FacilityInfo Facility = new(
        "Khoa Xét nghiệm — Bệnh viện Thử", "123 Đường Thử, Quận Thử", null, null, "9", "yyMMdd", 3, "per-specimen");

    private static readonly OrderHeader Header = new(
        Guid.NewGuid(), "456 Đường Mẫu", "BS. Trần Văn Thử", "Khoa Nội", "Viêm gan B", OrderedAt);

    private static ResultRow Leaf(string code, string name, string? value, string? unit, string? refText, ResultFlag flag) =>
        new(Guid.NewGuid(), value is null ? null : Guid.NewGuid(), Guid.NewGuid(), code, name, false, 1, value, unit, refText, flag,
            value is null ? null : "da-duyet", null, value is null ? null : "tay");

    private static ResultRow Panel(string code, string name) =>
        new(Guid.NewGuid(), null, Guid.NewGuid(), code, name, true, 0, null, null, null, ResultFlag.None, null, null, null);

    private static SampleResults SampleWithRows(IReadOnlyList<ResultRow> rows, DateTimeOffset? approvedAt) =>
        new(SampleId, "9261104001", "Nguyễn Văn Mẫu", "BN2611040001", new PatientFacts('M', new DateOnly(1980, 5, 1), null),
            approvedAt, approvedAt is null ? null : "Kỹ thuật viên Thử", rows);

    private static ReportData Data(SampleResults sample, bool approved = true) =>
        new(Facility, sample, [], approved, approved ? "Kỹ thuật viên Thử" : null, PrintedAt, Header);

    [Fact]
    public void Render_ContainsPatientAndValues()
    {
        var sample = SampleWithRows([Leaf("HGB", "Huyết sắc tố", "130", "g/L", "120 – 150", ResultFlag.None)], ApprovedAt);
        var pdf = ResultReportPdf.Render(Data(sample));

        using var document = PdfDocument.Open(pdf);
        var text = PageText(document.GetPage(1));

        text.ShouldContain("Nguyễn Văn Mẫu");
        text.ShouldContain("BN2611040001");
        text.ShouldContain("Huyết sắc tố");
        text.ShouldContain("130");
        text.ShouldContain("g/L");
        text.ShouldContain("120");
        text.ShouldContain("BS. Trần Văn Thử");
        text.ShouldContain("Viêm gan B");
        text.ShouldContain("Khoa Xét nghiệm");
    }

    [Fact]
    public void Render_Unapproved_HasWatermarkText()
    {
        var sample = SampleWithRows([Leaf("HGB", "Huyết sắc tố", "130", "g/L", "120 – 150", ResultFlag.None)], approvedAt: null);
        var pdf = ResultReportPdf.Render(Data(sample, approved: false));

        using var document = PdfDocument.Open(pdf);
        var text = PageText(document.GetPage(1));

        var occurrences = CountOccurrences(text, "CHƯA DUYỆT");
        occurrences.ShouldBeGreaterThanOrEqualTo(2);
        text.ShouldContain("KHÔNG CÓ GIÁ TRỊ TRẢ KẾT QUẢ");
    }

    [Fact]
    public void Render_Approved_HasApproverAndNoWatermark()
    {
        var sample = SampleWithRows([Leaf("HGB", "Huyết sắc tố", "130", "g/L", "120 – 150", ResultFlag.None)], ApprovedAt);
        var pdf = ResultReportPdf.Render(Data(sample));

        using var document = PdfDocument.Open(pdf);
        var text = PageText(document.GetPage(1));

        text.ShouldNotContain("CHƯA DUYỆT");
        text.ShouldContain("Kỹ thuật viên Thử");
        text.ShouldContain("Người duyệt");
    }

    [Fact]
    public void Render_AbnormalHasArrow()
    {
        var sample = SampleWithRows([
            Leaf("HGB", "Huyết sắc tố", "190", "g/L", "120 – 150", ResultFlag.High),
            Leaf("K", "Kali", "2.0", "mmol/L", "3.5 – 5.0", ResultFlag.Low),
        ], ApprovedAt);
        var pdf = ResultReportPdf.Render(Data(sample));

        using var document = PdfDocument.Open(pdf);
        var text = PageText(document.GetPage(1));

        text.ShouldContain("190 ↑");
        text.ShouldContain("2.0 ↓");
    }

    [Fact]
    public void Render_PanelChildrenIndented()
    {
        var sample = SampleWithRows([
            Panel("CTM", "Công thức máu"),
            Leaf("WBC", "Bạch cầu", "7.0", "G/L", "4.0 – 10.0", ResultFlag.None),
            Leaf("HGB", "Huyết sắc tố", "130", "g/L", "120 – 150", ResultFlag.None),
        ], ApprovedAt);
        var pdf = ResultReportPdf.Render(Data(sample));

        using var document = PdfDocument.Open(pdf);
        var text = PageText(document.GetPage(1));

        var panelIndex = text.IndexOf("Công thức máu", StringComparison.Ordinal);
        var wbcIndex = text.IndexOf("Bạch cầu", StringComparison.Ordinal);
        var hgbIndex = text.IndexOf("Huyết sắc tố", StringComparison.Ordinal);

        panelIndex.ShouldBeGreaterThanOrEqualTo(0);
        wbcIndex.ShouldBeGreaterThan(panelIndex);
        hgbIndex.ShouldBeGreaterThan(wbcIndex);
    }

    [Fact]
    public void Render_MergesOtherSamplesOfSameOrder()
    {
        var mainSample = SampleWithRows([Leaf("HGB", "Huyết sắc tố", "130", "g/L", "120 – 150", ResultFlag.None)], ApprovedAt);
        var otherSampleId = Guid.NewGuid();
        var otherSample = new SampleResults(
            otherSampleId, "9261104002", "Nguyễn Văn Mẫu", "BN2611040001", mainSample.Patient, ApprovedAt, "Kỹ thuật viên Thử",
            [Leaf("GLU", "Glucose", "5.5", "mmol/L", "3.9 – 6.4", ResultFlag.None)]);

        var data = new ReportData(Facility, mainSample, [otherSample], true, "Kỹ thuật viên Thử", PrintedAt, Header);
        var pdf = ResultReportPdf.Render(data);

        using var document = PdfDocument.Open(pdf);
        var text = PageText(document.GetPage(1));

        text.ShouldContain("9261104001");
        text.ShouldContain("9261104002");
        text.ShouldContain("Glucose");
    }

    /// <summary>
    /// <c>Page.Text</c> của PdfPig dính liền các từ không có khoảng trắng với PDF do MigraDoc
    /// dựng (mỗi từ là một toán tử vẽ riêng, không có glyph dấu cách giữa) — <c>GetWords()</c>
    /// tách đúng ranh giới từ theo khoảng cách hình học nên dùng để đọc lại text ở đây.
    /// </summary>
    private static string PageText(Page page) => string.Join(" ", page.GetWords().Select(w => w.Text));

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
