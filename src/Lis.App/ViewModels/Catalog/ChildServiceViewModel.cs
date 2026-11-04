using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lis.App.ViewModels.Catalog;

/// <summary>
/// Một dòng trong tab "Chỉ số con" của một dịch vụ cha (panel). Các lệnh xóa/đổi thứ tự gọi
/// ngược lại <see cref="CatalogViewModel"/> qua callback truyền vào constructor, để XAML chỉ
/// cần bind thẳng <c>Command="{Binding RemoveCommand}"</c> trên chính dòng, không cần tìm
/// ViewModel cha trong cây giao diện.
/// </summary>
public sealed partial class ChildServiceViewModel : ObservableObject
{
    public ChildServiceViewModel(Guid id, string code, string name, Action<ChildServiceViewModel> remove, Action<ChildServiceViewModel> moveUp, Action<ChildServiceViewModel> moveDown)
    {
        Id = id;
        Code = code;
        Name = name;
        _remove = remove;
        _moveUp = moveUp;
        _moveDown = moveDown;
    }

    private readonly Action<ChildServiceViewModel> _remove;
    private readonly Action<ChildServiceViewModel> _moveUp;
    private readonly Action<ChildServiceViewModel> _moveDown;

    public Guid Id { get; }

    public string Code { get; }

    public string Name { get; }

    [RelayCommand]
    private void Remove() => _remove(this);

    [RelayCommand]
    private void MoveUp() => _moveUp(this);

    [RelayCommand]
    private void MoveDown() => _moveDown(this);
}
