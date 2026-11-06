using Lis.Core.Auth;
using Lis.Data.Auth;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Auth;

/// <summary>
/// Kiểm tra <see cref="UserStore"/>: tạo/sửa tài khoản, validate mật khẩu/tên đăng nhập trùng,
/// và luật không bao giờ để hệ thống còn 0 tài khoản Quản trị đang hoạt động.
/// </summary>
[Collection(PostgresCollection.Name)]
public class UserStoreTests(PostgresFixture fixture)
{
    private const string ValidPassword = "MatKhau123";

    private async Task<UserStore> CreateStoreAsync()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        return new UserStore(Db.Create(connectionString));
    }

    [Fact]
    public async Task Create_ThenList_ReturnsCreatedUser()
    {
        var store = await CreateStoreAsync();

        var created = await store.CreateAsync("ktv-mau", "Trần Thị Mẫu", "Kỹ thuật viên", ValidPassword, [Roles.KyThuatVien]);

        var all = await store.ListAsync();
        all.ShouldContain(u => u.Id == created.Id && u.Username == "ktv-mau" && u.Active);
    }

    [Fact]
    public async Task Create_DuplicateUsername_ThrowsVietnameseMessage()
    {
        var store = await CreateStoreAsync();
        await store.CreateAsync("trung-ten", "Người Một", null, ValidPassword, [Roles.TiepNhan]);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            store.CreateAsync("trung-ten", "Người Hai", null, ValidPassword, [Roles.TiepNhan]));

        exception.Message.ShouldBe("Tên đăng nhập đã tồn tại");
    }

    [Fact]
    public async Task Update_ChangesFullNameRolesAndActive()
    {
        var store = await CreateStoreAsync();
        await store.CreateAsync("admin-goc", "Quản Trị Gốc", null, ValidPassword, [Roles.QuanTri]);
        var user = await store.CreateAsync("ktv-sua", "Trước Khi Sửa", null, ValidPassword, [Roles.KyThuatVien]);

        await store.UpdateAsync(user.Id, "Sau Khi Sửa", "Chức danh mới", [Roles.TruongKhoa], false, null);

        var updated = (await store.ListAsync()).Single(u => u.Id == user.Id);
        updated.FullName.ShouldBe("Sau Khi Sửa");
        updated.Title.ShouldBe("Chức danh mới");
        updated.Roles.ShouldBe([Roles.TruongKhoa]);
        updated.Active.ShouldBeFalse();
    }

    [Fact]
    public async Task Update_ShortNewPassword_Throws()
    {
        var store = await CreateStoreAsync();
        await store.CreateAsync("admin-goc", "Quản Trị Gốc", null, ValidPassword, [Roles.QuanTri]);
        var user = await store.CreateAsync("ktv-mat-khau", "Nguyễn Văn Thử", null, ValidPassword, [Roles.KyThuatVien]);

        var exception = await Should.ThrowAsync<ArgumentException>(() =>
            store.UpdateAsync(user.Id, user.FullName, null, user.Roles, true, "123"));

        exception.Message.ShouldContain("Mật khẩu tối thiểu 8 ký tự");
    }

    [Fact]
    public async Task Update_LastActiveAdmin_Deactivate_Throws()
    {
        var store = await CreateStoreAsync();
        var admin = await store.CreateAsync("quan-tri-duy-nhat", "Nguyễn Văn Thử", null, ValidPassword, [Roles.QuanTri]);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            store.UpdateAsync(admin.Id, admin.FullName, null, admin.Roles, active: false, null));

        exception.Message.ShouldContain("ít nhất một tài khoản Quản trị");
    }

    [Fact]
    public async Task Update_LastActiveAdmin_RemoveQuanTriRole_Throws()
    {
        var store = await CreateStoreAsync();
        var admin = await store.CreateAsync("quan-tri-duy-nhat-2", "Nguyễn Văn Thử", null, ValidPassword, [Roles.QuanTri]);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            store.UpdateAsync(admin.Id, admin.FullName, null, [Roles.TruongKhoa], active: true, null));

        exception.Message.ShouldContain("ít nhất một tài khoản Quản trị");
    }

    [Fact]
    public async Task Update_LastActiveAdmin_KeepingQuanTriActive_Succeeds()
    {
        var store = await CreateStoreAsync();
        var admin = await store.CreateAsync("quan-tri-duy-nhat-3", "Nguyễn Văn Thử", null, ValidPassword, [Roles.QuanTri]);

        await store.UpdateAsync(admin.Id, "Tên mới", "Chức danh mới", [Roles.QuanTri], active: true, null);

        var updated = (await store.ListAsync()).Single(u => u.Id == admin.Id);
        updated.FullName.ShouldBe("Tên mới");
        updated.Active.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_DeactivateOneOfTwoActiveAdmins_Succeeds()
    {
        var store = await CreateStoreAsync();
        var admin1 = await store.CreateAsync("quan-tri-a", "Quản Trị A", null, ValidPassword, [Roles.QuanTri]);
        await store.CreateAsync("quan-tri-b", "Quản Trị B", null, ValidPassword, [Roles.QuanTri]);

        // Còn quan-tri-b đang hoạt động, nên khóa quan-tri-a phải được phép.
        await store.UpdateAsync(admin1.Id, admin1.FullName, null, admin1.Roles, active: false, null);

        var updated = (await store.ListAsync()).Single(u => u.Id == admin1.Id);
        updated.Active.ShouldBeFalse();
    }

    /// <summary>
    /// Hai quản trị A, B đang hoạt động — đồng thời A khóa B và B khóa A. Không được để cả hai
    /// đều thành công (sẽ còn 0 quản trị hoạt động): đúng một lệnh thành công, lệnh kia bị chặn
    /// bởi InvalidOperationException, và sau cùng vẫn còn đúng một quản trị đang hoạt động.
    /// Dùng hai <see cref="UserStore"/> riêng trên cùng <see cref="Db"/> (cùng pool kết nối) để
    /// mô phỏng hai máy trạm gọi cùng lúc, như race thật trên sản phẩm.
    /// </summary>
    [Fact]
    public async Task Update_ConcurrentMutualDeactivation_ExactlyOneSucceeds_OneActiveAdminRemains()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);
        var seedStore = new UserStore(db);
        var adminA = await seedStore.CreateAsync("quan-tri-doi-a", "Quản Trị A", null, ValidPassword, [Roles.QuanTri]);
        var adminB = await seedStore.CreateAsync("quan-tri-doi-b", "Quản Trị B", null, ValidPassword, [Roles.QuanTri]);

        var storeForA = new UserStore(db);
        var storeForB = new UserStore(db);

        var aDeactivatesB = Record.ExceptionAsync(() =>
            storeForA.UpdateAsync(adminB.Id, adminB.FullName, null, adminB.Roles, active: false, null)).AsTask();
        var bDeactivatesA = Record.ExceptionAsync(() =>
            storeForB.UpdateAsync(adminA.Id, adminA.FullName, null, adminA.Roles, active: false, null)).AsTask();

        var results = await Task.WhenAll(aDeactivatesB, bDeactivatesA);

        var succeeded = results.Count(ex => ex is null);
        var blocked = results.Count(ex => ex is InvalidOperationException);

        succeeded.ShouldBe(1);
        blocked.ShouldBe(1);

        var stillActiveAdmins = (await seedStore.ListAsync())
            .Count(u => u.Active && u.Roles.Contains(Roles.QuanTri));
        stillActiveAdmins.ShouldBe(1);
    }
}
