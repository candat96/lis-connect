using System.Text.Json;

namespace Lis.Server.Api;

/// <summary>
/// Lỗi nghiệp vụ của <c>/api</c>: được trả thành <c>{"statusCode": mã, "message": "câu tiếng Việt"}</c>
/// (web đọc trường <c>message</c> và hiển thị thẳng cho người dùng).
/// </summary>
public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    public static ApiException BadRequest(string message) => new(StatusCodes.Status400BadRequest, message);

    public static ApiException Unauthorized(string message) => new(StatusCodes.Status401Unauthorized, message);

    public static ApiException Forbidden(string message) => new(StatusCodes.Status403Forbidden, message);

    public static ApiException NotFound(string message) => new(StatusCodes.Status404NotFound, message);

    public static ApiException Conflict(string message) => new(StatusCodes.Status409Conflict, message);

    public static ApiException TooManyRequests(string message) => new(StatusCodes.Status429TooManyRequests, message);
}

internal static class ApiErrors
{
    /// <summary>Bọc mọi request <c>/api</c>: bắt <see cref="ApiException"/>, lỗi đọc thân request, và lỗi bất ngờ.</summary>
    public static async Task HandleAsync(HttpContext http, RequestDelegate next)
    {
        // Cho phép đọc lại thân request (bộ lọc "cấm trường lạ" kiểm lại sau khi đã bind).
        http.Request.EnableBuffering();
        try
        {
            await next(http);
        }
        catch (ApiException ex) when (!http.Response.HasStarted)
        {
            await WriteAsync(http, ex.Status, ex.Message);
        }
        catch (BadHttpRequestException) when (!http.Response.HasStarted)
        {
            await WriteAsync(http, StatusCodes.Status400BadRequest, "Dữ liệu gửi lên không hợp lệ");
        }
        catch (JsonException) when (!http.Response.HasStarted)
        {
            await WriteAsync(http, StatusCodes.Status400BadRequest, "Dữ liệu gửi lên không hợp lệ");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !http.Response.HasStarted)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Lis.Server.Api")
                .LogError(ex, "Lỗi không xử lý ở {Path}", http.Request.Path);
            await WriteAsync(http, StatusCodes.Status500InternalServerError, "Máy chủ gặp lỗi — thử lại sau");
        }
    }

    private static Task WriteAsync(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { statusCode = status, message });
    }
}
