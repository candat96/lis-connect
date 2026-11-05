using Lis.App.ViewModels.Settings;
using Lis.Data.Facility;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>IFacilityStore giả — lưu trong bộ nhớ, có thể ép ném lỗi để kiểm thông báo lỗi tiếng Việt.</summary>
internal sealed class FakeFacilitySettingsStore : IFacilityStore
{
    public FacilityInfo? Saved { get; private set; }

    public FacilityInfo? Stored { get; set; }

    public Exception? SaveException { get; set; }

    public Task<FacilityInfo?> GetAsync() => Task.FromResult(Stored);

    public Task SaveAsync(FacilityInfo facility)
    {
        if (SaveException is not null)
        {
            return Task.FromException(SaveException);
        }

        Saved = facility;
        Stored = facility;
        return Task.CompletedTask;
    }
}

public class FacilitySettingsViewModelTests
{
    private static FacilitySettingsViewModel CreateViewModel(out FakeFacilitySettingsStore store)
    {
        store = new FakeFacilitySettingsStore();
        return new FacilitySettingsViewModel(store);
    }

    [Fact]
    public async Task Save_WritesFacilityInfo_WithBarcodeFields()
    {
        var viewModel = CreateViewModel(out var store);
        viewModel.Name = "Khoa Xét nghiệm — Bệnh viện Thử";
        viewModel.Address = "123 Đường Thử";
        viewModel.Phone = "0900000001";
        viewModel.BarcodePrefix = "9";
        viewModel.BarcodeDatePattern = "yyMMdd";
        viewModel.BarcodeSeqDigitsText = "3";
        viewModel.BarcodeModeSingle = false;

        await viewModel.SaveCommand.ExecuteAsync(null);

        store.Saved.ShouldNotBeNull();
        store.Saved!.Name.ShouldBe("Khoa Xét nghiệm — Bệnh viện Thử");
        store.Saved.BarcodePrefix.ShouldBe("9");
        store.Saved.BarcodeMode.ShouldBe("per-specimen");
        viewModel.ErrorMessage.ShouldBeNull();
        viewModel.SuccessMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Save_StoreThrows_ShowsVietnameseErrorMessage()
    {
        var viewModel = CreateViewModel(out var store);
        viewModel.Name = "Khoa Xét nghiệm — Bệnh viện Thử";
        store.SaveException = new InvalidOperationException("mất kết nối CSDL");

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        viewModel.ErrorMessage.ShouldContain("mất kết nối CSDL");
        viewModel.ErrorMessage.ShouldContain("Lưu thông tin cơ sở thất bại");
    }

    [Fact]
    public async Task Save_MissingName_ShowsErrorAndDoesNotCallStore()
    {
        var viewModel = CreateViewModel(out var store);
        viewModel.Name = "   ";

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Nhập tên cơ sở");
        store.Saved.ShouldBeNull();
    }

    [Fact]
    public void BarcodePreview_ReflectsCurrentFields_WithLength()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        var viewModel = new FacilitySettingsViewModel(new FakeFacilitySettingsStore(), clock)
        {
            BarcodePrefix = "9",
            BarcodeDatePattern = "yyMMdd",
            BarcodeSeqDigitsText = "3",
        };

        viewModel.BarcodePreview.ShouldBe("9261103001 (10 chữ số)");
    }
}
