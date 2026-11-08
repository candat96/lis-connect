namespace Lis.Core.Instruments;

/// <summary>
/// Một lần máy gửi một mã kênh chưa map cho một mẫu: giá trị, và các chỉ số của mẫu đó còn
/// chưa map trên máy này (ứng viên). Ứng viên rỗng = không biết (không có mẫu, mẫu QC…).
/// </summary>
public sealed record ChannelObservation(string ChannelCode, string Barcode, decimal? Value, IReadOnlySet<Guid> Candidates);

/// <summary>Khoảng giá trị bình thường của một chỉ số (để loại ứng viên có giá trị vô lý).</summary>
public sealed record ValueRange(decimal? Low, decimal? High);

/// <summary>
/// Gợi ý cho một mã kênh: các chỉ số còn khả năng (một = chắc), và số mẫu đã dùng để suy ra.
/// </summary>
public sealed record ChannelSuggestion(string ChannelCode, IReadOnlyList<Guid> Candidates, int SampleCount)
{
    public bool IsCertain => Candidates.Count == 1;
}

/// <summary>
/// Đoán mã kênh của máy (vd số test AU480) ứng với chỉ số nào, chỉ từ barcode: mỗi lần máy gửi
/// mã kênh X cho mẫu M thì X phải là một trong các chỉ số được chỉ định cho M mà chưa map.
/// <list type="number">
/// <item>Bỏ ứng viên có giá trị vô lý (lệch quá <see cref="PlausibleFactor"/> lần so với khoảng
/// bình thường) — chỉ khi vẫn còn ứng viên khác.</item>
/// <item>Giao các tập ứng viên của cùng một mã kênh qua mọi mẫu.</item>
/// <item>Loại trừ: mã kênh nào chỉ còn một chỉ số thì chỉ số đó không thể là của mã kênh khác.</item>
/// </list>
/// Chỉ là gợi ý — người dùng xác nhận rồi mới lưu map.
/// </summary>
public static class ChannelMapInference
{
    public const decimal PlausibleFactor = 20m;

    public static IReadOnlyList<ChannelSuggestion> Infer(
        IEnumerable<ChannelObservation> observations, IReadOnlyDictionary<Guid, ValueRange> ranges)
    {
        var sets = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var observation in observations)
        {
            if (observation.Candidates.Count == 0)
            {
                continue;
            }

            var candidates = Plausible(observation, ranges);
            counts[observation.ChannelCode] = counts.GetValueOrDefault(observation.ChannelCode) + 1;
            if (sets.TryGetValue(observation.ChannelCode, out var existing))
            {
                var narrowed = existing.Intersect(candidates).ToHashSet();
                // Giao rỗng = dữ liệu mâu thuẫn (máy chạy test không chỉ định…) — giữ tập cũ.
                if (narrowed.Count > 0)
                {
                    sets[observation.ChannelCode] = narrowed;
                }
            }
            else
            {
                sets[observation.ChannelCode] = candidates.ToHashSet();
            }
        }

        // Loại trừ lặp đến khi không đổi.
        bool changed;
        do
        {
            changed = false;
            var certain = sets.Where(kv => kv.Value.Count == 1).ToList();
            foreach (var (code, set) in certain)
            {
                var service = set.Single();
                foreach (var (otherCode, otherSet) in sets)
                {
                    if (otherCode != code && otherSet.Count > 1 && otherSet.Remove(service))
                    {
                        changed = true;
                    }
                }
            }
        }
        while (changed);

        return sets
            .Select(kv => new ChannelSuggestion(kv.Key, kv.Value.OrderBy(id => id).ToList(), counts[kv.Key]))
            .OrderBy(s => s.ChannelCode, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlySet<Guid> Plausible(ChannelObservation observation, IReadOnlyDictionary<Guid, ValueRange> ranges)
    {
        if (observation.Value is not { } value)
        {
            return observation.Candidates;
        }

        var kept = observation.Candidates.Where(id => !ranges.TryGetValue(id, out var range) || IsPlausible(value, range)).ToHashSet();
        return kept.Count > 0 ? kept : observation.Candidates;
    }

    private static bool IsPlausible(decimal value, ValueRange range)
    {
        if (range.High is { } high && high > 0 && value > high * PlausibleFactor)
        {
            return false;
        }

        if (range.Low is { } low && low > 0 && value >= 0 && value < low / PlausibleFactor)
        {
            return false;
        }

        return true;
    }
}
