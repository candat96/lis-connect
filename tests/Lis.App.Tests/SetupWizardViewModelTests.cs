using Lis.App.ViewModels;
using Lis.Data;
using Lis.Data.Local;
using Lis.Data.Setup;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>
/// Cài đặt giả của <see cref="ISetupService"/> — không chạm CSDL thật, chỉ trả kết quả định sẵn.
/// </summary>
internal sealed class FakeSetupService : ISetupService
{
    public ConnectionTest NextTestResult { get; set; } = new(false, "Chưa thử", false);

    public MigrationResult NextMigrationResult { get; set; } = new(true, [], null);

    public Exception? MigrateException { get; set; }

    public bool NeedsFirstAdminResult { get; set; }

    public Exception? CreateFirstAdminException { get; set; }

    public Action? OnMigrate { get; set; }

    public Task<ConnectionTest> TestAsync(DbSettings settings) => Task.FromResult(NextTestResult);

    public MigrationResult Migrate(DbSettings settings)
    {
        OnMigrate?.Invoke();
        return MigrateException is not null ? throw MigrateException : NextMigrationResult;
    }

    public Task<bool> NeedsFirstAdminAsync(DbSettings settings) => Task.FromResult(NeedsFirstAdminResult);

    public Task CreateFirstAdminAsync(DbSettings settings, string username, string fullName, string password) =>
        CreateFirstAdminException is not null ? Task.FromException(CreateFirstAdminException) : Task.CompletedTask;
}

public class SetupWizardViewModelTests
{
    private static SetupWizardViewModel CreateViewModel(out FakeSetupService fake)
    {
        fake = new FakeSetupService();
        return new SetupWizardViewModel(fake);
    }

    [Fact]
    public void Wizard_NextDisabledUntilConnectionOk()
    {
        var vm = CreateViewModel(out var fake);

        vm.NextCommand.CanExecute(null).ShouldBeFalse();

        fake.NextTestResult = new ConnectionTest(true, "Kết nối thành công", false);
        vm.TestConnectionCommand.Execute(null);

        vm.ConnectionOk.ShouldBeTrue();
        vm.NextCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void Wizard_FailedConnection_KeepsNextDisabled()
    {
        var vm = CreateViewModel(out var fake);

        fake.NextTestResult = new ConnectionTest(false, "Sai tài khoản hoặc mật khẩu CSDL", false);
        vm.TestConnectionCommand.Execute(null);

        vm.ConnectionOk.ShouldBeFalse();
        vm.ConnectionMessage.ShouldBe("Sai tài khoản hoặc mật khẩu CSDL");
        vm.NextCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void Next_MigrateThrows_ShowsErrorAndStaysOnStep1()
    {
        var vm = CreateViewModel(out var fake);
        fake.NextTestResult = new ConnectionTest(true, "Kết nối thành công", true); // CSDL trống -> sẽ gọi Migrate
        vm.TestConnectionCommand.Execute(null);
        fake.MigrateException = new InvalidOperationException("mất kết nối giữa chừng");

        vm.NextCommand.Execute(null);

        vm.Step.ShouldBe(SetupWizardStep.ConnectDb);
        vm.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        vm.ErrorMessage.ShouldContain("mất kết nối giữa chừng");
    }

    [Fact]
    public void Next_MigrationFails_ShowsVietnameseError()
    {
        var vm = CreateViewModel(out var fake);
        fake.NextTestResult = new ConnectionTest(true, "Kết nối thành công", true); // CSDL trống -> sẽ gọi Migrate
        vm.TestConnectionCommand.Execute(null);
        fake.NextMigrationResult = new MigrationResult(false, [], "bảng service đã tồn tại");

        vm.NextCommand.Execute(null);

        vm.Step.ShouldBe(SetupWizardStep.ConnectDb);
        vm.ErrorMessage.ShouldBe("Không tạo/nâng cấp được bảng CSDL: bảng service đã tồn tại");
    }

    [Fact]
    public void Next_DatabaseNotEmpty_StillMigrates()
    {
        // Finding 1 rà soát toàn nhánh: phải luôn gọi Migrate (idempotent), không chỉ khi CSDL
        // trống — một lần migrate trước lỗi giữa chừng phải được thử lại ở lần kết nối sau.
        var vm = CreateViewModel(out var fake);
        fake.NextTestResult = new ConnectionTest(true, "Kết nối thành công", false); // CSDL không trống
        vm.TestConnectionCommand.Execute(null);
        var migrateCalled = false;
        fake.OnMigrate = () => migrateCalled = true;

        vm.NextCommand.Execute(null);

        migrateCalled.ShouldBeTrue();
    }

    [Fact]
    public void Next_CreateFirstAdminThrows_ShowsErrorAndStaysOnStep2()
    {
        var vm = CreateViewModel(out var fake);
        fake.NextTestResult = new ConnectionTest(true, "Kết nối thành công", false);
        vm.TestConnectionCommand.Execute(null);
        fake.NeedsFirstAdminResult = true;
        vm.NextCommand.Execute(null); // sang bước 2
        vm.Step.ShouldBe(SetupWizardStep.FirstAdmin);

        vm.AdminUsername = "quantri";
        vm.AdminFullName = "Nguyễn Văn Thử";
        vm.AdminPassword = "MatKhau123";
        vm.AdminPasswordConfirm = "MatKhau123";
        fake.CreateFirstAdminException = new InvalidOperationException("Tên đăng nhập đã tồn tại");

        vm.NextCommand.Execute(null);

        vm.Step.ShouldBe(SetupWizardStep.FirstAdmin);
        vm.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        vm.ErrorMessage.ShouldContain("Tên đăng nhập đã tồn tại");
    }
}
