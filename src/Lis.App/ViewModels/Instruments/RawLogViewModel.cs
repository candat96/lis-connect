using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Core.Instruments;
using Lis.Data.Instruments;

namespace Lis.App.ViewModels.Instruments;

/// <summary>
/// Màn "Log thô": xem nguyên văn byte đi/về giữa LIS và từng máy để debug kết nối. Chọn máy,
/// ngày, phiên; lọc theo chiều, tìm chữ, ẩn ACK lẻ; xem dạng ký tự điều khiển hoặc hex; tự cập
/// nhật mỗi 2 giây khi đang theo dõi trực tiếp. Quyền <see cref="Lis.Core.Auth.Permission.XemLogTho"/>.
/// </summary>
public partial class RawLogViewModel : ObservableObject
{
    public const string AllSessions = "Tất cả phiên";

    /// <summary>Số dòng hiện tối đa (lấy các dòng cuối) để giao diện không ì khi log lớn.</summary>
    public const int MaxRows = 3000;

    public static readonly IReadOnlyList<string> DirectionFilters = ["Tất cả", "Máy → LIS", "LIS → máy", "Sự kiện"];

    private readonly IInstrumentStore _instruments;
    private readonly IRawLogStore _logs;
    private readonly TimeSpan _refreshInterval;
    private IReadOnlyList<RawLogEntry> _entries = [];
    private bool _autoRefreshRunning;

    public RawLogViewModel(IInstrumentStore instruments, IRawLogStore logs, TimeSpan? refreshInterval = null)
    {
        _instruments = instruments;
        _logs = logs;
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(2);
    }

    public ObservableCollection<InstrumentInfo> Instruments { get; } = [];

    public ObservableCollection<string> Dates { get; } = [];

    public ObservableCollection<string> Sessions { get; } = [];

    public ObservableCollection<RawLogRowViewModel> Rows { get; } = [];

    public IReadOnlyList<string> DirectionOptions => DirectionFilters;

    public string RootDirectory => _logs.RootDirectory;

    [ObservableProperty]
    private InstrumentInfo? _selectedInstrument;

    [ObservableProperty]
    private string? _selectedDate;

    [ObservableProperty]
    private string _selectedSession = AllSessions;

    [ObservableProperty]
    private string _selectedDirection = DirectionFilters[0];

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private bool _hideLoneAcks = true;

    [ObservableProperty]
    private bool _showHex;

    [ObservableProperty]
    private bool _autoRefresh;

    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Báo đã thêm dòng mới khi tự cập nhật — View cuộn xuống cuối.</summary>
    public event EventHandler? RowsAppended;

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            ErrorMessage = null;
            var selectedId = SelectedInstrument?.Id;
            var list = await _instruments.ListAsync();
            Instruments.Clear();
            foreach (var instrument in list)
            {
                Instruments.Add(instrument);
            }

