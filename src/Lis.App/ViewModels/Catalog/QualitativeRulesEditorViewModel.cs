using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Data.Catalog;

namespace Lis.App.ViewModels.Catalog;

/// <summary>
/// ViewModel cho tab "Quy đổi định tính": nạp/sửa/lưu danh sách quy tắc quy đổi của dịch vụ
/// đang chọn ở <c>CatalogViewModel</c>.
/// </summary>
public partial class QualitativeRulesEditorViewModel : ObservableObject
{
    private readonly CatalogStore _store;
    private Guid _serviceId;

    public QualitativeRulesEditorViewModel(CatalogStore store)
    {
        _store = store;
    }

    public ObservableCollection<QualitativeRuleRowViewModel> Rules { get; } = [];

    public ObservableCollection<InstrumentItem> Instruments { get; } = [InstrumentItem.Any];

    [ObservableProperty]
    private string? _errorMessage;

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
        ErrorMessage = null;

        try
        {
            var rules = await _store.QualitativeRulesAsync(serviceId);
            Rules.Clear();
            foreach (var rule in rules)
            {
                Rules.Add(QualitativeRuleRowViewModel.From(rule, Instruments, RemoveRule));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không tải được quy đổi định tính: {ex.Message}";
        }
    }

    public void Clear()
    {
        _serviceId = Guid.Empty;
        ErrorMessage = null;
        Rules.Clear();
    }

    [RelayCommand]
    private void AddRule() => Rules.Add(new QualitativeRuleRowViewModel(Instruments, RemoveRule));

    private void RemoveRule(QualitativeRuleRowViewModel row) => Rules.Remove(row);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_serviceId == Guid.Empty)
        {
            ErrorMessage = "Chọn dịch vụ trước";
            return;
        }

        try
        {
            var rules = Rules.Select(r => r.ToRule(_serviceId)).ToList();
            await _store.SaveQualitativeRulesAsync(_serviceId, rules);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không lưu được quy đổi định tính: {ex.Message}";
        }
    }
}
