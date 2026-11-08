using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Lis.App.ViewModels.Reception;
using Lis.Data.Orders;

namespace Lis.App.Views.Reception;

/// <summary>
/// Hộp người bệnh của màn Tiếp nhận: Enter ở ô tìm → tìm; chọn một dòng → chọn người bệnh đó
/// và đóng; rời ô ngày sinh → tự định dạng dd/MM/yyyy; "Xong" (hoặc Enter ở ô ngày sinh) kiểm
/// tra người bệnh mới rồi đóng.
/// </summary>
public partial class PatientDialog : Window
{
    public PatientDialog()
    {
        InitializeComponent();
        SearchBox.KeyDown += OnSearchKeyDown;
        ResultsList.SelectionChanged += OnResultSelected;
        DobBox.LostFocus += (_, _) => (DataContext as ReceptionViewModel)?.FormatDob();
        Opened += (_, _) => SearchBox.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ReceptionViewModel viewModel)
        {
            viewModel.PatientConfirmed += Close;
            Closed += (_, _) => viewModel.PatientConfirmed -= Close;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ReceptionViewModel viewModel)
        {
            viewModel.SearchPatientsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ReceptionViewModel viewModel && ResultsList.SelectedItem is PatientHit hit)
        {
            viewModel.PickPatientCommand.Execute(hit);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
