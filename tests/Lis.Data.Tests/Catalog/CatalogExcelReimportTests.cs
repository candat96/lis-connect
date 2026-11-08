using ClosedXML.Excel;
using Dapper;
using Lis.Data.Catalog;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Catalog;

/// <summary>
/// Nhập danh mục: dịch vụ cha giữ ĐỦ chỉ số con theo thứ tự (trước đây ghi từng dòng nên chỉ còn
/// con cuối), và nhập lại cùng tệp không lỗi trùng mã / không sinh máy, kênh trùng.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CatalogExcelReimportTests(PostgresFixture fixture)
{
    private static MemoryStream Workbook(string panelName = "Tổng phân tích tế bào máu")
    {
        var workbook = new XLWorkbook();
        var dv = workbook.Worksheets.Add("DichVu");
        string[] headers = ["Ma", "Ten", "Nhom", "LoaiMau", "DonVi", "KieuKetQua", "SoLe", "ThuTuIn", "LaPanel", "TuDuyet", "MaHIS"];
        for (var i = 0; i < headers.Length; i++)
        {
            dv.Cell(1, i + 1).Value = headers[i];
        }

        (string Code, string Name, bool Panel)[] services = [("CBC", panelName, true), ("WBC", "Bạch cầu", false), ("RBC", "Hồng cầu", false), ("HGB", "Huyết sắc tố", false)];
        for (var r = 0; r < services.Length; r++)
        {
            dv.Cell(r + 2, 1).Value = services[r].Code;
            dv.Cell(r + 2, 2).Value = services[r].Name;
            dv.Cell(r + 2, 4).Value = "EDTA";
            dv.Cell(r + 2, 6).Value = "so";
            dv.Cell(r + 2, 9).Value = services[r].Panel;
        }

        var cs = workbook.Worksheets.Add("ChiSoCon");
        cs.Cell(1, 1).Value = "MaCha";
        cs.Cell(1, 2).Value = "MaCon";
        cs.Cell(1, 3).Value = "ThuTu";
        (string Child, int Order)[] children = [("HGB", 3), ("WBC", 1), ("RBC", 2)];
        for (var r = 0; r < children.Length; r++)
        {
            cs.Cell(r + 2, 1).Value = "CBC";
            cs.Cell(r + 2, 2).Value = children[r].Child;
            cs.Cell(r + 2, 3).Value = children[r].Order;
        }

        var map = workbook.Worksheets.Add("MapMay");
        map.Cell(1, 1).Value = "TenMay";
        map.Cell(1, 2).Value = "MaKenh";
        map.Cell(1, 3).Value = "MaChiSo";
        map.Cell(2, 1).Value = "Mindray BC-5380";
        map.Cell(2, 2).Value = "6690-2";
        map.Cell(2, 3).Value = "WBC";

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task Import_PanelKeepsAllChildrenInOrder_AndReimportUpdatesWithoutDuplicates()
    {
        var db = Db.Create(await fixture.NewDatabaseAsync());
        var importer = new CatalogExcelImporter(db);

        var first = await importer.ImportAsync(Workbook(), dryRun: false);
        first.Errors.ShouldBeEmpty();

        var second = await importer.ImportAsync(Workbook("Tổng phân tích tế bào máu (đổi tên)"), dryRun: false);
        second.Errors.ShouldBeEmpty();
        second.Created.ShouldBe(1); // không dịch vụ nào tạo mới — 1 là dòng MapMay (importer đếm mỗi dòng map là "tạo")

        var panel = (await new CatalogStore(db).ListServicesAsync(activeOnly: false)).Single(s => s.Code == "CBC");
        panel.Name.ShouldBe("Tổng phân tích tế bào máu (đổi tên)");
        var all = await new CatalogStore(db).ListServicesAsync(activeOnly: false);
        panel.ChildIds.Select(id => all.Single(s => s.Id == id).Code).ShouldBe(["WBC", "RBC", "HGB"]);

        await using var connection = await db.OpenAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM service")).ShouldBe(4);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM instrument")).ShouldBe(1);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM instrument_channel")).ShouldBe(1);
    }

    [Fact]
    public async Task Import_DuplicateCodeInFile_ReportsError()
    {
        var db = Db.Create(await fixture.NewDatabaseAsync());
        var stream = Workbook();
        using (var workbook = new XLWorkbook(stream))
        {
            workbook.Worksheet("DichVu").Cell(6, 1).Value = "WBC";
            workbook.Worksheet("DichVu").Cell(6, 2).Value = "Bạch cầu lần 2";
            workbook.Worksheet("DichVu").Cell(6, 4).Value = "EDTA";
            workbook.Worksheet("DichVu").Cell(6, 6).Value = "so";
            stream = new MemoryStream();
            workbook.SaveAs(stream);
        }

        stream.Position = 0;
        var report = await new CatalogExcelImporter(db).ImportAsync(stream, dryRun: false);

        report.Errors.ShouldBe(["Sheet DichVu dòng 6: mã WBC lặp lại trong tệp"]);
    }
}
