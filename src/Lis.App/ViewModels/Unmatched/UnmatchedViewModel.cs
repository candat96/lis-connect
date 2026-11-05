using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Data.Ingest;

namespace Lis.App.ViewModels.Unmatched;

/// <summary>
/// ViewModel cho màn Chưa ghép: bảng kết quả máy gửi mà chưa ghép được (lọc theo ngày nhận),
/// nút "Gán vào mẫu…" mở <see cref="AssignPanel"/> (barcode → chỉ số → Gán) và "Bỏ qua" (bắt
/// buộc lý do). Chỉ người có quyền <see cref="Permission.GhepTay"/> mới thao tác được.
/// </summary>
public partial class UnmatchedViewModel : ObservableObject
{
    private const string DateFormat = "dd/MM/yyyy";

    private readonly IUnmatchedService _service;
    private readonly CurrentUser _currentUser;
    private readonly TimeProvider _clock;

    public UnmatchedViewModel(IUnmatchedService service, CurrentUser currentUser, TimeProvider? clock = null)
    {
        _service = service;
        _currentUser = currentUser;
        _clock = clock ?? TimeProvider.System;
        AssignPanel = new AssignPanelViewModel(service, AssignSelectedAsync);

        var today = Today();
        _fromText = FormatDate(today);
        _toText = FormatDate(today);
    }

    public ObservableCollection<UnmatchedRowViewModel> Rows { get; } = [];

    public AssignPanelViewModel AssignPanel { get; }

    public Guid ActorId => _currentUser.User.Id;

    public bool CanAct => _currentUser.Can(Permission.GhepTay);

    [ObservableProperty]
    private string _fromText;

    [ObservableProperty]
    private string _toText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private UnmatchedRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _dismissReason = "";

    [ObservableProperty]
    private bool _isDismissPromptVisible;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasSelection => SelectedRow is not null;

    public string CountText => Rows.Count == 0 ? "Không có dòng nào chưa ghép" : $"{Rows.Count} dòng chưa ghép";

    /// <summary>Nạp các dòng hôm nay — gọi một lần khi mở màn lần đầu.</summary>
    public Task LoadAsync() => LoadListAsync();

    [RelayCommand]
    private Task ShowTodayAsync() => ApplyQuickRangeAsync(Today(), Today());

    [RelayCommand]
    private Task ShowYesterdayAsync() => ApplyQuickRangeAsync(Today().AddDays(-1), Today().AddDays(-1));

    [RelayCommand]
    private Task ShowLastSevenDaysAsync() => ApplyQuickRangeAsync(Today().AddDays(-6), Today());

    [RelayCommand]
    private Task RefreshAsync() => LoadListAsync();

    private Task ApplyQuickRangeAsync(DateOnly from, DateOnly to)
    {
        FromText = FormatDate(from);
        ToText = FormatDate(to);
        return LoadListAsync();
    }

    private async Task LoadListAsync()
    {
        if (!TryParseDate(FromText, out var from) || !TryParseDate(ToText, out var to))
        {
            ErrorMessage = $"Ngày phải theo dạng {DateFormat}";
            return;
        }

        if (to < from)
        {
            ErrorMessage = "Ngày kết thúc phải sau ngày bắt đầu";
            return;
        }

        try
        {
            ErrorMessage = null;
            var items = await _service.ListOpenAsync(from, to);

            var selectedId = SelectedRow?.Id;
            Rows.Clear();
            foreach (var item in items)
            {
                Rows.Add(new UnmatchedRowViewModel(item, _clock.LocalTimeZone));
            }

            SelectedRow = Rows.FirstOrDefault(r => r.Id == selectedId);
            OnPropertyChanged(nameof(CountText));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không nạp được danh sách: {ex.Message}";
        }
    }

    partial void OnSelectedRowChanged(UnmatchedRowViewModel? value)
    {
        IsDismissPromptVisible = false;
        DismissReason = "";
        if (AssignPanel.Row is not null && AssignPanel.Row != value)
        {
            AssignPanel.Close();
        }
    }

    [RelayCommand]
    private void StartAssign()
    {
        if (SelectedRow is null || !CanAct)
        {
            return;
        }

        IsDismissPromptVisible = false;
        AssignPanel.Open(SelectedRow);
    }

    /// <summary>Người dùng bấm Gán trong ô gán: ghi qua service rồi nạp lại bảng.</summary>
    private async Task AssignSelectedAsync(Guid orderItemId)
    {
        if (AssignPanel.Row is not { } row || !CanAct)
        {
            return;
        }

        await _service.AssignAsync(row.Id, orderItemId, ActorId);
        AssignPanel.Close();
        await LoadListAsync();
    }

    [RelayCommand]
    private void StartDismiss()
    {
        if (SelectedRow is null || !CanAct)
        {
            return;
        }

        AssignPanel.Close();
        DismissReason = "";
        IsDismissPromptVisible = true;
    }

    [RelayCommand]
    private void CancelDismiss()
    {
        IsDismissPromptVisible = false;
        DismissReason = "";
    }

    [RelayCommand]
    private async Task ConfirmDismissAsync()
    {
        if (SelectedRow is null || !CanAct || IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DismissReason))
        {
            ErrorMessage = "Nhập lý do bỏ qua";
            return;
        }

        var id = SelectedRow.Id;
        var reason = DismissReason.Trim();
        IsBusy = true;
        try
        {
            ErrorMessage = null;
            await _service.DismissAsync(id, ActorId, reason);
            IsDismissPromptVisible = false;
            DismissReason = "";
            await LoadListAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bỏ qua thất bại: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    private static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
