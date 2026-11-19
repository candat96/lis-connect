using Lis.Data.Local;
using Shouldly;
using Xunit;

namespace Lis.Agent.Tests;

public sealed class AgentInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-install-test-" + Guid.NewGuid().ToString("N"));
    private string DataDir => Path.Combine(_root, "data");
    private string InstallDir => Path.Combine(_root, "ProgramFiles", "LIS Agent");
    private string Target => Path.Combine(InstallDir, "LisAgent.exe");
    private string Source => Path.Combine(_root, "Downloads", "LisAgent_x.exe");

    public AgentInstallerTests()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Source, "moi");
    }

    public void Dispose() => Directory.Delete(_root, true);

    private AgentInstaller Installer(FakeServiceControl service, Func<Lis.Core.Agents.PairingCode, CancellationToken, Task<PairingResult>>? pair = null,
        string? runningExe = null, Action<string>? deferDelete = null) =>
        new(new AgentPaths(DataDir), InstallDir, service,
            pair ?? ((_, _) => Task.FromResult(new PairingResult(TestCodes.State(), null))),
            TextWriter.Null, TimeSpan.FromSeconds(30), runningExe, deferDelete);

    [Fact]
    public async Task Cai_moi_dung_thu_tu_va_tep_vao_thu_muc_cai()
    {
        var service = new FakeServiceControl();
        var outcome = await Installer(service).InstallOrRepairAsync(Source, TestCodes.Code(), CancellationToken.None);

        outcome.ShouldBeOfType<InstallOutcome.Succeeded>();
        service.Commands.ShouldBe(["Exists", "Create", "Start", "WaitRunning"]);
        service.CreatedExe.ShouldBe(Target);
        File.ReadAllText(Target).ShouldBe("moi");
        File.Exists(Target + ".bak").ShouldBeFalse();
    }

    [Fact]
    public async Task Ma_het_han_thi_khong_con_dich_vu_moi_va_khong_con_tep()
    {
        var service = new FakeServiceControl();
        var installer = Installer(service, (_, _) => throw new PairingException("Mã ghép nối đã hết hạn — lấy mã mới trên web"));

        var outcome = await installer.InstallOrRepairAsync(Source, TestCodes.Code(), CancellationToken.None);

        var failed = outcome.ShouldBeOfType<InstallOutcome.Failed>();
        failed.Message.ShouldContain("hết hạn");
        service.Commands.ShouldBe(["Exists", "Create", "Delete"]);
        service.Present.ShouldBeFalse();
        File.Exists(Target).ShouldBeFalse();
    }

    [Fact]
    public async Task Ghep_noi_lai_loi_thi_tra_tep_cu_va_bat_lai_dich_vu_cu()
    {
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Target, "cu");
        var service = new FakeServiceControl(exists: true, running: true);
        var installer = Installer(service, (_, _) => throw new PairingException("Mã đã được dùng"));

        var outcome = await installer.InstallOrRepairAsync(Source, TestCodes.Code(), CancellationToken.None);

        outcome.ShouldBeOfType<InstallOutcome.Failed>();
        File.ReadAllText(Target).ShouldBe("cu");
        File.Exists(Target + ".bak").ShouldBeFalse();
        service.Commands.Last().ShouldBe("Start");
        service.Commands.ShouldNotContain("Delete");
        service.Commands.ShouldContain("Stop");
        service.Running.ShouldBeTrue();
    }

    [Fact]
    public async Task Ghep_noi_lai_thanh_cong_thay_tep_moi_va_xoa_bak()
    {
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Target, "cu");
        var service = new FakeServiceControl(exists: true, running: true);

        var outcome = await Installer(service).InstallOrRepairAsync(Source, TestCodes.Code(), CancellationToken.None);

        outcome.ShouldBeOfType<InstallOutcome.Succeeded>();
        File.ReadAllText(Target).ShouldBe("moi");
        File.Exists(Target + ".bak").ShouldBeFalse();
        service.Commands.ShouldBe(["Exists", "IsRunning", "Stop", "Start", "WaitRunning"]);
    }

    [Fact]
    public async Task Chay_tu_chinh_thu_muc_cai_thi_khong_chep_de()
    {
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Target, "dang-chay");
        var service = new FakeServiceControl(exists: true, running: false);

        var outcome = await Installer(service).InstallOrRepairAsync(Target, TestCodes.Code(), CancellationToken.None);

        outcome.ShouldBeOfType<InstallOutcome.Succeeded>();
        File.ReadAllText(Target).ShouldBe("dang-chay");
        Directory.GetFiles(InstallDir).ShouldBe([Target]);
    }

    [Fact]
    public async Task Dich_vu_khong_len_trong_30_giay_thi_khong_hoan_tac_va_bao_xem_log()
    {
        var service = new FakeServiceControl { WaitRunningResult = false };

        var outcome = await Installer(service).InstallOrRepairAsync(Source, TestCodes.Code(), CancellationToken.None);

        var failed = outcome.ShouldBeOfType<InstallOutcome.Failed>();
        failed.Message.ShouldContain("LIS Agent");
        failed.Message.ShouldContain("Ghép nối lại");
        service.Commands.ShouldNotContain("Delete");
        File.Exists(Target).ShouldBeTrue();
    }

    [Fact]
    public async Task Go_xoa_dich_vu_va_khoa_giu_du_lieu()
    {
        var service = new FakeServiceControl();
        var installer = Installer(service);
        await installer.InstallOrRepairAsync(Source, TestCodes.Code(), CancellationToken.None);
        File.WriteAllText(new AgentPaths(DataDir).Pairing, "{}");
        Directory.CreateDirectory(new AgentPaths(DataDir).Pending);
        File.WriteAllText(Path.Combine(new AgentPaths(DataDir).Pending, "kq.json"), "x");

        var outcome = installer.Uninstall();

        var done = outcome.ShouldBeOfType<InstallOutcome.Uninstalled>();
        done.DataDirectory.ShouldBe(DataDir);
        service.Commands.ShouldContain("Delete");
        File.Exists(new AgentPaths(DataDir).Pairing).ShouldBeFalse();
        File.Exists(Path.Combine(new AgentPaths(DataDir).Pending, "kq.json")).ShouldBeTrue();
        Directory.Exists(InstallDir).ShouldBeFalse();
    }

    [Fact]
    public async Task Go_khi_dang_chay_tu_thu_muc_cai_thi_hen_xoa_sau()
    {
        var service = new FakeServiceControl();
        string? deferred = null;
        var installer = Installer(service, runningExe: Target, deferDelete: d => deferred = d);
        await installer.InstallOrRepairAsync(Source, TestCodes.Code(), CancellationToken.None);

        installer.Uninstall().ShouldBeOfType<InstallOutcome.Uninstalled>();

        deferred.ShouldBe(InstallDir);
        Directory.Exists(InstallDir).ShouldBeTrue();
    }

    [Fact]
    public async Task Sao_luu_loi_thi_bao_loi_goc_va_giu_tep_cu_va_bat_lai_dich_vu()
    {
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Target, "cu");
        Directory.CreateDirectory(Target + ".bak"); // chặn việc ghi .bak
        var service = new FakeServiceControl(exists: true, running: true);

        var outcome = await Installer(service).InstallOrRepairAsync(Source, TestCodes.Code(), CancellationToken.None);

        var failed = outcome.ShouldBeOfType<InstallOutcome.Failed>();
        failed.Message.ShouldNotContain("Hoàn tác chưa trọn vẹn");
        File.ReadAllText(Target).ShouldBe("cu");
        service.Running.ShouldBeTrue();
    }

    [Fact]
    public void Kich_ban_xoa_hen_gio_thu_lai_den_khi_thu_muc_het()
    {
        var script = AgentInstaller.BuildDeleteScript(@"C:\Program Files\LIS Agent");
        script.ShouldContain("for /l %%i in (1,1,60)");
        script.ShouldContain("rmdir /s /q \"C:\\Program Files\\LIS Agent\"");
        script.ShouldContain("ping -n 2");
        script.ShouldContain("cd /d \"%TEMP%\"");
        AgentInstaller.BuildDeleteScript(@"C:\100%x\LIS").ShouldContain("C:\\100%%x\\LIS");
    }

    [Fact]
    public void Go_xoa_ca_bo_dem_danh_tinh()
    {
        var paths = new AgentPaths(DataDir);
        File.WriteAllText(paths.IdentityCache, "{}");
        File.WriteAllText(paths.InstrumentsCache, "[]");

        Installer(new FakeServiceControl()).Uninstall().ShouldBeOfType<InstallOutcome.Uninstalled>();

        File.Exists(paths.IdentityCache).ShouldBeFalse();
        File.Exists(paths.InstrumentsCache).ShouldBeFalse();
    }
}
