using Lis.App.Services;
using Lis.Core.Patients;
using Lis.Core.Results;
using Lis.Data.Facility;
using Lis.Data.Results;
using Lis.Data.Settings;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using UglyToad.PdfPig;
using Xunit;

namespace Lis.App.Tests;

/// <summary>IFacilityStore giả — một cơ sở cố định, không chạm CSDL.</summary>
internal sealed class FakeFacilityStore(FacilityInfo? facility) : IFacilityStore
{
    public Task<FacilityInfo?> GetAsync() => Task.FromResult(facility);

    public Task SaveAsync(FacilityInfo facility) => Task.CompletedTask;
}

/// <summary>
/// Kiểm tra <see cref="ReportPrinter"/>: gộp mọi mẫu khác cùng phiếu chỉ định vào một phiếu in,
/// phiếu chỉ "đã duyệt" khi MỌI mẫu gộp vào đều đã duyệt, và PDF được gửi tới máy in đã lưu ở
/// setting <see cref="SettingKeys.ReportPrinter"/>.
/// </summary>
public class ReportPrinterTests
{
    private static readonly FacilityInfo Facility = new(
        "Khoa Xét nghiệm — Bệnh viện Thử", "123 Đường Thử", null, null, "9", "yyMMdd", 3, "per-specimen");

    private static ResultRow Leaf(string code, string name, string value) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), code, name, false, 0, value, "g/L", "120 – 150", ResultFlag.None, "da-duyet", null, "tay");

    private static SampleResults Sample(Guid id, string barcode, DateTimeOffset? approvedAt, params ResultRow[] rows) =>
        new(id, barcode, "Nguyễn Văn Mẫu", "BN1", new PatientFacts('M', new DateOnly(1980, 1, 1), null),
            approvedAt, approvedAt is null ? null : "Kỹ thuật viên Thử", rows);

    [Fact]
    public async Task PrintAsync_AllSamplesApproved_RendersApprovedReportAndPrints()
    {
        var sampleId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var approvedAt = new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.Zero);

        var results = new FakeResultServiceWithOrder
        {
            Header = new OrderHeader(Guid.NewGuid(), "Địa chỉ Thử", "BS. Thử", "Khoa Nội", "Viêm gan B", approvedAt),
            OrderSampleIds = [sampleId, otherId],
            SamplesById =
            {
                [sampleId] = Sample(sampleId, "9261104001", approvedAt, Leaf("HGB", "Huyết sắc tố", "130")),
                [otherId] = Sample(otherId, "9261104002", approvedAt, Leaf("GLU", "Glucose", "5.5")),
            },
        };

        var settings = new InMemorySettingStore();
        settings.Values[SettingKeys.ReportPrinter] = "HP LaserJet Thử";
        var printService = new FakePrintService();
        var printer = new ReportPrinter(
            results, new FakeFacilityStore(Facility), settings, printService, new FakeTimeProvider(approvedAt));

        await printer.PrintAsync(sampleId);

        var (pdf, printerName) = printService.Printed.ShouldHaveSingleItem();
        printerName.ShouldBe("HP LaserJet Thử");

        using var document = PdfDocument.Open(pdf);
        var text = string.Join(" ", document.GetPage(1).GetWords().Select(w => w.Text));
        text.ShouldContain("9261104001");
        text.ShouldContain("9261104002");
        text.ShouldContain("Glucose");
        text.ShouldNotContain("CHƯA DUYỆT");
    }

    [Fact]
    public async Task PrintAsync_OtherSampleOfOrderNotApproved_RendersUnapprovedReport()
    {
        var sampleId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var approvedAt = new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.Zero);

        var results = new FakeResultServiceWithOrder
        {
            Header = new OrderHeader(Guid.NewGuid(), null, null, null, null, approvedAt),
            OrderSampleIds = [sampleId, otherId],
            SamplesById =
            {
                [sampleId] = Sample(sampleId, "9261104001", approvedAt, Leaf("HGB", "Huyết sắc tố", "130")),
                [otherId] = Sample(otherId, "9261104002", null, Leaf("GLU", "Glucose", "5.5")),
            },
        };

        var settings = new InMemorySettingStore();
        var printService = new FakePrintService();
        var printer = new ReportPrinter(
            results, new FakeFacilityStore(Facility), settings, printService, new FakeTimeProvider(approvedAt));

        await printer.PrintAsync(sampleId);

        var (pdf, printerName) = printService.Printed.ShouldHaveSingleItem();
        printerName.ShouldBeNull();

        using var document = PdfDocument.Open(pdf);
        var text = string.Join(" ", document.GetPage(1).GetWords().Select(w => w.Text));
        text.ShouldContain("CHƯA DUYỆT");
    }

    /// <summary>IResultService giả cho kiểm thử <see cref="ReportPrinter"/>: nhiều mẫu, có id phiếu chỉ định.</summary>
    private sealed class FakeResultServiceWithOrder : IResultService
    {
        public Dictionary<Guid, SampleResults> SamplesById { get; } = [];

        public OrderHeader Header { get; set; } = new(Guid.NewGuid(), null, null, null, null, DateTimeOffset.UtcNow);

        public Guid[] OrderSampleIds { get; set; } = [];

        public Task<IReadOnlyList<SampleListItem>> ListAsync(DateOnly from, DateOnly to, string? status, string? query) =>
            throw new NotSupportedException();

        public Task<SampleResults> GetAsync(Guid sampleId) => Task.FromResult(SamplesById[sampleId]);

        public Task<OrderHeader> GetOrderHeaderAsync(Guid sampleId) => Task.FromResult(Header);

        public Task<IReadOnlyList<Guid>> GetOrderSampleIdsAsync(Guid sampleId) => Task.FromResult<IReadOnlyList<Guid>>(OrderSampleIds);

        public Task SetManualAsync(Guid orderItemId, string value, Guid actorId, string? reason) => throw new NotSupportedException();

        public Task ApproveAsync(Guid sampleId, Guid actorId) => throw new NotSupportedException();

        public Task UnapproveAsync(Guid sampleId, Guid actorId, string reason) => throw new NotSupportedException();

        public Task AcceptPendingAsync(Guid resultId, Guid actorId) => throw new NotSupportedException();

        public Task RejectPendingAsync(Guid resultId, Guid actorId) => throw new NotSupportedException();

        public Task<bool> TryAutoApproveAsync(Guid sampleId) => throw new NotSupportedException();
    }
}
