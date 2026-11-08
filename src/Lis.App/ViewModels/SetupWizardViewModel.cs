using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Data;
using Lis.Data.Facility;
using Lis.Data.Local;
using Lis.Data.Setup;

namespace Lis.App.ViewModels;

/// <summary>Bước hiện tại của trình hướng dẫn cài đặt lần đầu (mục 5 bản thiết kế).</summary>
public enum SetupWizardStep
{
    ConnectDb,
    FirstAdmin,
    FacilityInfo,
}

/// <summary>
/// ViewModel cho <c>SetupWizardWindow</c>: đi qua 3 bước cài đặt lần đầu — kết nối CSDL,
/// tài khoản quản trị đầu tiên (chỉ khi CSDL chưa có tài khoản), thông tin cơ sở (chỉ khi
/// CSDL vừa được tạo mới). Nhận <see cref="ISetupService"/> qua constructor để test được
/// bằng một cài đặt giả (xem <c>SetupWizardViewModelTests</c>).
/// </summary>
public partial class SetupWizardViewModel : ObservableObject
{
    private readonly ISetupService _setupService;

    public SetupWizardViewModel(ISetupService setupService)
    {
        _setupService = setupService;
    }

    /// <summary>Báo trình hướng dẫn đã xong, kèm cấu hình cục bộ để lưu rồi mở màn đăng nhập.</summary>
    public event EventHandler<LocalConfig>? Completed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectStep))]
    [NotifyPropertyChangedFor(nameof(IsAdminStep))]
    [NotifyPropertyChangedFor(nameof(IsFacilityStep))]
    private SetupWizardStep _step = SetupWizardStep.ConnectDb;

    public bool IsConnectStep => Step == SetupWizardStep.ConnectDb;

    public bool IsAdminStep => Step == SetupWizardStep.FirstAdmin;

    public bool IsFacilityStep => Step == SetupWizardStep.FacilityInfo;

    // Bước 1: kết nối CSDL.
    [ObservableProperty]
    private string _host = "localhost";

    [ObservableProperty]
    private int _port = 5432;

    [ObservableProperty]
    private string _database = "lis";

    [ObservableProperty]
    private string _username = "lis_user";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private bool _requireSsl;

    [ObservableProperty]
    private string _workstationCode = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _connectionOk;

    [ObservableProperty]
    private string? _connectionMessage;

    partial void OnHostChanged(string value) => ResetConnectionTest();
    partial void OnPortChanged(int value) => ResetConnectionTest();
    partial void OnDatabaseChanged(string value) => ResetConnectionTest();
    partial void OnUsernameChanged(string value) => ResetConnectionTest();
    partial void OnPasswordChanged(string value) => ResetConnectionTest();
    partial void OnRequireSslChanged(bool value) => ResetConnectionTest();

    /// <summary>
    /// Sửa bất kỳ ô kết nối nào sau khi đã "Thử kết nối" thành công phải tắt "Tiếp theo" ngay —
    /// nếu không, người dùng có thể đổi mật khẩu/host rồi bấm "Tiếp theo" dựa trên kết quả thử
    /// của giá trị CŨ, kết nối CSDL bằng giá trị MỚI chưa từng được kiểm chứng.
    /// </summary>
    private void ResetConnectionTest()
    {
        ConnectionOk = false;
        ConnectionMessage = null;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _isBusy;

    private bool _needsFirstAdmin;

    // Bước 2: tài khoản quản trị đầu tiên.
    [ObservableProperty]
    private string _adminUsername = "";

    [ObservableProperty]
    private string _adminFullName = "";

    [ObservableProperty]
    private string _adminPassword = "";

    [ObservableProperty]
    private string _adminPasswordConfirm = "";

    [ObservableProperty]
    private string? _adminError;

    // Bước 3: thông tin cơ sở.
    [ObservableProperty]
    private string _facilityName = "";

    [ObservableProperty]
    private string? _facilityAddress;

    [ObservableProperty]
    private string? _facilityPhone;

    [ObservableProperty]
    private string? _facilityError;

    /// <summary>Lỗi không mong đợi (CSDL mất kết nối, lỗi tạo tài khoản/lưu thông tin cơ sở...) ở bước hiện tại.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    private DbSettings BuildDbSettings() => new(Host, Port, Database, Username, Password, RequireSsl);

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        ConnectionMessage = null;
        try
        {
            var result = await _setupService.TestAsync(BuildDbSettings());
            ConnectionOk = result.Ok;
            ConnectionMessage = result.Message;
        }
        catch (Exception ex)
        {
            // SetupService.TestAsync đã tự bắt lỗi và trả ConnectionTest — nhánh này chỉ còn lại
            // lỗi hoàn toàn không mong đợi (vd lỗi dựng DbSettings), không để crash trình hướng dẫn.
            ConnectionOk = false;
            ConnectionMessage = $"Thử kết nối lỗi: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanTestConnection() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            switch (Step)
            {
                case SetupWizardStep.ConnectDb:
                    await AdvanceFromConnectDbAsync();
                    break;
                case SetupWizardStep.FirstAdmin:
                    await AdvanceFromFirstAdminAsync();
                    break;
                case SetupWizardStep.FacilityInfo:
                    await AdvanceFromFacilityInfoAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            // Lỗi không mong đợi (mất kết nối CSDL giữa chừng, lỗi ghi dữ liệu...) — hiện cho
            // người dùng thay vì để ứng dụng crash, và ở lại bước hiện tại để họ thử lại.
            ErrorMessage = $"Có lỗi xảy ra, thử lại: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanGoNext() => ConnectionOk && !IsBusy;

    private async Task AdvanceFromConnectDbAsync()
    {
        var settings = BuildDbSettings();

        // Luôn chạy migration sau khi kết nối thành công, không chỉ khi CSDL trống: migration là
        // idempotent (DbUp tự bỏ qua script đã chạy), nên gọi lại không hại gì — nhưng nếu bỏ qua
        // khi CSDL không trống, một lần chạy trước bị lỗi giữa chừng (vd thiếu quyền tạo
        // extension) sẽ không bao giờ được thử lại vì bảng schemaversions đã tồn tại.
        var migration = _setupService.Migrate(settings);
        if (!migration.Ok)
        {
            ErrorMessage = $"Không tạo/nâng cấp được bảng CSDL: {migration.Error}";
            return;
        }

        _needsFirstAdmin = await _setupService.NeedsFirstAdminAsync(settings);

        if (_needsFirstAdmin)
        {
            Step = SetupWizardStep.FirstAdmin;
            return;
        }

        // Không chỉ dựa vào "CSDL vừa tạo mới" (_databaseEmpty) — trình hướng dẫn có thể đã bị
        // đóng giữa chừng ở một lần chạy trước (vd mất điện), sau khi tạo tài khoản quản trị
        // nhưng trước khi lưu thông tin cơ sở. Kiểm tra thẳng CSDL còn thiếu bước nào thì hiện
        // lại đúng bước đó, không bỏ qua và kết thúc trình hướng dẫn với dữ liệu thiếu.
        if (await _setupService.NeedsFacilityInfoAsync(settings))
        {
            Step = SetupWizardStep.FacilityInfo;
        }
        else
        {
            Finish(settings);
        }
    }

    private async Task AdvanceFromFirstAdminAsync()
    {
        AdminError = ValidateAdmin();
        if (AdminError is not null)
        {
            return;
        }

        var settings = BuildDbSettings();
        await _setupService.CreateFirstAdminAsync(settings, AdminUsername, AdminFullName, AdminPassword);

        if (await _setupService.NeedsFacilityInfoAsync(settings))
        {
            Step = SetupWizardStep.FacilityInfo;
        }
        else
        {
            Finish(settings);
        }
    }

    private async Task AdvanceFromFacilityInfoAsync()
    {
        if (string.IsNullOrWhiteSpace(FacilityName))
        {
            FacilityError = "Nhập tên cơ sở";
            return;
        }

        FacilityError = null;
        var settings = BuildDbSettings();
        var facilityStore = new FacilityStore(Db.Create(settings.ToConnectionString()));
        await facilityStore.SaveAsync(new FacilityInfo(
            FacilityName,
            string.IsNullOrWhiteSpace(FacilityAddress) ? null : FacilityAddress,
            string.IsNullOrWhiteSpace(FacilityPhone) ? null : FacilityPhone,
            Logo: null,
            BarcodePrefix: "9",
            BarcodeDatePattern: "yyMMdd",
            BarcodeSeqDigits: 3,
            BarcodeMode: "single"));

        Finish(settings);
    }

    private string? ValidateAdmin()
    {
        if (string.IsNullOrWhiteSpace(AdminUsername))
        {
            return "Nhập tên đăng nhập";
        }

        if (string.IsNullOrWhiteSpace(AdminFullName))
        {
            return "Nhập họ tên";
        }

        if (AdminPassword.Length < 8)
        {
            return "Mật khẩu tối thiểu 8 ký tự";
        }

        if (AdminPassword != AdminPasswordConfirm)
        {
            return "Hai lần nhập mật khẩu không khớp";
        }

        return null;
    }

    private void Finish(DbSettings settings)
    {
        var config = new LocalConfig(settings, WorkstationCode);
        Completed?.Invoke(this, config);
    }
}
