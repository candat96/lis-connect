using Lis.Core.Agents;
using Lis.Data.Local;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Agent.Tests;

public sealed class SetupConsoleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-setup-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceControl _service;
    private readonly StringWriter _output = new();
    private readonly AgentPaths _paths;
    private readonly PairingStore _store;
    private readonly List<PairingCode> _paired = [];
    private string ExeDir => Path.Combine(_root, "Downloads");

    public SetupConsoleTests() : this(false) { }

    private SetupConsoleTests(bool serviceExists)
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateDirectory(ExeDir);
        _paths = new AgentPaths(Path.Combine(_root, "data"));
        _store = new PairingStore(_paths, new DevKeySecretProtector(_paths.Root), new FakeTimeProvider());
        _service = new FakeServiceControl(exists: serviceExists);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string MakeExe(string name)
    {
        var path = Path.Combine(ExeDir, name);
        File.WriteAllText(path, "exe");
        return path;
    }

    private AgentInstaller Installer() =>
        new(_paths, Path.Combine(_root, "PF", "LIS Agent"), _service,
            (code, _) =>
            {
                _paired.Add(code);
                var state = TestCodes.State(code.ServerUrl);
                _store.Save(state, "khoa-thu");
                return Task.FromResult(new PairingResult(state, code.ServerUrl.StartsWith("http://") ? PairingFlow.HttpWarning : null));
            },
            _output, TimeSpan.FromSeconds(30));

    private SetupConsole Console(FakeElevation elevation, string input, string exePath) =>
        new(new StringReader(input), _output, elevation, Installer(), _store, exePath);

    [Fact]
    public async Task Huy_UAC_thi_bao_can_quyen_quan_tri_va_khong_cai()
    {
        var elevation = new FakeElevation(false, ElevationResult.Cancelled);

        var exit = await Console(elevation, "", MakeExe("LisAgent.exe")).RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(5);
        _output.ToString().ShouldContain("Cần quyền quản trị Windows để cài agent. Nhờ người có tài khoản quản trị bấm chạy lại tệp này.");
        _service.Commands.ShouldBeEmpty();
        _paired.ShouldBeEmpty();
    }

    [Fact]
    public async Task Chua_nang_quyen_thi_chay_lai_nang_quyen_khong_tham_so_va_tra_ma_con()
    {
        var elevation = new FakeElevation(false, ElevationResult.Started, childExit: 0);
        var exe = MakeExe("LisAgent.exe");

        var exit = await Console(elevation, "", exe).RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(0);
        elevation.Relaunches.ShouldBe([(exe, "")]);
        _service.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ten_tep_co_ma_thi_cai_khong_hoi_gi()
    {
        var code = TestCodes.Code();
        var exe = MakeExe($"LisAgent_{code.Encode()} (1).exe");

        var exit = await Console(new FakeElevation(true), "", exe).RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(0);
        _paired.Count.ShouldBe(1);
        _paired[0].Secret.ShouldBe(code.Secret);
        _output.ToString().ShouldNotContain("Dán mã");
        _output.ToString().ShouldContain("Xong. Agent đang chạy và đã kết nối tới https://may-chu.thu. Có thể đóng cửa sổ này.");
        _output.ToString().ShouldContain("Nhấn Enter để đóng");
    }

    [Fact]
    public async Task Ten_tep_khong_co_ma_thi_hoi_dan_ma_sai_hai_lan_dung_lan_ba()
    {
        var code = TestCodes.Code();
        var input = "linh tinh\nLIS1.hong\n  " + code.Encode() + "  \n\n";

        var exit = await Console(new FakeElevation(true), input, MakeExe("LisAgent.exe")).RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(0);
        var text = _output.ToString();
        text.Split("Dán mã ghép nối (lấy ở mục Agent trên web) rồi Enter:").Length.ShouldBe(4);
        text.ShouldContain("Không đọc được mã ghép nối");
        _paired.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sai_ba_lan_thi_thoat_ma_1_khong_cai()
    {
        var exit = await Console(new FakeElevation(true), "a\nb\nc\n", MakeExe("LisAgent.exe")).RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(1);
        _paired.ShouldBeEmpty();
        _service.Commands.ShouldBe(["Exists"]);
    }

    [Fact]
    public async Task Da_cai_thi_hien_menu_chon_2_la_go()
    {
        _store.Save(TestCodes.State(), "khoa-thu");
        _service.Create("x"); // dịch vụ đã có từ trước
        _service.Commands.Clear();

        var exit = await Console(new FakeElevation(true), "2\n\n", MakeExe("LisAgent.exe")).RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(0);
        var text = _output.ToString();
        text.ShouldContain("Agent đã cài trên máy này: phòng khám Phòng khám Thử, agent Agent Thử.");
        text.ShouldContain("1. Ghép nối lại");
        text.ShouldContain("2. Gỡ agent");
        text.ShouldContain("0. Thoát");
        _service.Commands.ShouldContain("Delete");
        File.Exists(_paths.Pairing).ShouldBeFalse();
        text.ShouldContain(_paths.Root);
    }

    [Fact]
    public async Task Da_cai_chon_1_thi_ghep_noi_lai_voi_ma_dan_vao()
    {
        _store.Save(TestCodes.State(), "khoa-thu");
        var code = TestCodes.Code();

        var exit = await Console(new FakeElevation(true), $"1\n{code.Encode()}\n\n", MakeExe("LisAgent.exe")).RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(0);
        _paired.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Da_cai_chon_0_thi_thoat_khong_lam_gi()
    {
        _store.Save(TestCodes.State(), "khoa-thu");

        var exit = await Console(new FakeElevation(true), "0\n", MakeExe("LisAgent.exe")).RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(0);
        _service.Commands.ShouldBe(["Exists"]);
        _output.ToString().ShouldContain("Nhấn Enter để đóng");
    }

    [Fact]
    public async Task Khong_khoi_dong_duoc_tien_trinh_quan_tri_thi_bao_loi_khong_nem()
    {
        var elevation = new FakeElevation(false, failWith: "Windows không khởi động được tiến trình quản trị");

        var exit = await Console(elevation, "\n", MakeExe("LisAgent.exe")).RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(1);
        _output.ToString().ShouldContain("Không xin được quyền quản trị");
    }

    [Fact]
    public async Task Go_khi_thu_muc_cai_dang_bi_khoa_thi_noi_se_xoa_sau_khi_dong_cua_so()
    {
        _store.Save(TestCodes.State(), "khoa-thu");
        var installDir = Path.Combine(_root, "PF", "LIS Agent");
        Directory.CreateDirectory(installDir);
        var exe = Path.Combine(installDir, "LisAgent.exe");
        File.WriteAllText(exe, "x");
        var installer = new AgentInstaller(_paths, installDir, _service, (_, _) => throw new PairingException("x"), _output,
            runningExe: exe, deferDelete: _ => { });

        var exit = await new SetupConsole(new StringReader("2\n\n"), _output, new FakeElevation(true), installer, _store, exe)
            .RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(0);
        _output.ToString().ShouldContain("sẽ được xoá sau khi đóng cửa sổ này");
    }

    [Fact]
    public async Task Url_http_thi_in_canh_bao()
    {
        var code = TestCodes.Code("http://may-chu.thu:5080");

        var exit = await Console(new FakeElevation(true), "", MakeExe($"LisAgent_{code.Encode()}.exe")).RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(0);
        _output.ToString().ShouldContain(PairingFlow.HttpWarning);
    }

    [Fact]
    public async Task Cai_that_bai_thi_in_loi_va_thoat_ma_1()
    {
        var installer = new AgentInstaller(_paths, Path.Combine(_root, "PF", "LIS Agent"), _service,
            (_, _) => throw new PairingException("Mã ghép nối đã hết hạn"), _output, TimeSpan.FromSeconds(30));
        var code = TestCodes.Code();
        var console = new SetupConsole(new StringReader("\n"), _output, new FakeElevation(true), installer, _store, MakeExe($"LisAgent_{code.Encode()}.exe"));

        var exit = await console.RunAsync(new AgentCommand.Interactive(), CancellationToken.None);

        exit.ShouldBe(1);
        _output.ToString().ShouldContain("Mã ghép nối đã hết hạn");
    }

    [Fact]
    public async Task Go_tu_dong_lenh_khong_hien_menu()
    {
        _store.Save(TestCodes.State(), "khoa-thu");

        var exit = await Console(new FakeElevation(true), "", MakeExe("LisAgent.exe")).RunAsync(new AgentCommand.Uninstall(), CancellationToken.None);

        exit.ShouldBe(0);
        _output.ToString().ShouldNotContain("1. Ghép nối lại");
        _service.Commands.ShouldContain("Exists");
    }
}
