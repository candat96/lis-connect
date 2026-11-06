using Dapper;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.His;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Local;
using Lis.Data.Orders;
using Lis.Data.Results;
using Lis.Data.Settings;
using Lis.Reports.Printing;

namespace Lis.App.Services;

/// <summary>
/// Các service của ứng dụng giao diện, khởi tạo từ <see cref="LocalConfig"/> đã lưu trên máy trạm:
/// kết nối CSDL (<see cref="Db"/>) và các store/service dùng chung (tài khoản, đăng nhập, cơ sở,
/// cài đặt, in ấn, kết quả).
/// </summary>
public sealed class AppServices
{
    public AppServices(LocalConfig config)
    {
        Config = config;
        Db = Db.Create(config.Db.ToConnectionString());
        Users = new UserStore(Db);
        Auth = new AuthService(Db, new PasswordHasher(), TimeProvider.System);
        Facility = new FacilityStore(Db);
        Catalog = new CatalogStore(Db);
        Orders = new OrderService(Db, TimeProvider.System);
        Settings = new SettingStore(Db);
        PrintService = new PdfPrintService();
        LabelPrinter = new LabelPrinter(Settings, PrintService);
        Results = new ResultService(Db, TimeProvider.System);
        ReportPrinter = new ReportPrinter(Results, Facility, Settings, PrintService, TimeProvider.System);
        Unmatched = new UnmatchedService(Db, TimeProvider.System);
        Instruments = new InstrumentStore(Db);
        HisProfiles = new HisProfileStore(Db, SecretProtectors.ForCurrentOs(LocalPaths.DataDirectory));
        // HisConnectionTester tự áp một CancellationTokenSource ~10s cho mỗi lần gọi, nhưng đặt
        // thêm HttpClient.Timeout ở đây làm lớp bảo vệ thứ hai — nếu có đường gọi nào sau này
        // dùng lại HttpClient này mà quên truyền CancellationToken, vẫn không treo vô hạn
        // (mặc định của HttpClient.Timeout là 100s).
        HisConnectionTester = new HisConnectionTester(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        CatalogExcelImporter = new CatalogExcelImporter(Db);
        FilePicker = new AvaloniaFilePicker();
    }

    public LocalConfig Config { get; }

    public Db Db { get; }

    public UserStore Users { get; }

    public IAuthService Auth { get; }

    public FacilityStore Facility { get; }

    public CatalogStore Catalog { get; }

    public IOrderService Orders { get; }

    public ISettingStore Settings { get; }

    public IPrintService PrintService { get; }

    public ILabelPrinter LabelPrinter { get; }

    public IResultService Results { get; }

    public IReportPrinter ReportPrinter { get; }

    public IUnmatchedService Unmatched { get; }

    public IInstrumentStore Instruments { get; }

    public HisProfileStore HisProfiles { get; }

    public HisConnectionTester HisConnectionTester { get; }

    public ICatalogExcelImporter CatalogExcelImporter { get; }

    public IFilePicker FilePicker { get; }

    /// <summary>
    /// Cập nhật máy trạm (<c>workstation.last_seen_at</c>) ngay sau khi đăng nhập thành công.
    /// Tạo dòng mới nếu máy trạm chưa có trong CSDL.
    /// </summary>
    public async Task TouchWorkstationAsync()
    {
        await using var connection = await Db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO workstation (code, name, last_seen_at)
            VALUES (@code, @code, now())
            ON CONFLICT (code) DO UPDATE SET last_seen_at = now()
            """,
            new { code = Config.WorkstationCode });
    }
}
