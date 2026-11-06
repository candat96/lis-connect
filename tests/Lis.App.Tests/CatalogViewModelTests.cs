using Lis.App.Services;
using Lis.App.ViewModels.Catalog;
using Lis.Core.Auth;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>ICatalogExcelImporter giả: trả báo cáo định sẵn cho dryRun/nhập thật, ghi lại số lần gọi mỗi loại.</summary>
internal sealed class FakeCatalogExcelImporter : ICatalogExcelImporter
{
    public ImportReport? DryRunReport { get; set; }

    public ImportReport? RealImportReport { get; set; }

    public int DryRunCallCount { get; private set; }

    public int RealImportCallCount { get; private set; }

    public Task<ImportReport> ImportAsync(Stream xlsx, bool dryRun)
    {
        if (dryRun)
        {
            DryRunCallCount++;
            return Task.FromResult(DryRunReport ?? new ImportReport(0, 0, []));
        }

        RealImportCallCount++;
        return Task.FromResult(RealImportReport ?? DryRunReport ?? new ImportReport(0, 0, []));
    }
}

/// <summary>IFilePicker giả: trả một tệp định sẵn, hoặc null (như người dùng bấm Hủy trên hộp thoại).</summary>
internal sealed class FakeFilePicker : IFilePicker
{
    public PickedFile? NextFile { get; set; }

    public int CallCount { get; private set; }

    public Task<PickedFile?> PickExcelFileAsync()
    {
        CallCount++;
        return Task.FromResult(NextFile);
    }
}

/// <summary>
/// Kiểm tra nút "Nhập Excel…" của <see cref="CatalogViewModel"/>: dryRun trước, lỗi thì chặn
/// nút "Nhập thật"; thành công thì "Nhập thật" gọi nhập thật đúng một lần và nạp lại danh sách
/// dịch vụ; bấm Hủy trên hộp thoại chọn tệp thì không làm gì. Dùng <see cref="FakeCatalogExcelImporter"/>
/// và <see cref="FakeFilePicker"/> — không chạm CSDL thật, trừ test kiểm tra "nạp lại danh
/// sách" (CatalogStore trỏ tới một cổng chắc chắn không ai nghe để thất bại ngay, cùng cách
/// <c>HisSettingsViewModelTests</c> dùng).
/// </summary>
public class CatalogViewModelTests
{
    private static readonly byte[] FakeXlsxBytes = [1, 2, 3];

    private static CurrentUser AdminUser() =>
        new(new UserInfo(Guid.NewGuid(), "quantri.thu", "Nguyễn Văn Thử", "Quản trị", [Roles.QuanTri], true));

    private static CurrentUser NonAdminUser() =>
        new(new UserInfo(Guid.NewGuid(), "tiepnhan.thu", "Lê Thị Thử", "Tiếp nhận", [Roles.TiepNhan], true));

    private static CatalogStore UnreachableStore() =>
        new(Db.Create("Host=127.0.0.1;Port=5;Database=khong-dung;Username=x;Password=x;Timeout=1"));

    private static CatalogViewModel CreateViewModel(
        FakeCatalogExcelImporter importer, FakeFilePicker picker, CurrentUser? user = null) =>
        new(UnreachableStore(), importer, picker, user ?? AdminUser());

    [Fact]
    public void CanImportExcel_NonAdmin_IsFalse()
    {
        var viewModel = CreateViewModel(new FakeCatalogExcelImporter(), new FakeFilePicker(), NonAdminUser());

        viewModel.CanImportExcel.ShouldBeFalse();
    }

    [Fact]
    public void CanImportExcel_Admin_IsTrue()
    {
        var viewModel = CreateViewModel(new FakeCatalogExcelImporter(), new FakeFilePicker());

        viewModel.CanImportExcel.ShouldBeTrue();
    }

