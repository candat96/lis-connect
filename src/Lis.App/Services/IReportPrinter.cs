namespace Lis.App.Services;

/// <summary>
/// In phiếu kết quả của một mẫu (nút "In phiếu (F11)" ở màn Kết quả). Cài đặt thật là
/// <see cref="ReportPrinter"/> (Task 12). Kiểm thử ViewModel dùng bản giả — cùng cách
/// <see cref="ILabelPrinter"/> làm với tem barcode.
/// </summary>
public interface IReportPrinter
{
    Task PrintAsync(Guid sampleId);
}
