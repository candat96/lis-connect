using Lis.Data.Orders;
using Lis.Reports.Labels;

namespace Lis.App.Services;

/// <summary>
/// Dựng nội dung tem (<see cref="LabelData"/>) từ người bệnh và các mẫu vừa cấp barcode — mỗi
/// mẫu một tem. Năm sinh lấy từ ngày sinh nếu có, không thì từ năm sinh nhập riêng.
/// </summary>
public static class LabelDataBuilder
{
    public static IReadOnlyList<LabelData> Build(
        string patientName, DateOnly? dob, int? birthYear, char sex, IReadOnlyList<CreatedSample> samples, DateOnly day)
    {
        var year = dob?.Year ?? birthYear;

        return samples
            .Select(sample => new LabelData(sample.Barcode, patientName, year, sex, sample.SpecimenName, day))
            .ToList();
    }
}
