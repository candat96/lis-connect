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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _isBusy;

    private bool _databaseEmpty;
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
            _databaseEmpty = result.DatabaseEmpty;
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
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanGoNext() => ConnectionOk && !IsBusy;

    private async Task AdvanceFromConnectDbAsync()
    {
        var settings = BuildDbSettings();

        if (_databaseEmpty)
        {
            var migration = _setupService.Migrate(settings);
            if (!migration.Ok)
            {
                ConnectionOk = false;
                ConnectionMessage = $"Khởi tạo CSDL thất bại: {migration.Error}";
                return;
            }
        }

        _needsFirstAdmin = await _setupService.NeedsFirstAdminAsync(settings);

        if (_needsFirstAdmin)
        {
            Step = SetupWizardStep.FirstAdmin;
        }
        else if (_databaseEmpty)
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

        if (_databaseEmpty)
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
            BarcodeMode: "per-specimen"));

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
