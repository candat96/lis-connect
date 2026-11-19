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
var store = new PairingStore(paths, SecretProtectors.ForCurrentOs(paths.Root), TimeProvider.System);

switch (command)
{
    case AgentCommand.Pair pair:
        return await PairAsync(pair.CodeText, paths, store);
    case AgentCommand.Service or AgentCommand.Run:
        Directory.CreateDirectory(paths.Root);
        return await RunAsync(config, environment, paths, store);
    default:
        // Bấm đúp tệp cài / install / uninstall: chỉ Windows (dịch vụ, UAC).
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Chỉ hỗ trợ trên Windows");
            return 2;
        }

        return await SetupAsync(command, paths, store);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static async Task<int> SetupAsync(AgentCommand command, AgentPaths paths, PairingStore store)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.InputEncoding = System.Text.Encoding.UTF8;
    var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LIS Agent");
    var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Không xác định được đường dẫn tệp đang chạy");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var installer = new AgentInstaller(
        paths, installDir, new ScServiceControl(),
        (code, ct) => PairingFlow.PairAsync(code, new PairingClient(http), store, TimeProvider.System, ct),
        Console.Out, runningExe: exePath, dataSecurity: new WindowsDataDirSecurity());
    return await new SetupConsole(Console.In, Console.Out, new WindowsElevation(), installer, store, exePath)
        .RunAsync(command, CancellationToken.None);
}

static async Task<int> PairAsync(string? codeText, AgentPaths paths, PairingStore store)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    return await new PairCommand(
        paths, OperatingSystem.IsWindows() ? ServiceControlForWindows() : null,
        (code, ct) => PairingFlow.PairAsync(code, new PairingClient(http), store, TimeProvider.System, ct),
        Console.Out, Console.Error).RunAsync(codeText, CancellationToken.None);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static IServiceControl ServiceControlForWindows() => new ScServiceControl();

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
