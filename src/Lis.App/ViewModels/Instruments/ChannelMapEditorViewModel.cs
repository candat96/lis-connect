using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Core;
using Lis.Data.Catalog;
using Lis.Data.Instruments;

namespace Lis.App.ViewModels.Instruments;

/// <summary>Một dòng map kênh trên bảng bên phải màn "Máy &amp; map". "Sửa" nạp dòng vào form bên dưới; "Xóa" gọi ngược lại editor qua callback.</summary>
public sealed partial class ChannelRowViewModel(ChannelMapItem item, Action<ChannelRowViewModel> edit, Func<ChannelRowViewModel, Task> remove)
{
    private const string Dash = "—";

    public ChannelMapItem Item { get; } = item;

    public Guid Id => Item.Map.Id;

    public string ChannelCode => Item.Map.ChannelCode;

    public string ServiceCode => Item.ServiceCode;

    public string ServiceText => $"{Item.ServiceCode} – {Item.ServiceName}";

    public string DecimalsText => Item.Map.Decimals?.ToString(CultureInfo.InvariantCulture) ?? Dash;

    public string FactorText => Item.Map.Factor?.ToString(CultureInfo.InvariantCulture) ?? Dash;

    public string KeepComparatorText => Item.Map.KeepComparator ? "Có" : Dash;

    public string SendOnQueryText => Item.Map.SendOnQuery ? "Có" : Dash;

    [RelayCommand]
    private void Edit() => edit(this);

    [RelayCommand]
    private Task RemoveAsync() => remove(this);
}

/// <summary>
/// Bảng map kênh của máy đang chọn: thêm một dòng (mã kênh + mã chỉ số tra qua
/// <see cref="IServiceLookup"/>, số lẻ, hệ số, giữ dấu so sánh, gửi khi máy hỏi) lưu ngay, xóa
/// dòng. Chọn "Sửa" trên một dòng nạp dòng đó vào form, nút đổi thành "Lưu kênh" để cập nhật
/// thay vì thêm mới. Trùng kênh hoặc hệ số không hợp lệ thì lỗi của store hiện nguyên văn
/// ("Mã kênh … đã map cho chỉ số …", "Hệ số phải lớn hơn 0").
/// </summary>
public partial class ChannelMapEditorViewModel(IInstrumentStore store, IServiceLookup services) : ObservableObject
{
    private Guid _instrumentId;
    private Guid _editingId = Guid.Empty;

    public ObservableCollection<ChannelRowViewModel> Channels { get; } = [];

    [ObservableProperty]
    private string _newChannelCode = "";

    [ObservableProperty]
    private string _newServiceCode = "";

    [ObservableProperty]
    private string _newDecimalsText = "";

    [ObservableProperty]
    private string _newFactorText = "";

    [ObservableProperty]
    private bool _newKeepComparator;

    [ObservableProperty]
    private bool _newSendOnQuery;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstrument))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonLabel))]
    [NotifyPropertyChangedFor(nameof(IsEditingExisting))]
    private bool _isEditing;

    public bool HasInstrument => IsEnabled;

    public bool IsEditingExisting => IsEditing;

    public string SaveButtonLabel => IsEditing ? "Lưu kênh" : "+ Thêm kênh";

    public async Task LoadAsync(Guid instrumentId)
    {
        _instrumentId = instrumentId;
        IsEnabled = instrumentId != Guid.Empty;
        ErrorMessage = null;
        Channels.Clear();
        ClearNewRow();

        if (!IsEnabled)
        {
            return;
        }

        try
        {
            foreach (var item in await store.ChannelsAsync(instrumentId))
            {
                Channels.Add(CreateRow(item));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không tải được map kênh: {ex.Message}";
        }
    }

    public void Clear()
    {
        _instrumentId = Guid.Empty;
        IsEnabled = false;
        ErrorMessage = null;
        Channels.Clear();
        ClearNewRow();
    }

    /// <summary>Nạp một dòng đã có vào form để sửa (đổi "+ Thêm kênh" thành "Lưu kênh").</summary>
    private void SelectForEdit(ChannelRowViewModel row)
    {
        ErrorMessage = null;
        _editingId = row.Id;
        IsEditing = true;
        NewChannelCode = row.ChannelCode;
        NewServiceCode = row.ServiceCode;
        NewDecimalsText = row.Item.Map.Decimals?.ToString(CultureInfo.InvariantCulture) ?? "";
        NewFactorText = row.Item.Map.Factor?.ToString(CultureInfo.InvariantCulture) ?? "";
        NewKeepComparator = row.Item.Map.KeepComparator;
        NewSendOnQuery = row.Item.Map.SendOnQuery;
    }

    [RelayCommand]
    private void CancelEdit() => ClearNewRow();

    [RelayCommand]
    public async Task AddChannelAsync()
    {
        if (_instrumentId == Guid.Empty)
        {
            ErrorMessage = "Lưu máy trước khi map kênh";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewChannelCode) || string.IsNullOrWhiteSpace(NewServiceCode))
        {
            ErrorMessage = "Nhập mã kênh và mã chỉ số";
            return;
        }

        int? decimals = null;
        if (!string.IsNullOrWhiteSpace(NewDecimalsText))
        {
            if (!int.TryParse(NewDecimalsText.Trim(), out var parsedDecimals) || parsedDecimals < 0)
            {
                ErrorMessage = "Số lẻ phải là số nguyên không âm";
                return;
            }

            decimals = parsedDecimals;
        }

        decimal? factor = null;
        if (!string.IsNullOrWhiteSpace(NewFactorText))
        {
            if (!DecimalInput.TryParse(NewFactorText, out var parsedFactor))
            {
                ErrorMessage = "Hệ số không hợp lệ";
                return;
            }

            if (parsedFactor <= 0)
            {
                ErrorMessage = "Hệ số phải lớn hơn 0";
                return;
            }

            factor = parsedFactor;
        }

        try
        {
            ErrorMessage = null;
            var service = await services.FindByCodeAsync(NewServiceCode.Trim());
            if (service is null)
            {
                ErrorMessage = $"Không có chỉ số mã {NewServiceCode.Trim()}";
                return;
            }

            if (service.IsPanel)
            {
                ErrorMessage = "Kênh máy chỉ map tới chỉ số lá, không map tới dịch vụ cha (panel)";
                return;
            }

            await store.SaveChannelAsync(new ChannelMap(
                _editingId, _instrumentId, NewChannelCode.Trim(), service.Id, decimals, factor, NewKeepComparator, NewSendOnQuery));

            ClearNewRow();
            await LoadAsync(_instrumentId);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task RemoveAsync(ChannelRowViewModel row)
    {
        try
        {
            ErrorMessage = null;
            await store.DeleteChannelAsync(row.Id);
            Channels.Remove(row);
            if (_editingId == row.Id)
            {
                ClearNewRow();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không xóa được map kênh: {ex.Message}";
        }
    }

    private ChannelRowViewModel CreateRow(ChannelMapItem item) => new(item, SelectForEdit, RemoveAsync);

    private void ClearNewRow()
    {
        _editingId = Guid.Empty;
        IsEditing = false;
        NewChannelCode = "";
        NewServiceCode = "";
        NewDecimalsText = "";
        NewFactorText = "";
        NewKeepComparator = false;
        NewSendOnQuery = false;
    }
}
