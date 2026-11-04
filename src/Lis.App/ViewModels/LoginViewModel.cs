using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Data.Auth;

namespace Lis.App.ViewModels;

/// <summary>
/// ViewModel cho <c>LoginWindow</c>: nhập tên đăng nhập + mật khẩu, gọi <see cref="AuthService"/>.
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly AuthService _authService;

    public LoginViewModel(AuthService authService)
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
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanLogin() => !IsBusy && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    partial void OnUsernameChanged(string value) => LoginCommand.NotifyCanExecuteChanged();

    partial void OnPasswordChanged(string value) => LoginCommand.NotifyCanExecuteChanged();
}
