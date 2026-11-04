using Lis.Core.Auth;
using Lis.Data.Local;
using Lis.Data.Setup;
using Npgsql;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Setup;

/// <summary>
/// Kiểm tra dịch thông báo lỗi kết nối sang tiếng Việt và luồng tạo tài khoản quản trị đầu tiên.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SetupServiceTests(PostgresFixture fixture)
{
    private const string ValidPassword = "MatKhau123";

    [Fact]
    public async Task Test_WrongPassword_VietnameseMessage()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        var settings = new DbSettings(
            builder.Host!,
            builder.Port,
            builder.Database!,
            builder.Username!,
            "mat-khau-sai",
            RequireSsl: false);

        var service = new SetupService();
        var result = await service.TestAsync(settings);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldBe("Sai tài khoản hoặc mật khẩu CSDL");
    }

    [Fact]
    public async Task Test_DatabaseDoesNotExist_VietnameseMessage()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        var settings = new DbSettings(
            builder.Host!,
            builder.Port,
            "csdl-khong-ton-tai",
            builder.Username!,
            builder.Password!,
            RequireSsl: false);

        var service = new SetupService();
        var result = await service.TestAsync(settings);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldBe("CSDL csdl-khong-ton-tai chưa được tạo trên máy chủ");
    }

    [Fact]
    public async Task Test_HostUnreachable_VietnameseMessage()
    {
        var settings = new DbSettings("host-khong-ton-tai.invalid", 5432, "lis", "lis_user", "mat-khau", RequireSsl: false);

        var service = new SetupService();
        var result = await service.TestAsync(settings);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldBe("Không kết nối được máy chủ CSDL host-khong-ton-tai.invalid:5432");
    }

    [Fact]
    public async Task Test_Ok_ReportsDatabaseEmpty()
    {
        var connectionString = await fixture.NewEmptyDatabaseAsync();
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        var settings = new DbSettings(
            builder.Host!,
            builder.Port,
            builder.Database!,
            builder.Username!,
            builder.Password!,
            RequireSsl: false);

        var service = new SetupService();
        var result = await service.TestAsync(settings);

        result.Ok.ShouldBeTrue();
        result.DatabaseEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task FirstAdmin_OnlyWhenNoUsers()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var settings = ToDbSettings(connectionString);
        var service = new SetupService();

        (await service.NeedsFirstAdminAsync(settings)).ShouldBeTrue();

        await service.CreateFirstAdminAsync(settings, "quan-tri-01", "Nguyễn Văn Thử", ValidPassword);

        (await service.NeedsFirstAdminAsync(settings)).ShouldBeFalse();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            service.CreateFirstAdminAsync(settings, "quan-tri-02", "Trần Thị Thử", ValidPassword));
        exception.Message.ShouldContain("đã có tài khoản");
    }

    [Fact]
    public async Task FirstAdmin_GetsQuanTriRole()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var settings = ToDbSettings(connectionString);
        var service = new SetupService();

        await service.CreateFirstAdminAsync(settings, "quan-tri-moi", "Nguyễn Văn Thử", ValidPassword);

        var db = Db.Create(connectionString);
        var store = new Lis.Data.Auth.UserStore(db);
        var users = await store.ListAsync();
        var admin = users.Single(u => u.Username == "quan-tri-moi");

        admin.Roles.ShouldBe([Roles.QuanTri]);
    }

    private static DbSettings ToDbSettings(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return new DbSettings(builder.Host!, builder.Port, builder.Database!, builder.Username!, builder.Password!, RequireSsl: false);
    }
}
