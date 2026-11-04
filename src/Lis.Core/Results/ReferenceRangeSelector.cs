using Lis.Core.Patients;

namespace Lis.Core.Results;

/// <summary>
/// Chọn khoảng tham chiếu phù hợp nhất cho một người bệnh tại một thời điểm.
/// Chỉ xét các khoảng khớp tuổi và giới tính; trong số đó ưu tiên theo thứ tự:
/// đúng máy hơn máy null, rồi có giới hơn chung, rồi khoảng tuổi hẹp hơn.
/// </summary>
public static class ReferenceRangeSelector
{
    public static ReferenceRange? Select(
        IEnumerable<ReferenceRange> ranges,
        PatientFacts p,
        DateOnly at,
        Guid? instrumentId)
    {
        var ageDays = PatientAge.Days(p, at);

        bool MatchesAge(ReferenceRange r)
        {
            // Không rõ tuổi (-1): chỉ khớp khoảng không giới hạn tuổi nào cả.
            if (ageDays < 0)
            {
                return r.AgeFromDays is null && r.AgeToDays is null;
            }

            return (r.AgeFromDays is null || ageDays >= r.AgeFromDays)
                && (r.AgeToDays is null || ageDays <= r.AgeToDays);
        }

        bool MatchesSex(ReferenceRange r) => r.Sex is null || r.Sex == p.Sex;

        bool MatchesInstrument(ReferenceRange r) => r.InstrumentId is null || r.InstrumentId == instrumentId;

        var candidates = ranges.Where(r => MatchesAge(r) && MatchesSex(r) && MatchesInstrument(r)).ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        int InstrumentRank(ReferenceRange r) => r.InstrumentId is not null && r.InstrumentId == instrumentId ? 0 : 1;

        int SexRank(ReferenceRange r) => r.Sex is not null ? 0 : 1;

        long AgeWidth(ReferenceRange r)
        {
            var from = r.AgeFromDays.HasValue ? (long)r.AgeFromDays.Value : long.MinValue / 2;
            var to = r.AgeToDays.HasValue ? (long)r.AgeToDays.Value : long.MaxValue / 2;
            return to - from;
        }

        return candidates
            .OrderBy(InstrumentRank)
            .ThenBy(SexRank)
            .ThenBy(AgeWidth)
            .First();
    }
}
