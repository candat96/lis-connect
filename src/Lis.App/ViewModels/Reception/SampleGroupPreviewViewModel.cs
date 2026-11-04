namespace Lis.App.ViewModels.Reception;

/// <summary>
/// Xem trước một nhóm mẫu (một barcode) trước khi lưu — gom theo loại mẫu bệnh phẩm, giống
/// cách <c>OrderService.CreateAsync</c> sẽ cấp barcode thật khi lưu.
/// </summary>
public sealed record SampleGroupPreviewViewModel(string SpecimenName, IReadOnlyList<string> ServiceNames)
{
    public string ServiceNamesText => string.Join(", ", ServiceNames);
}
