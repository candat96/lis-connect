namespace Lis.Core.Results;

/// <summary>
/// Tách dấu so sánh (<c>"&lt;="</c>, <c>"&gt;="</c>, <c>"&lt;"</c>, <c>"&gt;"</c>)
/// đứng đầu một chuỗi giá trị đã cắt khoảng trắng. Dùng chung cho
/// <see cref="ResultValue"/> và <see cref="ValueRounding"/> để tránh lặp.
/// </summary>
internal static class ComparatorParsing
{
    private static readonly string[] Comparators = { "<=", ">=", "<", ">" };

    /// <summary>
    /// Trả về dấu so sánh (hoặc null nếu không có) và phần còn lại của chuỗi
    /// (đã cắt khoảng trắng) sau khi bỏ dấu so sánh.
    /// </summary>
    public static (string? Comparator, string Body) Strip(string trimmed)
    {
        foreach (var candidate in Comparators)
        {
            if (trimmed.StartsWith(candidate, StringComparison.Ordinal))
            {
                return (candidate, trimmed[candidate.Length..].Trim());
            }
        }

        return (null, trimmed);
    }
}
