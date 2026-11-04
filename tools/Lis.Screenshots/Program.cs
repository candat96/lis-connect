using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Lis.App.Services;
using Lis.App.ViewModels;
using Lis.App.ViewModels.Catalog;
using Lis.App.ViewModels.Reception;
using Lis.App.Views;
using Lis.App.Views.Catalog;
using Lis.App.Views.Reception;
using Lis.Core.Auth;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Local;
using Lis.Data.Orders;
using Lis.Data.Setup;

namespace Lis.Screenshots;

/// <summary>
/// Công cụ dòng lệnh dựng từng View của LIS Connect với một ViewModel đã nạp dữ liệu giả,
/// dùng Avalonia.Headless (+ Avalonia.Skia để có pixel thật) để chụp ảnh PNG kiểm tra giao
/// diện mà không cần máy có màn hình. Ảnh lưu ở <c>artifacts/screenshots/</c> (đã gitignore).
/// </summary>
internal static class Program
{
    public static void Main()
    {
        var outputDirectory = Path.Combine(FindRepoRoot(), "artifacts", "screenshots");
        Directory.CreateDirectory(outputDirectory);

        AppBuilder.Configure<ScreenshotApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
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

        Console.WriteLine($"Đã lưu ảnh vào {outputDirectory}");
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
        var orders = new OrderService(unusedDb, TimeProvider.System);
        var facilityStore = new FacilityStore(unusedDb);
        var viewModel = new MainViewModel(new CurrentUser(user), catalogStore, orders, facilityStore, new NoOpLabelPrinter());
        return new MainWindow { DataContext = viewModel };
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
        var orders = new OrderService(unusedDb, TimeProvider.System);
        var catalogStore = new CatalogStore(unusedDb);
        var facilityStore = new FacilityStore(unusedDb);
        var user = new UserInfo(Guid.NewGuid(), "tiepnhan.thu", "Lê Thị Thử", "Tiếp nhận", [Roles.TiepNhan], true);

        var viewModel = new ReceptionViewModel(orders, catalogStore, facilityStore, new CurrentUser(user), new NoOpLabelPrinter());

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
        var viewModel = new CatalogViewModel(catalogStore);

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
            var item = new ServiceListItemViewModel(service);
            viewModel.Services.Add(item);
            viewModel.FilteredServices.Add(item);
        }

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

        public Task CreateFirstAdminAsync(DbSettings settings, string username, string fullName, string password) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");
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
            Source = new Uri("avares://Lis.App/Styles/Theme.axaml"),
        });
    }
}