            SelectedInstrument = Instruments.FirstOrDefault(i => i.Id == selectedId) ?? Instruments.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không tải được danh sách máy: {ex.Message}";
        }
    }

    /// <summary>Mở log của một máy (từ nút "Xem log thô" ở màn Máy &amp; map).</summary>
    public async Task ShowInstrumentAsync(Guid instrumentId)
    {
        if (Instruments.Count == 0)
        {
            await LoadAsync();
        }

        SelectedInstrument = Instruments.FirstOrDefault(i => i.Id == instrumentId) ?? SelectedInstrument;
        Reload();
    }

    partial void OnSelectedInstrumentChanged(InstrumentInfo? value)
    {
        Dates.Clear();
        string? latest = null;
        if (value is not null)
        {
            var logDates = _logs.ListDates(value.Id);
            foreach (var date in logDates)
            {
                Dates.Add(Format(date));
            }

            var today = Format(DateOnly.FromDateTime(DateTime.Now));
            if (!Dates.Contains(today))
            {
                Dates.Insert(0, today);
            }

            // Mặc định: ngày mới nhất có log, không có thì hôm nay.
            latest = logDates.Count > 0 ? Format(logDates[0]) : today;
        }

        SelectedDate = latest;
        Reload();
    }

    partial void OnSelectedDateChanged(string? value) => Reload();

    partial void OnSelectedSessionChanged(string value) => ApplyFilter();

    partial void OnSelectedDirectionChanged(string value) => ApplyFilter();

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    partial void OnHideLoneAcksChanged(bool value) => ApplyFilter();

    partial void OnShowHexChanged(bool value) => ApplyFilter();

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value && !_autoRefreshRunning)
        {
            _ = AutoRefreshLoopAsync();
        }
    }

    /// <summary>Đọc lại tệp log của máy/ngày đang chọn.</summary>
    [RelayCommand]
    public void Reload()
    {
        ErrorMessage = null;
        if (SelectedInstrument is null || SelectedDate is null || !TryParse(SelectedDate, out var date))
        {
            _entries = [];
            RebuildSessions();
            ApplyFilter();
            return;
        }

        try
        {
            _entries = _logs.Read(SelectedInstrument.Id, date);
        }
        catch (Exception ex)
        {
            _entries = [];
            ErrorMessage = $"Không đọc được log: {ex.Message}";
        }

        RebuildSessions();
        ApplyFilter();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var folder = SelectedInstrument is not null && SelectedDate is not null && TryParse(SelectedDate, out var date)
                ? Path.GetDirectoryName(_logs.FilePath(SelectedInstrument.Id, date))!
                : _logs.RootDirectory;
            if (!Directory.Exists(folder))
            {
                folder = _logs.RootDirectory;
            }

            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không mở được thư mục log: {ex.Message}";
        }
    }

    /// <summary>Nội dung các dòng đang hiện, dạng chữ dễ đọc — để dán vào báo lỗi / gửi đi debug.</summary>
    public string BuildCopyText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Log thô {SelectedInstrument?.Name} ngày {SelectedDate} — CHỨA DỮ LIỆU NGƯỜI BỆNH, không gửi ra ngoài khi chưa xóa thông tin.");
        foreach (var row in Rows)
        {
            sb.Append(row.Time).Append(' ').Append(row.DirectionText).Append(' ').Append('[').Append(row.Session).Append("] ");
            sb.AppendLine(row.Text.Replace("\n", "\n    "));
        }

        return sb.ToString();
    }

    private async Task AutoRefreshLoopAsync()
    {
        _autoRefreshRunning = true;
        try
        {
            while (AutoRefresh)
            {
                await Task.Delay(_refreshInterval);
                if (!AutoRefresh)
                {
                    break;
                }

                // Khi theo dõi trực tiếp thì nhảy sang ngày hôm nay nếu có log mới sang ngày.
                var before = Rows.Count;
                Reload();
                if (Rows.Count > before)
                {
                    RowsAppended?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            _autoRefreshRunning = false;
        }
    }

    private void RebuildSessions()
    {
        var current = SelectedSession;
        Sessions.Clear();
        Sessions.Add(AllSessions);
        foreach (var session in _entries.Select(e => e.Session).Where(s => s.Length > 0).Distinct())
        {
            Sessions.Add(session);
        }

        SelectedSession = Sessions.Contains(current) ? current : AllSessions;
    }

    private void ApplyFilter()
    {
        IEnumerable<RawLogEntry> query = _entries;

        if (SelectedSession != AllSessions)
        {
            query = query.Where(e => e.Session == SelectedSession);
        }

        query = SelectedDirection switch
        {
            "Máy → LIS" => query.Where(e => e.Direction == RawLogDirection.In),
            "LIS → máy" => query.Where(e => e.Direction == RawLogDirection.Out),
            "Sự kiện" => query.Where(e => e.Direction == RawLogDirection.Event),
            _ => query,
        };

        if (HideLoneAcks)
        {
            query = query.Where(e => !e.IsLoneAck);
        }

        var search = SearchText?.Trim();
        var rows = query.Select(e => new RawLogRowViewModel(e, ShowHex));
        if (!string.IsNullOrEmpty(search))
        {
            rows = rows.Where(r => r.Text.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (r.Note?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = rows.ToList();
        var shown = list.Count > MaxRows ? list.Skip(list.Count - MaxRows).ToList() : list;

        Rows.Clear();
        foreach (var row in shown)
        {
            Rows.Add(row);
        }

        var bytesIn = _entries.Where(e => e.Direction == RawLogDirection.In).Sum(e => e.Bytes.Length);
        var bytesOut = _entries.Where(e => e.Direction == RawLogDirection.Out).Sum(e => e.Bytes.Length);
        Summary = _entries.Count == 0
            ? "Chưa có log cho máy / ngày này."
            : $"{list.Count} / {_entries.Count} bản ghi · nhận {bytesIn:N0} byte · gửi {bytesOut:N0} byte"
              + (list.Count > MaxRows ? $" · chỉ hiện {MaxRows} dòng cuối" : "");
    }

    private static string Format(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static bool TryParse(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

/// <summary>Một dòng log đã định dạng để hiển thị.</summary>
public sealed class RawLogRowViewModel(RawLogEntry entry, bool hex)
{
    public string Time { get; } = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string DirectionText { get; } = entry.Direction switch
    {
        RawLogDirection.In => "Máy → LIS",
        RawLogDirection.Out => "LIS → máy",
        _ => "Sự kiện",
    };

    public bool IsIn => entry.Direction == RawLogDirection.In;

    public bool IsOut => entry.Direction == RawLogDirection.Out;

    public bool IsEvent => entry.Direction == RawLogDirection.Event;

    public string Session { get; } = entry.Session;

    public string? Note { get; } = entry.Note;

    public string ByteCountText { get; } = entry.Bytes.Length == 0 ? "" : $"{entry.Bytes.Length} B";

    public string Text { get; } = entry.Bytes.Length == 0
        ? entry.Note ?? ""
        : (hex ? RawBytes.ToHex(entry.Bytes).TrimEnd() : RawBytes.ToReadable(entry.Bytes).TrimEnd('\n'))
          + (entry.Note is null ? "" : $"   ({entry.Note})");
}
