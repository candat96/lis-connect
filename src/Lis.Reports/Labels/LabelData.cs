namespace Lis.Reports.Labels;

/// <summary>
/// Nội dung một tem dán ống mẫu: barcode (chỉ chữ số, đúng chuỗi đã cấp cho mẫu), họ tên,
/// năm sinh, giới tính (<c>'M'</c>/<c>'F'</c>/<c>'O'</c> như cột <c>patient.sex</c>), tên loại
/// mẫu và ngày lấy mẫu.
/// </summary>
public sealed record LabelData(
    string Barcode,
    string PatientName,
    int? BirthYear,
    char Sex,
    string SpecimenName,
    DateOnly Day);
