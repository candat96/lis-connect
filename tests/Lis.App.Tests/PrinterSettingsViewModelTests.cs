using Lis.App.ViewModels.Settings;
using Lis.Data.Settings;
using Lis.Reports.Printing;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>Bộ nhớ khóa/giá trị thay cho bảng <c>setting</c> — không chạm CSDL.</summary>
internal sealed class InMemorySettingStore : ISettingStore
{
    public Dictionary<string, string?> Values { get; } = [];

    public Task<string?> GetAsync(string key) => Task.FromResult(Values.GetValueOrDefault(key));

    public Task SetAsync(string key, string? value)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }
}

/// <summary>Dịch vụ in giả: ghi lại PDF và tên máy in đã nhận, không in thật.</summary>
internal sealed class FakePrintService : IPrintService
{
    public List<string> InstalledPrinters { get; set; } = [];

    public List<(byte[] Pdf, string? PrinterName)> Printed { get; } = [];

    public Exception? PrintException { get; set; }

    public Task PrintAsync(byte[] pdf, string? printerName, CancellationToken ct = default)
    {
        if (PrintException is not null)
        {
            return Task.FromException(PrintException);
        }

        Printed.Add((pdf, printerName));
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> Printers() => InstalledPrinters;
}

public class PrinterSettingsViewModelTests
{
    private static PrinterSettingsViewModel CreateViewModel(out InMemorySettingStore settings, out FakePrintService printService)
    {
        settings = new InMemorySettingStore();
        printService = new FakePrintService();
        return new PrinterSettingsViewModel(settings, printService);
    }

    [Fact]
    public async Task Load_ReadsSavedValues_AndDefaultsLabelSize()
    {
        var viewModel = CreateViewModel(out var settings, out var printService);
        printService.InstalledPrinters = ["Xprinter XP-350B", "HP LaserJet Thử"];
        settings.Values[SettingKeys.LabelPrinter] = "Xprinter XP-350B";
        settings.Values[SettingKeys.ReportPrinter] = "HP LaserJet Thử";

        await viewModel.LoadAsync();

        viewModel.Printers.ShouldBe(["Xprinter XP-350B", "HP LaserJet Thử"]);
        viewModel.HasPrinters.ShouldBeTrue();
        viewModel.LabelPrinter.ShouldBe("Xprinter XP-350B");
        viewModel.ReportPrinter.ShouldBe("HP LaserJet Thử");
        viewModel.LabelWidthText.ShouldBe("50");
        viewModel.LabelHeightText.ShouldBe("25");
    }

    [Fact]
    public async Task Load_NoPrinterList_ShowsSavedValueAndHint()
    {
        var viewModel = CreateViewModel(out var settings, out _);
        settings.Values[SettingKeys.LabelPrinter] = "Máy in tem cũ";
        settings.Values[SettingKeys.LabelSize] = "40x20";

        await viewModel.LoadAsync();

        viewModel.HasPrinters.ShouldBeFalse();
        viewModel.LabelPrinter.ShouldBe("Máy in tem cũ");
        viewModel.PrinterListHint.ShouldBe("Không liệt kê được máy in trên hệ này");
        viewModel.LabelWidthText.ShouldBe("40");
        viewModel.LabelHeightText.ShouldBe("20");
    }

    [Fact]
    public async Task Save_WritesSettings_AndShowsSuccess()
    {
        var viewModel = CreateViewModel(out var settings, out _);
        await viewModel.LoadAsync();

        viewModel.LabelPrinter = "Xprinter XP-350B";
        viewModel.ReportPrinter = "";
        viewModel.LabelWidthText = "40,5";
        viewModel.LabelHeightText = "20";

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBeNull();
        viewModel.SuccessMessage.ShouldBe("Đã lưu cài đặt máy in");
        settings.Values[SettingKeys.LabelPrinter].ShouldBe("Xprinter XP-350B");
        settings.Values[SettingKeys.ReportPrinter].ShouldBeNull();
        settings.Values[SettingKeys.LabelSize].ShouldBe("40.5x20");
    }

    [Theory]
    [InlineData("0", "25", "Chiều rộng tem phải là số lớn hơn 0 (mm)")]
    [InlineData("abc", "25", "Chiều rộng tem phải là số lớn hơn 0 (mm)")]
    [InlineData("50", "-1", "Chiều cao tem phải là số lớn hơn 0 (mm)")]
    [InlineData("50", "", "Chiều cao tem phải là số lớn hơn 0 (mm)")]
    public async Task Save_InvalidLabelSize_SetsErrorAndDoesNotWrite(string width, string height, string expectedError)
    {
        var viewModel = CreateViewModel(out var settings, out _);
        await viewModel.LoadAsync();

        viewModel.LabelWidthText = width;
        viewModel.LabelHeightText = height;

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe(expectedError);
        viewModel.SuccessMessage.ShouldBeNull();
        settings.Values.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrintTestLabel_SendsOnePageToLabelPrinter()
    {
        var viewModel = CreateViewModel(out _, out var printService);
        await viewModel.LoadAsync();
        viewModel.LabelPrinter = "Xprinter XP-350B";

        await viewModel.PrintTestLabelCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBeNull();
        viewModel.SuccessMessage.ShouldBe("Đã gửi tem thử tới máy in");
        var (pdf, printerName) = printService.Printed.ShouldHaveSingleItem();
        printerName.ShouldBe("Xprinter XP-350B");
        pdf.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task PrintTestLabel_Failure_SetsVietnameseError()
    {
        var viewModel = CreateViewModel(out _, out var printService);
        await viewModel.LoadAsync();
        printService.PrintException = new InvalidOperationException("Không tìm thấy máy in \"XP\"");

        await viewModel.PrintTestLabelCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("In tem thử lỗi: Không tìm thấy máy in \"XP\"");
        viewModel.SuccessMessage.ShouldBeNull();
    }

    [Fact]
    public async Task Save_StoreFailure_SetsVietnameseError()
    {
        var viewModel = new PrinterSettingsViewModel(new ThrowingSettingStore(), new FakePrintService());

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Lưu cài đặt máy in thất bại: mất kết nối CSDL");
    }

    private sealed class ThrowingSettingStore : ISettingStore
    {
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);

        public Task SetAsync(string key, string? value) => throw new InvalidOperationException("mất kết nối CSDL");
    }
}
