using Shouldly;
using Xunit;

namespace Lis.Agent.Tests;

public sealed class AgentCommandTests
{
    [Fact]
    public void Dich_vu_Windows_luon_la_Service()
    {
        AgentCommand.Parse([], isWindowsService: true, isWindows: true).ShouldBeOfType<AgentCommand.Service>();
        AgentCommand.Parse(["run"], isWindowsService: true, isWindows: true).ShouldBeOfType<AgentCommand.Service>();
    }

    [Fact]
    public void Run_la_Run()
    {
        AgentCommand.Parse(["run"], false, true).ShouldBeOfType<AgentCommand.Run>();
        AgentCommand.Parse(["RUN"], false, false).ShouldBeOfType<AgentCommand.Run>();
    }

    [Fact]
    public void Pair_kem_ma()
    {
        AgentCommand.Parse(["pair", "LIS1.abc"], false, false).ShouldBe(new AgentCommand.Pair("LIS1.abc"));
    }

    [Fact]
    public void Pair_thieu_ma_thi_bao_loi_huong_dan()
    {
        var ex = Should.Throw<ArgumentException>(() => AgentCommand.Parse(["pair"], false, false));
        ex.Message.ShouldContain("pair");
    }

    [Fact]
    public void Install_co_hoac_khong_co_ma()
    {
        AgentCommand.Parse(["install"], false, true).ShouldBe(new AgentCommand.Install(null));
        AgentCommand.Parse(["install", "--code", "LIS1.abc"], false, true).ShouldBe(new AgentCommand.Install("LIS1.abc"));
    }

    [Fact]
    public void Install_code_thieu_gia_tri_thi_bao_loi()
    {
        Should.Throw<ArgumentException>(() => AgentCommand.Parse(["install", "--code"], false, true));
    }

    [Fact]
    public void Uninstall()
    {
        AgentCommand.Parse(["uninstall"], false, true).ShouldBeOfType<AgentCommand.Uninstall>();
    }

    [Fact]
    public void Version_la_Version_tren_moi_he_dieu_hanh()
    {
        AgentCommand.Parse(["version"], false, true).ShouldBeOfType<AgentCommand.Version>();
        AgentCommand.Parse(["VERSION"], false, false).ShouldBeOfType<AgentCommand.Version>();
        Should.Throw<ArgumentException>(() => AgentCommand.Parse(["version", "x"], false, false));
    }

    [Fact]
    public void Khong_tham_so_Windows_la_Interactive_noi_khac_la_Run()
    {
        AgentCommand.Parse([], false, true).ShouldBeOfType<AgentCommand.Interactive>();
        AgentCommand.Parse([], false, false).ShouldBeOfType<AgentCommand.Run>();
    }

    [Fact]
    public void Tham_so_la_thi_bao_loi_tieng_Viet()
    {
        var ex = Should.Throw<ArgumentException>(() => AgentCommand.Parse(["xoa"], false, true));
        ex.Message.ShouldContain("Lệnh không hợp lệ");
    }
}
