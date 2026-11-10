using CommunityToolkit.Mvvm.Input;
using Lis.Data.Ingest;
using Lis.Data.Instruments;

namespace Lis.App.ViewModels.Instruments;

/// <summary>Một chỉ số gợi ý cho mã kênh; bấm = xác nhận map mã kênh này vào chỉ số đó.</summary>
public sealed class CandidateOption(ServiceRef service, bool isCertain, Func<ServiceRef, Task> confirm)
{
    public ServiceRef Service { get; } = service;

    public bool IsCertain { get; } = isCertain;

    public string Label => $"✓ {Service.Code} – {Service.Name}";

    public IAsyncRelayCommand ConfirmCommand { get; } = new AsyncRelayCommand(() => confirm(service));
}

/// <summary>
/// Một dòng "Kênh máy gửi chưa map": mã kênh, giá trị gần nhất, số lần gặp, và gợi ý chỉ số
/// suy ra từ chỉ định của các barcode (<see cref="IChannelMapLearning"/>).
/// </summary>
public sealed class UnmappedRowViewModel
{
    public UnmappedRowViewModel(UnmappedChannel channel, ChannelMapHint? hint, Func<string, ServiceRef, Task> confirm)
    {
        Channel = channel;
        Hint = hint;
        Candidates = hint?.Candidates.Select(s => new CandidateOption(s, hint.IsCertain, svc => confirm(channel.ChannelCode, svc))).ToList() ?? [];
    }

    public UnmappedChannel Channel { get; }

    public ChannelMapHint? Hint { get; }

    public string ChannelCode => Channel.ChannelCode;

    public string ValueText => string.IsNullOrWhiteSpace(Channel.LastUnit) ? Channel.LastValue : $"{Channel.LastValue} {Channel.LastUnit}";

    public int Count => Channel.Count;

    public IReadOnlyList<CandidateOption> Candidates { get; }

    public string HintText => Hint switch
    {
        null => "Chưa đủ dữ liệu — mẫu không có chỉ định hoặc chưa có trong LIS",
        { HasConflict: true } => $"Dữ liệu các mẫu mâu thuẫn nhau (từ {Hint.SampleCount} mẫu) — kiểm tra trên máy rồi chọn tay:",
        { IsCertain: true, SampleCount: 1 } => "Khớp ở 1 mẫu — thêm 1 mẫu nữa khớp thì phần mềm tự map, hoặc bấm để map ngay:",
        { IsCertain: true } => $"Chắc chắn (từ {Hint.SampleCount} mẫu) — bấm để map:",
        _ => $"Có thể là một trong {Hint.Candidates.Count} chỉ số (từ {Hint.SampleCount} mẫu) — chạy thêm mẫu sẽ rõ, hoặc chọn:",
    };
}
