using Dapper;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Orders;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Orders;

/// <summary>
/// Kiểm tra tạo chỉ định: tách panel ra chỉ số con (<c>order_item.parent_item_id</c>), gom
/// mẫu theo loại mẫu bệnh phẩm và cấp barcode nguyên tử (kể cả khi nhiều máy trạm tạo chỉ
/// định cùng lúc), và tìm người bệnh không phân biệt dấu/hoa thường.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OrderServiceTests(PostgresFixture fixture)
{
    private async Task<(OrderService Orders, CatalogStore Catalog, Guid EdtaId, Guid SerumId, Guid ActorId, Db Db)> CreateServiceAsync(
        string barcodeMode = "per-specimen", int seqDigits = 3)
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);

        var facilityStore = new FacilityStore(db);
        await facilityStore.SaveAsync(new FacilityInfo(
            "Khoa Xét nghiệm — Bệnh viện Thử", null, null, null, "9", "yyMMdd", seqDigits, barcodeMode));

        var catalogStore = new CatalogStore(db);
        var specimenTypes = await catalogStore.SpecimenTypesAsync();
        var edtaId = specimenTypes.First(s => s.Code == "EDTA").Id;
        var serumId = specimenTypes.First(s => s.Code == "SERUM").Id;

        var userStore = new UserStore(db);
        var actor = await userStore.CreateAsync("tiepnhan.thu", "Tiếp nhận Thử", null, "mat-khau-thu-1", []);

        return (new OrderService(db, TimeProvider.System), catalogStore, edtaId, serumId, actor.Id, db);
    }

    private static NewService NewServiceInfo(string code, string name, Guid specimenTypeId, bool isPanel = false) =>
        new(code, name, specimenTypeId, isPanel);

    private sealed record NewService(string Code, string Name, Guid SpecimenTypeId, bool IsPanel);

    private static async Task<Guid> SaveServiceAsync(CatalogStore catalog, NewService s) =>
        await catalog.SaveServiceAsync(new ServiceInfo(
            Guid.Empty, s.Code, s.Name, GroupName: null, s.SpecimenTypeId, Unit: null, ValueType: "so",
            Decimals: null, PrintOrder: 0, s.IsPanel, AutoApprove: false, Active: true, HisCode: null, ChildIds: []));

    [Fact]
    public async Task CreateOrder_PanelExpandsChildren()
    {
        var (orders, catalog, edtaId, _, actorId, db) = await CreateServiceAsync();

        var panelId = await SaveServiceAsync(catalog, NewServiceInfo("TPTTBM", "Tổng phân tích tế bào máu", edtaId, isPanel: true));
        var wbcId = await SaveServiceAsync(catalog, NewServiceInfo("WBC", "Bạch cầu", edtaId));
        var rbcId = await SaveServiceAsync(catalog, NewServiceInfo("RBC", "Hồng cầu", edtaId));
        var hgbId = await SaveServiceAsync(catalog, NewServiceInfo("HGB", "Huyết sắc tố", edtaId));
        await catalog.SetChildrenAsync(panelId, [wbcId, rbcId, hgbId]);

        var patient = new NewPatient("Nguyễn Văn Mẫu", new DateOnly(1990, 1, 1), null, 'M', null, "0900000001");
        var order = new NewOrder(PatientId: null, patient, [panelId], "BS. Thử", "Khoa Thử", "Chẩn đoán thử");

        var created = await orders.CreateAsync(order, actorId);

        created.PatientCode.ShouldStartWith("BN");
        created.Samples.Count.ShouldBe(1);
        created.Samples[0].ServiceNames.ShouldBe(["Bạch cầu", "Hồng cầu", "Huyết sắc tố"]);

        var itemCount = await CountOrderItemsAsync(db, created.OrderId);
        itemCount.ShouldBe(4);
    }

    [Fact]
    public async Task CreateOrder_TwoSpecimens_TwoBarcodes()
    {
        var (orders, catalog, edtaId, serumId, actorId, _) = await CreateServiceAsync();

        var wbcId = await SaveServiceAsync(catalog, NewServiceInfo("WBC2", "Bạch cầu", edtaId));
        var gluId = await SaveServiceAsync(catalog, NewServiceInfo("GLU2", "Glucose", serumId));

        var patient = new NewPatient("Trần Thị Mẫu", null, 1985, 'F', null, "0900000002");
        var order = new NewOrder(PatientId: null, patient, [wbcId, gluId], null, null, null);

        var created = await orders.CreateAsync(order, actorId);

        created.Samples.Count.ShouldBe(2);
        created.Samples.Select(s => s.Barcode).Distinct().Count().ShouldBe(2);
        var edtaSample = created.Samples.Single(s => s.SpecimenName == "Máu EDTA (huyết học)");
        edtaSample.ServiceNames.ShouldBe(["Bạch cầu"]);

        var serumSample = created.Samples.Single(s => s.SpecimenName == "Huyết thanh (hóa sinh, miễn dịch)");
        serumSample.ServiceNames.ShouldBe(["Glucose"]);
    }

    [Fact]
    public async Task CreateOrder_Concurrent_ProducesUniqueBarcodes()
    {
        var (orders, catalog, edtaId, _, actorId, _) = await CreateServiceAsync();
        var wbcId = await SaveServiceAsync(catalog, NewServiceInfo("WBC3", "Bạch cầu", edtaId));

        var tasks = Enumerable.Range(0, 20).Select(i =>
        {
            var patient = new NewPatient($"Người Mẫu {i:00}", null, 1990, 'M', null, $"090000{i:0000}");
            var order = new NewOrder(PatientId: null, patient, [wbcId], null, null, null);
            return orders.CreateAsync(order, actorId);
        });

        var results = await Task.WhenAll(tasks);

        var barcodes = results.SelectMany(r => r.Samples.Select(s => s.Barcode)).ToList();
        barcodes.Count.ShouldBe(20);
        barcodes.Distinct().Count().ShouldBe(20);

        var patientCodes = results.Select(r => r.PatientCode).ToList();
        patientCodes.Distinct().Count().ShouldBe(20);
    }

    [Fact]
    public async Task FindPatients_AccentInsensitive()
    {
        var (orders, catalog, edtaId, _, actorId, _) = await CreateServiceAsync();
        var wbcId = await SaveServiceAsync(catalog, NewServiceInfo("WBC4", "Bạch cầu", edtaId));

        var patient = new NewPatient("Nguyễn Văn Mẫu", null, 1990, 'M', null, "0900000003");
        await orders.CreateAsync(new NewOrder(null, patient, [wbcId], null, null, null), actorId);

        var hits = await orders.FindPatientsAsync("nguyen van mau");

        hits.ShouldContain(h => h.FullName == "Nguyễn Văn Mẫu");
    }

    private static async Task<long> CountOrderItemsAsync(Db db, Guid orderId)
    {
        // Kiểm tra trực tiếp bảng order_item — OrderService không lộ chi tiết này ra ngoài.
        await using var connection = await db.OpenAsync();
        return await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM order_item WHERE order_id = @orderId", new { orderId });
    }
}
