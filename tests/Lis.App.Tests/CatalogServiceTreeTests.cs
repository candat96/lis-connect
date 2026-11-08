using Lis.App.ViewModels.Catalog;
using Lis.Data.Catalog;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>Bảng dịch vụ dạng cây ở màn Danh mục: dịch vụ cha → chỉ số con thụt vào → dịch vụ lẻ.</summary>
public class CatalogServiceTreeTests
{
    private static readonly Guid Edta = Guid.NewGuid();

    private static ServiceListItemViewModel S(string code, string name, int order, params ServiceListItemViewModel[] children) =>
        new(new ServiceInfo(Guid.NewGuid(), code, name, null, Edta, null, "so", null, order, children.Length > 0, false, true, null, children.Select(c => c.Id).ToList()));

    private static (List<ServiceListItemViewModel> All, ServiceListItemViewModel Panel) Sample()
    {
        var wbc = S("WBC", "Bạch cầu", 2);
        var rbc = S("RBC", "Hồng cầu", 3);
        var glu = S("GLU", "Glucose", 10);
        var panel = S("TPTTBM", "Tổng phân tích tế bào máu", 1, wbc, rbc);
        var lipid = S("MOMAU", "Bộ mỡ máu", 5, glu); // GLU vừa là con của bộ mỡ máu
        var crp = S("CRP", "CRP", 20);
        return ([glu, crp, rbc, wbc, lipid, panel], panel);
    }

    [Fact]
    public void Build_GroupsChildrenUnderParentInPrintOrder_ThenSingles()
    {
        var (all, _) = Sample();

        var rows = CatalogViewModel.BuildServiceRows(all, "");

        rows.Select(r => $"{r.TreeMark}{r.Code}").ShouldBe(["▾TPTTBM", "├─WBC", "└─RBC", "▾MOMAU", "└─GLU", "CRP"]);
        rows[0].Loai.ShouldBe("Dịch vụ · 2 chỉ số con");
        rows[1].Loai.ShouldBe("Chỉ số con");
        rows[1].Parent!.Code.ShouldBe("TPTTBM");
        rows[^1].Loai.ShouldBe("Dịch vụ lẻ");
    }

    [Fact]
    public void Filter_ChildMatch_KeepsItsParentRow()
    {
        var (all, _) = Sample();

        var rows = CatalogViewModel.BuildServiceRows(all, "hồng cầu");

        rows.Select(r => r.Code).ShouldBe(["TPTTBM", "RBC"]);
        rows[1].TreeMark.ShouldBe("└─");
    }

    [Fact]
    public void Filter_ParentMatch_ShowsWholeGroup()
    {
        var (all, _) = Sample();

        CatalogViewModel.BuildServiceRows(all, "TPTT").Select(r => r.Code).ShouldBe(["TPTTBM", "WBC", "RBC"]);
    }

    [Fact]
    public void SelectingChild_ShowsWhichServiceItBelongsTo()
    {
        var (all, panel) = Sample();
        var viewModel = new CatalogViewModel(
            new CatalogStore(Lis.Data.Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused")),
            null!, null!, new Lis.Data.Auth.CurrentUser(new Lis.Data.Auth.UserInfo(Guid.NewGuid(), "qt", "QT", null, [Lis.Core.Auth.Roles.QuanTri], true)));
        foreach (var item in all)
        {
            viewModel.Services.Add(item);
        }

        viewModel.ApplyFilter();
        viewModel.SelectedRow = viewModel.FilteredServices.Single(r => r.Code == "WBC");

        viewModel.SelectedService!.Code.ShouldBe("WBC");
        viewModel.ParentsText.ShouldBe("Chỉ số con của: TPTTBM – Tổng phân tích tế bào máu");

        viewModel.SelectedService = panel;
        viewModel.SelectedRow!.Code.ShouldBe("TPTTBM");
        viewModel.ParentsText.ShouldBeNull();
    }

    private static CatalogViewModel ViewModelWith(IEnumerable<ServiceListItemViewModel> all)
    {
        var viewModel = new CatalogViewModel(
            new CatalogStore(Lis.Data.Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused")),
            null!, null!, new Lis.Data.Auth.CurrentUser(new Lis.Data.Auth.UserInfo(Guid.NewGuid(), "qt", "QT", null, [Lis.Core.Auth.Roles.QuanTri], true)));
        foreach (var item in all)
        {
            viewModel.Services.Add(item);
        }

        viewModel.ApplyFilter();
        return viewModel;
    }

    [Fact]
    public void TogglePanel_HidesAndShowsItsChildren()
    {
        var (all, _) = Sample();
        var viewModel = ViewModelWith(all);

        viewModel.FilteredServices.Single(r => r.Code == "TPTTBM").ToggleCommand.Execute(null);

        viewModel.FilteredServices.Select(r => $"{r.TreeMark}{r.Code}").ShouldBe(["▸TPTTBM", "▾MOMAU", "└─GLU", "CRP"]);

        viewModel.FilteredServices.Single(r => r.Code == "TPTTBM").ToggleCommand.Execute(null);
        viewModel.FilteredServices.Select(r => r.Code).ShouldBe(["TPTTBM", "WBC", "RBC", "MOMAU", "GLU", "CRP"]);
    }

    [Fact]
    public void CollapseAll_ThenFilter_StillShowsMatchingChildren()
    {
        var (all, _) = Sample();
        var viewModel = ViewModelWith(all);

        viewModel.CollapseAllCommand.Execute(null);
        viewModel.FilteredServices.Select(r => r.Code).ShouldBe(["TPTTBM", "MOMAU", "CRP"]);

        viewModel.FilterText = "hồng cầu";
        viewModel.FilteredServices.Select(r => r.Code).ShouldBe(["TPTTBM", "RBC"]);

        viewModel.FilterText = "";
        viewModel.ExpandAllCommand.Execute(null);
        viewModel.FilteredServices.Count.ShouldBe(6);
    }

    [Fact]
    public void ChildOrSingleRow_ToggleDoesNothing()
    {
        var (all, _) = Sample();
        var viewModel = ViewModelWith(all);

        viewModel.FilteredServices.Single(r => r.Code == "WBC").ToggleCommand.CanExecute(null).ShouldBeFalse();
        viewModel.FilteredServices.Single(r => r.Code == "CRP").ToggleCommand.CanExecute(null).ShouldBeFalse();
    }
}
