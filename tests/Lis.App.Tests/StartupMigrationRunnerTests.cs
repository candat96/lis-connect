using Lis.App.Services;
using Lis.Data;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>
/// Kiểm tra <see cref="StartupMigrationRunner"/> — tách khỏi App.StartUp để kiểm thử được bằng
/// một hàm migrate giả, không cần CSDL thật hay Avalonia. Trọng tâm: CSDL không kết nối được
/// lúc khởi động (Migrator.Upgrade ném ngoại lệ, vd từ EnsureUuidv7Compat's connection.Open())
/// không được ném ra ngoài, phải trở thành một kết quả thất bại bình thường kèm ngoại lệ gốc để
/// App.StartUp ghi log và hiện hộp thoại thay vì crash ứng dụng.
/// </summary>
public class StartupMigrationRunnerTests
{
    [Fact]
    public void Run_MigrateSucceeds_ReturnsSuccess()
    {
        var result = StartupMigrationRunner.Run(() => new MigrationResult(true, ["0001_khoi_tao.sql"], null));

        result.Ok.ShouldBeTrue();
        result.Outcome.ShouldBe(StartupMigrationOutcome.Success);
        result.ErrorMessage.ShouldBeNull();
        result.Exception.ShouldBeNull();
    }

    [Fact]
    public void Run_MigrateReturnsNotOk_ReturnsFailedWithErrorMessageAndNoException()
    {
        var result = StartupMigrationRunner.Run(() => new MigrationResult(false, [], "thiếu quyền tạo bảng"));

        result.Ok.ShouldBeFalse();
        result.Outcome.ShouldBe(StartupMigrationOutcome.Failed);
        result.ErrorMessage.ShouldBe("thiếu quyền tạo bảng");
        result.Exception.ShouldBeNull();
    }

    [Fact]
    public void Run_MigrateThrows_ReturnsFailedWithoutThrowing()
    {
        var thrown = new InvalidOperationException("Không kết nối được CSDL");

        var result = StartupMigrationRunner.Run(() => throw thrown);

        result.Ok.ShouldBeFalse();
        result.Outcome.ShouldBe(StartupMigrationOutcome.Failed);
        result.ErrorMessage.ShouldBe(thrown.Message);
        result.Exception.ShouldBeSameAs(thrown);
    }
}
