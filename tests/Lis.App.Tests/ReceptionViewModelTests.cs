using Lis.App.Services;
using Lis.App.ViewModels.Reception;
using Lis.Core.Auth;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Orders;
using Lis.Reports.Labels;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>Máy in tem giả: ghi lại các tem đã nhận, hoặc ném lỗi định sẵn.</summary>
internal sealed class FakeLabelPrinter : ILabelPrinter
{
    public List<IReadOnlyList<LabelData>> Jobs { get; } = [];

    public Exception? PrintException { get; set; }

    public Task PrintAsync(IReadOnlyList<LabelData> labels)
    {
        if (PrintException is not null)
        {
            return Task.FromException(PrintException);
        }

        Jobs.Add(labels);
        return Task.CompletedTask;
    }
}

/// <summary>IOrderService giả: trả kết quả tạo chỉ định định sẵn, ghi lại chỉ định đã nhận.</summary>
internal sealed class FakeOrderService : IOrderService
{
    public OrderCreated? NextCreated { get; set; }

    public NewOrder? LastOrder { get; private set; }

    public Task<IReadOnlyList<PatientHit>> FindPatientsAsync(string query) =>
        Task.FromResult<IReadOnlyList<PatientHit>>([]);

    public Task<OrderCreated> CreateAsync(NewOrder order, Guid actorId)
    {
        LastOrder = order;
        return Task.FromResult(NextCreated ?? throw new InvalidOperationException("mất kết nối CSDL"));
    }
}

