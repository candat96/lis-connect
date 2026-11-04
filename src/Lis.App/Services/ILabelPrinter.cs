using Lis.Data.Orders;

namespace Lis.App.Services;

/// <summary>
/// In tem barcode cho các mẫu vừa được cấp ở màn Tiếp nhận. Cài đặt thật (máy in tem) sẽ
/// được làm ở Task 9 — ở Task 8, nút "Lưu và in tem" chỉ lưu và hiện bảng barcode đã cấp,
/// gọi <see cref="NoOpLabelPrinter"/> (không làm gì) qua hook này.
/// </summary>
public interface ILabelPrinter
{
    void PrintLabels(IReadOnlyList<CreatedSample> samples);
}

/// <summary>Cài đặt mặc định — chưa in gì, chỉ để màn Tiếp nhận có chỗ gọi tới khi Task 9 làm máy in thật.</summary>
public sealed class NoOpLabelPrinter : ILabelPrinter
{
    public void PrintLabels(IReadOnlyList<CreatedSample> samples)
    {
        // Task 9 sẽ cài đặt in tem thật ở đây.
    }
}
