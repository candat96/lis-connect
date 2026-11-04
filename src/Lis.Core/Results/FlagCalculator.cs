namespace Lis.Core.Results;

/// <summary>
/// Tính cờ cảnh báo từ giá trị số và khoảng tham chiếu. Cờ nguy kịch
/// (CriticalLow/CriticalHigh) luôn thắng cờ cao/thấp thường. Giá trị kèm dấu
/// so sánh (ví dụ ">500") so trực tiếp số đó với các ngưỡng. Không bao giờ
/// trả về <see cref="ResultFlag.Abnormal"/> — cờ đó chỉ do quy đổi định tính sinh ra.
/// </summary>
public static class FlagCalculator
{
    public static ResultFlag Compute(ResultValue v, ReferenceRange? r)
    {
        if (v.IsEmpty || v.Number is null || r is null)
        {
            return ResultFlag.None;
        }

        var so = v.Number.Value;

        if (r.CriticalLow.HasValue && so <= r.CriticalLow.Value)
        {
            return ResultFlag.CriticalLow;
        }

        if (r.CriticalHigh.HasValue && so >= r.CriticalHigh.Value)
        {
            return ResultFlag.CriticalHigh;
        }

        if (r.Low.HasValue && so < r.Low.Value)
        {
            return ResultFlag.Low;
        }

        if (r.High.HasValue && so > r.High.Value)
        {
            return ResultFlag.High;
        }

        return ResultFlag.None;
    }
}