/// <summary>
/// Kiểm tra màn Tiếp nhận: chặn chọn trùng chỉ số ngay lúc thêm dịch vụ (cùng quy tắc với
/// <c>OrderService.CreateAsync</c>), và "Lưu và in tem" dựng đúng tem từ người bệnh + mẫu đã
/// cấp, in lỗi không làm mất chỉ định đã lưu. Danh mục nạp qua
/// <see cref="ReceptionViewModel.SeedCatalog"/>, chỉ định qua <see cref="FakeOrderService"/> —
/// không chạm CSDL (<see cref="Db"/> chỉ tạo nguồn kết nối, chưa mở kết nối nào).
/// </summary>
public class ReceptionViewModelTests
{
    private static readonly Guid EdtaId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 4);

    private static ServiceInfo Leaf(string code, string name) =>
        new(Guid.NewGuid(), code, name, "Huyết học", EdtaId, null, "so", 2, 0, false, false, true, null, []);

    private static ServiceInfo Panel(string code, string name, params ServiceInfo[] children) =>
        new(Guid.NewGuid(), code, name, "Huyết học", EdtaId, null, "so", null, 0, true, false, true, null,
            children.Select(c => c.Id).ToList());

    private static ReceptionViewModel CreateViewModel(params ServiceInfo[] services) =>
        CreateViewModel(new FakeOrderService(), new FakeLabelPrinter(), services);

    private static ReceptionViewModel CreateViewModel(FakeOrderService orders, FakeLabelPrinter printer, params ServiceInfo[] services)
    {
        var unusedDb = Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused");
        var user = new UserInfo(Guid.NewGuid(), "tiepnhan.thu", "Lê Thị Thử", "Tiếp nhận", [Roles.TiepNhan], true);
        var clock = new FakeTimeProvider(new DateTimeOffset(Today, new TimeOnly(8, 0), TimeSpan.FromHours(7)));
        clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));

        var viewModel = new ReceptionViewModel(
            orders,
            new CatalogStore(unusedDb),
            new FacilityStore(unusedDb),
            new CurrentUser(user),
            printer,
            clock);

        viewModel.SeedCatalog(services, new Dictionary<Guid, string> { [EdtaId] = "Máu EDTA (huyết học)" }, "per-specimen");
        return viewModel;
    }

    private static OrderCreated CreatedWithSamples(params CreatedSample[] samples) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "BN2611040001", samples);

    [Fact]
    public void AddService_ChildOfSelectedPanel_SetsErrorAndKeepsList()
    {
        var wbc = Leaf("WBC", "Bạch cầu");
        var rbc = Leaf("RBC", "Hồng cầu");
        var panel = Panel("TPTTBM", "Tổng phân tích tế bào máu", wbc, rbc);
        var viewModel = CreateViewModel(panel, wbc, rbc);

        viewModel.ServiceCodeInput = "TPTTBM";
        viewModel.AddServiceByCodeCommand.Execute(null);
        viewModel.SelectedServices.Count.ShouldBe(1);
        viewModel.ErrorMessage.ShouldBeNull();

        viewModel.ServiceCodeInput = "WBC";
        viewModel.AddServiceByCodeCommand.Execute(null);

        viewModel.ErrorMessage.ShouldBe("Chỉ số 'Bạch cầu' đã nằm trong gói 'Tổng phân tích tế bào máu'");
        viewModel.SelectedServices.Select(s => s.ServiceId).ShouldBe([panel.Id]);
        viewModel.GroupPreview.Single().ServiceNames.ShouldBe(["Bạch cầu", "Hồng cầu"]);
    }

    [Fact]
    public void AddService_SameServiceTwice_SetsErrorAndKeepsList()
    {
        var glu = Leaf("GLU", "Glucose");
        var viewModel = CreateViewModel(glu);

        viewModel.ServiceCodeInput = "GLU";
        viewModel.AddServiceByCodeCommand.Execute(null);
        viewModel.PickServiceSuggestionCommand.Execute(glu);

        viewModel.ErrorMessage.ShouldBe("Dịch vụ 'Glucose' đã được chọn hai lần");
        viewModel.SelectedServices.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Save_PrintsLabelsBuiltFromPatientAndSamples()
    {
        var glu = Leaf("GLU", "Glucose");
        var orders = new FakeOrderService
        {
            NextCreated = CreatedWithSamples(
                new CreatedSample(Guid.NewGuid(), "9261104001", "Máu EDTA (huyết học)", ["Glucose"]),
                new CreatedSample(Guid.NewGuid(), "9261104002", "Huyết thanh", ["Glucose"])),
        };
        var printer = new FakeLabelPrinter();
        var viewModel = CreateViewModel(orders, printer, glu);

        viewModel.NewPatientFullName = "Nguyễn Văn Mẫu";
        viewModel.NewPatientDobText = "15/06/1980";
        viewModel.NewPatientSex = SexOption.Male;
        viewModel.ServiceCodeInput = "GLU";
        viewModel.AddServiceByCodeCommand.Execute(null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBeNull();
        orders.LastOrder!.Patient!.FullName.ShouldBe("Nguyễn Văn Mẫu");
        printer.Jobs.ShouldHaveSingleItem().ShouldBe([
            new LabelData("9261104001", "Nguyễn Văn Mẫu", 1980, 'M', "Máu EDTA (huyết học)", Today),
            new LabelData("9261104002", "Nguyễn Văn Mẫu", 1980, 'M', "Huyết thanh", Today),
        ]);
        viewModel.IssuedSamples.Select(s => s.Barcode).ShouldBe(["9261104001", "9261104002"]);
        viewModel.IsSaved.ShouldBeTrue();
        viewModel.SelectedServices.ShouldHaveSingleItem().Barcode.ShouldBe("9261104001");

        viewModel.NewOrderCommand.Execute(null);
        viewModel.SelectedServices.ShouldBeEmpty();
        viewModel.NewPatientFullName.ShouldBe("");
        viewModel.IsSaved.ShouldBeFalse();
    }

    [Fact]
    public void ConfirmNewPatient_InvalidDob_ShowsErrorInDialog()
    {
        var viewModel = CreateViewModel();
        var closed = false;
        viewModel.PatientConfirmed += () => closed = true;

        viewModel.NewPatientFullName = "Nguyễn Văn Mẫu";
        viewModel.NewPatientDobText = "31/02/1980";
        viewModel.ConfirmNewPatientCommand.Execute(null);
        viewModel.PatientErrorMessage.ShouldBe("Ngày 31/02/1980 không có thật");
        closed.ShouldBeFalse();

        viewModel.NewPatientDobText = "15061980";
        viewModel.ConfirmNewPatientCommand.Execute(null);
        viewModel.NewPatientDobText.ShouldBe("15/06/1980");
        viewModel.PatientSummary.ShouldBe("Nguyễn Văn Mẫu · 15/06/1980 · 46 tuổi · Nam · người bệnh mới");
        closed.ShouldBeTrue();
    }

    [Fact]
    public async Task Save_ExistingPatient_UsesBirthYearAndSexFromHit()
    {
        var glu = Leaf("GLU", "Glucose");
        var orders = new FakeOrderService
        {
            NextCreated = CreatedWithSamples(new CreatedSample(Guid.NewGuid(), "9261104003", "Huyết thanh", ["Glucose"])),
        };
        var printer = new FakeLabelPrinter();
        var viewModel = CreateViewModel(orders, printer, glu);

        viewModel.PickPatientCommand.Execute(new PatientHit(Guid.NewGuid(), "BN0001", "Trần Thị Mẫu", null, 1995, 'F', null));
        viewModel.ServiceCodeInput = "GLU";
        viewModel.AddServiceByCodeCommand.Execute(null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        printer.Jobs.ShouldHaveSingleItem().ShouldBe([new LabelData("9261104003", "Trần Thị Mẫu", 1995, 'F', "Huyết thanh", Today)]);
    }

    [Fact]
    public async Task Save_PrintFails_KeepsIssuedSamplesAndShowsMessage()
    {
        var glu = Leaf("GLU", "Glucose");
        var orders = new FakeOrderService
        {
            NextCreated = CreatedWithSamples(new CreatedSample(Guid.NewGuid(), "9261104001", "Máu EDTA (huyết học)", ["Glucose"])),
        };
        var printer = new FakeLabelPrinter { PrintException = new InvalidOperationException("Không tìm thấy máy in \"XP\"") };
        var viewModel = CreateViewModel(orders, printer, glu);

        viewModel.NewPatientFullName = "Nguyễn Văn Mẫu";
        viewModel.NewPatientDobText = "1980";
        viewModel.ServiceCodeInput = "GLU";
        viewModel.AddServiceByCodeCommand.Execute(null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Đã lưu chỉ định nhưng in tem lỗi: Không tìm thấy máy in \"XP\"");
        viewModel.IssuedSamples.Select(s => s.Barcode).ShouldBe(["9261104001"]);
        viewModel.SelectedServices.ShouldHaveSingleItem().Barcode.ShouldBe("9261104001");
        viewModel.IsSaved.ShouldBeTrue();
        viewModel.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task Save_CreateFails_ShowsErrorAndKeepsForm()
    {
        var glu = Leaf("GLU", "Glucose");
        var printer = new FakeLabelPrinter();
        var viewModel = CreateViewModel(new FakeOrderService(), printer, glu);

        viewModel.NewPatientFullName = "Nguyễn Văn Mẫu";
        viewModel.NewPatientDobText = "1980";
        viewModel.ServiceCodeInput = "GLU";
        viewModel.AddServiceByCodeCommand.Execute(null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Lưu chỉ định thất bại: mất kết nối CSDL");
        viewModel.IssuedSamples.ShouldBeEmpty();
        viewModel.SelectedServices.Count.ShouldBe(1);
        viewModel.NewPatientFullName.ShouldBe("Nguyễn Văn Mẫu");
        printer.Jobs.ShouldBeEmpty();
    }
}
