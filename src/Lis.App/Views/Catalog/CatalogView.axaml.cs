using Avalonia.Controls;
using Avalonia.Input;
using Lis.App.ViewModels.Catalog;

namespace Lis.App.Views.Catalog;

/// <summary>
/// Màn Danh mục dịch vụ. Enter trên ô mã chỉ số con thêm luôn chỉ số đó vào panel đang chọn
/// (nguyên tắc "tối ưu bàn phím": nhập mã + Enter) — hành vi này riêng cho ô này, khác với
/// <see cref="Lis.App.Services.KeyboardFormBehavior"/> (chỉ chuyển ô, gắn ở <c>MainWindow</c>).
/// </summary>
public partial class CatalogView : UserControl
{
    public CatalogView()
    {
        InitializeComponent();
        AddChildBox.KeyDown += OnAddChildBoxKeyDown;
        ServiceList.SelectionChanged += OnServiceSelectionChanged;
    }

    private void OnServiceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is CatalogViewModel viewModel)
        {
            viewModel.LoadSelectedDetailCommand.Execute(null);
        }
    }

    /// <summary>Chuyển tab hiện hành — dùng cho công cụ chụp ảnh (tools/Lis.Screenshots).</summary>
    public void SelectTab(int index) => Tabs.SelectedIndex = index;

    private void OnAddChildBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || AddChildBox.DataContext is not ChildrenEditorViewModel childrenEditor)
        {
            return;
        }

        childrenEditor.AddChildCommand.Execute(null);
        e.Handled = true;
    }
}
