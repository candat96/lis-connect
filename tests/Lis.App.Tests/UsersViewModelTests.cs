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
/// (không gọi lệnh nào chạm CSDL).
/// </summary>
public class UsersViewModelTests
{
    private static UsersViewModel CreateViewModel() =>
        new(new UserStore(Db.Create("Host=khong-dung-toi;Database=unused;Username=unused;Password=unused")));

    [Fact]
    public void FilteredUsers_UpdatesWhenUsersChanges()
    {
        var viewModel = CreateViewModel();

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
        var viewModel = CreateViewModel();
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
}
