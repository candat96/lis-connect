using Lis.Data.Catalog;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Catalog;

/// <summary>
/// Kiểm tra <c>tools/seed/mau-danh-muc.xlsx</c> (sinh bằng <c>Lis.SeedTool make-seed-xlsx</c>)
/// nhập được (dryRun) vào một CSDL mới, không lỗi — tệp này được commit vào repo nên phải luôn
/// nhập được đúng khuôn của <see cref="CatalogExcelImporter"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SeedWorkbookTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Import_CommittedSeedWorkbook_DryRun_NoErrors()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);
        var importer = new CatalogExcelImporter(db);

        using var stream = File.OpenRead(FindSeedWorkbookPath());

        var report = await importer.ImportAsync(stream, dryRun: true);

        report.Errors.ShouldBeEmpty();
        report.Created.ShouldBeGreaterThan(0);
    }

    private static string FindSeedWorkbookPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LisConnect.slnx")))
        {
            directory = directory.Parent;
        }

        var repoRoot = directory?.FullName
            ?? throw new InvalidOperationException("Không tìm được gốc repo (LisConnect.slnx).");

        return Path.Combine(repoRoot, "tools", "seed", "mau-danh-muc.xlsx");
    }
}
