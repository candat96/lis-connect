using Lis.App.ViewModels.Settings;
using Lis.Data.Local;
using Lis.Data.Setup;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

public class DbConnectionViewModelTests
{
    private static readonly LocalConfig ExistingConfig = new(
        new DbSettings("localhost", 5432, "lis", "lis_user", "mat-khau-cu", false), "XN-01");

    private static DbConnectionViewModel CreateViewModel(out FakeSetupService setup, out LocalConfigStore store, out string directory)
    {
        setup = new FakeSetupService();
        directory = Path.Combine(Path.GetTempPath(), "lis-connect-tests", Guid.NewGuid().ToString());
        store = new LocalConfigStore(directory, new DevKeySecretProtector(directory));
        return new DbConnectionViewModel(setup, store, ExistingConfig);
    }

    [Fact]
    public void Save_DisabledUntilTestOk()
    {
        var viewModel = CreateViewModel(out var setup, out _, out _);

        viewModel.SaveCommand.CanExecute(null).ShouldBeFalse();

        setup.NextTestResult = new ConnectionTest(true, "Kết nối thành công", false);
        viewModel.TestConnectionCommand.Execute(null);

        viewModel.TestOk.ShouldBeTrue();
        viewModel.SaveCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void EditingFieldAfterSuccessfulTest_DisablesSaveAgain()
    {
        var viewModel = CreateViewModel(out var setup, out _, out _);
        setup.NextTestResult = new ConnectionTest(true, "Kết nối thành công", false);
        viewModel.TestConnectionCommand.Execute(null);
        viewModel.SaveCommand.CanExecute(null).ShouldBeTrue();

        viewModel.Host = "192.168.1.50";

        viewModel.SaveCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void Save_EmptyPassword_KeepsOldPassword()
    {
        var viewModel = CreateViewModel(out var setup, out var store, out _);
        setup.NextTestResult = new ConnectionTest(true, "Kết nối thành công", false);
        viewModel.Password = ""; // không đổi mật khẩu
        viewModel.TestConnectionCommand.Execute(null);

        viewModel.SaveCommand.Execute(null);

        var saved = store.Load();
        saved.ShouldNotBeNull();
        saved!.Db.Password.ShouldBe("mat-khau-cu");
    }

    [Fact]
    public void Save_NewPassword_IsSaved()
    {
        var viewModel = CreateViewModel(out var setup, out var store, out _);
        setup.NextTestResult = new ConnectionTest(true, "Kết nối thành công", false);
        viewModel.Password = "mat-khau-moi-123";
        viewModel.TestConnectionCommand.Execute(null);

        viewModel.SaveCommand.Execute(null);

        var saved = store.Load();
        saved!.Db.Password.ShouldBe("mat-khau-moi-123");
    }

    [Fact]
    public void Save_RaisesConfigSaved_WithWorkstationCode()
    {
        var viewModel = CreateViewModel(out var setup, out _, out _);
        setup.NextTestResult = new ConnectionTest(true, "Kết nối thành công", false);
        viewModel.WorkstationCode = "XN-02";
        viewModel.TestConnectionCommand.Execute(null);

        LocalConfig? raised = null;
        viewModel.ConfigSaved += (_, config) => raised = config;

        viewModel.SaveCommand.Execute(null);

        raised.ShouldNotBeNull();
        raised!.WorkstationCode.ShouldBe("XN-02");
    }

    [Fact]
    public void TestConnection_Failure_ShowsVietnameseMessage_AndKeepsSaveDisabled()
    {
        var viewModel = CreateViewModel(out var setup, out _, out _);
        setup.NextTestResult = new ConnectionTest(false, "Sai tài khoản hoặc mật khẩu CSDL", false);

        viewModel.TestConnectionCommand.Execute(null);

        viewModel.TestOk.ShouldBeFalse();
        viewModel.TestMessage.ShouldBe("Sai tài khoản hoặc mật khẩu CSDL");
        viewModel.SaveCommand.CanExecute(null).ShouldBeFalse();
    }
}
