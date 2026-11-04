using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.App.Services;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Data.Results;

namespace Lis.App.ViewModels.Results;

/// <summary>
/// ViewModel cho màn Kết quả: bên trái là danh sách mẫu (lọc theo ngày, trạng thái, tìm
/// barcode/mã/họ tên), bên phải là bảng chỉ số của mẫu đang chọn — nhập/sửa giá trị bằng bàn
/// phím (sửa ô đã có giá trị phải ghi lý do), Duyệt (F10), Hủy duyệt (chỉ với quyền
/// <see cref="Permission.HuyDuyet"/>, phải ghi lý do), In phiếu (F11), và Chấp nhận / Bỏ bản
/// thay thế khi máy gửi lại kết quả cho mẫu đã duyệt. Sau mỗi thao tác nạp lại mẫu và dòng
/// tương ứng trong danh sách.
/// </summary>
public partial class ResultsViewModel : ObservableObject
{
    private const string DateFormat = "dd/MM/yyyy";

    private readonly IResultService _results;
    private readonly CurrentUser _currentUser;
    private readonly IReportPrinter _reportPrinter;
    private readonly TimeProvider _clock;

    public ResultsViewModel(IResultService results, CurrentUser currentUser, IReportPrinter reportPrinter, TimeProvider? clock = null)
    {
        _results = results;
        _currentUser = currentUser;
        _reportPrinter = reportPrinter;
        _clock = clock ?? TimeProvider.System;

        var today = Today();
        _fromText = FormatDate(today);
        _toText = FormatDate(today);
    }

    public ObservableCollection<SampleListItemViewModel> Samples { get; } = [];

    public ObservableCollection<ResultRowViewModel> Rows { get; } = [];

    public IReadOnlyList<StatusOption> StatusOptions => StatusOption.Options;

    public Guid ActorId => _currentUser.User.Id;

    [ObservableProperty]
    private string _fromText;

    [ObservableProperty]
    private string _toText;

