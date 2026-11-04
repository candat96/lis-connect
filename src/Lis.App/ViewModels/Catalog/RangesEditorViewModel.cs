using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Core.Results;
using Lis.Data.Catalog;

namespace Lis.App.ViewModels.Catalog;

/// <summary>
/// ViewModel cho tab "Khoảng tham chiếu": nạp/sửa/lưu danh sách <see cref="ReferenceRange"/>
/// của dịch vụ đang chọn ở <c>CatalogViewModel</c>. Lưu kiểm tra 2 lớp trước khi ghi CSDL —
/// ô số không parse được (xem <see cref="ReferenceRangeRowViewModel.ToReferenceRange"/>) rồi
/// các quy tắc logic (<see cref="ReferenceRangeValidator"/>) — gộp lỗi, hiện cạnh bảng, và
/// không lưu gì nếu còn lỗi.
/// </summary>
public partial class RangesEditorViewModel : ObservableObject
{
    private readonly CatalogStore _store;
    private Guid _serviceId;

    public RangesEditorViewModel(CatalogStore store)
    {
        _store = store;
    }

    public ObservableCollection<ReferenceRangeRowViewModel> Ranges { get; } = [];

    public ObservableCollection<InstrumentItem> Instruments { get; } = [InstrumentItem.Any];

    [ObservableProperty]
    private IReadOnlyList<string> _errors = [];

    public void SetInstruments(IEnumerable<InstrumentItem> instruments)
    {
        Instruments.Clear();
        Instruments.Add(InstrumentItem.Any);
        foreach (var instrument in instruments)
        {
            Instruments.Add(instrument);
        }
    }

    public async Task LoadAsync(Guid serviceId)
    {
        _serviceId = serviceId;
        Errors = [];

        try
        {
            var ranges = await _store.RangesAsync(serviceId);
            Ranges.Clear();
            foreach (var range in ranges)
            {
                Ranges.Add(ReferenceRangeRowViewModel.From(range, Instruments, RemoveRange));
            }
        }
        catch (Exception ex)
        {
            Errors = [$"Không tải được khoảng tham chiếu: {ex.Message}"];
        }
    }

    public void Clear()
    {
        _serviceId = Guid.Empty;
        Errors = [];
        Ranges.Clear();
    }

    [RelayCommand]
    private void AddRange() => Ranges.Add(new ReferenceRangeRowViewModel(Instruments, RemoveRange));

    private void RemoveRange(ReferenceRangeRowViewModel row) => Ranges.Remove(row);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_serviceId == Guid.Empty)
        {
            Errors = ["Chọn dịch vụ trước"];
            return;
        }

        var errors = new List<string>();
        var ranges = new List<ReferenceRange>();
        for (var i = 0; i < Ranges.Count; i++)
        {
            ranges.Add(Ranges[i].ToReferenceRange(_serviceId, i + 1, errors));
        }

        errors.AddRange(ReferenceRangeValidator.Validate(ranges));

        if (errors.Count > 0)
        {
            Errors = errors;
            return;
        }

        try
        {
            await _store.SaveRangesAsync(_serviceId, ranges);
            Errors = [];
        }
        catch (Exception ex)
        {
            Errors = [$"Không lưu được khoảng tham chiếu: {ex.Message}"];
        }
    }
}
