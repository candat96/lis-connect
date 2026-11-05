using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Data.Ingest;

namespace Lis.App.ViewModels.Unmatched;

/// <summary>Một chỉ số lá của mẫu trong danh sách chọn của ô "Gán vào mẫu…".</summary>
public sealed class SampleLeafItemViewModel(SampleLeafItem item)
{
    public SampleLeafItem Item { get; } = item;

    public Guid OrderItemId => Item.OrderItemId;

    public string Label => $"{Item.ServiceCode} – {Item.ServiceName}";

    public string CurrentValueText => string.IsNullOrEmpty(Item.CurrentValue) ? "(chưa có)" : Item.CurrentValue;
}

/// <summary>
/// Ô "Gán vào mẫu…" trên màn Chưa ghép: gõ barcode + Enter → danh sách chỉ số lá của mẫu →
/// chọn một chỉ số → Gán. Việc gán thật do <see cref="UnmatchedViewModel"/> làm qua
/// <c>onConfirm</c>; ô này chỉ tra mẫu và giữ lựa chọn.
/// </summary>
public partial class AssignPanelViewModel(IUnmatchedService service, Func<Guid, Task> onConfirm) : ObservableObject
{
    public ObservableCollection<SampleLeafItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private UnmatchedRowViewModel? _row;

    [ObservableProperty]
    private string _barcodeText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private SampleLeafItemViewModel? _selectedItem;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private bool _isBusy;

    public bool IsVisible => Row is not null;

    public string Title => Row is null ? "" : $"Gán giá trị {Row.ValueText} (kênh {Row.Item.ChannelCode}, {Row.InstrumentName}) vào mẫu";

    public bool CanConfirm => SelectedItem is not null && !IsBusy;

    /// <summary>Mở ô gán cho một dòng; barcode máy gửi được điền sẵn để người dùng sửa hoặc Enter ngay.</summary>
    public void Open(UnmatchedRowViewModel row)
    {
        Row = row;
        BarcodeText = row.Item.Barcode;
        Items.Clear();
        SelectedItem = null;
        ErrorMessage = null;
        OnPropertyChanged(nameof(IsVisible));
    }

    [RelayCommand]
    public void Close()
    {
        Row = null;
        BarcodeText = "";
        Items.Clear();
        SelectedItem = null;
        ErrorMessage = null;
        OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>Enter trên ô barcode: tra mẫu và liệt kê các chỉ số lá của mẫu.</summary>
    [RelayCommand]
    public async Task LookupAsync()
    {
        if (string.IsNullOrWhiteSpace(BarcodeText))
        {
            ErrorMessage = "Nhập barcode của mẫu";
            return;
        }

        try
        {
            ErrorMessage = null;
            var items = await service.SampleItemsAsync(BarcodeText);
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(new SampleLeafItemViewModel(item));
            }

            SelectedItem = null;
            if (Items.Count == 0)
            {
                ErrorMessage = "Mẫu này không có chỉ số nào";
            }
        }
        catch (Exception ex)
        {
            Items.Clear();
            SelectedItem = null;
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    public async Task ConfirmAsync()
    {
        if (SelectedItem is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ErrorMessage = null;
            await onConfirm(SelectedItem.OrderItemId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Gán thất bại: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
