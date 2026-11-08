using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Lis.App.ViewModels.Reception;

namespace Lis.App.Views.Reception;

/// <summary>
/// Màn Tiếp nhận. F2 / nút "Người bệnh" mở <see cref="PatientDialog"/>; Enter trên ô mã dịch vụ
/// thêm dịch vụ; chọn một dòng gợi ý thêm dịch vụ đó; F9 lưu chỉ định, cấp barcode, in tem.
/// </summary>
public partial class ReceptionView : UserControl
{
    public ReceptionView()
    {
        InitializeComponent();
        PatientButton.Click += OnPatientClick;
        ServiceCodeBox.KeyDown += OnServiceCodeKeyDown;
        SuggestionsList.SelectionChanged += OnSuggestionSelected;
        KeyDown += OnViewKeyDown;
    }

    private async void OnPatientClick(object? sender, RoutedEventArgs e) => await OpenPatientDialogAsync();

    private async Task OpenPatientDialogAsync()
    {
        if (DataContext is not ReceptionViewModel viewModel || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        if (viewModel.IsSaved)
        {
            viewModel.NewOrderCommand.Execute(null);
        }

        await new PatientDialog { DataContext = viewModel }.ShowDialog(owner);
        ServiceCodeBox.Focus();
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

    private async void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ReceptionViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.F2)
        {
            e.Handled = true;
            await OpenPatientDialogAsync();
        }
        else if (e.Key == Key.F9)
        {
            e.Handled = true;
            if (viewModel.SaveCommand.CanExecute(null))
            {
                viewModel.SaveCommand.Execute(null);
            }
        }
    }
}
