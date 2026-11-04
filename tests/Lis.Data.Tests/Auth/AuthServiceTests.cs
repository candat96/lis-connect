using Dapper;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Auth;

/// <summary>
/// Kiểm tra băm mật khẩu Argon2id và luồng đăng nhập: sai mật khẩu, khóa sau 5 lần sai
/// liên tiếp trong 15 phút, tài khoản ngừng hoạt động, đặt lại số lần sai khi đăng nhập thành công.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuthServiceTests(PostgresFixture fixture)
{
    private const string ValidPassword = "MatKhau123";

    [Fact]
    public void Hash_VerifyRoundTrip_AndRejectsWrong()
    {
        var hasher = new PasswordHasher();
        var hash = hasher.Hash(ValidPassword);

        hasher.Verify(ValidPassword, hash).ShouldBeTrue();
        hasher.Verify("MatKhauSai123", hash).ShouldBeFalse();
    }

    [Fact]
    public async Task Create_ShortPassword_Throws()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var store = new UserStore(Db.Create(connectionString));

        var exception = await Should.ThrowAsync<ArgumentException>(() =>
            store.CreateAsync("ktv-ngan", "Nguyễn Văn Thử", null, "1234567", [Roles.KyThuatVien]));

        exception.Message.ShouldContain("Mật khẩu tối thiểu 8 ký tự");
    }

    [Fact]
    public async Task Login_SuccessResetsFailedCount()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);
        var store = new UserStore(db);
        var user = await store.CreateAsync("ktv-thanh-cong", "Nguyễn Văn Thử", null, ValidPassword, [Roles.KyThuatVien]);

        var service = new AuthService(db, new PasswordHasher(), new FakeTimeProvider());
        await service.LoginAsync(user.Username, "sai-mat-khau");

        var result = await service.LoginAsync(user.Username, ValidPassword);

        result.Ok.ShouldBeTrue();
        result.Code.ShouldBe("ok");
        result.User.ShouldNotBeNull();
        result.User.Username.ShouldBe(user.Username);

        await using var connection = await db.OpenAsync(TestContext.Current.CancellationToken);
        var failedCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT failed_count FROM app_user WHERE id = @id",
                new { user.Id },
                cancellationToken: TestContext.Current.CancellationToken));
        failedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Login_Inactive_ReturnsNgung()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);
        var store = new UserStore(db);
        var user = await store.CreateAsync("ktv-ngung", "Nguyễn Văn Thử", null, ValidPassword, [Roles.KyThuatVien]);

        await store.UpdateAsync(user.Id, user.FullName, user.Title, user.Roles, active: false, newPassword: null);

        var service = new AuthService(db, new PasswordHasher(), new FakeTimeProvider());
        var result = await service.LoginAsync(user.Username, ValidPassword);

        result.Ok.ShouldBeFalse();
        result.Code.ShouldBe("ngung");
        result.Message.ShouldBe("Tài khoản đã ngừng hoạt động");
    }

    [Fact]
    public async Task Login_FifthFailureLocksFor15Minutes()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);
        var store = new UserStore(db);
        var user = await store.CreateAsync("ktv-khoa", "Nguyễn Văn Thử", null, ValidPassword, [Roles.KyThuatVien]);

        var clock = new FakeTimeProvider();
        var service = new AuthService(db, new PasswordHasher(), clock);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var failedResult = await service.LoginAsync(user.Username, "sai-mat-khau");
            failedResult.Code.ShouldBe("sai");
        }

        var fifthResult = await service.LoginAsync(user.Username, "sai-mat-khau");
        fifthResult.Code.ShouldBe("khoa");

        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        var resultAfterLockExpires = await service.LoginAsync(user.Username, ValidPassword);
        resultAfterLockExpires.Ok.ShouldBeTrue();
        resultAfterLockExpires.Code.ShouldBe("ok");
    }
}
