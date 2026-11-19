using Lis.Core.Agents;
using Lis.Data.Local;
using Microsoft.Extensions.Time.Testing;

namespace Lis.Agent.Tests;

/// <summary>Dịch vụ Windows giả: ghi lại chuỗi lệnh để kiểm thứ tự cài và hoàn tác.</summary>
internal sealed class FakeServiceControl(bool exists = false, bool running = false, string? binPath = null) : IServiceControl
{
    public List<string> Commands { get; } = [];

    public bool Present { get; private set; } = exists;

    public bool Running { get; private set; } = running;

    public string? CurrentBinPath { get; private set; } = binPath;

    public string? BinPath() => CurrentBinPath;

    public void SetBinPath(string exePath) { Commands.Add("SetBinPath"); CurrentBinPath = exePath; }

    public bool WaitRunningResult { get; set; } = true;

    public string? CreatedExe { get; private set; }

    public bool Exists() { Commands.Add("Exists"); return Present; }

    public bool IsRunning() { Commands.Add("IsRunning"); return Running; }

    public void Create(string exePath) { Commands.Add("Create"); CreatedExe = exePath; CurrentBinPath = exePath; Present = true; }

    public void Start() { Commands.Add("Start"); Running = true; }

    public void Stop() { Commands.Add("Stop"); Running = false; }

    public void Delete() { Commands.Add("Delete"); Present = false; Running = false; }

    public void Disable() { Commands.Add("Disable"); }

    public bool WaitRunning(TimeSpan timeout) { Commands.Add("WaitRunning"); return WaitRunningResult; }
}

internal sealed class FakeElevation(bool elevated, ElevationResult result = ElevationResult.Started, int childExit = 0, string? failWith = null) : IElevation
{
    public List<(string Exe, string Args)> Relaunches { get; } = [];

    public bool IsElevated() => elevated;

    public ElevationResult RelaunchElevated(string exePath, string arguments, out int exitCode)
    {
        Relaunches.Add((exePath, arguments));
        if (failWith is not null)
        {
            throw new InvalidOperationException(failWith);
        }

        exitCode = childExit;
        return result;
    }
}

internal static class TestCodes
{
    public static PairingCode Code(string url = "https://may-chu.thu") => new(url, "pkthu", PairingCode.NewSecret());

    public static PairingState State(string url = "https://may-chu.thu") =>
        new(url, Guid.NewGuid(), "pkthu", "Phòng khám Thử", Guid.NewGuid(), "Agent Thử", DateTimeOffset.UnixEpoch);
}

internal sealed class FakeDataDirSecurity(List<string>? log = null) : IDataDirSecurity
{
    public int Calls { get; private set; }

    public void Secure(string directory) { Calls++; log?.Add("Secure:" + directory); }
}
