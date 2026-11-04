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

    public Task<ConnectionTest> TestAsync(DbSettings settings) => Task.FromResult(NextTestResult);

    public MigrationResult Migrate(DbSettings settings) => new(true, [], null);

    public Task<bool> NeedsFirstAdminAsync(DbSettings settings) => Task.FromResult(false);

    public Task CreateFirstAdminAsync(DbSettings settings, string username, string fullName, string password) =>
        Task.CompletedTask;
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
}
