using System.Globalization;
using Lis.Data.Ingest;

namespace Lis.App.ViewModels.Unmatched;

/// <summary>Một dòng trên bảng Chưa ghép: máy, barcode, kênh (kèm chỉ số nếu kênh đã map), giá trị, lý do tiếng Việt, nhận lúc (giờ máy).</summary>
public sealed class UnmatchedRowViewModel(UnmatchedItem item, TimeZoneInfo timeZone)
{
    public UnmatchedItem Item { get; } = item;

    public Guid Id => Item.Id;

    public string InstrumentName => Item.InstrumentName;

    public string Barcode => Item.Barcode.Length == 0 ? "(trống)" : Item.Barcode;

    public string ChannelText => Item.ServiceCode is null
        ? Item.ChannelCode
        : $"{Item.ChannelCode} → {Item.ServiceCode} – {Item.ServiceName}";

    public string ValueText => string.IsNullOrEmpty(Item.Unit) ? Item.Value : $"{Item.Value} {Item.Unit}";

    public string ReasonLabel => ReasonLabelFor(Item.Reason);

    public string ReceivedAtText =>
        TimeZoneInfo.ConvertTime(Item.ReceivedAt, timeZone).ToString("HH:mm dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>Lý do lưu dạng mã (<see cref="UnmatchedReason"/>) → chữ hiện trên bảng.</summary>
    public static string ReasonLabelFor(string reason) => reason switch
    {
        UnmatchedReason.NoBarcode => "Không có barcode",
        UnmatchedReason.NotOrdered => "Chưa có chỉ định",
        UnmatchedReason.ChannelUnmapped => "Kênh chưa map",
        _ => reason,
    };
}
