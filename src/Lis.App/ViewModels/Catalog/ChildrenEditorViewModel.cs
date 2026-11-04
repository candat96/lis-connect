using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Data.Catalog;

namespace Lis.App.ViewModels.Catalog;

/// <summary>
/// ViewModel cho tab "Chỉ số con" của màn Danh mục: thêm chỉ số con bằng mã (+ Enter — xem
/// <c>CatalogView.axaml.cs</c>), xóa, đổi thứ tự. Mỗi thao tác lưu ngay xuống CSDL qua
/// <see cref="CatalogStore.SetChildrenAsync"/> (không có nút "Lưu" riêng cho tab này).
/// Dùng <see cref="LoadForService"/> để nạp theo dịch vụ cha đang chọn ở <c>CatalogViewModel</c>
/// mà không gọi CSDL (danh sách dịch vụ đã có sẵn), và <see cref="Clear"/> khi không chọn gì.
/// </summary>
public partial class ChildrenEditorViewModel : ObservableObject
{
    private readonly CatalogStore _store;
    private Guid _panelId;

    public ChildrenEditorViewModel(CatalogStore store)
    {
        _store = store;
    }

    public ObservableCollection<ChildServiceViewModel> Children { get; } = [];

    [ObservableProperty]
    private string _addChildCode = "";

    [ObservableProperty]
    private string? _childError;

    public void LoadForService(ServiceInfo service, IReadOnlyList<ServiceListItemViewModel> allServices)
    {
        _panelId = service.Id;
        ChildError = null;
        Children.Clear();

        foreach (var childId in service.ChildIds)
        {
            var child = allServices.FirstOrDefault(s => s.Id == childId);
            if (child is not null)
            {
                Children.Add(NewRow(child.Id, child.Code, child.Name));
            }
        }
    }

    public void Clear()
    {
        _panelId = Guid.Empty;
        ChildError = null;
        AddChildCode = "";
        Children.Clear();
    }

    private ChildServiceViewModel NewRow(Guid id, string code, string name) =>
        new(id, code, name, remove: RemoveChild, moveUp: MoveChildUp, moveDown: MoveChildDown);

    [RelayCommand]
    private async Task AddChildAsync()
    {
        ChildError = null;

        if (_panelId == Guid.Empty)
        {
            ChildError = "Chọn dịch vụ cha (panel) trước";
            return;
        }

        if (string.IsNullOrWhiteSpace(AddChildCode))
        {
            return;
        }

        try
        {
            var child = await _store.FindByCodeAsync(AddChildCode.Trim());
            if (child is null)
            {
                ChildError = "Không tìm thấy mã dịch vụ này";
                return;
            }

            if (child.Id == _panelId)
            {
                ChildError = "Dịch vụ không thể là con của chính nó";
                return;
            }

            if (child.IsPanel)
            {
                ChildError = "Chỉ số con không được là một dịch vụ cha (panel)";
                return;
            }

            if (Children.Any(c => c.Id == child.Id))
            {
                ChildError = "Chỉ số này đã có trong danh sách";
                return;
            }

            Children.Add(NewRow(child.Id, child.Code, child.Name));
            await PersistAsync();
            AddChildCode = "";
        }
        catch (Exception ex)
        {
            ChildError = $"Không thêm được chỉ số con: {ex.Message}";
        }
    }

    private void RemoveChild(ChildServiceViewModel child)
    {
        Children.Remove(child);
        _ = PersistAsync();
    }

    private void MoveChildUp(ChildServiceViewModel child)
    {
        var index = Children.IndexOf(child);
        if (index <= 0)
        {
            return;
        }

        Children.Move(index, index - 1);
        _ = PersistAsync();
    }

    private void MoveChildDown(ChildServiceViewModel child)
    {
        var index = Children.IndexOf(child);
        if (index < 0 || index >= Children.Count - 1)
        {
            return;
        }

        Children.Move(index, index + 1);
        _ = PersistAsync();
    }

    private async Task PersistAsync()
    {
        if (_panelId == Guid.Empty)
        {
            return;
        }

        try
        {
            await _store.SetChildrenAsync(_panelId, Children.Select(c => c.Id).ToList());
        }
        catch (Exception ex)
        {
            ChildError = $"Không lưu được danh sách chỉ số con: {ex.Message}";
        }
    }
}
