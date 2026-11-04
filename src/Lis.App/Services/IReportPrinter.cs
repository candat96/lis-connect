namespace Lis.App.Services;

/// <summary>
/// In phiếu kết quả của một mẫu (nút "In phiếu (F11)" ở màn Kết quả). Cài đặt thật đến ở
/// Task 12 (phiếu kết quả PDF); hiện tại ứng dụng dùng <see cref="NoOpReportPrinter"/>. Kiểm
/// thử ViewModel dùng bản giả — cùng cách <see cref="ILabelPrinter"/> làm với tem barcode.
/// </summary>
public interface IReportPrinter
{
    Task PrintAsync(Guid sampleId);
}

/// <summary>Bản mặc định chưa in gì — giữ chỗ cho tới khi có phiếu kết quả (Task 12).</summary>
public sealed class NoOpReportPrinter : IReportPrinter
{
    public Task PrintAsync(Guid sampleId) => Task.CompletedTask;
}
