using Lis.App.ViewModels;
using Lis.Data.Auth;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>
/// Xác thực giả dùng cho test — có thể trả kết quả định sẵn hoặc ném lỗi (mô phỏng CSDL mất
/// kết nối giữa lúc đăng nhập) để kiểm tra <see cref="LoginViewModel"/> không crash.
/// </summary>
internal sealed class FakeAuthService : IAuthService
{
    public LoginResult NextResult { get; set; } = new(false, "sai", "Sai tên đăng nhập hoặc mật khẩu", null);

    public Exception? LoginException { get; set; }

    public Task<LoginResult> LoginAsync(string username, string password) =>
        LoginException is not null ? Task.FromException<LoginResult>(LoginException) : Task.FromResult(NextResult);
}

public class LoginViewModelTests
{
    [Fact]
    public async Task Login_AuthServiceThrows_ShowsErrorInsteadOfCrashing()
    {
        var fake = new FakeAuthService
        {
            LoginException = new InvalidOperationException("Connection refused (máy chủ CSDL không chạy)"),
        };
        var vm = new LoginViewModel(fake)
        {
            Username = "quan-tri",
            Password = "MatKhau123",
        };

        await vm.LoginCommand.ExecuteAsync(null);

        vm.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        vm.ErrorMessage.ShouldContain("Không kết nối được CSDL");
        vm.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task Login_WrongCredentials_ShowsMessageFromAuthService()
    {
        var fake = new FakeAuthService
        {
            NextResult = new LoginResult(false, "sai", "Sai tên đăng nhập hoặc mật khẩu", null),
        };
        var vm = new LoginViewModel(fake)
        {
            Username = "quan-tri",
            Password = "sai-mat-khau",
        };

        await vm.LoginCommand.ExecuteAsync(null);

        vm.ErrorMessage.ShouldBe("Sai tên đăng nhập hoặc mật khẩu");
    }

    [Fact]
    public async Task Login_Success_RaisesLoggedIn()
    {
        var user = new UserInfo(Guid.NewGuid(), "quan-tri", "Nguyễn Văn Thử", null, ["quan-tri"], true);
        var fake = new FakeAuthService
        {
            NextResult = new LoginResult(true, "ok", "Đăng nhập thành công", user),
        };
        var vm = new LoginViewModel(fake)
        {
            Username = "quan-tri",
            Password = "MatKhau123",
        };

        UserInfo? loggedInUser = null;
        vm.LoggedIn += (_, u) => loggedInUser = u;

        await vm.LoginCommand.ExecuteAsync(null);

        loggedInUser.ShouldBe(user);
        vm.ErrorMessage.ShouldBeNull();
    }
}
