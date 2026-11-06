using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Core.Auth;
using Lis.Data.Auth;

namespace Lis.App.ViewModels.Admin;

/// <summary>
/// Quản lý tài khoản: danh sách người dùng, thêm/sửa/khóa mở.
/// </summary>
public sealed partial class UsersViewModel : ObservableObject
{
    private readonly UserStore _userStore;

    [ObservableProperty]
    private string errorMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredUsers))]
    private List<UserListItemViewModel> users = [];

    [ObservableProperty]
    private UserListItemViewModel? selectedUser;

    [ObservableProperty]
    private string filterText = "";

    [ObservableProperty]
    private string editUsername = "";

    [ObservableProperty]
    private string editFullName = "";

    [ObservableProperty]
    private string? editTitle;

    [ObservableProperty]
    private List<string> editRoles = [];

    [ObservableProperty]
    private bool editActive = true;

    [ObservableProperty]
    private string newPassword = "";

    [ObservableProperty]
    private bool isEditingNew = false;

    [ObservableProperty]
    private bool editRoleQuanTri;

    [ObservableProperty]
    private bool editRoleTiepNhan;

    [ObservableProperty]
    private bool editRoleKyThuatVien;

    [ObservableProperty]
    private bool editRoleTruongKhoa;

    public List<string> AvailableRoles { get; } = [.. Roles.All];

    public IReadOnlyList<UserListItemViewModel> FilteredUsers =>
        Users.Where(u => string.IsNullOrEmpty(FilterText) ||
                        u.Username.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                        u.FullName.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public UsersViewModel(UserStore userStore)
    {
        _userStore = userStore;
        _ = LoadUsersCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    public async Task LoadUsers()
    {
        try
        {
            ErrorMessage = "";
            var userInfos = await _userStore.ListAsync();
            Users = userInfos.Select(u => new UserListItemViewModel(
                u.Id, u.Username, u.FullName, u.Title, u.Roles, u.Active)).ToList();
            SelectedUser = Users.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lỗi tải danh sách: {ex.Message}";
        }
    }

    [RelayCommand]
    public void NewUser()
    {
        IsEditingNew = true;
        EditUsername = "";
        EditFullName = "";
        EditTitle = "";
        EditRoles = [];
        EditActive = true;
        NewPassword = "";
        SelectedUser = null;
        ErrorMessage = "";
        UpdateRoleCheckboxes();
    }

    [RelayCommand]
    public void SelectUser()
    {
        if (SelectedUser is null)
        {
            IsEditingNew = false;
            return;
        }

        IsEditingNew = false;
        EditUsername = SelectedUser.Username;
        EditFullName = SelectedUser.FullName;
        EditTitle = SelectedUser.Title;
        EditRoles = SelectedUser.Roles.ToList();
        EditActive = SelectedUser.Active;
        NewPassword = "";
        ErrorMessage = "";
        UpdateRoleCheckboxes();
    }

    [RelayCommand]
    public async Task SaveUser()
    {
        try
        {
            ErrorMessage = "";

            if (string.IsNullOrWhiteSpace(EditFullName))
            {
                ErrorMessage = "Nhập họ tên";
                return;
            }

            if (IsEditingNew)
            {
                if (string.IsNullOrWhiteSpace(EditUsername))
                {
                    ErrorMessage = "Nhập tên đăng nhập";
                    return;
                }

                if (string.IsNullOrWhiteSpace(NewPassword))
                {
                    ErrorMessage = "Nhập mật khẩu";
                    return;
                }

                if (EditRoles.Count == 0)
                {
                    ErrorMessage = "Chọn ít nhất một vai trò";
                    return;
                }

                await _userStore.CreateAsync(EditUsername, EditFullName, EditTitle, NewPassword, EditRoles);
            }
            else if (SelectedUser is not null)
            {
                await _userStore.UpdateAsync(
                    SelectedUser.Id,
                    EditFullName,
                    EditTitle,
                    EditRoles,
                    EditActive,
                    string.IsNullOrWhiteSpace(NewPassword) ? null : NewPassword);
            }

            await LoadUsersCommand.ExecuteAsync(null);
            IsEditingNew = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    public void ToggleRole(string role)
    {
        if (EditRoles.Contains(role))
            EditRoles.Remove(role);
        else
            EditRoles.Add(role);

        UpdateRoleCheckboxes();
    }

    private void UpdateRoleCheckboxes()
    {
        EditRoleQuanTri = EditRoles.Contains(Roles.QuanTri);
        EditRoleTiepNhan = EditRoles.Contains(Roles.TiepNhan);
        EditRoleKyThuatVien = EditRoles.Contains(Roles.KyThuatVien);
        EditRoleTruongKhoa = EditRoles.Contains(Roles.TruongKhoa);
    }

    partial void OnEditRoleQuanTriChanged(bool value) => SyncRole(Roles.QuanTri, value);
    partial void OnEditRoleTiepNhanChanged(bool value) => SyncRole(Roles.TiepNhan, value);
    partial void OnEditRoleKyThuatVienChanged(bool value) => SyncRole(Roles.KyThuatVien, value);
    partial void OnEditRoleTruongKhoaChanged(bool value) => SyncRole(Roles.TruongKhoa, value);

    private void SyncRole(string role, bool isChecked)
    {
        if (isChecked && !EditRoles.Contains(role))
            EditRoles.Add(role);
        else if (!isChecked && EditRoles.Contains(role))
            EditRoles.Remove(role);
    }

    [RelayCommand]
    public void Cancel()
    {
        IsEditingNew = false;
        SelectedUser = null;
        NewPassword = "";
        ErrorMessage = "";
    }

    partial void OnFilterTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredUsers));
    }
}

public sealed record UserListItemViewModel(
    Guid Id,
    string Username,
    string FullName,
    string? Title,
    IReadOnlyList<string> Roles,
    bool Active);
