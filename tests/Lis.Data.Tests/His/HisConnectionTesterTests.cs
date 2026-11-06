using System.Net;
using System.Net.Sockets;
using Lis.Data.His;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.His;

/// <summary>
/// Kiểm tra <see cref="HisConnectionTester"/> với một máy chủ HTTP giả dựng bằng
/// <see cref="HttpListener"/> (không cần HIS thật). Bao các trường hợp: thành công, sai xác
/// thực (401), sai đường dẫn (404), không tới được máy chủ, thiếu token ở bearer-login.
/// </summary>
public sealed class HisConnectionTesterTests
{
    [Fact]
    public async Task TestAsync_Reachable_ReturnsOkWithStatusCode()
    {
        using var server = FakeHisServer.Start(request =>
        {
            request.Response.StatusCode = 200;
            WriteAndClose(request.Response, "{\"ok\":true}");
        });

        var profile = HisProfile.Empty with { BaseUrl = server.BaseUrl, AuthType = "none", OrdersPath = "/api/v1/system/info" };
        using var httpClient = new HttpClient();
        var tester = new HisConnectionTester(httpClient);

        var result = await tester.TestAsync(profile, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue();
        result.Message.ShouldContain("200");
    }

    [Fact]
    public async Task TestAsync_Unauthorized_ReturnsVietnameseMessage()
    {
        using var server = FakeHisServer.Start(request =>
        {
            request.Response.StatusCode = 401;
            WriteAndClose(request.Response, "");
        });

        var profile = HisProfile.Empty with { BaseUrl = server.BaseUrl, AuthType = "none", OrdersPath = "/orders" };
        using var httpClient = new HttpClient();
        var tester = new HisConnectionTester(httpClient);

        var result = await tester.TestAsync(profile, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("Sai xác thực");
    }

    [Fact]
    public async Task TestAsync_NotFound_ReturnsVietnameseMessage()
    {
        using var server = FakeHisServer.Start(request =>
        {
            request.Response.StatusCode = 404;
            WriteAndClose(request.Response, "");
        });

        var profile = HisProfile.Empty with { BaseUrl = server.BaseUrl, AuthType = "none", OrdersPath = "/orders" };
        using var httpClient = new HttpClient();
        var tester = new HisConnectionTester(httpClient);

        var result = await tester.TestAsync(profile, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("Sai đường dẫn");
    }

    [Fact]
    public async Task TestAsync_Unreachable_ReturnsVietnameseMessage()
    {
        // Cổng chắc chắn không có ai nghe: mở rồi đóng ngay để lấy một cổng hợp lệ nhưng trống.
        var freePort = GetFreePort();

        var profile = HisProfile.Empty with { BaseUrl = $"http://127.0.0.1:{freePort}", AuthType = "none", OrdersPath = "/orders" };
        using var httpClient = new HttpClient();
        var tester = new HisConnectionTester(httpClient);

        var result = await tester.TestAsync(profile, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("Không kết nối được máy chủ HIS");
    }

    [Fact]
    public async Task TestAsync_BearerLogin_MissingToken_ReturnsVietnameseMessage()
    {
        using var server = FakeHisServer.Start(request =>
        {
            if (request.Request.Url!.AbsolutePath == "/login")
            {
                request.Response.StatusCode = 200;
                WriteAndClose(request.Response, "{\"khong_co_token\":true}");
                return;
            }

            request.Response.StatusCode = 200;
            WriteAndClose(request.Response, "{}");
        });

        var profile = HisProfile.Empty with
        {
            BaseUrl = server.BaseUrl,
            AuthType = "bearer-login",
            LoginPath = "/login",
            LoginBodyTemplate = "{\"username\":\"{username}\",\"password\":\"{password}\"}",
            TokenJsonPath = "token",
            Username = "bs",
            Secret = "matkhau",
            OrdersPath = "/orders",
        };
        using var httpClient = new HttpClient();
        var tester = new HisConnectionTester(httpClient);

        var result = await tester.TestAsync(profile, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("token");
    }

    [Fact]
    public async Task TestAsync_BearerLogin_Success_UsesTokenOnOrdersCall()
    {
        string? receivedAuthHeader = null;

        using var server = FakeHisServer.Start(request =>
        {
            if (request.Request.Url!.AbsolutePath == "/login")
            {
                request.Response.StatusCode = 200;
                WriteAndClose(request.Response, "{\"data\":{\"token\":\"abc123\"}}");
                return;
            }

            receivedAuthHeader = request.Request.Headers["Authorization"];
            request.Response.StatusCode = 200;
            WriteAndClose(request.Response, "[]");
        });

        var profile = HisProfile.Empty with
        {
            BaseUrl = server.BaseUrl,
            AuthType = "bearer-login",
            LoginPath = "/login",
            LoginBodyTemplate = "{\"username\":\"{username}\",\"password\":\"{password}\"}",
            TokenJsonPath = "data.token",
            Username = "bs",
            Secret = "matkhau",
            OrdersPath = "/orders",
        };
        using var httpClient = new HttpClient();
        var tester = new HisConnectionTester(httpClient);

        var result = await tester.TestAsync(profile, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue();
        receivedAuthHeader.ShouldBe("Bearer abc123");
    }

    [Fact]
    public async Task TestAsync_ApiKey_SendsConfiguredHeader()
    {
        string? receivedHeader = null;

        using var server = FakeHisServer.Start(request =>
        {
            receivedHeader = request.Request.Headers["X-Integration-Key"];
            request.Response.StatusCode = 200;
            WriteAndClose(request.Response, "{\"ok\":true}");
        });

        var profile = HisProfile.Empty with
        {
            BaseUrl = server.BaseUrl,
            AuthType = "api-key",
            ApiKeyHeader = "X-Integration-Key",
            Secret = "khoa-bi-mat",
            OrdersPath = "/api/v1/system/info",
        };
        using var httpClient = new HttpClient();
        var tester = new HisConnectionTester(httpClient);

        var result = await tester.TestAsync(profile, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue();
        receivedHeader.ShouldBe("khoa-bi-mat");
    }

    [Fact]
    public async Task TestAsync_ApiKey_MissingHeaderName_ReturnsVietnameseMessage_WithoutSendingRequest()
    {
        var requestSent = false;
        using var server = FakeHisServer.Start(request =>
        {
            requestSent = true;
            request.Response.StatusCode = 200;
            WriteAndClose(request.Response, "{\"ok\":true}");
        });

        var profile = HisProfile.Empty with
        {
            BaseUrl = server.BaseUrl,
            AuthType = "api-key",
            ApiKeyHeader = "",
            Secret = "khoa-bi-mat",
            OrdersPath = "/api/v1/system/info",
        };
        using var httpClient = new HttpClient();
        var tester = new HisConnectionTester(httpClient);

        var result = await tester.TestAsync(profile, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldBe("Chưa nhập tên header chứa khóa API");
        requestSent.ShouldBeFalse();
    }

    private static void WriteAndClose(HttpListenerResponse response, string body)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    private static int GetFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }
}

/// <summary>Máy chủ HTTP giả dựng bằng <see cref="HttpListener"/>, chạy trên một cổng trống ngẫu nhiên.</summary>
internal sealed class FakeHisServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private FakeHisServer(HttpListener listener, string baseUrl)
    {
        _listener = listener;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public static FakeHisServer Start(Action<HttpListenerContext> handle)
    {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var server = new FakeHisServer(listener, prefix.TrimEnd('/'));
        server.AcceptLoop(handle);
        return server;
    }

    private void AcceptLoop(Action<HttpListenerContext> handle)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    handle(context);
                }
                catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }
            }
        });
    }

    private static int GetFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }
}
