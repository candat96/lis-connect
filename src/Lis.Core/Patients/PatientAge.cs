namespace Lis.Core.Patients;

/// <summary>
/// Thông tin người bệnh cần để chọn khoảng tham chiếu: giới tính, ngày sinh
/// (nếu có) hoặc chỉ năm sinh (khi không rõ ngày/tháng).
/// </summary>
public sealed record PatientFacts(char Sex, DateOnly? Dob, int? BirthYear);

/// <summary>
/// Tính tuổi theo ngày của người bệnh tại một thời điểm, dùng để chọn khoảng
/// tham chiếu theo tuổi (kể cả khoảng theo ngày cho trẻ sơ sinh).
/// </summary>
public static class PatientAge
{
    /// <summary>
    /// Trả về số ngày tuổi tại thời điểm <paramref name="at"/>.
    /// Có ngày sinh thì tính đúng ngày. Chỉ có năm sinh thì giả định sinh
    /// ngày 01/07 năm đó. Không có cả hai thì trả -1 (không rõ tuổi).
    /// </summary>
    public static int Days(PatientFacts p, DateOnly at)
    {
        DateOnly? dob = p.Dob ?? (p.BirthYear.HasValue ? new DateOnly(p.BirthYear.Value, 7, 1) : null);

        if (dob is null)
        {
            return -1;
        }

        return at.DayNumber - dob.Value.DayNumber;
    }
}
