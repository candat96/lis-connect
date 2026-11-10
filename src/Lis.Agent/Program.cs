using Lis.Agent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Chạy thử: LisAgent.exe (cửa sổ dòng lệnh). Cài làm dịch vụ Windows (tự chạy khi bật máy):
//   sc create "LIS Agent" binPath= "C:\LisAgent\LisAgent.exe" start= auto
// Cấu hình ở appsettings.json cạnh tệp chạy: Server:Url, Server:AgentKey (lấy từ lệnh "LisServer agent add").
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "LIS Agent");

// Dịch vụ Windows chạy với thư mục làm việc là System32 — đọc cấu hình cạnh tệp chạy.
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true);
builder.Configuration.AddEnvironmentVariables("LISAGENT_");

var serverUrl = builder.Configuration["Server:Url"];
var agentKey = builder.Configuration["Server:AgentKey"];
if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(agentKey))
{
    Console.Error.WriteLine("Thiếu cấu hình Server:Url hoặc Server:AgentKey trong appsettings.json.");
    return 2;
}

var dataRoot = builder.Configuration["DataDirectory"];
builder.Services.AddSingleton(string.IsNullOrWhiteSpace(dataRoot) ? AgentPaths.Default : new AgentPaths(dataRoot));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new ServerClient(ServerClient.CreateHttp(serverUrl, agentKey)));
builder.Services.AddSingleton<LogUploader>();
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
return 0;
