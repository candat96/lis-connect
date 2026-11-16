using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Lis.Server;

/// <summary>Phục vụ giao diện web đã build (tệp tĩnh + fallback <c>index.html</c> cho route phía trình duyệt) và header của reverse proxy.</summary>
public static class WebHosting
{
    // /agent/v1 là API của agent; "/agent" một mình là trang Agent của web nên vẫn nhận index.html.
    // /assets: tệp băm tên không còn thì 404 (không trả HTML cho yêu cầu JavaScript của trang cũ).
    private static readonly string[] ServerPrefixes = ["/api", "/agent/v1", "/healthz", "/assets"];

    /// <summary>
    /// <c>Proxy:TrustForwardedHeaders=true</c> → dùng <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> (địa chỉ gốc cho
    /// chặn dò mã thiết lập và log). Mặc định tắt: header này tự khai nên chỉ bật khi server đứng sau proxy tin cậy và không mở ra ngoài trực tiếp.
    /// </summary>
    public static void UseTrustedForwardedHeaders(this WebApplication app, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Proxy:TrustForwardedHeaders"))
        {
            return;
        }

        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        app.UseForwardedHeaders(options);
    }

    /// <summary>Thư mục web: <c>Web:Root</c>, không đặt thì <c>wwwroot</c> cạnh tệp chạy nếu có; không có thì không phục vụ web.</summary>
    public static string? ResolveRoot(string? configured)
    {
        if (configured is not null)
        {
            return configured;
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        return Directory.Exists(fallback) ? fallback : null;
    }

    public static void UseWebApp(this WebApplication app, string? configuredRoot)
    {
        var root = ResolveRoot(configuredRoot);
        if (root is null)
        {
            return;
        }

        var files = new PhysicalFileProvider(Path.GetFullPath(root));
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context =>
            {
                var path = context.Context.Request.Path;
                context.Context.Response.Headers.CacheControl = path.StartsWithSegments("/assets")
                    ? "public,max-age=31536000,immutable"
                    : "no-cache";
            },
        });

        // Route phía trình duyệt (vd /ket-qua/abc): chỉ GET/HEAD, chỉ khi chưa có route nào khớp, và không đụng tới /api, /agent/v1, /healthz, /assets.
        app.Use(async (http, next) =>
        {
            var index = files.GetFileInfo("index.html");
            if (http.GetEndpoint() is null
                && (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
                && !ServerPrefixes.Any(prefix => http.Request.Path.StartsWithSegments(prefix))
                && index.Exists)
            {
                http.Response.ContentType = "text/html; charset=utf-8";
                http.Response.Headers.CacheControl = "no-cache";
                http.Response.ContentLength = index.Length;
                if (HttpMethods.IsGet(http.Request.Method))
                {
                    await http.Response.SendFileAsync(index, http.RequestAborted);
                }

                return;
            }

            await next(http);
        });
    }
}
