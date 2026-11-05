using Lis.Data.Facility;
using Lis.Data.Results;
using Lis.Data.Settings;
using Lis.Reports.Printing;
using Lis.Reports.ResultReport;

namespace Lis.App.Services;

/// <summary>
/// In phiếu kết quả thật: gộp mọi mẫu của cùng phiếu chỉ định với mẫu đang chọn
/// (<see cref="IResultService.GetOrderSampleIdsAsync"/>) vào một <see cref="ReportData"/>, dựng
/// PDF bằng <see cref="ResultReportPdf"/> rồi gửi tới máy in phiếu đã lưu
/// (<see cref="SettingKeys.ReportPrinter"/>) qua <see cref="IPrintService"/>. Cả phiếu chỉ coi
/// là "đã duyệt" khi MỌI mẫu gộp vào đều đã duyệt — một mẫu chưa duyệt là đủ để
/// <see cref="ResultReportPdf"/> in dòng "CHƯA DUYỆT" (xem Task 12).
/// </summary>
public sealed class ReportPrinter(IResultService results, IFacilityStore facility, ISettingStore settings, IPrintService printService, TimeProvider clock)
    : IReportPrinter
{
    public async Task PrintAsync(Guid sampleId)
    {
        var header = await results.GetOrderHeaderAsync(sampleId);
        var sampleIds = await results.GetOrderSampleIdsAsync(sampleId);

        var mainSample = await results.GetAsync(sampleId);
        var otherSamples = new List<SampleResults>();
        foreach (var otherId in sampleIds.Where(id => id != sampleId))
        {
            otherSamples.Add(await results.GetAsync(otherId));
        }

        var facilityInfo = await facility.GetAsync()
            ?? throw new InvalidOperationException("Chưa cài đặt thông tin cơ sở");

        var approved = mainSample.ApprovedAt is not null && otherSamples.All(s => s.ApprovedAt is not null);
        var approverName = approved ? mainSample.ApprovedBy : null;

        var data = new ReportData(facilityInfo, mainSample, otherSamples, approved, approverName, clock.GetUtcNow(), header);
        var pdf = ResultReportPdf.Render(data);

        var printerName = await settings.GetAsync(SettingKeys.ReportPrinter);
        await printService.PrintAsync(pdf, string.IsNullOrWhiteSpace(printerName) ? null : printerName);
    }
}
