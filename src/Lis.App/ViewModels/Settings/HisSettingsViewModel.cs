using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Data.Auth;
using Lis.Data.His;

namespace Lis.App.ViewModels.Settings;

/// <summary>
/// Tab "Kết nối HIS" của màn Cài đặt — chỉ hiện cho vai trò <c>quan-tri</c> (xem
/// <c>SettingsViewModel</c>/<c>MainViewModel</c>). Đợt 1 (Task 14): chỉ lưu cấu hình và cho
/// "Thử kết nối" — kéo chỉ định/đẩy kết quả/đồng bộ nhân sự thuộc đợt 3.
///
/// Mật khẩu/khóa API (<see cref="Secret"/>) để trống nghĩa là giữ bí mật đã lưu (không hiện
/// lại bí mật cũ ra màn hình, giống <c>DbConnectionViewModel</c>).
/// </summary>
public partial class HisSettingsViewModel : ObservableObject
{
    private readonly HisProfileStore _store;
    private readonly HisConnectionTester _tester;
    private readonly CurrentUser _currentUser;
    private bool _hasExistingSecret;

    public HisSettingsViewModel(HisProfileStore store, HisConnectionTester tester, CurrentUser currentUser)
    {
        _store = store;
        _tester = tester;
        _currentUser = currentUser;
    }

