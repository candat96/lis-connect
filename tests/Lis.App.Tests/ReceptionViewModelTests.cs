using Lis.App.Services;
using Lis.App.ViewModels.Reception;
using Lis.Core.Auth;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Orders;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>
/// Kiểm tra màn Tiếp nhận chặn chọn trùng chỉ số ngay lúc thêm dịch vụ (cùng quy tắc với
/// <c>OrderService.CreateAsync</c>). Danh mục nạp qua <see cref="ReceptionViewModel.SeedCatalog"/>,
/// không chạm CSDL — <see cref="Db"/> chỉ tạo nguồn kết nối, chưa mở kết nối nào.
/// </summary>
public class ReceptionViewModelTests
{
    private static readonly Guid EdtaId = Guid.NewGuid();

    private static ServiceInfo Leaf(string code, string name) =>
        new(Guid.NewGuid(), code, name, "Huyết học", EdtaId, null, "so", 2, 0, false, false, true, null, []);

    private static ServiceInfo Panel(string code, string name, params ServiceInfo[] children) =>
        new(Guid.NewGuid(), code, name, "Huyết học", EdtaId, null, "so", null, 0, true, false, true, null,
            children.Select(c => c.Id).ToList());

    private static ReceptionViewModel CreateViewModel(params ServiceInfo[] services)
    {
        var unusedDb = Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused");
        var user = new UserInfo(Guid.NewGuid(), "tiepnhan.thu", "Lê Thị Thử", "Tiếp nhận", [Roles.TiepNhan], true);

        var viewModel = new ReceptionViewModel(
            new OrderService(unusedDb, TimeProvider.System),
            new CatalogStore(unusedDb),
            new FacilityStore(unusedDb),
            new CurrentUser(user),
            new NoOpLabelPrinter());

        viewModel.SeedCatalog(services, new Dictionary<Guid, string> { [EdtaId] = "Máu EDTA (huyết học)" }, "per-specimen");
        return viewModel;
    }

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
}
