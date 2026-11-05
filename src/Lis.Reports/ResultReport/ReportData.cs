using Lis.Data.Facility;
using Lis.Data.Results;

namespace Lis.Reports.ResultReport;

/// <summary>
/// Dữ liệu dựng một phiếu kết quả xét nghiệm: cơ sở, mẫu chính đang in (<see cref="Sample"/>)
/// cùng các mẫu khác của cùng phiếu chỉ định (<see cref="OtherSamplesSameOrder"/>, gộp vào một
/// phiếu in — xem Task 12), trạng thái duyệt của CẢ phiếu (<see cref="Approved"/> chỉ true khi
/// mọi mẫu đã duyệt — một mẫu chưa duyệt là cả phiếu in "CHƯA DUYỆT"), tên người duyệt hiển thị
/// và thời điểm in.
/// </summary>
public sealed record ReportData(
    FacilityInfo Facility,
    SampleResults Sample,
    IReadOnlyList<SampleResults> OtherSamplesSameOrder,
    bool Approved,
    string? ApproverName,
    DateTimeOffset PrintedAt,
    OrderHeader Header);
