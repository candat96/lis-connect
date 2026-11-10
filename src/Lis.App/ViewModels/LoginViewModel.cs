using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Data.Auth;

namespace Lis.App.ViewModels;

/// <summary>
/// ViewModel cho <c>LoginWindow</c>: nhập tên đăng nhập + mật khẩu, gọi <see cref="AuthService"/>.
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly IAuthService _authService;

    public LoginViewModel(IAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>Báo đăng nhập thành công, kèm thông tin tài khoản.</summary>
    public event EventHandler<UserInfo>? LoggedIn;

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Ghi nhớ đăng nhập trên máy này — lần mở sau vào thẳng, tới khi bấm "Đăng xuất" hoặc đổi mật khẩu.</summary>
    [ObservableProperty]
    private bool _rememberMe = true;

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _authService.LoginAsync(Username, Password);
            if (!result.Ok || result.User is null)
            {
                ErrorMessage = result.Message;
                return;
            }

            LoggedIn?.Invoke(this, result.User);
        }
        catch (Exception ex)
        {
            // CSDL mất kết nối (máy chủ tắt, sai cấu hình...) không được để crash màn đăng
            // nhập — hiện thông báo tiếng Việt và cho người dùng vào Cài đặt kết nối để sửa.
            ErrorMessage = "Không kết nối được CSDL — kiểm tra máy chủ PostgreSQL hoặc vào Cài đặt kết nối. " +
                           $"Chi tiết: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanLogin() => !IsBusy && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    partial void OnUsernameChanged(string value) => LoginCommand.NotifyCanExecuteChanged();

    partial void OnPasswordChanged(string value) => LoginCommand.NotifyCanExecuteChanged();
}
