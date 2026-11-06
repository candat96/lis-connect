using Dapper;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Data.His;
using Lis.Data.Local;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.His;

/// <summary>Kiểm tra bảng <c>his_profile</c>: lưu/đọc, mã hóa bí mật, giữ bí mật cũ khi để trống.</summary>
[Collection(PostgresCollection.Name)]
public class HisProfileStoreTests(PostgresFixture fixture)
{
    private async Task<(HisProfileStore Store, Db Db, Guid ActorId)> CreateStoreAsync()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);
        var users = new UserStore(db);
        var actor = await users.CreateAsync("qt-mau", "Quản Trị Mẫu", null, "MatKhau123", [Roles.QuanTri]);

        var protector = new DevKeySecretProtector(Path.Combine(Path.GetTempPath(), "lis-connect-tests-his-" + Guid.NewGuid()));
        var store = new HisProfileStore(db, protector);
        return (store, db, actor.Id);
    }

    [Fact]
    public async Task GetAsync_NoRow_ReturnsNull()
    {
        var (store, _, _) = await CreateStoreAsync();

        (await store.GetAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Save_ThenGet_RoundTrips_SecretEncrypted()
    {
        var (store, db, actorId) = await CreateStoreAsync();

        var profile = HisProfile.Empty with
        {
            Enabled = true,
            Name = "HIS Thử",
            BaseUrl = "https://his.local",
            AuthType = "bearer-login",
            Secret = "Bi-Mat-123",
            Username = "tich-hop",
            LoginPath = "/login",
            OrdersMode = "pull",
            OrdersPath = "/orders",
            PollSeconds = 30,
        };

        await store.SaveAsync(profile, actorId);

        var loaded = await store.GetAsync();
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("HIS Thử");
        loaded.Secret.ShouldBe("Bi-Mat-123");
        loaded.OrdersPath.ShouldBe("/orders");

        await using var connection = await db.OpenAsync(TestContext.Current.CancellationToken);
        var raw = await connection.ExecuteScalarAsync<string?>(
            new Dapper.CommandDefinition(
                "SELECT secret_protected FROM his_profile WHERE id = 1",
                cancellationToken: TestContext.Current.CancellationToken));
        raw.ShouldNotBeNull();
        raw.ShouldNotContain("Bi-Mat-123");
    }

    [Fact]
    public async Task Save_WithEmptySecret_KeepsExistingSecret()
    {
        var (store, _, actorId) = await CreateStoreAsync();

        var first = HisProfile.Empty with { Name = "HIS", BaseUrl = "https://a", AuthType = "api-key", Secret = "Khoa-API-1" };
        await store.SaveAsync(first, actorId);

        var second = (await store.GetAsync())! with { Name = "HIS đổi tên", Secret = null };
        await store.SaveAsync(second, actorId);

        var loaded = await store.GetAsync();
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("HIS đổi tên");
        loaded.Secret.ShouldBe("Khoa-API-1");
    }

    [Fact]
    public async Task ToString_RedactsSecret()
    {
        var profile = HisProfile.Empty with { Secret = "Bi-Mat-Khong-Duoc-Lo" };

        profile.ToString().ShouldNotContain("Bi-Mat-Khong-Duoc-Lo");
    }

    [Fact]
    public async Task GetAsync_SecretUndecryptable_TreatsAsNoSecret()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);
        var users = new UserStore(db);
        var actor = await users.CreateAsync("qt-mau2", "Quản Trị Mẫu Hai", null, "MatKhau123", [Roles.QuanTri]);

        var directoryA = Path.Combine(Path.GetTempPath(), "lis-connect-tests-his-" + Guid.NewGuid());
        var protectorA = new DevKeySecretProtector(directoryA);
        var storeA = new HisProfileStore(db, protectorA);
        await storeA.SaveAsync(HisProfile.Empty with { Name = "HIS", BaseUrl = "https://a", Secret = "Bi-Mat" }, actor.Id);

        // Mô phỏng mở lại ở máy khác (khóa mã hóa khác) — giải mã thất bại phải trả về null,
        // không ném lỗi.
        var directoryB = Path.Combine(Path.GetTempPath(), "lis-connect-tests-his-" + Guid.NewGuid());
        var protectorB = new DevKeySecretProtector(directoryB);
        var storeB = new HisProfileStore(db, protectorB);

        var loaded = await storeB.GetAsync();
        loaded.ShouldNotBeNull();
        loaded.Secret.ShouldBeNull();
    }
}
