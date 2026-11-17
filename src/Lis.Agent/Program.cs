using Lis.Agent;
using Lis.Core.Agents;
using Lis.Data.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

// Cấu hình đến từ phép ghép nối (pairing.json trong thư mục dữ liệu, khoá đã mã hoá), không còn nhập tay.
// Lệnh: run | pair <mã> | install [--code <mã>] | uninstall. Chạy phát triển: biến LISAGENT_Server__Url + LISAGENT_Server__AgentKey.
AgentCommand command;
try
{
    command = AgentCommand.Parse(args, WindowsServiceHelpers.IsWindowsService(), OperatingSystem.IsWindows());
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

// Dịch vụ Windows chạy với thư mục làm việc là System32 — đọc cấu hình cạnh tệp chạy.
var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
    .AddEnvironmentVariables("LISAGENT_")
    .Build();
var environment = AgentConnection.EnvironmentConfiguration(
    Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
        .Select(e => KeyValuePair.Create((string)e.Key, (string?)e.Value)));
var dataRoot = config["DataDirectory"];
var paths = string.IsNullOrWhiteSpace(dataRoot) ? AgentPaths.Default : new AgentPaths(dataRoot);
Directory.CreateDirectory(paths.Root);
var store = new PairingStore(paths, SecretProtectors.ForCurrentOs(paths.Root), TimeProvider.System);

switch (command)
{
    case AgentCommand.Pair pair:
        return await PairAsync(pair.CodeText, store);
    case AgentCommand.Service or AgentCommand.Run:
        return await RunAsync(config, environment, paths, store);
    default:
        // Cài / gỡ dịch vụ và chế độ bấm đúp tệp cài do bước sau nối vào.
        Console.Error.WriteLine("Chỉ hỗ trợ trên Windows");
        return 2;
}

static async Task<int> PairAsync(string? codeText, PairingStore store)
{
    if (!PairingCode.TryFind(codeText, out var code))
    {
        Console.Error.WriteLine("Mã ghép nối không hợp lệ — sao chép đủ chuỗi bắt đầu bằng LIS1. từ mục Agent trên web.");
        return 2;
    }

    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var result = await PairingFlow.PairAsync(code, new PairingClient(http), store, TimeProvider.System, CancellationToken.None);
        if (result.Warning is not null)
        {
            Console.Error.WriteLine(result.Warning);
        }

        if (result.QuarantinedTo is not null)
        {
            Console.Error.WriteLine($"Đổi sang phòng khám khác: kết quả đang chờ gửi của phòng khám cũ đã chuyển vào {result.QuarantinedTo} (không gửi lên phòng khám mới).");
        }

        Console.WriteLine($"Đã ghép nối với phòng khám {result.State.ClinicName} ({result.State.ClinicCode}), agent \"{result.State.AgentName}\".");
        return 0;
    }
    catch (Exception ex) when (ex is PairingException or UriFormatException)
    {
        Console.Error.WriteLine(ex is UriFormatException ? $"Địa chỉ máy chủ trong mã không hợp lệ: {ex.Message}" : ex.Message);
        return 1;
    }
}

static async Task<int> RunAsync(IConfiguration config, IConfiguration environment, AgentPaths paths, PairingStore store)
{
    var connection = AgentConnection.Resolve(environment, store, out var problem);
    if (connection is null)
    {
        Console.Error.WriteLine(problem);
        return 2;
    }

    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddWindowsService(options => options.ServiceName = "LIS Agent");
    builder.Configuration.AddConfiguration(config);
    builder.Services.AddSingleton(paths);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(new ServerClient(ServerClient.CreateHttp(connection.ServerUrl, connection.AgentKey)));
    builder.Services.AddSingleton<LogUploader>();
    builder.Services.AddHostedService<AgentWorker>();

    var host = builder.Build();
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LisAgent")
        .LogInformation("Agent {Version} kết nối tới {Url} (nguồn: {Source})", AgentVersion.Current, connection.ServerUrl, connection.Source);
    await host.RunAsync();
    return 0;
}
