namespace Lis.Server.Api;

/// <summary>Định dạng dùng chung của <c>/api</c> (giữ đúng như NestJS): id trên đường dẫn và thời điểm trong JSON.</summary>
internal static class ApiFormat
{
    /// <summary>Id trên đường dẫn phải là uuid dạng có gạch nối; sai → 400 với câu của NestJS.</summary>
    public static Guid ParseId(string id) =>
        Guid.TryParseExact(id, "D", out var guid) ? guid : throw ApiException.BadRequest("Validation failed (uuid is expected)");

    /// <summary>Thời điểm theo UTC, dạng <c>Date.toISOString()</c> của JavaScript.</summary>
    public static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
