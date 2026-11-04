using Lis.Data.Catalog;
using Lis.Data.Orders;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Orders;

/// <summary>
/// Kiểm tra quy tắc chọn dịch vụ dùng chung giữa <c>OrderService</c> và màn Tiếp nhận: sau khi
/// tách gói ra chỉ số con, mỗi chỉ số chỉ được xuất hiện đúng một lần. Thuần túy, không cần CSDL.
/// </summary>
public class ServiceSelectionValidatorTests
{
    private static readonly Guid Edta = Guid.NewGuid();
    private static readonly Guid Serum = Guid.NewGuid();

    private static ServiceInfo Leaf(string code, string name, Guid specimenTypeId) =>
        new(Guid.NewGuid(), code, name, GroupName: null, specimenTypeId, Unit: null, ValueType: "so",
            Decimals: null, PrintOrder: 0, IsPanel: false, AutoApprove: false, Active: true, HisCode: null, ChildIds: []);

    private static ServiceInfo Panel(string code, string name, params ServiceInfo[] children) =>
        new(Guid.NewGuid(), code, name, GroupName: null, Edta, Unit: null, ValueType: "so",
            Decimals: null, PrintOrder: 0, IsPanel: true, AutoApprove: false, Active: true, HisCode: null,
            ChildIds: children.Select(c => c.Id).ToList());

    private static Dictionary<Guid, ServiceInfo> Catalog(params ServiceInfo[] services) => services.ToDictionary(s => s.Id);

    [Fact]
    public void Validate_DuplicateLeaf_Throws()
    {
        var glu = Leaf("GLU", "Glucose", Serum);

        var ex = Should.Throw<InvalidOperationException>(() =>
            ServiceSelectionValidator.Validate([glu.Id, glu.Id], Catalog(glu)));

        ex.Message.ShouldBe("Dịch vụ 'Glucose' đã được chọn hai lần");
    }

    [Fact]
    public void Validate_PanelAndOwnChild_Throws()
    {
        var wbc = Leaf("WBC", "Bạch cầu", Edta);
        var rbc = Leaf("RBC", "Hồng cầu", Edta);
        var panel = Panel("TPTTBM", "Tổng phân tích tế bào máu", wbc, rbc);
        var catalog = Catalog(wbc, rbc, panel);

        Should.Throw<InvalidOperationException>(() => ServiceSelectionValidator.Validate([panel.Id, wbc.Id], catalog))
            .Message.ShouldBe("Chỉ số 'Bạch cầu' đã nằm trong gói 'Tổng phân tích tế bào máu'");

        // Thứ tự ngược lại (chọn chỉ số lẻ trước, gói sau) cũng phải bị chặn, cùng thông điệp.
        Should.Throw<InvalidOperationException>(() => ServiceSelectionValidator.Validate([wbc.Id, panel.Id], catalog))
            .Message.ShouldBe("Chỉ số 'Bạch cầu' đã nằm trong gói 'Tổng phân tích tế bào máu'");
    }

    [Fact]
    public void Validate_TwoPanelsSharingChild_Throws()
    {
        var wbc = Leaf("WBC", "Bạch cầu", Edta);
        var rbc = Leaf("RBC", "Hồng cầu", Edta);
        var hgb = Leaf("HGB", "Huyết sắc tố", Edta);
        var full = Panel("TPTTBM", "Tổng phân tích tế bào máu", wbc, rbc, hgb);
        var mini = Panel("CBC-MINI", "Công thức máu rút gọn", wbc, hgb);

        var ex = Should.Throw<InvalidOperationException>(() =>
            ServiceSelectionValidator.Validate([full.Id, mini.Id], Catalog(wbc, rbc, hgb, full, mini)));

        ex.Message.ShouldBe("Chỉ số 'Bạch cầu' nằm trong cả gói 'Tổng phân tích tế bào máu' và gói 'Công thức máu rút gọn'");
    }

    [Fact]
    public void Validate_DuplicatePanel_Throws()
    {
        var wbc = Leaf("WBC", "Bạch cầu", Edta);
        var panel = Panel("TPTTBM", "Tổng phân tích tế bào máu", wbc);

        var ex = Should.Throw<InvalidOperationException>(() =>
            ServiceSelectionValidator.Validate([panel.Id, panel.Id], Catalog(wbc, panel)));

        ex.Message.ShouldBe("Dịch vụ 'Tổng phân tích tế bào máu' đã được chọn hai lần");
    }

    [Fact]
    public void Validate_EmptyPanel_Throws()
    {
        var empty = Panel("GOI-RONG", "Gói rỗng");

        var ex = Should.Throw<InvalidOperationException>(() =>
            ServiceSelectionValidator.Validate([empty.Id], Catalog(empty)));

        ex.Message.ShouldBe("Gói 'GOI-RONG' chưa có chỉ số con");
    }

    [Fact]
    public void Validate_UnknownService_Throws()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            ServiceSelectionValidator.Validate([Guid.NewGuid()], Catalog()));

        ex.Message.ShouldBe("Dịch vụ không tồn tại trong danh mục");
    }

    [Fact]
    public void Validate_MixedSelection_Passes()
    {
        var wbc = Leaf("WBC", "Bạch cầu", Edta);
        var rbc = Leaf("RBC", "Hồng cầu", Edta);
        var hgb = Leaf("HGB", "Huyết sắc tố", Edta);
        var panel = Panel("TPTTBM", "Tổng phân tích tế bào máu", wbc, rbc, hgb);
        var glu = Leaf("GLU", "Glucose", Serum);
        var crea = Leaf("CREA", "Creatinin", Serum);
        var kidney = Panel("THAN", "Chức năng thận", crea);

        Should.NotThrow(() =>
            ServiceSelectionValidator.Validate([panel.Id, glu.Id, kidney.Id], Catalog(wbc, rbc, hgb, panel, glu, crea, kidney)));
    }
}
