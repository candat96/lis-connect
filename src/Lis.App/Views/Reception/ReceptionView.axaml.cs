using Avalonia.Controls;
using Avalonia.Input;
using Lis.App.ViewModels.Reception;
using Lis.Data.Orders;

namespace Lis.App.Views.Reception;

/// <summary>
/// Màn Tiếp nhận. Enter trên ô tìm người bệnh gọi tìm kiếm; chọn một dòng kết quả chọn luôn
/// người bệnh đó; Enter trên ô mã dịch vụ thêm dịch vụ (nguyên tắc "tối ưu bàn phím" — xem
/// <see cref="Lis.App.Services.KeyboardFormBehavior"/> cho Enter chuyển ô thông thường ở các
/// ô khác); F9 lưu chỉ định và cấp barcode.
/// </summary>
public partial class ReceptionView : UserControl
{
    public ReceptionView()
    {
        InitializeComponent();
        PatientSearchBox.KeyDown += OnPatientSearchKeyDown;
        PatientResultsList.SelectionChanged += OnPatientResultSelected;
        ServiceCodeBox.KeyDown += OnServiceCodeKeyDown;
        SuggestionsList.SelectionChanged += OnSuggestionSelected;
        KeyDown += OnViewKeyDown;
    }

    private void OnPatientSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ReceptionViewModel viewModel)
        {
            return;
        }

        viewModel.SearchPatientsCommand.Execute(null);
        e.Handled = true;
    }

    private void OnPatientResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ReceptionViewModel viewModel && PatientResultsList.SelectedItem is PatientHit hit)
        {
            viewModel.PickPatientCommand.Execute(hit);
        }
    }

    private void OnServiceCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ReceptionViewModel viewModel)
        {
            return;
        }

        viewModel.AddServiceByCodeCommand.Execute(null);
        e.Handled = true;
    }

    private void OnSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ReceptionViewModel viewModel && SuggestionsList.SelectedItem is Lis.Data.Catalog.ServiceInfo service)
        {
            viewModel.PickServiceSuggestionCommand.Execute(service);
        }
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F9 || DataContext is not ReceptionViewModel viewModel)
        {
            return;
        }

        if (viewModel.SaveCommand.CanExecute(null))
        {
            viewModel.SaveCommand.Execute(null);
        }

        e.Handled = true;
    }
}
