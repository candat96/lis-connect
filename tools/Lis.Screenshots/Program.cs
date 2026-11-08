using Avalonia;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Lis.App.Services;
using Lis.App.ViewModels;
using Lis.App.ViewModels.Admin;
using Lis.App.ViewModels.Catalog;
using Lis.App.ViewModels.Instruments;
using Lis.App.ViewModels.Reception;
using Lis.App.ViewModels.Results;
using Lis.App.ViewModels.Settings;
using Lis.App.ViewModels.Unmatched;
using Lis.App.Views;
using Lis.App.Views.Admin;
using Lis.App.Views.Catalog;
using Lis.App.Views.Instruments;
using Lis.App.Views.Reception;
using Lis.App.Views.Results;
using Lis.App.Views.Settings;
using Lis.App.Views.Unmatched;
using Lis.Core.Auth;
using Lis.Core.Instruments;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.His;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Local;
using Lis.Data.Orders;
using Lis.Core.Patients;
using Lis.Core.Results;
using Lis.Data.Results;
using Lis.Data.Settings;
using Lis.Data.Setup;
using Lis.Reports.Labels;
using Lis.Reports.Printing;
using Microsoft.Extensions.Time.Testing;
using PDFtoImage;

namespace Lis.Screenshots;

/// <summary>
/// Công cụ dòng lệnh dựng từng View của LIS Connect với một ViewModel đã nạp dữ liệu giả,
/// dùng Avalonia.Headless (+ Avalonia.Skia để có pixel thật) để chụp ảnh PNG kiểm tra giao
/// diện mà không cần máy có màn hình. Ảnh lưu ở <c>artifacts/screenshots/</c> (đã gitignore).
/// Ngoài ra raster hóa một PDF tem mẫu (<see cref="LabelPdf"/>) ra <c>artifacts/labels/</c> để
/// kiểm bố cục tem.
/// </summary>
internal static class Program
{
    public static void Main()
    {
        var repoRoot = FindRepoRoot();
        var outputDirectory = Path.Combine(repoRoot, "artifacts", "screenshots");
        Directory.CreateDirectory(outputDirectory);

        AppBuilder.Configure<ScreenshotApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = "avares://LisConnect/Assets/Fonts#Be Vietnam Pro" })
            .SetupWithoutStarting();

        Capture(outputDirectory, "01-cai-dat-ket-noi-csdl.png", CreateWizardWindow(SetupWizardStep.ConnectDb, withConnectionOk: false));
        Capture(outputDirectory, "02-cai-dat-tai-khoan-quan-tri.png", CreateWizardWindow(SetupWizardStep.FirstAdmin, withConnectionOk: true));
        Capture(outputDirectory, "03-cai-dat-thong-tin-co-so.png", CreateWizardWindow(SetupWizardStep.FacilityInfo, withConnectionOk: true));
        Capture(outputDirectory, "04-dang-nhap.png", CreateLoginWindow());
        Capture(outputDirectory, "05-man-chinh.png", CreateMainWindow());

        var (catalogChildrenWindow, catalogChildrenViewModel) = CreateCatalogWindow(selectPanel: true, tabIndex: 0);
        Capture(outputDirectory, "06-danh-muc-chi-so-con.png", catalogChildrenWindow, () => ClearDetailErrors(catalogChildrenViewModel));

        var (catalogRangesWindow, catalogRangesViewModel) = CreateCatalogWindow(selectPanel: false, tabIndex: 1);
        Capture(outputDirectory, "07-danh-muc-khoang-tham-chieu.png", catalogRangesWindow, () => ClearDetailErrors(catalogRangesViewModel));

        Capture(outputDirectory, "08-tiep-nhan-sau-khi-luu.png", CreateReceptionWindow());
        Capture(outputDirectory, "09-cai-dat-may-in.png", CreatePrinterSettingsWindow());
        Capture(outputDirectory, "10-ket-qua.png", CreateResultsWindow());
        Capture(outputDirectory, "11-chua-ghep.png", CreateUnmatchedWindow());
        Capture(outputDirectory, "12-may-va-map.png", CreateInstrumentsWindow());
        var (usersWindow, usersViewModel) = CreateUsersWindow();
        Capture(outputDirectory, "13-tai-khoan.png", usersWindow, () =>
        {
            // Lệnh tải thật (gọi tự động từ constructor UsersViewModel) nhắm CSDL không có
            // thật, có thể báo lỗi muộn hơn lần ghi đè đầu — chờ một chút rồi ghi đè lại.
            ApplyFakeUsers(usersViewModel);
            Thread.Sleep(3000);
            ApplyFakeUsers(usersViewModel);
        });
        Capture(outputDirectory, "14-cai-dat-thong-tin-co-so.png", CreateFacilityInfoWindow());
        Capture(outputDirectory, "15-cai-dat-barcode-tem.png", CreateBarcodeSettingsWindow());
        Capture(outputDirectory, "16-cai-dat-ket-noi-csdl.png", CreateDbConnectionWindow());
        Capture(outputDirectory, "17-cai-dat-ket-noi-his.png", CreateHisSettingsWindow());
        Capture(outputDirectory, "19-log-tho.png", CreateRawLogWindow());
        Capture(outputDirectory, "18-danh-muc-nhap-excel.png", CreateCatalogImportResultWindow());

        Console.WriteLine($"Đã lưu ảnh vào {outputDirectory}");

        var labelDirectory = Path.Combine(repoRoot, "artifacts", "labels");
        Directory.CreateDirectory(labelDirectory);
        SaveSampleLabel(Path.Combine(labelDirectory, "09-tem-mau.png"));
        Console.WriteLine($"Đã lưu ảnh tem mẫu vào {labelDirectory}");
    }

    private static Window CreateWizardWindow(SetupWizardStep step, bool withConnectionOk)
    {
        var viewModel = new SetupWizardViewModel(new UnusedSetupService())
        {
            Host = "localhost",
            Port = 5442,
            Database = "lis",
            Username = "lis_user",
            Password = "lis_dev_pw",
            WorkstationCode = "XN-01",
            ConnectionOk = withConnectionOk,
            ConnectionMessage = withConnectionOk ? "Kết nối thành công" : null,
            Step = step,
        };

        if (step == SetupWizardStep.FirstAdmin)
        {
            viewModel.AdminUsername = "quantri";
            viewModel.AdminFullName = "Nguyễn Văn Thử";
        }

        if (step == SetupWizardStep.FacilityInfo)
        {
            viewModel.FacilityName = "Khoa Xét nghiệm — Bệnh viện Thử";
            viewModel.FacilityAddress = "123 Đường Thử, Quận Thử";
            viewModel.FacilityPhone = "0900000001";
        }

        return new SetupWizardWindow { DataContext = viewModel };
    }

    private static Window CreateLoginWindow()
    {
        var authService = new AuthService(Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused"), new PasswordHasher(), TimeProvider.System);
        var viewModel = new LoginViewModel(authService) { Username = "ktv.thu" };
        return new LoginWindow { DataContext = viewModel };
    }

    private static Window CreateMainWindow()
    {
        var unusedDb = Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused");
        var user = new UserInfo(Guid.NewGuid(), "ktv.thu", "Nguyễn Văn Thử", "Kỹ thuật viên trưởng", [Roles.QuanTri], true);
        var catalogStore = new CatalogStore(unusedDb);
        var orders = new UnusedOrderService();
        var facilityStore = new FacilityStore(unusedDb);
        var users = new UserStore(unusedDb);
        var localConfig = new LocalConfig(new DbSettings("localhost", 5432, "unused", "unused", "unused", false), "XN-01");
        var screenshotsConfigDir = Path.Combine(Path.GetTempPath(), "lis-screenshots-unused");
        var configStore = new LocalConfigStore(screenshotsConfigDir, new DevKeySecretProtector(screenshotsConfigDir));
        var hisScreenshotsDir = Path.Combine(Path.GetTempPath(), "lis-screenshots-unused-his-main");
        var hisProfileStore = new HisProfileStore(unusedDb, new DevKeySecretProtector(hisScreenshotsDir));
        var hisConnectionTester = new HisConnectionTester(new HttpClient());
        var viewModel = new MainViewModel(
            new CurrentUser(user), catalogStore, orders, facilityStore, new UnusedLabelPrinter(), new UnusedSettingStore(), new FakePrintService(),
            new SeededResultService(null, []), new UnusedReportPrinter(), new UnusedUnmatchedService(), new UnusedInstrumentStore(), users,
            new UnusedSetupService(), configStore, localConfig, hisProfileStore, hisConnectionTester,
            new UnusedCatalogExcelImporter(), new UnusedFilePicker());
        return new MainWindow { DataContext = viewModel };
    }

    /// <summary>
    /// Màn Kết quả với dữ liệu giả: ba mẫu trong danh sách (một chờ kết quả, một chờ duyệt có
    /// chỉ số bất thường, một đã duyệt), mẫu đang chọn của "Nguyễn Văn Mẫu" gồm panel Tổng
    /// phân tích tế bào máu (WBC cao, RBC thấp, HGB bình thường) và Glucose. Dữ liệu đi qua
    /// <see cref="SeededResultService"/> nên chọn mẫu trong danh sách nạp đúng bảng chỉ số
    /// mà không cần CSDL.
    /// </summary>
    private static Window CreateResultsWindow()
    {
        var sampleId = Guid.NewGuid();
        var orderedAt = new DateTimeOffset(2026, 11, 4, 7, 45, 0, TimeSpan.FromHours(7));

        var panelItemId = Guid.NewGuid();
        var rows = new List<ResultRow>
        {
            new(panelItemId, null, Guid.NewGuid(), "TPTTBM", "Tổng phân tích tế bào máu", true, 0, null, null, null, ResultFlag.None, null, null, null),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "WBC", "Bạch cầu", false, 1, "12.50", "G/L", "4.00 – 10.00", ResultFlag.High, "cho-duyet", null, "may"),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "RBC", "Hồng cầu", false, 1, "3.80", "T/L", "4.20 – 5.90", ResultFlag.Low, "cho-duyet", null, "may"),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "HGB", "Huyết sắc tố", false, 1, "142", "g/L", "130 – 170", ResultFlag.None, "cho-duyet", null, "may"),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GLU", "Glucose", false, 0, "5.6", "mmol/L", "3.9 – 6.4", ResultFlag.None, "cho-duyet", null, "tay"),
        };

        var sample = new SampleResults(
            sampleId, "9261104001", "Nguyễn Văn Mẫu", "BN2611040007", new PatientFacts('M', new DateOnly(1990, 5, 1), null),
            null, null, rows);

        var items = new List<SampleListItem>
        {
            new(Guid.NewGuid(), "9261104003", "Lê Thị Mẫu", orderedAt.AddMinutes(40), "Huyết thanh (hóa sinh, miễn dịch)", 3, 1, false, false, false),
            new(sampleId, "9261104001", "Nguyễn Văn Mẫu", orderedAt, "Máu EDTA (huyết học)", 4, 4, false, true, false),
            new(Guid.NewGuid(), "9261104002", "Trần Văn Mẫu", orderedAt.AddMinutes(-30), "Máu EDTA (huyết học)", 3, 3, true, false, false),
        };

        var user = new UserInfo(Guid.NewGuid(), "ktv.thu", "Nguyễn Văn Thử", "Kỹ thuật viên", [Roles.TruongKhoa], true);
        var viewModel = new ResultsViewModel(new SeededResultService(sample, items), new CurrentUser(user), new UnusedReportPrinter());

        viewModel.LoadAsync().GetAwaiter().GetResult();
        viewModel.SelectedSample = viewModel.Samples.First(s => s.SampleId == sampleId);

        var view = new ResultsView { DataContext = viewModel };

        return new Window
        {
            Title = "LIS Connect — Kết quả",
            Width = 1360,
            Height = 820,
            Content = view,
        };
    }

    /// <summary>
    /// Màn Chưa ghép với 3 dòng, mỗi dòng một lý do khác nhau (sinh bằng test seed, không
    /// qua CSDL): "261103005" chỉ trùng đuôi với một mẫu có thật → không có barcode; kênh
    /// "MCV" chưa map tới chỉ số nào; kênh "PLT" đã map nhưng mẫu không có chỉ định tiểu cầu.
    /// </summary>
    private static Window CreateUnmatchedWindow()
    {
        var receivedAt = new DateTimeOffset(2026, 11, 4, 9, 15, 0, TimeSpan.FromHours(7));
        var items = new List<UnmatchedItem>
        {
            new(Guid.NewGuid(), "Máy thử XN-1000", "261103005", "HGB", "140", "g/L", UnmatchedReason.NoBarcode, receivedAt, null, null),
            new(Guid.NewGuid(), "Máy thử XN-1000", "9261104002", "MCV", "88.1", "fL", UnmatchedReason.ChannelUnmapped, receivedAt.AddMinutes(5), null, null),
            new(Guid.NewGuid(), "Máy thử XN-1000", "9261104003", "PLT", "250", "G/L", UnmatchedReason.NotOrdered, receivedAt.AddMinutes(10), "PLT", "Tiểu cầu"),
        };

        var user = new UserInfo(Guid.NewGuid(), "ktv.thu", "Nguyễn Văn Thử", "Kỹ thuật viên", [Roles.KyThuatVien], true);
        // Giờ máy khớp với receivedAt ở trên — bộ lọc ngày mặc định ("hôm nay") phải ra đúng 3 dòng.
        var clock = new FakeTimeProvider(receivedAt);
        clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
        var viewModel = new UnmatchedViewModel(new SeededUnmatchedService(items), new CurrentUser(user), clock);
        viewModel.LoadAsync().GetAwaiter().GetResult();
        viewModel.SelectedRow = viewModel.Rows.First();

        var view = new UnmatchedView { DataContext = viewModel };

        return new Window
        {
            Title = "LIS Connect — Chưa ghép",
            Width = 1360,
            Height = 820,
            Content = view,
        };
    }

    /// <summary>
    /// Màn "Máy &amp; map" với một máy giả "Máy thử XN-1000" (JSON cấu hình driver/kết nối thô)
    /// đã chọn, bảng map kênh gồm WBC# (hệ số 0.001, gửi khi máy hỏi) và HGB (theo số lẻ chỉ số).
    /// </summary>
    private static Window CreateInstrumentsWindow()
    {
        var instrumentId = Guid.NewGuid();
        var instrument = new InstrumentInfo(
            instrumentId, "Máy thử XN-1000", "XN-1000", "astm-e1394", """{"frame": "lis2a2"}""", """{"type":"serial","port":"COM3","baudRate":9600,"dataBits":8,"parity":"None","stopBits":1}""", "XN-01", true);

        var wbcId = Guid.NewGuid();
        var hgbId = Guid.NewGuid();
        var channels = new List<ChannelMapItem>
        {
            new(new ChannelMap(Guid.NewGuid(), instrumentId, "WBC#", wbcId, 1, 0.001m, false, true), "WBC", "Bạch cầu"),
            new(new ChannelMap(Guid.NewGuid(), instrumentId, "HGB", hgbId, null, null, false, false), "HGB", "Huyết sắc tố"),
        };

        var user = new UserInfo(Guid.NewGuid(), "quantri.thu", "Nguyễn Văn Thử", "Quản trị", [Roles.QuanTri], true);
        var viewModel = new InstrumentsViewModel(
            new SeededInstrumentStore(instrument, channels), new UnusedServiceLookup(), new CurrentUser(user), new FakeSerialPorts());
        viewModel.LoadAsync().GetAwaiter().GetResult();
        viewModel.SelectedInstrument = viewModel.Instruments.First();
        viewModel.ChannelEditor.LoadAsync(instrumentId).GetAwaiter().GetResult();

        // Bảng dò cổng: COM3 nhận khung ASTM, COM4 im lặng, COM5 bị LIS cũ chiếm.
        viewModel.PortScanner.ListenSeconds = 10;
        viewModel.PortScanner.RefreshCommand.Execute(null);
        viewModel.PortScanner.ListenAllCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        var view = new InstrumentsView { DataContext = viewModel };

        return new Window
        {
            Title = "LIS Connect — Máy & map",
            Width = 1360,
            Height = 820,
            Content = view,
        };
    }

    /// <summary>
    /// Màn "Log thô" với log giả của một máy ASTM (sự kiện mở cổng, ENQ/ACK, các khung H/P/O/R/L,
    /// một lần mất kết nối) ghi vào thư mục tạm.
    /// </summary>
    private static Window CreateRawLogWindow()
    {
        var instrumentId = Guid.NewGuid();
        var instrument = new InstrumentInfo(
            instrumentId, "Cell-Dyn Ruby", "Abbott Cell-Dyn Ruby", "astm-e1394", null, """{"type":"serial","port":"COM4","baudRate":9600}""", "DK10.3", true);

        var root = Path.Combine(Path.GetTempPath(), "lis-screenshots-rawlog-" + Guid.NewGuid().ToString("N"));
        var logs = new RawLogStore(root);
        var t = new DateTimeOffset(DateTime.Today.AddHours(8).AddMinutes(12).AddSeconds(5));
        void Add(double seconds, RawLogDirection direction, string text, string? note = null) =>
            logs.Append(instrumentId, new RawLogEntry(t = t.AddSeconds(seconds), direction, "phien-1", System.Text.Encoding.Latin1.GetBytes(text), note));

        Add(0, RawLogDirection.Event, "", "Mở cổng COM4 · 9600 8N1");
        Add(42.3, RawLogDirection.In, "\u0005");
        Add(0.04, RawLogDirection.Out, "\u0006");
        Add(0.05, RawLogDirection.In, "\u00021H|\\^&|||CELL-DYN Ruby^1.0|||||||P|1|20261108081247\r\u000346\r\n");
        Add(0.03, RawLogDirection.Out, "\u0006");
        Add(0.05, RawLogDirection.In, "\u00022P|1||||Nguyen^Van Mau\r\u00034E\r\n");
        Add(0.03, RawLogDirection.Out, "\u0006");
        Add(0.05, RawLogDirection.In, "\u00023O|1|9261108012||^^^ALL|R\r\u000391\r\n");
        Add(0.03, RawLogDirection.Out, "\u0006");
        Add(0.05, RawLogDirection.In, "\u00024R|1|^^^WBC|7.2|10^9/L||N||F\r\u0003C2\r\n");
        Add(0.03, RawLogDirection.Out, "\u0006");
        Add(0.05, RawLogDirection.In, "\u00025R|2|^^^HGB|132|g/L||N||F\r\u0003A9\r\n");
        Add(0.03, RawLogDirection.Out, "\u0006");
        Add(0.05, RawLogDirection.In, "\u00026L|1|N\r\u000309\r\n");
        Add(0.03, RawLogDirection.Out, "\u0006");
        Add(0.05, RawLogDirection.In, "\u0004");
        Add(612.4, RawLogDirection.In, "\u00022R|3|^^^");
        Add(15.0, RawLogDirection.Event, "", "Lỗi: hết thời gian chờ khung tiếp theo (15 s), bỏ bản tin dở");

        var viewModel = new RawLogViewModel(new SeededInstrumentStore(instrument, []), logs);
        viewModel.LoadAsync().GetAwaiter().GetResult();

        return new Window
        {
            Title = "LIS Connect — Log thô",
            Width = 1360,
            Height = 820,
            Content = new RawLogView { DataContext = viewModel },
        };
    }

    /// <summary>Cổng COM giả cho ảnh chụp màn dò cổng.</summary>
    private sealed class FakeSerialPorts : ISerialPortService
    {
        public IReadOnlyList<SerialPortEntry> ListPorts() =>
            [new("COM3", "USB-SERIAL CH340"), new("COM4", "Communications Port"), new("COM5", "Prolific USB-to-Serial")];

        public Task<byte[]> ListenAsync(Lis.Core.Instruments.ConnectionSettings settings, TimeSpan duration, Action<byte[]>? onBytes, CancellationToken ct) =>
            settings.PortName switch
            {
                "COM3" => Task.FromResult(System.Text.Encoding.Latin1.GetBytes("\u0005\u00021H|\\^&|||CS-T240|||||||P|1\r\u00034F\r\n")),
                "COM5" => Task.FromException<byte[]>(new SerialPortBusyException("COM5", new UnauthorizedAccessException())),
                _ => Task.FromResult(Array.Empty<byte>()),
            };
    }

    /// <summary>
    /// Màn "Tài khoản": danh sách giả (ba người dùng, vai trò khác nhau) và form đang sửa người
    /// dùng đầu tiên. Constructor của <see cref="UsersViewModel"/> tự gọi
    /// <c>LoadUsersCommand</c> với CSDL không kết nối được — chờ nó thất bại (Postgres trả lỗi
    /// kết nối rất nhanh với "localhost" không có máy chủ) rồi ghi đè bằng dữ liệu giả.
    /// </summary>
    private static (Window Window, UsersViewModel ViewModel) CreateUsersWindow()
    {
        // "invalid-host-dung-de-chup-anh" không phân giải được (NXDOMAIN ngay, không chờ
        // timeout) — constructor của UsersViewModel tự gọi LoadUsersCommand (không await);
        // AfterShow (chạy sau Dispatcher.UIThread.RunJobs đầu, xem Capture) mới ghi dữ liệu giả
        // đè lên, lúc đó lệnh tải thật đã chắc chắn xong (lỗi phân giải tên máy chủ rất nhanh).
        var unusedDb = Db.Create("Host=invalid-host-dung-de-chup-anh;Database=unused;Username=unused;Password=unused;Timeout=1");
        var currentUser = new CurrentUser(new UserInfo(Guid.NewGuid(), "quantri.thu", "Nguyễn Văn Thử", "Quản trị", [Roles.QuanTri], true));
        var viewModel = new UsersViewModel(new UserStore(unusedDb), currentUser);

        var view = new UsersView { DataContext = viewModel };

        var window = new Window
        {
            Title = "LIS Connect — Tài khoản",
            Width = 1100,
            Height = 700,
            Content = view,
        };

        return (window, viewModel);
    }

    private static void ApplyFakeUsers(UsersViewModel viewModel)
    {
        viewModel.ErrorMessage = "";
        viewModel.Users =
        [
            new UserListItemViewModel(Guid.NewGuid(), "quantri.thu", "Nguyễn Văn Thử", "Quản trị khoa", [Roles.QuanTri], true),
            new UserListItemViewModel(Guid.NewGuid(), "ktv.mau", "Trần Thị Mẫu", "Kỹ thuật viên trưởng", [Roles.KyThuatVien], true),
            new UserListItemViewModel(Guid.NewGuid(), "tiepnhan.thu", "Lê Văn Thử", "Tiếp nhận", [Roles.TiepNhan], false),
        ];
        viewModel.SelectedUser = viewModel.Users[0];
        viewModel.SelectUserCommand.Execute(null);
    }

    /// <summary>Tab "Thông tin cơ sở" của màn Cài đặt, với thông tin cơ sở mẫu đã nạp.</summary>
    private static Window CreateFacilityInfoWindow()
    {
        var unusedDb = Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused");
        var viewModel = new FacilitySettingsViewModel(new FacilityStore(unusedDb))
        {
            Name = "Khoa Xét nghiệm — Bệnh viện Thử",
            Address = "123 Đường Thử, Quận Thử, TP. Thử",
            Phone = "0900000001",
        };

        var view = new FacilityInfoView { DataContext = viewModel };

        return new Window
        {
            Title = "LIS Connect — Cài đặt — Thông tin cơ sở",
            Width = 900,
            Height = 560,
            Content = view,
        };
    }

    /// <summary>Tab "Barcode & tem" của màn Cài đặt, với cấu hình mặc định và tem mẫu xem trước.</summary>
    private static Window CreateBarcodeSettingsWindow()
    {
        var unusedDb = Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused");
        var viewModel = new FacilitySettingsViewModel(new FacilityStore(unusedDb));

        var view = new BarcodeSettingsView { DataContext = viewModel };

        return new Window
        {
            Title = "LIS Connect — Cài đặt — Barcode & tem",
            Width = 900,
            Height = 480,
            Content = view,
        };
    }

    /// <summary>Tab "Kết nối CSDL" của màn Cài đặt, sau một lần "Thử kết nối" thành công.</summary>
    private static Window CreateDbConnectionWindow()
    {
        var localConfig = new LocalConfig(new DbSettings("db-01.noi-bo", 5432, "lis", "lis_user", "***", false), "XN-01");
        var screenshotsConfigDir = Path.Combine(Path.GetTempPath(), "lis-screenshots-unused-db");
        var configStore = new LocalConfigStore(screenshotsConfigDir, new DevKeySecretProtector(screenshotsConfigDir));
        var viewModel = new DbConnectionViewModel(new UnusedSetupService(), configStore, localConfig)
        {
            TestMessage = "Kết nối thành công",
            TestOk = true,
        };

        var view = new DbConnectionView { DataContext = viewModel };

        return new Window
        {
            Title = "LIS Connect — Cài đặt — Kết nối CSDL",
            Width = 900,
            Height = 620,
            Content = view,
        };
    }

    /// <summary>Tab "Kết nối HIS" của màn Cài đặt, biến thể bearer-login đã điền sẵn và thử kết nối thành công.</summary>
    private static Window CreateHisSettingsWindow()
    {
        var unusedDb = Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused");
        var screenshotsDir = Path.Combine(Path.GetTempPath(), "lis-screenshots-unused-his");
        var store = new HisProfileStore(unusedDb, new DevKeySecretProtector(screenshotsDir));
        var tester = new HisConnectionTester(new HttpClient());
        var currentUser = new CurrentUser(new UserInfo(Guid.NewGuid(), "quantri.thu", "Nguyễn Văn Thử", "Quản trị", [Roles.QuanTri], true));

        var viewModel = new HisSettingsViewModel(store, tester, currentUser)
        {
            Enabled = true,
            Name = "HIS Bệnh viện Thử",
            BaseUrl = "https://his.benhvien-thu.vn",
            AuthType = "bearer-login",
            Username = "tich-hop-lis",
            LoginPath = "/api/auth/dang-nhap",
            LoginBodyTemplate = "{\"username\":\"{username}\",\"password\":\"{password}\"}",
            TokenJsonPath = "data.token",
            TokenTtlMinutesText = "60",
            OrdersPath = "/api/chi-dinh",
            TestMessage = "Kết nối HIS thành công (mã HTTP 200)",
            TestOk = true,
        };

        var view = new HisSettingsView { DataContext = viewModel };

        return new Window
        {
            Title = "LIS Connect — Cài đặt — Kết nối HIS",
            Width = 900,
            Height = 760,
            Content = view,
        };
    }

    /// <summary>
    /// Màn Cài đặt — mục Máy in, như trên Windows có hai máy in đã cài: máy in tem đã chọn,
    /// máy in phiếu đã chọn, kích thước tem mặc định 50 × 25. Giá trị gán thẳng vào ViewModel,
    /// không gọi <c>LoadAsync</c> (không có CSDL).
    /// </summary>
    private static Window CreatePrinterSettingsWindow()
    {
        var printService = new FakePrintService();
        var viewModel = new PrinterSettingsViewModel(new UnusedSettingStore(), printService)
        {
            Printers = printService.Printers(),
            LabelPrinter = "Xprinter XP-350B",
            ReportPrinter = "HP LaserJet Thử",
            SuccessMessage = "Đã lưu cài đặt máy in",
        };

        var view = new PrinterSettingsView { DataContext = viewModel };

        return new Window
        {
            Title = "LIS Connect — Cài đặt",
            Width = 900,
            Height = 420,
            Content = view,
        };
    }

    /// <summary>Raster hóa tem mẫu 50 × 25 mm (300 DPI) bằng PDFtoImage — PDFium chỉ có bản native cho Windows/Linux/macOS.</summary>
    private static void SaveSampleLabel(string path)
    {
        var pdf = LabelPdf.Render([PrinterSettingsViewModel.TestLabel(new DateOnly(2026, 11, 4))], LabelSize.Default);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            using var stream = File.Create(path);
            Conversion.SavePng(stream, pdf, options: new RenderOptions(Dpi: 300));
            return;
        }

        throw new PlatformNotSupportedException("PDFium chỉ có bản cho Windows, Linux và macOS");
    }

    /// <summary>
    /// Màn Tiếp nhận sau khi lưu: người bệnh giả "Nguyễn Văn Mẫu" đã chọn, một panel (3 chỉ
    /// số, loại mẫu EDTA) + một dịch vụ huyết thanh đã chọn, và bảng barcode đã cấp cho thấy
    /// đúng 2 mẫu (2 barcode) — một cho nhóm EDTA, một cho nhóm huyết thanh. Nạp danh mục
    /// trực tiếp qua <see cref="ReceptionViewModel.SeedCatalog"/>, không gọi CSDL thật.
    /// </summary>
    private static Window CreateReceptionWindow()
    {
        var unusedDb = Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused");
        var orders = new UnusedOrderService();
        var catalogStore = new CatalogStore(unusedDb);
        var facilityStore = new FacilityStore(unusedDb);
        var user = new UserInfo(Guid.NewGuid(), "tiepnhan.thu", "Lê Thị Thử", "Tiếp nhận", [Roles.TiepNhan], true);

        var viewModel = new ReceptionViewModel(orders, catalogStore, facilityStore, new CurrentUser(user), new UnusedLabelPrinter());

        var edtaId = Guid.NewGuid();
        var serumId = Guid.NewGuid();

        var wbcId = Guid.NewGuid();
        var rbcId = Guid.NewGuid();
        var hgbId = Guid.NewGuid();
        var wbc = new ServiceInfo(wbcId, "WBC", "Bạch cầu", "Huyết học", edtaId, "G/L", "so", 2, 2, false, false, true, null, []);
        var rbc = new ServiceInfo(rbcId, "RBC", "Hồng cầu", "Huyết học", edtaId, "T/L", "so", 2, 3, false, false, true, null, []);
        var hgb = new ServiceInfo(hgbId, "HGB", "Huyết sắc tố", "Huyết học", edtaId, "g/L", "so", 0, 4, false, false, true, null, []);
        var panel = new ServiceInfo(Guid.NewGuid(), "TPTTBM", "Tổng phân tích tế bào máu", "Huyết học",
            edtaId, null, "so", null, 1, true, false, true, null, [wbcId, rbcId, hgbId]);
        var glu = new ServiceInfo(Guid.NewGuid(), "GLU", "Glucose", "Hóa sinh", serumId, "mmol/L", "so", 1, 5, false, false, true, null, []);

        viewModel.SeedCatalog(
            [panel, wbc, rbc, hgb, glu],
            new Dictionary<Guid, string> { [edtaId] = "Máu EDTA (huyết học)", [serumId] = "Huyết thanh (hóa sinh, miễn dịch)" },
            "per-specimen");

        viewModel.PickPatientCommand.Execute(new PatientHit(
            Guid.NewGuid(), "BN2611040007", "Nguyễn Văn Mẫu", new DateOnly(1990, 5, 1), null, 'M', "0900000099"));

        viewModel.SelectedServices.Add(new SelectedServiceViewModel(panel, _ => { }));
        viewModel.SelectedServices.Add(new SelectedServiceViewModel(glu, _ => { }));
        viewModel.GroupPreview.Add(new SampleGroupPreviewViewModel("Máu EDTA (huyết học)", ["Bạch cầu", "Hồng cầu", "Huyết sắc tố"]));
        viewModel.GroupPreview.Add(new SampleGroupPreviewViewModel("Huyết thanh (hóa sinh, miễn dịch)", ["Glucose"]));

        viewModel.IssuedSamples.Add(new CreatedSample(Guid.NewGuid(), "9261104001", "Máu EDTA (huyết học)", ["Bạch cầu", "Hồng cầu", "Huyết sắc tố"]));
        viewModel.IssuedSamples.Add(new CreatedSample(Guid.NewGuid(), "9261104002", "Huyết thanh (hóa sinh, miễn dịch)", ["Glucose"]));

        var view = new ReceptionView { DataContext = viewModel };

        return new Window
        {
            Title = "LIS Connect — Tiếp nhận",
            Width = 1360,
            Height = 820,
            Content = view,
        };
    }

    /// <summary>
    /// Màn Danh mục với dữ liệu giả: panel "Tổng phân tích tế bào máu" gồm 3 chỉ số con
    /// WBC/RBC/HGB, và khoảng tham chiếu của HGB gồm nam/nữ (từ 15 tuổi) và trẻ sơ sinh
    /// (0-28 ngày), một máy giả "Sysmex XN-1000" trong danh sách chọn máy. Gán trực tiếp vào
    /// các collection của <see cref="CatalogViewModel"/> và các editor con — không gọi
    /// <see cref="CatalogStore"/> thật để nạp dữ liệu (xem ghi chú ở
    /// <c>CatalogViewModel.OnSelectedServiceChanged</c>). Gán <see cref="CatalogViewModel.SelectedService"/>
    /// vẫn kéo theo <c>ServiceList.SelectionChanged</c> trong <c>CatalogView</c> gọi
    /// <c>LoadSelectedDetailCommand</c> một lần — lệnh này thật sự thử tải khoảng tham
    /// chiếu/quy đổi định tính từ CSDL (thất bại ngay vì không có máy chủ), nên trả thêm
    /// <see cref="CatalogViewModel"/> để gọi nơi chụp ảnh xóa lỗi đó sau khi nó đã hiện ra
    /// (xem <see cref="ClearDetailErrors"/>).
    /// </summary>
    private static (Window Window, CatalogViewModel ViewModel) CreateCatalogWindow(bool selectPanel, int tabIndex)
    {
        var catalogStore = new CatalogStore(Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused"));
        var admin = new CurrentUser(new UserInfo(Guid.NewGuid(), "quantri.thu", "Nguyễn Văn Thử", "Quản trị", [Roles.QuanTri], true));
        var viewModel = new CatalogViewModel(catalogStore, new UnusedCatalogExcelImporter(), new UnusedFilePicker(), admin);

        var edta = new SpecimenTypeItem(Guid.NewGuid(), "EDTA", "Máu EDTA (huyết học)");
        viewModel.SpecimenTypes.Add(edta);

        var instruments = new List<InstrumentItem> { new(Guid.NewGuid(), "Sysmex XN-1000") };
        viewModel.RangesEditor.SetInstruments(instruments);
        viewModel.QualitativeEditor.SetInstruments(instruments);

        var wbcId = Guid.NewGuid();
        var rbcId = Guid.NewGuid();
        var hgbId = Guid.NewGuid();

        var wbc = new ServiceInfo(wbcId, "WBC", "Bạch cầu", "Huyết học", edta.Id, "G/L", "so", 2, 2, false, false, true, null, []);
        var rbc = new ServiceInfo(rbcId, "RBC", "Hồng cầu", "Huyết học", edta.Id, "T/L", "so", 2, 3, false, false, true, null, []);
        var hgb = new ServiceInfo(hgbId, "HGB", "Huyết sắc tố", "Huyết học", edta.Id, "g/L", "so", 0, 4, false, false, true, null, []);
        var panel = new ServiceInfo(Guid.NewGuid(), "TPTTBM", "Tổng phân tích tế bào máu", "Huyết học",
            edta.Id, null, "so", null, 1, true, false, true, null, [wbcId, rbcId, hgbId]);

        foreach (var service in new[] { panel, wbc, rbc, hgb })
        {
            viewModel.Services.Add(new ServiceListItemViewModel(service));
        }

        viewModel.ApplyFilter();

        // SelectedService không gọi CSDL (xem CatalogViewModel.OnSelectedServiceChanged) — chỉ
        // nạp form sửa dịch vụ và danh sách chỉ số con (với panel) từ dữ liệu đã có trong
        // Services, nên gán trực tiếp ở đây an toàn, không cần Db thật.
        var selected = selectPanel ? panel : hgb;
        viewModel.SelectedService = viewModel.Services.First(s => s.Id == selected.Id);

        if (!selectPanel)
        {
            var ranges = viewModel.RangesEditor.Ranges;
            ranges.Add(new ReferenceRangeRowViewModel(viewModel.RangesEditor.Instruments, _ => { })
            {
                Sex = "M", AgeFromText = "15", AgeFromUnit = AgeUnit.Nam,
                LowText = "130", HighText = "170", CriticalLowText = "70", CriticalHighText = "200",
            });
            ranges.Add(new ReferenceRangeRowViewModel(viewModel.RangesEditor.Instruments, _ => { })
            {
                Sex = "F", AgeFromText = "15", AgeFromUnit = AgeUnit.Nam,
                LowText = "120", HighText = "150", CriticalLowText = "70", CriticalHighText = "200",
            });
            ranges.Add(new ReferenceRangeRowViewModel(viewModel.RangesEditor.Instruments, _ => { })
            {
                Sex = null, AgeFromText = "0", AgeFromUnit = AgeUnit.Ngay, AgeToText = "28", AgeToUnit = AgeUnit.Ngay,
                LowText = "140", HighText = "220", CriticalLowText = "90", CriticalHighText = "250",
            });
        }

        var view = new CatalogView { DataContext = viewModel };
        view.SelectTab(tabIndex);

        var window = new Window
        {
            Title = "LIS Connect — Danh mục",
            Width = 1360,
            Height = 780,
            Content = view,
        };

        return (window, viewModel);
    }

    /// <summary>Màn Danh mục với panel kết quả "Nhập Excel" đang hiện (dryRun thành công) — gán trực tiếp các trường kết quả, không gọi lệnh thật.</summary>
    private static Window CreateCatalogImportResultWindow()
    {
        var (window, viewModel) = CreateCatalogWindow(selectPanel: true, tabIndex: 0);
        ClearDetailErrors(viewModel);

        viewModel.IsImportResultVisible = true;
        viewModel.ImportResultMessage = "Sẽ tạo 12, cập nhật 3.";

        return window;
    }

    /// <summary>Xóa lỗi do <c>LoadSelectedDetailCommand</c> thật sự thử (và không) kết nối CSDL khi gán <see cref="CatalogViewModel.SelectedService"/> cho ảnh chụp — xem ghi chú ở <see cref="CreateCatalogWindow"/>.</summary>
    private static void ClearDetailErrors(CatalogViewModel viewModel)
    {
        viewModel.RangesEditor.Errors = [];
        viewModel.QualitativeEditor.ErrorMessage = null;
    }

    private static void Capture(string outputDirectory, string fileName, Window window, Action? afterShow = null)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();

        if (afterShow is not null)
        {
            afterShow();
            Dispatcher.UIThread.RunJobs();
        }

        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(outputDirectory, fileName), new PngBitmapEncoderOptions());

        window.Close();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LisConnect.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Không tìm được gốc repo (LisConnect.slnx).");
    }

    /// <summary>ISetupService giả — các màn chụp ảnh không gọi tới CSDL thật.</summary>
    private sealed class UnusedSetupService : ISetupService
    {
        public Task<ConnectionTest> TestAsync(DbSettings settings) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không thử kết nối thật.");

        public MigrationResult Migrate(DbSettings settings) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không chạy migration thật.");

        public Task<bool> NeedsFirstAdminAsync(DbSettings settings) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task<bool> NeedsFacilityInfoAsync(DbSettings settings) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task CreateFirstAdminAsync(DbSettings settings, string username, string fullName, string password) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");
    }

    /// <summary>IOrderService giả — các màn chụp ảnh không tìm người bệnh/tạo chỉ định thật.</summary>
    private sealed class UnusedOrderService : IOrderService
    {
        public Task<IReadOnlyList<PatientHit>> FindPatientsAsync(string query) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không tìm người bệnh thật.");

        public Task<OrderCreated> CreateAsync(NewOrder order, Guid actorId) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không tạo chỉ định thật.");
    }

    /// <summary>IResultService giả: trả danh sách mẫu và một mẫu đã dựng sẵn; không ghi gì.</summary>
    private sealed class SeededResultService(SampleResults? sample, IReadOnlyList<SampleListItem> items) : IResultService
    {
        public Task<IReadOnlyList<SampleListItem>> ListAsync(DateOnly from, DateOnly to, string? status, string? query) =>
            Task.FromResult(items);

        public Task<SampleResults> GetAsync(Guid sampleId) =>
            Task.FromResult(sample ?? throw new NotSupportedException("Chỉ dùng để chụp ảnh."));

        public Task<OrderHeader> GetOrderHeaderAsync(Guid sampleId) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task<IReadOnlyList<Guid>> GetOrderSampleIdsAsync(Guid sampleId) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task SetManualAsync(Guid orderItemId, string value, Guid actorId, string? reason) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không ghi kết quả thật.");

        public Task ApproveAsync(Guid sampleId, Guid actorId) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task UnapproveAsync(Guid sampleId, Guid actorId, string reason) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task AcceptPendingAsync(Guid resultId, Guid actorId) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task RejectPendingAsync(Guid resultId, Guid actorId) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task<bool> TryAutoApproveAsync(Guid sampleId) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");
    }

    /// <summary>ILabelPrinter giả — các màn chụp ảnh không in tem.</summary>
    private sealed class UnusedLabelPrinter : ILabelPrinter
    {
        public Task PrintAsync(IReadOnlyList<LabelData> labels) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không in tem.");
    }

    /// <summary>IReportPrinter giả — các màn chụp ảnh không in phiếu kết quả.</summary>
    private sealed class UnusedReportPrinter : IReportPrinter
    {
        public Task PrintAsync(Guid sampleId) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không in phiếu.");
    }

    /// <summary>ISettingStore giả — các màn chụp ảnh không đọc/ghi bảng setting.</summary>
    private sealed class UnusedSettingStore : ISettingStore
    {
        public Task<string?> GetAsync(string key) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không đọc cài đặt thật.");

        public Task SetAsync(string key, string? value) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không ghi cài đặt thật.");
    }

    /// <summary>IPrintService giả: liệt kê hai máy in giả như trên Windows, không in thật.</summary>
    private sealed class FakePrintService : IPrintService
    {
        public Task PrintAsync(byte[] pdf, string? printerName, CancellationToken ct = default) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không in thật.");

        public IReadOnlyList<string> Printers() => ["Xprinter XP-350B", "HP LaserJet Thử"];
    }

    /// <summary>IUnmatchedService giả dùng cho ảnh màn chính (placeholder) — không gọi tới thật.</summary>
    private sealed class UnusedUnmatchedService : IUnmatchedService
    {
        public Task<IReadOnlyList<UnmatchedItem>> ListOpenAsync(DateOnly from, DateOnly to) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task<IReadOnlyList<SampleLeafItem>> SampleItemsAsync(string barcode) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task AssignAsync(Guid unmatchedId, Guid orderItemId, Guid actorId) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task DismissAsync(Guid unmatchedId, Guid actorId, string reason) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");
    }

    /// <summary>IUnmatchedService giả: trả danh sách đã dựng sẵn (xem <see cref="CreateUnmatchedWindow"/>); không ghi gì.</summary>
    private sealed class SeededUnmatchedService(IReadOnlyList<UnmatchedItem> items) : IUnmatchedService
    {
        public Task<IReadOnlyList<UnmatchedItem>> ListOpenAsync(DateOnly from, DateOnly to) => Task.FromResult(items);

        public Task<IReadOnlyList<SampleLeafItem>> SampleItemsAsync(string barcode) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không tra mẫu thật.");

        public Task AssignAsync(Guid unmatchedId, Guid orderItemId, Guid actorId) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không gán thật.");

        public Task DismissAsync(Guid unmatchedId, Guid actorId, string reason) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không bỏ qua thật.");
    }

    /// <summary>IInstrumentStore giả dùng cho ảnh màn chính (placeholder) — không gọi tới thật.</summary>
    private sealed class UnusedInstrumentStore : IInstrumentStore
    {
        public Task<IReadOnlyList<InstrumentInfo>> ListAsync() => throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task<Guid> SaveAsync(InstrumentInfo instrument) => throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task<IReadOnlyList<ChannelMapItem>> ChannelsAsync(Guid instrumentId) => throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task<Guid> SaveChannelAsync(ChannelMap channel) => throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task DeleteChannelAsync(Guid channelId) => throw new NotSupportedException("Chỉ dùng để chụp ảnh.");
    }

    /// <summary>IInstrumentStore giả: một máy và bảng map kênh dựng sẵn (xem <see cref="CreateInstrumentsWindow"/>); không ghi gì.</summary>
    private sealed class SeededInstrumentStore(InstrumentInfo instrument, IReadOnlyList<ChannelMapItem> channels) : IInstrumentStore
    {
        public Task<IReadOnlyList<InstrumentInfo>> ListAsync() => Task.FromResult<IReadOnlyList<InstrumentInfo>>([instrument]);

        public Task<Guid> SaveAsync(InstrumentInfo instrument) => throw new NotSupportedException("Chỉ dùng để chụp ảnh, không lưu máy thật.");

        public Task<IReadOnlyList<ChannelMapItem>> ChannelsAsync(Guid instrumentId) => Task.FromResult(channels);

        public Task<Guid> SaveChannelAsync(ChannelMap channel) => throw new NotSupportedException("Chỉ dùng để chụp ảnh, không lưu map kênh thật.");

        public Task DeleteChannelAsync(Guid channelId) => throw new NotSupportedException("Chỉ dùng để chụp ảnh, không xóa map kênh thật.");
    }

    /// <summary>IServiceLookup giả — các màn chụp ảnh không tra chỉ số theo mã qua CSDL thật.</summary>
    private sealed class UnusedServiceLookup : IServiceLookup
    {
        public Task<ServiceInfo?> FindByCodeAsync(string code) => throw new NotSupportedException("Chỉ dùng để chụp ảnh.");
    }

    private sealed class UnusedCatalogExcelImporter : ICatalogExcelImporter
    {
        public Task<ImportReport> ImportAsync(Stream xlsx, bool dryRun) => throw new NotSupportedException("Chỉ dùng để chụp ảnh.");
    }

    private sealed class UnusedFilePicker : IFilePicker
    {
        public Task<PickedFile?> PickExcelFileAsync() => throw new NotSupportedException("Chỉ dùng để chụp ảnh.");
    }
}

/// <summary>Application tối giản cho công cụ chụp ảnh: chỉ nạp FluentTheme + Theme.axaml của Lis.App.</summary>
internal sealed class ScreenshotApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new global::Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Lis.Screenshots/"))
        {
            Source = new Uri("avares://LisConnect/Styles/Theme.axaml"),
        });
    }
}
