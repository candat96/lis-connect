using Lis.App.Services;
using Lis.Data.Orders;
using Lis.Data.Settings;
using Lis.Reports.Labels;
using Shouldly;
using UglyToad.PdfPig;
using Xunit;

namespace Lis.App.Tests;

/// <summary>
/// Kiểm tra in tem từ màn Tiếp nhận: dựng <see cref="LabelData"/> từ người bệnh + mẫu đã cấp
/// (<see cref="LabelDataBuilder"/>), và <see cref="LabelPrinter"/> gửi đúng PDF tới máy in tem
/// với kích thước tem đã lưu trong <c>setting</c>.
/// </summary>
public class LabelPrinterTests
{
    private static readonly DateOnly Day = new(2026, 11, 4);

    [Fact]
    public void Build_OneLabelPerSample_WithBirthYearFromDob()
    {
        var samples = new[]
        {
            new CreatedSample(Guid.NewGuid(), "9261104001", "Máu EDTA", ["Bạch cầu", "Hồng cầu"]),
            new CreatedSample(Guid.NewGuid(), "9261104002", "Huyết thanh", ["Glucose"]),
        };

        var labels = LabelDataBuilder.Build("Nguyễn Văn Mẫu", new DateOnly(1980, 5, 1), null, 'M', samples, Day);

        labels.ShouldBe([
            new LabelData("9261104001", "Nguyễn Văn Mẫu", 1980, 'M', "Máu EDTA", Day),
            new LabelData("9261104002", "Nguyễn Văn Mẫu", 1980, 'M', "Huyết thanh", Day),
        ]);
    }

    [Fact]
    public void Build_BirthYearOnly_UsesBirthYear()
    {
        var samples = new[] { new CreatedSample(Guid.NewGuid(), "9261104003", "Nước tiểu", ["Tổng phân tích nước tiểu"]) };

        var labels = LabelDataBuilder.Build("Trần Thị Mẫu", null, 1995, 'F', samples, Day);

        labels.ShouldHaveSingleItem().BirthYear.ShouldBe(1995);
    }

    [Fact]
    public async Task Print_UsesSavedPrinterAndLabelSize()
    {
        var settings = new InMemorySettingStore();
        settings.Values[SettingKeys.LabelPrinter] = "Xprinter XP-350B";
        settings.Values[SettingKeys.LabelSize] = "40x20";
        var printService = new FakePrintService();
        var printer = new LabelPrinter(settings, printService);

        await printer.PrintAsync([
            new LabelData("9261104001", "Nguyễn Văn Mẫu", 1980, 'M', "Máu EDTA", Day),
            new LabelData("9261104002", "Nguyễn Văn Mẫu", 1980, 'M', "Huyết thanh", Day),
        ]);

        var (pdf, printerName) = printService.Printed.ShouldHaveSingleItem();
        printerName.ShouldBe("Xprinter XP-350B");

        using var document = PdfDocument.Open(pdf);
        document.NumberOfPages.ShouldBe(2);
        // 40 × 20 mm = 113.39 × 56.69 pt
        document.GetPage(1).Width.ShouldBe(113.39, tolerance: 0.1);
        document.GetPage(1).Height.ShouldBe(56.69, tolerance: 0.1);
    }

    [Fact]
    public async Task Print_NoLabels_DoesNothing()
    {
        var printService = new FakePrintService();
        var printer = new LabelPrinter(new InMemorySettingStore(), printService);

        await printer.PrintAsync([]);

        printService.Printed.ShouldBeEmpty();
    }
}
