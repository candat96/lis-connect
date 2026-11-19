using Lis.Agent;
using Lis.Connections;
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
    case AgentCommand.ApplyUpdate apply:
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Chỉ hỗ trợ trên Windows");
            return 2;
        }

        return await ApplyUpdateAsync(apply.CommandId, paths);
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

/// <summary>Chạy từ <c>LisAgent.new.exe</c> (SYSTEM, tách rời khỏi dịch vụ): áp bản mới, quay về bản cũ nếu không nối lại được trong 2 phút.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static async Task<int> ApplyUpdateAsync(Guid commandId, AgentPaths paths)
{
    var logFile = Path.Combine(paths.Root, "cap-nhat.log");
    void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            File.AppendAllText(logFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{commandId}] {message}{Environment.NewLine}", new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    var service = new ScServiceControl();
    var newExe = Environment.ProcessPath ?? "";
    string? installed;
    try
    {
        installed = service.BinPath();
    }
    catch (Exception ex)
    {
        // Không đọc được cấu hình dịch vụ vẫn phải báo lên web, kẻo lệnh treo tới hết 10 phút.
        Log($"Đọc cấu hình dịch vụ lỗi: {ex.Message}");
        installed = null;
    }

    if (installed is null || !string.Equals(Path.GetDirectoryName(installed), Path.GetDirectoryName(newExe), StringComparison.OrdinalIgnoreCase)
        || !string.Equals(Path.GetFileName(newExe), UpdateHandler.NewFileName, StringComparison.OrdinalIgnoreCase))
    {
        UpdateApplier.FailEarly(commandId, "Không xác định được tệp cài của dịch vụ cạnh tệp bản mới — không cập nhật", paths, service, Log);
        return 2;
    }

    Log($"Bắt đầu cập nhật lên {AgentVersion.Current}");
    var report = await new UpdateApplier(
        installed, newExe, paths, service, TimeProvider.System, UpdateApplier.DefaultConnectTimeout, Log,
        deleteAfterExit: DeleteFileAfterExit).ApplyAsync(commandId, AgentVersion.Current, CancellationToken.None);
    Log(report.Ok ? "Xong: thành công" : $"Xong: thất bại — {report.Error}");
    return report.Ok ? 0 : 1;
}

static void DeleteFileAfterExit(string file)
{
    var script = Path.Combine(Path.GetTempPath(), $"lis-agent-del-{Guid.NewGuid():N}.cmd");
    File.WriteAllText(script, UpdateApplier.BuildDeleteFileScript(file));
    new ProcessLauncher().StartDetached("cmd.exe", $"/c \"\"{script}\"\"", Path.GetTempPath());
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

    // Kênh lệnh: các lệnh agent hiểu (task sau thêm quét cổng / cập nhật bằng cách đăng ký thêm IAgentCommandHandler).
    var runningAsService = WindowsServiceHelpers.IsWindowsService();
    builder.Services.AddSingleton<AgentRuntime>();
    builder.Services.AddSingleton<IAgentLifetime, HostAgentLifetime>();
    builder.Services.AddSingleton<IAgentCommandHandler, ReloadConfigHandler>();
    builder.Services.AddSingleton<IAgentCommandHandler, ReconnectInstrumentHandler>();
    builder.Services.AddSingleton<IAgentCommandHandler, FlushPendingHandler>();
    builder.Services.AddSingleton<ISerialPortService, SerialPortService>();
    builder.Services.AddSingleton<IPortProbe, SerialPortProbe>();
    builder.Services.AddSingleton<IAgentCommandHandler, ScanPortsHandler>();
    builder.Services.AddSingleton<IAgentCommandHandler, TestPortHandler>();
    builder.Services.AddSingleton<IAgentCommandHandler>(sp => new RestartHandler(
        sp.GetRequiredService<AgentRuntime>(), sp.GetRequiredService<IAgentLifetime>(), runningAsService,
        sp.GetRequiredService<ILogger<RestartHandler>>()));
    builder.Services.AddSingleton<IAgentCommandHandler>(sp => new RevokeHandler(
        sp.GetRequiredService<AgentRuntime>(), paths, sp.GetRequiredService<IAgentLifetime>(),
        runningAsService && OperatingSystem.IsWindows() ? ServiceControlForWindows() : null, runningAsService,
        sp.GetRequiredService<ILogger<RevokeHandler>>()));
    builder.Services.AddSingleton<IProcessLauncher, ProcessLauncher>();
    builder.Services.AddSingleton<IAgentCommandHandler>(sp => new UpdateHandler(
        sp.GetRequiredService<ServerClient>().DownloadInstallerAsync, AppContext.BaseDirectory, AgentVersion.Current,
        runningAsService, sp.GetRequiredService<IProcessLauncher>(), paths, sp.GetRequiredService<IAgentLifetime>(),
        sp.GetRequiredService<ILogger<UpdateHandler>>()));
    builder.Services.AddSingleton<CommandDispatcher>();
    builder.Services.AddSingleton(sp => new ChannelClient(
        new Uri(connection.ServerUrl), connection.AgentKey, sp.GetRequiredService<CommandDispatcher>(), paths,
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("LisAgent.Channel"),
        note: sp.GetRequiredService<LogUploader>().AgentNote));
    builder.Services.AddHostedService<AgentWorker>();

    var host = builder.Build();
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LisAgent")
        .LogInformation("Agent {Version} kết nối tới {Url} (nguồn: {Source})", AgentVersion.Current, connection.ServerUrl, connection.Source);
    await host.RunAsync();
    return 0;
}
