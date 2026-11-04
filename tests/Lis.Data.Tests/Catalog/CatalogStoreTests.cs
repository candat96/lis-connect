using Lis.Core.Results;
using Lis.Data.Catalog;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Catalog;

/// <summary>
/// Kiểm tra danh mục dịch vụ: quan hệ dịch vụ cha (panel) - chỉ số con (giữ thứ tự, không
/// cho panel làm con), tìm theo mã không phân biệt hoa thường, và thay toàn bộ khoảng tham
/// chiếu/quy tắc quy đổi định tính của một chỉ số.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CatalogStoreTests(PostgresFixture fixture)
{
    private async Task<(CatalogStore Store, Guid SpecimenTypeId)> CreateStoreAsync()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var store = new CatalogStore(Db.Create(connectionString));
        var specimenTypes = await store.SpecimenTypesAsync();
        return (store, specimenTypes[0].Id);
    }

    private static ServiceInfo NewService(string code, string name, Guid specimenTypeId, bool isPanel = false) =>
        new(Guid.Empty, code, name, GroupName: null, specimenTypeId, Unit: null, ValueType: "so", Decimals: null,
            PrintOrder: 0, isPanel, AutoApprove: false, Active: true, HisCode: null, ChildIds: []);

    [Fact]
    public async Task SetChildren_PreservesOrder()
    {
        var (store, specimenTypeId) = await CreateStoreAsync();

        var panelId = await store.SaveServiceAsync(NewService("TPTTBM", "Tổng phân tích tế bào máu", specimenTypeId, isPanel: true));
        var wbcId = await store.SaveServiceAsync(NewService("WBC", "Bạch cầu", specimenTypeId));
        var rbcId = await store.SaveServiceAsync(NewService("RBC", "Hồng cầu", specimenTypeId));
        var hgbId = await store.SaveServiceAsync(NewService("HGB", "Huyết sắc tố", specimenTypeId));

        await store.SetChildrenAsync(panelId, [wbcId, rbcId, hgbId]);

        var panel = await store.FindByCodeAsync("tpttbm");
        panel.ShouldNotBeNull();
        panel.ChildIds.ShouldBe([wbcId, rbcId, hgbId]);

        // Đổi thứ tự — lưu lại phải đúng thứ tự mới, không phải thứ tự cũ.
        await store.SetChildrenAsync(panelId, [hgbId, wbcId, rbcId]);
        var panelAfter = await store.FindByCodeAsync("TPTTBM");
        panelAfter.ShouldNotBeNull();
        panelAfter.ChildIds.ShouldBe([hgbId, wbcId, rbcId]);
    }

    [Fact]
    public async Task SetChildren_RejectsPanelAsChild()
    {
        var (store, specimenTypeId) = await CreateStoreAsync();

        var panelId = await store.SaveServiceAsync(NewService("PANEL1", "Panel 1", specimenTypeId, isPanel: true));
        var otherPanelId = await store.SaveServiceAsync(NewService("PANEL2", "Panel 2", specimenTypeId, isPanel: true));
        var childId = await store.SaveServiceAsync(NewService("WBC", "Bạch cầu", specimenTypeId));

        // Một panel không thể làm con của panel khác.
        var exception1 = await Should.ThrowAsync<InvalidOperationException>(() =>
            store.SetChildrenAsync(panelId, [otherPanelId, childId]));
        exception1.Message.ShouldBe("Chỉ số con không được là một dịch vụ cha (panel)");

        // Dịch vụ không thể là con của chính nó (vòng).
        var exception2 = await Should.ThrowAsync<InvalidOperationException>(() =>
            store.SetChildrenAsync(panelId, [panelId]));
        exception2.Message.ShouldBe("Dịch vụ không thể là con của chính nó");
    }

    [Fact]
    public async Task FindByCode_CaseInsensitive()
    {
        var (store, specimenTypeId) = await CreateStoreAsync();
        await store.SaveServiceAsync(NewService("Glu", "Glucose", specimenTypeId));

        var byLower = await store.FindByCodeAsync("glu");
        var byUpper = await store.FindByCodeAsync("GLU");
        var byMixed = await store.FindByCodeAsync("GlU");

        byLower.ShouldNotBeNull();
        byUpper.ShouldNotBeNull();
        byMixed.ShouldNotBeNull();
        byLower.Code.ShouldBe("Glu");
        byUpper.Id.ShouldBe(byLower.Id);
        byMixed.Id.ShouldBe(byLower.Id);

        var notFound = await store.FindByCodeAsync("khong-ton-tai");
        notFound.ShouldBeNull();
    }

    [Fact]
    public async Task SaveRanges_ReplacesAll()
    {
        var (store, specimenTypeId) = await CreateStoreAsync();
        var serviceId = await store.SaveServiceAsync(NewService("HGB", "Huyết sắc tố", specimenTypeId));

        var firstVersion = new List<ReferenceRange>
        {
            new(Guid.Empty, serviceId, 'M', AgeFromDays: 365 * 15, AgeToDays: null, InstrumentId: null,
                Low: 130, High: 170, CriticalLow: 70, CriticalHigh: 200, TextRange: null),
            new(Guid.Empty, serviceId, 'F', AgeFromDays: 365 * 15, AgeToDays: null, InstrumentId: null,
                Low: 120, High: 150, CriticalLow: 70, CriticalHigh: 200, TextRange: null),
        };
        await store.SaveRangesAsync(serviceId, firstVersion);

        var afterFirstSave = await store.RangesAsync(serviceId);
        afterFirstSave.Count.ShouldBe(2);

        // Trẻ sơ sinh thêm vào, cùng lúc đổi ngưỡng nữ — lưu lại phải thay toàn bộ, không gộp
        // với các dòng cũ.
        var secondVersion = new List<ReferenceRange>
        {
            new(Guid.Empty, serviceId, null, AgeFromDays: 0, AgeToDays: 28, InstrumentId: null,
                Low: 140, High: 220, CriticalLow: 90, CriticalHigh: 250, TextRange: null),
            new(Guid.Empty, serviceId, 'F', AgeFromDays: 365 * 15, AgeToDays: null, InstrumentId: null,
                Low: 118, High: 148, CriticalLow: 70, CriticalHigh: 200, TextRange: null),
        };
        await store.SaveRangesAsync(serviceId, secondVersion);

        var afterSecondSave = await store.RangesAsync(serviceId);
        afterSecondSave.Count.ShouldBe(2);
        afterSecondSave.ShouldContain(r => r.Sex == null && r.AgeFromDays == 0 && r.AgeToDays == 28 && r.Low == 140);
        afterSecondSave.ShouldContain(r => r.Sex == 'F' && r.Low == 118);
        afterSecondSave.ShouldNotContain(r => r.Sex == 'M');
    }

    [Fact]
    public async Task SaveQualitativeRules_ReplacesAll()
    {
        var (store, specimenTypeId) = await CreateStoreAsync();
        var serviceId = await store.SaveServiceAsync(NewService("HBSAG", "HBsAg test nhanh", specimenTypeId));

        var firstVersion = new List<QualitativeRule>
        {
            new(InstrumentId: null, serviceId, RawValue: "pos", DisplayValue: "Dương tính", Flag: ResultFlag.Abnormal),
            new(InstrumentId: null, serviceId, RawValue: "neg", DisplayValue: "Âm tính", Flag: null),
        };
        await store.SaveQualitativeRulesAsync(serviceId, firstVersion);

        var afterFirstSave = await store.QualitativeRulesAsync(serviceId);
        afterFirstSave.Count.ShouldBe(2);

        // Lưu lại một bộ khác (bỏ "neg", sửa hiển thị của "pos") — phải thay toàn bộ, không gộp.
        var secondVersion = new List<QualitativeRule>
        {
            new(InstrumentId: null, serviceId, RawValue: "pos", DisplayValue: "Dương tính (+)", Flag: ResultFlag.Abnormal),
        };
        await store.SaveQualitativeRulesAsync(serviceId, secondVersion);

        var afterSecondSave = await store.QualitativeRulesAsync(serviceId);
        afterSecondSave.Count.ShouldBe(1);
        afterSecondSave.ShouldContain(r => r.RawValue == "pos" && r.DisplayValue == "Dương tính (+)" && r.Flag == ResultFlag.Abnormal);
        afterSecondSave.ShouldNotContain(r => r.RawValue == "neg");
    }
}
