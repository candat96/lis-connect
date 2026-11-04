using Lis.Data.Settings;
using Lis.Reports.Labels;
using Lis.Reports.Printing;

namespace Lis.App.Services;

/// <summary>
/// In tem thật: dựng PDF bằng <see cref="LabelPdf"/> theo kích thước tem đã lưu
/// (<see cref="SettingKeys.LabelSize"/>) rồi gửi tới máy in tem (<see cref="SettingKeys.LabelPrinter"/>)
/// qua <see cref="IPrintService"/>.
/// </summary>
public sealed class LabelPrinter(ISettingStore settings, IPrintService printService) : ILabelPrinter
{
    public async Task PrintAsync(IReadOnlyList<LabelData> labels)
    {
        if (labels.Count == 0)
        {
            return;
        }

        var size = LabelSize.FromSetting(await settings.GetAsync(SettingKeys.LabelSize));
        var printerName = await settings.GetAsync(SettingKeys.LabelPrinter);

        var pdf = LabelPdf.Render(labels, size);
        await printService.PrintAsync(pdf, string.IsNullOrWhiteSpace(printerName) ? null : printerName);
    }
}