    [Fact]
    public async Task ImportExcel_PickerCancelled_DoesNothing()
    {
        var importer = new FakeCatalogExcelImporter();
        var picker = new FakeFilePicker { NextFile = null };
        var viewModel = CreateViewModel(importer, picker);

        await viewModel.ImportExcelCommand.ExecuteAsync(null);

        picker.CallCount.ShouldBe(1);
        importer.DryRunCallCount.ShouldBe(0);
        importer.RealImportCallCount.ShouldBe(0);
        viewModel.IsImportResultVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task ImportExcel_DryRunHasErrors_ShowsErrorsAndBlocksRealImport()
    {
        var importer = new FakeCatalogExcelImporter
        {
            DryRunReport = new ImportReport(0, 0, ["Sheet DichVu dòng 2: thiếu Mã"]),
        };
        var picker = new FakeFilePicker { NextFile = new PickedFile("mau-danh-muc.xlsx", FakeXlsxBytes) };
        var viewModel = CreateViewModel(importer, picker);

        await viewModel.ImportExcelCommand.ExecuteAsync(null);

        viewModel.IsImportResultVisible.ShouldBeTrue();
        viewModel.ImportHasErrors.ShouldBeTrue();
        viewModel.ImportErrors.ShouldBe(["Sheet DichVu dòng 2: thiếu Mã"]);
        viewModel.CanConfirmImport.ShouldBeFalse();

        // Bấm "Nhập thật" khi đang có lỗi không làm gì — không gọi nhập thật.
        await viewModel.ConfirmImportCommand.ExecuteAsync(null);

        importer.RealImportCallCount.ShouldBe(0);
        importer.DryRunCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ImportExcel_DryRunOk_ThenConfirm_CallsRealImportOnceAndReloadsList()
    {
        var importer = new FakeCatalogExcelImporter
        {
            DryRunReport = new ImportReport(12, 3, []),
            RealImportReport = new ImportReport(12, 3, []),
        };
        var picker = new FakeFilePicker { NextFile = new PickedFile("mau-danh-muc.xlsx", FakeXlsxBytes) };
        var viewModel = CreateViewModel(importer, picker);

        await viewModel.ImportExcelCommand.ExecuteAsync(null);

        viewModel.IsImportResultVisible.ShouldBeTrue();
        viewModel.ImportHasErrors.ShouldBeFalse();
        viewModel.ImportResultMessage.ShouldBe("Sẽ tạo 12, cập nhật 3.");
        viewModel.CanConfirmImport.ShouldBeTrue();

        await viewModel.ConfirmImportCommand.ExecuteAsync(null);

        importer.RealImportCallCount.ShouldBe(1);
        importer.DryRunCallCount.ShouldBe(1);
        viewModel.ImportResultMessage.ShouldBe("Đã tạo 12, cập nhật 3.");
        viewModel.ImportCompleted.ShouldBeTrue();
        viewModel.CanConfirmImport.ShouldBeFalse();
        viewModel.CancelButtonLabel.ShouldBe("Đóng");

        // CatalogStore trỏ tới cổng không ai nghe (xem UnreachableStore) — LoadAsync() thử nạp
        // lại và thất bại ngay, chứng minh ConfirmImportAsync thật sự gọi LoadAsync() để nạp lại
        // danh sách (chỉ kiểm được rằng có gọi, không kiểm được danh sách đã nạp vì không có
        // CSDL thật trong unit test này).
        viewModel.ErrorMessage.ShouldNotBeNullOrEmpty();
        viewModel.ErrorMessage.ShouldContain("Không tải được danh mục dịch vụ");
    }

    [Fact]
    public async Task CancelImport_ClosesPanelAndClearsPendingFile()
    {
        var importer = new FakeCatalogExcelImporter { DryRunReport = new ImportReport(1, 0, []) };
        var picker = new FakeFilePicker { NextFile = new PickedFile("mau-danh-muc.xlsx", FakeXlsxBytes) };
        var viewModel = CreateViewModel(importer, picker);

        await viewModel.ImportExcelCommand.ExecuteAsync(null);
        viewModel.CancelImportCommand.Execute(null);

        viewModel.IsImportResultVisible.ShouldBeFalse();
        viewModel.ImportErrors.ShouldBeEmpty();

        // Nút "Nhập thật" sau khi Hủy không còn tệp để nhập.
        await viewModel.ConfirmImportCommand.ExecuteAsync(null);
        importer.RealImportCallCount.ShouldBe(0);
    }
}
