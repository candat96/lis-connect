using Lis.App.ViewModels.Admin;
using Lis.Core.Auth;
using Lis.Data;
using Lis.Data.Auth;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>
/// Kiểm tra <see cref="UsersViewModel"/> — <c>UserStore</c> là lớp cụ thể (gắn CSDL thật) nên
/// chỉ dựng được với một chuỗi kết nối không bao giờ dùng tới trong các kiểm thử dưới đây
/// (không gọi lệnh nào chạm CSDL, trừ các test tự chủ ý gọi, vốn sẽ thất bại nếu cố chạm CSDL —
/// các test khóa/bỏ vai trò chính mình phải chặn TRƯỚC khi gọi store).
/// </summary>
public class UsersViewModelTests
{
    private static UsersViewModel CreateViewModel(out CurrentUser currentUser)
    {
        var user = new UserInfo(Guid.NewGuid(), "quantri.thu", "Nguyễn Văn Thử", null, [Roles.QuanTri], true);
        currentUser = new CurrentUser(user);
        var store = new UserStore(Db.Create("Host=khong-dung-toi;Database=unused;Username=unused;Password=unused"));
        return new UsersViewModel(store, currentUser);
    }

    [Fact]
    public void FilteredUsers_UpdatesWhenUsersChanges()
    {
        var viewModel = CreateViewModel(out _);

        viewModel.Users =
        [
            new UserListItemViewModel(Guid.NewGuid(), "quantri.thu", "Nguyễn Văn Thử", null, [Roles.QuanTri], true),
        ];

        viewModel.FilteredUsers.Count.ShouldBe(1);
        viewModel.FilteredUsers[0].Username.ShouldBe("quantri.thu");
    }

    [Fact]
    public void FilteredUsers_RaisesPropertyChanged_WhenUsersChanges()
    {
        var viewModel = CreateViewModel(out _);
        var raised = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UsersViewModel.FilteredUsers))
            {
                raised = true;
            }
        };

        viewModel.Users = [new UserListItemViewModel(Guid.NewGuid(), "a", "A", null, [], true)];

        raised.ShouldBeTrue();
    }

    private static void SelectSelf(UsersViewModel viewModel, CurrentUser currentUser)
    {
        var self = new UserListItemViewModel(currentUser.User.Id, currentUser.User.Username, currentUser.User.FullName, null, [Roles.QuanTri], true);
        viewModel.Users = [self];
        viewModel.SelectedUser = self;
        viewModel.SelectUserCommand.Execute(null);
    }

    [Fact]
    public async Task SaveUser_DeactivatingOwnAccount_ShowsVietnameseError_AndDoesNotCallStore()
    {
        var viewModel = CreateViewModel(out var currentUser);
        SelectSelf(viewModel, currentUser);

        viewModel.EditActive = false;

        // Nếu chặn sai (gọi xuống UserStore thật), lệnh sẽ ném lỗi kết nối ("khong-dung-toi")
        // thay vì thông báo tiếng Việt dưới đây — ExecuteAsync không throw nghĩa là bị bắt trong
        // try/catch của SaveUser và gán vào ErrorMessage, nên vẫn cần so khớp đúng câu.
        await viewModel.SaveUserCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Không thể tự khóa tài khoản của chính mình");
    }

    [Fact]
    public async Task SaveUser_RemovingOwnQuanTriRole_ShowsVietnameseError_AndDoesNotCallStore()
    {
        var viewModel = CreateViewModel(out var currentUser);
        SelectSelf(viewModel, currentUser);

        viewModel.EditRoles = [Roles.TruongKhoa];

        await viewModel.SaveUserCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Không thể tự bỏ vai trò Quản trị của chính mình");
    }

    [Fact]
    public async Task SaveUser_EditingSomeoneElse_DoesNotTriggerSelfLockoutGuard()
    {
        var viewModel = CreateViewModel(out _);
        var other = new UserListItemViewModel(Guid.NewGuid(), "ktv.mau", "Trần Thị Mẫu", null, [Roles.KyThuatVien], true);
        viewModel.Users = [other];
        viewModel.SelectedUser = other;
        viewModel.SelectUserCommand.Execute(null);

        viewModel.EditActive = false;

        // Sửa người khác (không phải chính mình) không bị chặn ở ViewModel — lệnh đi xuống
        // UserStore thật và lỗi kết nối tới "khong-dung-toi" (SaveUser bắt mọi Exception và gán
        // vào ErrorMessage), không phải một trong hai thông báo tự khóa/tự bỏ vai trò.
        await viewModel.SaveUserCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldNotBe("Không thể tự khóa tài khoản của chính mình");
        viewModel.ErrorMessage.ShouldNotBe("Không thể tự bỏ vai trò Quản trị của chính mình");
    }
}
