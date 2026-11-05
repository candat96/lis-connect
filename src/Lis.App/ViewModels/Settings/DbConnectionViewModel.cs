using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Data.Local;
using Lis.Data.Setup;

namespace Lis.App.ViewModels.Settings;

/// <summary>
/// Mục "Kết nối CSDL" của màn Cài đặt — chỉ hiện cho vai trò <c>quan-tri</c> (xem
/// <c>SettingsViewModel</c>). Sửa <see cref="DbSettings"/> và mã máy trạm lưu trong
/// <c>config.json</c> (<see cref="LocalConfigStore"/>). Bắt buộc "Thử kết nối" thành công với
/// đúng các giá trị đang gõ trước khi "Lưu" được bật — sửa bất kỳ ô nào sau khi thử lại tắt
/// "Lưu" ngay (xem các <c>OnXChanged</c>), để không lưu nhầm cấu hình chưa kiểm chứng.
/// Mật khẩu để trống nghĩa là giữ mật khẩu cũ (không hiện mật khẩu đã lưu ra màn hình).
/// </summary>
public partial class DbConnectionViewModel : ObservableObject
{
    private readonly ISetupService _setupService;
    private readonly LocalConfigStore _configStore;
    private readonly string _existingPassword;

    /// <summary>Báo đã lưu cấu hình CSDL mới — nơi gọi (MainViewModel/App) cần đăng xuất và mở lại màn đăng nhập.</summary>
    public event EventHandler<LocalConfig>? ConfigSaved;

    public DbConnectionViewModel(ISetupService setupService, LocalConfigStore configStore, LocalConfig current)
    {
        _setupService = setupService;
        _configStore = configStore;
        _existingPassword = current.Db.Password;

        _host = current.Db.Host;
        _portText = current.Db.Port.ToString();
        _database = current.Db.Database;
        _username = current.Db.Username;
        _requireSsl = current.Db.RequireSsl;
        _workstationCode = current.WorkstationCode;
    }

    [ObservableProperty]
    private string _host;

    [ObservableProperty]
    private string _portText;

    [ObservableProperty]
    private string _database;

    [ObservableProperty]
    private string _username;

    /// <summary>Để trống: giữ mật khẩu đang lưu. Không bao giờ hiện lại mật khẩu cũ ở đây.</summary>
    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private bool _requireSsl;

    [ObservableProperty]
    private string _workstationCode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _testOk;

    [ObservableProperty]
    private string? _testMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    private DbSettings? _testedSettings;

    partial void OnHostChanged(string value) => ResetTest();
    partial void OnPortTextChanged(string value) => ResetTest();
    partial void OnDatabaseChanged(string value) => ResetTest();
    partial void OnUsernameChanged(string value) => ResetTest();
    partial void OnPasswordChanged(string value) => ResetTest();
    partial void OnRequireSslChanged(bool value) => ResetTest();
    partial void OnWorkstationCodeChanged(string value) => ResetTest();

    private void ResetTest()
    {
        TestOk = false;
        TestMessage = null;
        _testedSettings = null;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        ErrorMessage = null;

        if (!TryBuildSettings(out var settings, out var buildError))
        {
            TestOk = false;
            TestMessage = buildError;
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _setupService.TestAsync(settings);
            TestMessage = result.Message;

            if (result.Ok)
            {
                TestOk = true;
                _testedSettings = settings;
            }
            else
            {
                TestOk = false;
                _testedSettings = null;
            }
        }
        catch (Exception ex)
        {
            TestOk = false;
            _testedSettings = null;
            TestMessage = $"Thử kết nối lỗi: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(TestOk))]
    private void Save()
    {
        ErrorMessage = null;

        if (!TestOk || _testedSettings is null)
        {
            ErrorMessage = "Phải thử kết nối thành công trước khi lưu";
            return;
        }

        if (string.IsNullOrWhiteSpace(WorkstationCode))
        {
            ErrorMessage = "Nhập mã máy trạm";
            return;
        }

        try
        {
            var config = new LocalConfig(_testedSettings, WorkstationCode.Trim());
            _configStore.Save(config);
            ConfigSaved?.Invoke(this, config);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lưu cấu hình kết nối thất bại: {ex.Message}";
        }
    }

    private bool TryBuildSettings(out DbSettings settings, out string? error)
    {
        settings = null!;

        if (string.IsNullOrWhiteSpace(Host))
        {
            error = "Nhập máy chủ CSDL";
            return false;
        }

        if (!int.TryParse(PortText, out var port) || port <= 0)
        {
            error = "Cổng CSDL không hợp lệ";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Database))
        {
            error = "Nhập tên CSDL";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            error = "Nhập tên đăng nhập CSDL";
            return false;
        }

        var password = string.IsNullOrEmpty(Password) ? _existingPassword : Password;

        settings = new DbSettings(Host.Trim(), port, Database.Trim(), Username.Trim(), password, RequireSsl);
        error = null;
        return true;
    }
}
