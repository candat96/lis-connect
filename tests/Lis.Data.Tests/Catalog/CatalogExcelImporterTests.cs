using ClosedXML.Excel;
using Lis.Data.Catalog;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Catalog;

/// <summary>
/// Kiểm tra nhập danh mục từ workbook Excel: ánh xạ dữ liệu (dịch vụ, chỉ số con,
/// khoảng tham chiếu, máy), ghi một giao dịch, lỗi ghi theo dạng "Sheet X dòng Y: ...",
/// và chế độ dryRun không ghi gì.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CatalogExcelImporterTests(PostgresFixture fixture)
{
    private async Task<(CatalogExcelImporter Importer, Guid SpecimenTypeId)> CreateImporterAsync()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);
        var importer = new CatalogExcelImporter(db);
        var store = new CatalogStore(db);
        var specimenTypes = await store.SpecimenTypesAsync();
        return (importer, specimenTypes[0].Id);
    }

    private static MemoryStream CreateValidWorkbook(Guid specimenTypeId)
    {
        var workbook = new XLWorkbook();

        // Sheet DichVu
        var dichVuSheet = workbook.Worksheets.Add("DichVu");
        dichVuSheet.Cell("A1").Value = "Ma";
        dichVuSheet.Cell("B1").Value = "Ten";
        dichVuSheet.Cell("C1").Value = "Nhom";
        dichVuSheet.Cell("D1").Value = "LoaiMau";
        dichVuSheet.Cell("E1").Value = "DonVi";
        dichVuSheet.Cell("F1").Value = "KieuKetQua";
        dichVuSheet.Cell("G1").Value = "SoLe";
        dichVuSheet.Cell("H1").Value = "ThuTuIn";
        dichVuSheet.Cell("I1").Value = "LaPanel";
        dichVuSheet.Cell("J1").Value = "TuDuyet";
        dichVuSheet.Cell("K1").Value = "MaHIS";

        // Thêm dịch vụ
        dichVuSheet.Cell("A2").Value = "WBC";
        dichVuSheet.Cell("B2").Value = "Bạch cầu";
        dichVuSheet.Cell("D2").Value = specimenTypeId.ToString();
        dichVuSheet.Cell("E2").Value = "10^3/uL";
        dichVuSheet.Cell("F2").Value = "so";
        dichVuSheet.Cell("G2").Value = 1;
        dichVuSheet.Cell("H2").Value = 1;
        dichVuSheet.Cell("I2").Value = false;
        dichVuSheet.Cell("J2").Value = true;

        // Sheet ChiSoCon
        var chiSoConSheet = workbook.Worksheets.Add("ChiSoCon");
        chiSoConSheet.Cell("A1").Value = "MaCha";
        chiSoConSheet.Cell("B1").Value = "MaCon";
        chiSoConSheet.Cell("C1").Value = "ThuTu";

        // Sheet KhoangThamChieu
        var khoangSheet = workbook.Worksheets.Add("KhoangThamChieu");
        khoangSheet.Cell("A1").Value = "Ma";
        khoangSheet.Cell("B1").Value = "Gioi";
        khoangSheet.Cell("C1").Value = "TuoiTu";
        khoangSheet.Cell("D1").Value = "TuoiDen";
        khoangSheet.Cell("E1").Value = "DonViTuoi";
        khoangSheet.Cell("F1").Value = "Thap";
        khoangSheet.Cell("G1").Value = "Cao";
        khoangSheet.Cell("H1").Value = "NguyHiemThap";
        khoangSheet.Cell("I1").Value = "NguyHiemCao";
        khoangSheet.Cell("J1").Value = "Chu";

        // Add range
        khoangSheet.Cell("A2").Value = "WBC";
        khoangSheet.Cell("B2").Value = "M";
        khoangSheet.Cell("C2").Value = 18;
        khoangSheet.Cell("D2").Value = 100;
        khoangSheet.Cell("E2").Value = "nam";
        khoangSheet.Cell("F2").Value = "4.5";
        khoangSheet.Cell("G2").Value = "11.0";

        // Sheet MapMay
        var mapMaySheet = workbook.Worksheets.Add("MapMay");
        mapMaySheet.Cell("A1").Value = "TenMay";
        mapMaySheet.Cell("B1").Value = "MaKenh";
        mapMaySheet.Cell("C1").Value = "MaChiSo";
        mapMaySheet.Cell("D1").Value = "SoLe";
        mapMaySheet.Cell("E1").Value = "HeSo";
        mapMaySheet.Cell("F1").Value = "GiuDauSoSanh";
        mapMaySheet.Cell("G1").Value = "GuiKhiMayHoi";

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task Import_ValidWorkbook_CreatesPanelsAndRanges()
    {
        var (importer, specimenTypeId) = await CreateImporterAsync();
        var workbook = CreateValidWorkbook(specimenTypeId);

        var report = await importer.ImportAsync(workbook, dryRun: false);

        report.Errors.ShouldBeEmpty();
        report.Created.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Import_DryRun_WritesNothing()
    {
        var (importer, specimenTypeId) = await CreateImporterAsync();
        var workbook = CreateValidWorkbook(specimenTypeId);

        var report = await importer.ImportAsync(workbook, dryRun: true);

        // Sau dryRun, lại import bình thường, nên dữ liệu phải không có
        // (nếu dryRun có ghi, sẽ gặp lỗi duplicate)
        workbook = CreateValidWorkbook(specimenTypeId);
        var report2 = await importer.ImportAsync(workbook, dryRun: false);

        report2.Errors.ShouldBeEmpty();
        report2.Created.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Import_Error_RollsBackAll_WithRowNumbers()
    {
        var (importer, specimenTypeId) = await CreateImporterAsync();

        var workbook = new XLWorkbook();
        var dichVuSheet = workbook.Worksheets.Add("DichVu");
        dichVuSheet.Cell("A1").Value = "Ma";
        dichVuSheet.Cell("B1").Value = "Ten";
        dichVuSheet.Cell("D1").Value = "LoaiMau";
        dichVuSheet.Cell("F1").Value = "KieuKetQua";

        // Dòng 1 OK
        dichVuSheet.Cell("A2").Value = "WBC";
        dichVuSheet.Cell("B2").Value = "Bạch cầu";
        dichVuSheet.Cell("D2").Value = specimenTypeId.ToString();
        dichVuSheet.Cell("F2").Value = "so";

        // Dòng 2 lỗi (thiếu mã)
        dichVuSheet.Cell("A3").Value = "";
        dichVuSheet.Cell("B3").Value = "Test";
        dichVuSheet.Cell("D3").Value = specimenTypeId.ToString();
        dichVuSheet.Cell("F3").Value = "so";

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var report = await importer.ImportAsync(stream, dryRun: false);

        report.Errors.Count.ShouldBeGreaterThan(0);
        report.Errors[0].ShouldContain("DichVu");
        report.Errors[0].ShouldContain("dòng");
        report.Errors[0].ShouldContain("3"); // Row numbers should be in error messages
        report.Created.ShouldBe(0); // Cả giao dịch bị rollback
    }

    [Fact]
    public async Task Import_DuplicateChannel_ReportsError()
    {
        var (importer, specimenTypeId) = await CreateImporterAsync();

        var workbook = new XLWorkbook();

        // Sheet DichVu
        var dichVuSheet = workbook.Worksheets.Add("DichVu");
        dichVuSheet.Cell("A1").Value = "Ma";
        dichVuSheet.Cell("B1").Value = "Ten";
        dichVuSheet.Cell("C1").Value = "Nhom";
        dichVuSheet.Cell("D1").Value = "LoaiMau";
        dichVuSheet.Cell("E1").Value = "DonVi";
        dichVuSheet.Cell("F1").Value = "KieuKetQua";
        dichVuSheet.Cell("G1").Value = "SoLe";
        dichVuSheet.Cell("H1").Value = "ThuTuIn";
        dichVuSheet.Cell("I1").Value = "LaPanel";
        dichVuSheet.Cell("J1").Value = "TuDuyet";
        dichVuSheet.Cell("K1").Value = "MaHIS";

        dichVuSheet.Cell("A2").Value = "WBC";
        dichVuSheet.Cell("B2").Value = "Bạch cầu";
        dichVuSheet.Cell("D2").Value = specimenTypeId.ToString();
        dichVuSheet.Cell("E2").Value = "10^3/uL";
        dichVuSheet.Cell("F2").Value = "so";
        dichVuSheet.Cell("G2").Value = 1;
        dichVuSheet.Cell("H2").Value = 1;

        // Sheet MapMay
        var mapMaySheet = workbook.Worksheets.Add("MapMay");
        mapMaySheet.Cell("A1").Value = "TenMay";
        mapMaySheet.Cell("B1").Value = "MaKenh";
        mapMaySheet.Cell("C1").Value = "MaChiSo";
        mapMaySheet.Cell("D1").Value = "SoLe";
        mapMaySheet.Cell("E1").Value = "HeSo";
        mapMaySheet.Cell("F1").Value = "GiuDauSoSanh";
        mapMaySheet.Cell("G1").Value = "GuiKhiMayHoi";

        // Same instrument + channel twice
        mapMaySheet.Cell("A2").Value = "SampleMachine";
        mapMaySheet.Cell("B2").Value = "CH01";
        mapMaySheet.Cell("C2").Value = "WBC";

        mapMaySheet.Cell("A3").Value = "SampleMachine";
        mapMaySheet.Cell("B3").Value = "CH01";
        mapMaySheet.Cell("C3").Value = "WBC";

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var report = await importer.ImportAsync(stream, dryRun: false);

        report.Errors.Count.ShouldBeGreaterThan(0);
        report.Errors.ShouldContain(e => e.Contains("MapMay") && e.Contains("dòng"));
    }
}
