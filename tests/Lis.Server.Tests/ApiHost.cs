using Lis.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Shouldly;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lis.Data.Auth;
using Lis.Server.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lis.Server.Tests;

/// <summary>Server web chạy trong tiến trình thử, nối vào container của <see cref="ServerFixture"/>.</summary>
public sealed class ApiHost : IAsyncDisposable
{
    public const string TestSecret = "khoa-thu-nghiem-khong-dung-that-0123456789";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ServerFixture _fixture;
    private readonly MasterStore _master;
    private readonly TimeProvider _clock;
    private readonly TenantRegistry _tenants;
    private readonly List<HttpClient> _clients = [];

    private ApiHost(ServerFixture fixture, string? setupKey, IReadOnlyDictionary<string, string>? settings, TimeProvider? clock)
    {
        _fixture = fixture;
        _clock = clock ?? TimeProvider.System;
        _master = new MasterStore(fixture.MasterConnection);
        _tenants = new TenantRegistry(fixture.TenantTemplate, clock ?? TimeProvider.System);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Master:ConnectionString", fixture.MasterConnection);
            builder.UseSetting("Tenants:ConnectionTemplate", fixture.TenantTemplate);
            builder.UseSetting("Jwt:Secret", TestSecret);
            builder.ConfigureServices(services =>
            {
                services.AddTransient<IStartupFilter, TestRoutes>();
                if (clock is not null)
                {
                    services.AddSingleton(clock);
                }
            });
            if (setupKey is not null)
            {
                builder.UseSetting("Setup:Key", setupKey);
            }

            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }
        });
        Anonymous = Track(_factory.CreateClient());
    }

    /// <summary>Route thử chỉ có trong host kiểm thử: POST cần phiên, nhận thân JSON <c>{name}</c>.</summary>
    public const string EchoRoute = "/api/test/echo";

    private sealed record EchoBody(string Name);

    private sealed class TestRoutes : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(endpoints =>
                ApiSession.NewApiGroup(endpoints).MapGroup("/test").RequireSession()
                    .MapPost("/echo", (EchoBody body) => Results.Ok(new { body.Name })));
            // Địa chỉ và giao thức server thấy được (kiểm cấu hình proxy).
            app.UseEndpoints(endpoints =>
                endpoints.MapGet("/api/test/ip", (HttpContext http) =>
                    Results.Ok(new { ip = http.Connection.RemoteIpAddress?.ToString(), scheme = http.Request.Scheme })));
        };
    }

    public HttpClient Anonymous { get; }

    /// <summary>Bộ tạo phòng khám / nâng cấp CSDL của server thử.</summary>
    public ClinicProvisioner Provisioner => _factory.Services.GetRequiredService<ClinicProvisioner>();

    /// <summary>Mã thiết lập + độ trễ khi nhập sai của server thử — test đặt <see cref="SetupGuard.Delay"/> để khỏi ngủ thật.</summary>
    public SetupGuard SetupGuard => _factory.Services.GetRequiredService<SetupGuard>();

    /// <summary>Ghi phòng khám vào master với CSDL chưa tạo (không migrate được) — mô phỏng nâng cấp lỗi lúc khởi động.</summary>
    public Task<Clinic> RegisterUnmigratableClinicAsync(string code, string name) =>
        _master.AddClinicAsync(code, name, $"lis_{code.Replace('-', '_')}_khong_ton_tai");

    /// <summary>Thêm agent và trả khoá (chỉ có lúc tạo).</summary>
    public async Task<string> AddAgentKeyAsync(Clinic clinic, string name, string workstationCode) =>
        (await _master.AddAgentAsync(clinic.Id, name, workstationCode)).Key;

    /// <summary>Token phiên hợp lệ cho một tài khoản bất kỳ của phòng khám (không cần CSDL phòng khám).</summary>
    public string IssueToken(Clinic clinic) =>
        _factory.Services.GetRequiredService<SessionTokens>().Issue(Guid.NewGuid(), clinic.Id);

    /// <param name="clock">Đồng hồ gốc của server thử (mặc định đồng hồ hệ thống); múi giờ phòng khám vẫn theo <c>LocalUtcOffsetMinutes</c>.</param>
    /// <param name="settings">Cấu hình thêm cho server thử (vd <c>LocalUtcOffsetMinutes</c>).</param>
    public static async Task<ApiHost> StartAsync(ServerFixture fixture, string? setupKey = null, IReadOnlyDictionary<string, string>? settings = null, TimeProvider? clock = null)
    {
        await new MasterStore(fixture.MasterConnection).EnsureSchemaAsync();
        return new ApiHost(fixture, setupKey, settings, clock);
    }

    /// <summary>Tạo CSDL phòng khám + migrate + ghi vào master.</summary>
    public async Task<Clinic> AddClinicAsync(string code, string name)
    {
        (await AdminCommands.AddClinicAsync(code, name, $"lis_{code}", _master, _tenants)).ShouldBe(0);
        return (await _master.FindClinicAsync(code))!;
    }

    public async Task AddUserAsync(Clinic clinic, string username, string password, params string[] roles) =>
        await new UserStore(_tenants.For(clinic).Db).CreateAsync(username, $"{username} Thử", null, password, roles);

    /// <summary>Thêm agent cho phòng khám (mã máy trạm = <paramref name="workstationCode"/>).</summary>
    public async Task<AgentInfo> AddAgentAsync(Clinic clinic, string name, string workstationCode) =>
        (await _master.AddAgentAsync(clinic.Id, name, workstationCode)).Agent;

    /// <summary>Master của server thử — để gọi thẳng <see cref="MasterStore"/> (tạo mã ghép nối, đọc agent).</summary>
    public MasterStore Master => _master;

    /// <summary>Đồng hồ gốc của server thử.</summary>
    public TimeProvider Clock => _clock;

    /// <summary>Tạo agent chưa có khoá kèm mã ghép nối đầu tiên, tính hạn theo đồng hồ của host.</summary>
    public Task<(AgentInfo Agent, PairingTicket Pairing)> CreateAgentWithPairingAsync(Clinic clinic, string name) =>
        _master.CreateAgentAsync(clinic.Id, name, null, _clock.GetUtcNow());

    /// <summary>Client gọi thẳng server thử, gắn sẵn khoá agent (không gắn khi <paramref name="agentKey"/> null).</summary>
    public HttpClient AgentClient(string? agentKey = null)
    {
        var client = Track(_factory.CreateClient());
        if (agentKey is not null)
        {
            client.DefaultRequestHeaders.Add(Lis.Agent.ServerClient.KeyHeader, agentKey);
        }

        return client;
    }

    /// <summary>Agent báo "còn sống" kèm trạng thái các máy (JSON mảng) — như <c>POST /agent/v1/status</c>.</summary>
    public Task TouchAgentAsync(AgentInfo agent, string? statusJson) => _master.TouchAgentAsync(agent.Id, statusJson);

    /// <summary>Dịch vụ nghiệp vụ của phòng khám — để kiểm tra những gì đã ghi.</summary>
    public TenantServices TenantOf(Clinic clinic) => _tenants.For(clinic);

    /// <summary>Client đã gắn Bearer của phiên vừa đăng nhập.</summary>
    public async Task<HttpClient> LoginAsync(string clinicCode, string username, string password)
    {
        var response = await Anonymous.PostAsJsonAsync("/api/auth/login", new { clinicCode, username, password });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString();
        var client = Track(_factory.CreateClient());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public Task SetActiveAsync(Clinic clinic, string username, bool active) =>
        SetActiveCoreAsync(clinic, username, active);

    private async Task SetActiveCoreAsync(Clinic clinic, string username, bool active)
    {
        var users = _tenants.For(clinic).Users;
        var user = (await users.ListAsync()).Single(u => u.Username == username);
        await users.UpdateAsync(user.Id, user.FullName, user.Title, user.Roles, active, null);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        return _factory.DisposeAsync();
    }

    private HttpClient Track(HttpClient client)
    {
        _clients.Add(client);
        return client;
    }
}
