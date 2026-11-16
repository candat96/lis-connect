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
    private readonly TenantRegistry _tenants;
    private readonly List<HttpClient> _clients = [];

    private ApiHost(ServerFixture fixture, string? setupKey)
    {
        _fixture = fixture;
        _master = new MasterStore(fixture.MasterConnection);
        _tenants = new TenantRegistry(fixture.TenantTemplate, TimeProvider.System);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Master:ConnectionString", fixture.MasterConnection);
            builder.UseSetting("Tenants:ConnectionTemplate", fixture.TenantTemplate);
            builder.UseSetting("Jwt:Secret", TestSecret);
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, TestRoutes>());
            if (setupKey is not null)
            {
                builder.UseSetting("Setup:Key", setupKey);
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
        };
    }

    public HttpClient Anonymous { get; }

    public static async Task<ApiHost> StartAsync(ServerFixture fixture, string? setupKey = null)
    {
        await new MasterStore(fixture.MasterConnection).EnsureSchemaAsync();
        return new ApiHost(fixture, setupKey);
    }

    /// <summary>Tạo CSDL phòng khám + migrate + ghi vào master.</summary>
    public async Task<Clinic> AddClinicAsync(string code, string name)
    {
        (await AdminCommands.AddClinicAsync(code, name, $"lis_{code}", _master, _tenants)).ShouldBe(0);
        return (await _master.FindClinicAsync(code))!;
    }

    public async Task AddUserAsync(Clinic clinic, string username, string password, params string[] roles) =>
        await new UserStore(_tenants.For(clinic).Db).CreateAsync(username, $"{username} Thử", null, password, roles);

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