    /// <summary>Các kiểu xác thực hợp lệ (xem CHECK của cột <c>auth_type</c>).</summary>
    public static IReadOnlyList<string> AuthTypes { get; } = ["none", "api-key", "basic", "bearer-login"];

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _baseUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApiKeyFields))]
    [NotifyPropertyChangedFor(nameof(ShowBasicFields))]
    [NotifyPropertyChangedFor(nameof(ShowBearerLoginFields))]
    [NotifyPropertyChangedFor(nameof(ShowSecretHint))]
    private string _authType = "none";

    [ObservableProperty]
    private string _apiKeyHeader = "";

    /// <summary>Khóa API hoặc mật khẩu đăng nhập HIS. Để trống: giữ bí mật đã lưu.</summary>
    [ObservableProperty]
    private string _secret = "";

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _loginPath = "";

    [ObservableProperty]
    private string _loginBodyTemplate = "";

    [ObservableProperty]
    private string _tokenJsonPath = "";

    [ObservableProperty]
    private string _tokenTtlMinutesText = "";

    [ObservableProperty]
    private string _ordersPath = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _testMessage;

    [ObservableProperty]
    private bool _testOk;

    [ObservableProperty]
    private string? _successMessage;

    [ObservableProperty]
    private bool _isBusy;

    public bool ShowApiKeyFields => AuthType == "api-key";

    public bool ShowBasicFields => AuthType == "basic";

    public bool ShowBearerLoginFields => AuthType == "bearer-login";

    public bool ShowSecretHint => AuthType != "none";

    /// <summary>true nếu đã có bí mật lưu từ trước — dùng để hiện gợi ý "để trống giữ bí mật cũ".</summary>
    public bool HasExistingSecret => _hasExistingSecret;

    public string SecretHint => _hasExistingSecret
        ? "Để trống nếu không đổi — bí mật đã lưu mã hóa riêng theo máy này, mở trên máy khác phải nhập lại."
        : "Bí mật được mã hóa riêng theo máy này — mở cấu hình trên máy khác sẽ phải nhập lại.";

    partial void OnAuthTypeChanged(string value) => ResetTest();
    partial void OnBaseUrlChanged(string value) => ResetTest();
    partial void OnOrdersPathChanged(string value) => ResetTest();
    partial void OnSecretChanged(string value) => ResetTest();
    partial void OnUsernameChanged(string value) => ResetTest();
    partial void OnApiKeyHeaderChanged(string value) => ResetTest();
    partial void OnLoginPathChanged(string value) => ResetTest();
    partial void OnLoginBodyTemplateChanged(string value) => ResetTest();
    partial void OnTokenJsonPathChanged(string value) => ResetTest();

    private void ResetTest()
    {
        TestOk = false;
        TestMessage = null;
    }

    public async Task LoadAsync()
    {
        try
        {
            ErrorMessage = null;
            var profile = await _store.GetAsync() ?? HisProfile.Empty;
            ApplyProfile(profile);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không nạp được cấu hình kết nối HIS: {ex.Message}";
        }
    }

    /// <summary>Điền sẵn hồ sơ "HIS-PROMAX": xác thực api-key, đường kiểm tra kết nối là API công khai.</summary>
    [RelayCommand]
    private void ApplyHisPromaxPreset()
    {
        var preset = HisProfile.HisPromaxPreset;
        Name = preset.Name;
        AuthType = preset.AuthType;
        ApiKeyHeader = preset.ApiKeyHeader ?? "";
        OrdersPath = preset.OrdersPath ?? "";
        SuccessMessage = null;
        ErrorMessage = null;
        TestMessage = "API đặt lệnh/kết quả/nhân sự thật của HIS-PROMAX làm ở đợt 3 — mẫu này chỉ kiểm tra kết nối.";
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!TryBuildProfile(out var profile, out var buildError))
        {
            TestOk = false;
            TestMessage = buildError;
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _tester.TestAsync(profile);
            TestOk = result.Ok;
            TestMessage = result.Message;
        }
        catch (Exception ex)
        {
            TestOk = false;
            TestMessage = $"Thử kết nối lỗi: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!TryBuildProfile(out var profile, out var buildError))
        {
            ErrorMessage = buildError;
            return;
        }

        IsBusy = true;
        try
        {
            await _store.SaveAsync(profile, _currentUser.User.Id);
            _hasExistingSecret = _hasExistingSecret || !string.IsNullOrEmpty(Secret);
            Secret = "";
            OnPropertyChanged(nameof(HasExistingSecret));
            OnPropertyChanged(nameof(SecretHint));
            SuccessMessage = "Đã lưu cấu hình kết nối HIS";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lưu cấu hình kết nối HIS thất bại: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyProfile(HisProfile profile)
    {
        Enabled = profile.Enabled;
        Name = profile.Name;
        BaseUrl = profile.BaseUrl;
        AuthType = profile.AuthType;
        ApiKeyHeader = profile.ApiKeyHeader ?? "";
        Secret = "";
        _hasExistingSecret = !string.IsNullOrEmpty(profile.Secret);
        Username = profile.Username ?? "";
        LoginPath = profile.LoginPath ?? "";
        LoginBodyTemplate = profile.LoginBodyTemplate ?? "";
        TokenJsonPath = profile.TokenJsonPath ?? "";
        TokenTtlMinutesText = profile.TokenTtlMinutes?.ToString() ?? "";
        OrdersPath = profile.OrdersPath ?? "";
        OnPropertyChanged(nameof(HasExistingSecret));
        OnPropertyChanged(nameof(SecretHint));
        ResetTest();
    }

    private bool TryBuildProfile(out HisProfile profile, out string? error)
    {
        profile = HisProfile.Empty;

        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            error = "Nhập địa chỉ máy chủ HIS (base URL)";
            return false;
        }

        if (AuthType == "bearer-login" && string.IsNullOrWhiteSpace(LoginPath))
        {
            error = "Nhập đường đăng nhập (login_path) khi dùng xác thực bearer-login";
            return false;
        }

        int? tokenTtlMinutes = null;
        if (!string.IsNullOrWhiteSpace(TokenTtlMinutesText))
        {
            if (!int.TryParse(TokenTtlMinutesText, out var parsedTtl) || parsedTtl <= 0)
            {
                error = "Thời hạn token (phút) không hợp lệ";
                return false;
            }

            tokenTtlMinutes = parsedTtl;
        }

        profile = new HisProfile(
            Enabled: Enabled,
            Name: Name.Trim(),
            BaseUrl: BaseUrl.Trim(),
            AuthType: AuthType,
            ApiKeyHeader: NullIfBlank(ApiKeyHeader),
            Secret: string.IsNullOrEmpty(Secret) ? null : Secret,
            Username: NullIfBlank(Username),
            LoginPath: NullIfBlank(LoginPath),
            LoginBodyTemplate: NullIfBlank(LoginBodyTemplate),
            TokenJsonPath: NullIfBlank(TokenJsonPath),
            TokenTtlMinutes: tokenTtlMinutes,
            OrdersMode: "pull",
            OrdersPath: NullIfBlank(OrdersPath),
            PollSeconds: null,
            ResultsPath: null,
            StaffPath: null,
            PushListenPort: null,
            PushApiKeyHash: null,
            PushWorkstationCode: null);

        error = null;
        return true;
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
