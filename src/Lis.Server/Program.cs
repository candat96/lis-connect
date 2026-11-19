using Lis.Server;
using Lis.Server.AgentApi;
using Lis.Server.Api;
using Lis.Server.RawMessages;
using Lis.Server.Tenancy;

// Lệnh quản trị (chạy rồi thoát):
//   LisServer clinic add --code pkstl --name "Phòng khám STL" --db lis
//   LisServer clinic list
//   LisServer user add --clinic pkstl --username admin --name "Quản trị" --password "..."   (tài khoản Quản trị đầu tiên)
//   LisServer agent add --clinic pkstl --name "Máy phòng xét nghiệm" [--workstation XN-01]   (in khoá — cứu hộ / phát triển; thường ghép nối bằng mã trên web)
//   LisServer --healthcheck   (gọi /healthz của chính server đang chạy; thoát 0 = khoẻ, 1 = lỗi — cho HEALTHCHECK của Docker)
// Không có lệnh → chạy server web.
if (HealthCheck.IsRequested(args))
{
    // Địa chỉ nghe đọc y như lúc chạy server (appsettings, ASPNETCORE_URLS, Urls, --urls) để đổi cổng ở đâu cũng không lệch.
    var listenUrls = WebApplication.CreateBuilder(args[1..]).Configuration["urls"];
    using var http = new HttpClient { BaseAddress = new Uri(HealthCheck.LocalUrl(listenUrls)) };
    return await HealthCheck.RunAsync(http);
}

var builder = WebApplication.CreateBuilder(args);

var masterConnection = builder.Configuration["Master:ConnectionString"]
    ?? throw new InvalidOperationException("Thiếu cấu hình Master:ConnectionString");
var tenantTemplate = builder.Configuration["Tenants:ConnectionTemplate"]
    ?? throw new InvalidOperationException("Thiếu cấu hình Tenants:ConnectionTemplate");

var master = new MasterStore(masterConnection);
var localUtcOffset = ServerOptions.LocalUtcOffsetFrom(builder.Configuration);

if (AdminCommands.IsCommand(args))
{
    return await AdminCommands.RunAsync(args, master, new TenantRegistry(tenantTemplate, TimeProvider.System, localUtcOffset));
}

var options = ServerOptions.From(builder.Configuration);

builder.Services.AddSingleton(options);
// Tệp cài agent đọc từ Agent:DistDir (AGENT_DIST_DIR); không cấu hình thì web báo chưa có tệp.
builder.Services.AddSingleton(new AgentDistribution(options.AgentDistDir));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new SessionTokens(options.JwtSecret, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(master);
// Lệnh gửi cho agent (bảng agent_command trên CSDL master) — cùng CSDL với MasterStore.
builder.Services.AddSingleton(new AgentCommandStore(masterConnection));
builder.Services.AddSingleton<AgentChannelHub>();
builder.Services.AddSingleton<AgentCommandSweeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentCommandSweeper>());
// Đồng hồ lấy từ DI (test thay được); TenantRegistry bọc nó thành giờ phòng khám cho mọi dịch vụ chia ngày / tính tuổi.
builder.Services.AddSingleton(sp =>
{
    var rawLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Lis.Server.RawMessages");
    return new TenantRegistry(tenantTemplate, sp.GetRequiredService<TimeProvider>(), localUtcOffset, message => rawLog.LogInformation("{Message}", message));
});
// Bản tin thô: phục hồi khi khởi động và mỗi 60 giây, dọn bản tin quá 90 ngày mỗi giờ.
builder.Services.AddSingleton<RawMessageWorker>();
// RawMessages:WorkerEnabled=false tắt vòng nền (test: worker không được vớt bản tin của test khác); mặc định bật.
if (builder.Configuration.GetValue("RawMessages:WorkerEnabled", true))
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RawMessageWorker>());
}

builder.Services.AddSingleton<ClinicProvisioner>();
builder.Services.AddSingleton(new SetupGuard(options.SetupKey));

var app = builder.Build();

// Sau reverse proxy: chỉ khi bật Proxy:TrustForwardedHeaders mới tin X-Forwarded-For / X-Forwarded-Proto.
app.UseTrustedForwardedHeaders(builder.Configuration);

await master.EnsureSchemaAsync();

// Nâng schema CSDL của mọi phòng khám lên bản mới nhất trước khi nhận request; phòng khám lỗi bị cô lập (503), phòng khác chạy bình thường.
await app.Services.GetRequiredService<ClinicProvisioner>().UpgradeAllAsync();
app.Services.GetRequiredService<SetupGuard>().LogIfGenerated(app.Logger);

// Lệnh gửi agent còn dở từ lần chạy trước không rõ kết quả — đánh dấu unknown, không tự chạy lại. Làm trước khi nhận kết nối agent.
await app.Services.GetRequiredService<AgentCommandStore>().MarkUnfinishedUnknownAsync(app.Services.GetRequiredService<TimeProvider>().GetUtcNow());

// Kênh lệnh agent: ping 20 giây, mất ping 60 giây thì coi là đứt.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(60) });

// Sau khi agent quét cổng COM xong (scan-ports → done) thì lưu danh sách để web hiện.
var channelHub = app.Services.GetRequiredService<AgentChannelHub>();
var timeProvider = app.Services.GetRequiredService<TimeProvider>();
channelHub.ResultObserver = async (agentId, row, result) =>
{
    if (row is { Name: "scan-ports", Status: "done" } && result.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } data
        && data.TryGetProperty("ports", out var ports) && ports.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        await master.SavePortsAsync(agentId, ports.GetRawText(), timeProvider.GetUtcNow());
    }
};

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
app.MapAgentApi();
var api = app.MapApiGroup();
api.MapSetupApi();
api.MapAuthApi();
api.MapUsersApi();
api.MapCatalogApi();
api.MapInstrumentsApi();
api.MapAgentsApi();
api.MapSamplesApi();
api.MapRawMessagesApi();

// Giao diện web: sau các route (route khớp được ưu tiên), chỉ chạy khi có thư mục web.
app.UseWebApp(options.WebRoot);

await app.RunAsync();
return 0;

/// <summary>Để kiểm thử tích hợp (WebApplicationFactory) tham chiếu được.</summary>
public partial class Program;
