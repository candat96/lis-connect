using Lis.Reports.Labels;

namespace Lis.App.Services;

/// <summary>
/// In tem barcode cho các mẫu vừa được cấp ở màn Tiếp nhận (nút "Lưu và in tem"). Cài đặt thật
/// là <see cref="LabelPrinter"/>; kiểm thử ViewModel dùng bản giả.
/// </summary>
public interface ILabelPrinter
{
    Task PrintAsync(IReadOnlyList<LabelData> labels);
}
