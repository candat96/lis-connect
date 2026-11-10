using Lis.Data;
using Lis.Server;
using Lis.Server.AgentApi;
using Lis.Server.Tenancy;

// Lệnh quản trị (chạy rồi thoát):
//   LisServer clinic add --code pkstl --name "Phòng khám STL" --db lis
//   LisServer clinic list
//   LisServer agent add --clinic pkstl --name "Máy phòng xét nghiệm" --workstation XN-01
// Không có lệnh → chạy server web.
var builder = WebApplication.CreateBuilder(args);

var masterConnection = builder.Configuration["Master:ConnectionString"]
    ?? throw new InvalidOperationException("Thiếu cấu hình Master:ConnectionString");
var tenantTemplate = builder.Configuration["Tenants:ConnectionTemplate"]
    ?? throw new InvalidOperationException("Thiếu cấu hình Tenants:ConnectionTemplate");

var master = new MasterStore(masterConnection);
var tenants = new TenantRegistry(tenantTemplate, TimeProvider.System);

if (AdminCommands.IsCommand(args))
{
    return await AdminCommands.RunAsync(args, master, tenants);
}

builder.Services.AddSingleton(master);
builder.Services.AddSingleton(tenants);

var app = builder.Build();

await master.EnsureSchemaAsync();

// Nâng schema CSDL của mọi phòng khám lên bản mới nhất trước khi nhận request.
foreach (var clinic in await master.ClinicsAsync())
{
    var result = Migrator.Upgrade(tenants.ConnectionStringFor(clinic));
    if (!result.Ok)
    {
        app.Logger.LogError("Nâng cấp CSDL phòng khám {Clinic} thất bại: {Error}", clinic.Code, result.Error);
    }
}

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
app.MapAgentApi();

await app.RunAsync();
return 0;

/// <summary>Để kiểm thử tích hợp (WebApplicationFactory) tham chiếu được.</summary>
public partial class Program;