    [ObservableProperty]
    private StatusOption _selectedStatus = StatusOption.All;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private SampleListItemViewModel? _selectedSample;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSample))]
    [NotifyPropertyChangedFor(nameof(IsApproved))]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    [NotifyPropertyChangedFor(nameof(ApprovalText))]
    [NotifyPropertyChangedFor(nameof(CanEditValues))]
    [NotifyPropertyChangedFor(nameof(CanApprove))]
    [NotifyPropertyChangedFor(nameof(CanUnapprove))]
    private SampleResults? _current;

    [ObservableProperty]
    private string _reasonText = "";

    [ObservableProperty]
    private bool _isReasonVisible;

    [ObservableProperty]
    private string _unapproveReason = "";

    [ObservableProperty]
    private bool _isUnapprovePromptVisible;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasSample => Current is not null;

    public bool IsApproved => Current?.ApprovedAt is not null;

    public string HeaderText => Current is null
        ? "Chọn một mẫu ở danh sách bên trái"
        : $"{Current.Barcode} — {Current.PatientName} ({Current.PatientCode})";

    public string ApprovalText => Current?.ApprovedAt is { } approvedAt
        ? $"Đã duyệt lúc {TimeZoneInfo.ConvertTime(approvedAt, _clock.LocalTimeZone):HH:mm dd/MM/yyyy} — {Current.ApprovedBy}"
        : "Chưa duyệt";

    /// <summary>Người dùng có quyền nhập/sửa, mẫu đang chọn và chưa duyệt.</summary>
    public bool CanEditValues => _currentUser.Can(Permission.NhapSuaKetQua) && Current is not null && !IsApproved;

    public bool CanApprove => _currentUser.Can(Permission.Duyet) && Current is not null && !IsApproved;

    public bool CanUnapprove => _currentUser.Can(Permission.HuyDuyet) && Current is not null && IsApproved;

    /// <summary>Nạp danh sách mẫu hôm nay — gọi một lần khi mở màn Kết quả lần đầu.</summary>
    public Task LoadAsync() => LoadListAsync();

    [RelayCommand]
    private Task ShowTodayAsync() => ApplyQuickRangeAsync(Today(), Today());

    [RelayCommand]
    private Task ShowYesterdayAsync() => ApplyQuickRangeAsync(Today().AddDays(-1), Today().AddDays(-1));

    [RelayCommand]
    private Task ShowLastSevenDaysAsync() => ApplyQuickRangeAsync(Today().AddDays(-6), Today());

    private Task ApplyQuickRangeAsync(DateOnly from, DateOnly to)
    {
        FromText = FormatDate(from);
        ToText = FormatDate(to);
        return LoadListAsync();
    }

    partial void OnSelectedStatusChanged(StatusOption value) => _ = LoadListAsync();

    /// <summary>Tìm/lọc lại danh sách theo ngày, trạng thái và ô tìm kiếm (Enter trên ô tìm kiếm).</summary>
    [RelayCommand]
    private Task SearchAsync() => LoadListAsync();

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
            var items = await _results.ListAsync(from, to, SelectedStatus.Code, string.IsNullOrWhiteSpace(SearchText) ? null : SearchText);

            var selectedId = SelectedSample?.SampleId;
            Samples.Clear();
            foreach (var item in items)
            {
                Samples.Add(new SampleListItemViewModel(item, _clock.LocalTimeZone));
            }

            SelectedSample = Samples.FirstOrDefault(s => s.SampleId == selectedId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không nạp được danh sách mẫu: {ex.Message}";
        }
    }

    partial void OnSelectedSampleChanged(SampleListItemViewModel? value)
    {
        ReasonText = "";
        IsUnapprovePromptVisible = false;
        UnapproveReason = "";

        if (value is null)
        {
            Current = null;
            Rows.Clear();
            IsReasonVisible = false;
            return;
        }

        _ = LoadSampleAsync(value.SampleId);
    }

    /// <summary>Nạp bảng chỉ số của một mẫu (khi chọn trong danh sách, và sau mỗi thao tác ghi).</summary>
    public async Task LoadSampleAsync(Guid sampleId)
    {
        try
        {
            ErrorMessage = null;
            ShowSample(await _results.GetAsync(sampleId));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không nạp được kết quả: {ex.Message}";
        }
    }

    /// <summary>
    /// Hiện một mẫu đã nạp. Nếu vẫn cùng các dòng chỉ định thì cập nhật tại chỗ (ô đang có
    /// focus không bị dựng lại), không thì dựng lại bảng. Cũng dùng cho công cụ chụp ảnh.
    /// </summary>
    public void ShowSample(SampleResults sample)
    {
        Current = sample;
        var editable = CanEditValues;

        var sameRows = Rows.Count == sample.Rows.Count
            && Rows.Zip(sample.Rows).All(pair => pair.First.OrderItemId == pair.Second.OrderItemId);

        if (sameRows)
        {
            foreach (var (rowViewModel, row) in Rows.Zip(sample.Rows))
            {
                rowViewModel.Update(row, editable);
            }
        }
        else
        {
            Rows.Clear();
            foreach (var row in sample.Rows)
            {
                Rows.Add(new ResultRowViewModel(row, editable, this));
            }
        }

        OnRowEditTextChanged();
    }

    /// <summary>Ô lý do chỉ hiện khi đang sửa một ô đã có giá trị.</summary>
    internal void OnRowEditTextChanged() => IsReasonVisible = Rows.Any(r => r.IsDirty && r.HasValue);

    /// <summary>Enter trên ô giá trị: ghi giá trị (sửa ô đã có giá trị phải có lý do) rồi nạp lại mẫu.</summary>
    public async Task CommitValueAsync(ResultRowViewModel row)
    {
        if (IsBusy || !row.IsEditable || !row.IsDirty)
        {
            return;
        }

        string? reason = null;
        if (row.HasValue)
        {
            if (string.IsNullOrWhiteSpace(ReasonText))
            {
                ErrorMessage = "Nhập lý do sửa kết quả";
                return;
            }

            reason = ReasonText.Trim();
        }

        var typed = row.EditText;
        var storedBefore = row.Value;

        await RunAsync("Ghi kết quả thất bại", async () =>
        {
            await _results.SetManualAsync(row.OrderItemId, typed, ActorId, reason);
            ReasonText = "";
        });

        // Ghi lỗi (vd "Giá trị không hợp lệ"): nạp lại mẫu đã trả ô về giá trị đã lưu — đặt lại
        // chuỗi vừa gõ để người nhập sửa tiếp, trừ khi giá trị lưu đã bị máy trạm khác đổi.
        if (ErrorMessage is not null && row.Value == storedBefore)
        {
            row.EditText = typed;
        }
    }

    /// <summary>Esc trên ô giá trị: bỏ nội dung đang gõ, trả lại giá trị đã lưu.</summary>
    public void CancelEdit(ResultRowViewModel row)
    {
        row.EditText = row.Value ?? "";
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task ApproveAsync()
    {
        if (Current is null || !CanApprove)
        {
            return;
        }

        var sampleId = Current.SampleId;
        await RunAsync("Duyệt thất bại", () => _results.ApproveAsync(sampleId, ActorId));
    }

    [RelayCommand]
    private void StartUnapprove()
    {
        if (!CanUnapprove)
        {
            return;
        }

        UnapproveReason = "";
        IsUnapprovePromptVisible = true;
    }

    [RelayCommand]
    private void CancelUnapprove()
    {
        IsUnapprovePromptVisible = false;
        UnapproveReason = "";
    }

    [RelayCommand]
    private async Task ConfirmUnapproveAsync()
    {
        if (Current is null || !CanUnapprove)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(UnapproveReason))
        {
            ErrorMessage = "Nhập lý do hủy duyệt";
            return;
        }

        var sampleId = Current.SampleId;
        var reason = UnapproveReason.Trim();
        await RunAsync("Hủy duyệt thất bại", async () =>
        {
            await _results.UnapproveAsync(sampleId, ActorId, reason);
            IsUnapprovePromptVisible = false;
            UnapproveReason = "";
        });
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (Current is null)
        {
            return;
        }

        var sampleId = Current.SampleId;
        try
        {
            ErrorMessage = null;
            await _reportPrinter.PrintAsync(sampleId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"In phiếu thất bại: {ex.Message}";
        }
    }

    /// <summary>"Chấp nhận" bản thay thế đưa mẫu về chưa duyệt, nên cần quyền <see cref="Permission.HuyDuyet"/>.</summary>
    public bool CanAcceptPending => _currentUser.Can(Permission.HuyDuyet);

    /// <summary>"Bỏ bản thay thế" không đổi giá trị chính thức — chỉ cần quyền nhập/sửa.</summary>
    public bool CanRejectPending => _currentUser.Can(Permission.NhapSuaKetQua);

    public Task AcceptPendingAsync(ResultRowViewModel row)
    {
        if (row.ResultId is not { } resultId || !CanAcceptPending)
        {
            return Task.CompletedTask;
        }

        return RunAsync("Chấp nhận bản thay thế thất bại", () => _results.AcceptPendingAsync(resultId, ActorId));
    }

    public Task RejectPendingAsync(ResultRowViewModel row)
    {
        if (row.ResultId is not { } resultId || !CanRejectPending)
        {
            return Task.CompletedTask;
        }

        return RunAsync("Bỏ bản thay thế thất bại", () => _results.RejectPendingAsync(resultId, ActorId));
    }

    /// <summary>
    /// Chạy một thao tác ghi rồi nạp lại mẫu và dòng danh sách; lỗi hiện ở <see cref="ErrorMessage"/>.
    /// Thất bại cũng nạp lại mẫu, để trạng thái do máy trạm khác vừa đổi (vd "Mẫu đã duyệt")
    /// hiện đúng trên bảng.
    /// </summary>
    private async Task RunAsync(string failurePrefix, Func<Task> action)
    {
        if (Current is null)
        {
            return;
        }

        var sampleId = Current.SampleId;
        IsBusy = true;
        try
        {
            ErrorMessage = null;
            await action();
            await LoadSampleAsync(sampleId);
            await RefreshListItemAsync(sampleId);
        }
        catch (Exception ex)
        {
            var message = $"{failurePrefix}: {ex.Message}";
            await ReloadAfterFailureAsync(sampleId);
            ErrorMessage = message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAfterFailureAsync(Guid sampleId)
    {
        try
        {
            ShowSample(await _results.GetAsync(sampleId));
            await RefreshListItemAsync(sampleId);
        }
        catch (Exception)
        {
            // Lỗi gốc đã được báo ở ErrorMessage; nạp lại không được thì giữ bảng cũ.
        }
    }

    /// <summary>Nạp lại một dòng trong danh sách (tìm đúng barcode, không lọc trạng thái) để số đã có/tổng và trạng thái cập nhật tại chỗ.</summary>
    private async Task RefreshListItemAsync(Guid sampleId)
    {
        var item = Samples.FirstOrDefault(s => s.SampleId == sampleId);
        if (item is null || !TryParseDate(FromText, out var from) || !TryParseDate(ToText, out var to))
        {
            return;
        }

        var refreshed = await _results.ListAsync(from, to, status: null, query: item.Barcode);
        var updated = refreshed.FirstOrDefault(s => s.SampleId == sampleId);
        if (updated is not null)
        {
            item.Update(updated);
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    private static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
