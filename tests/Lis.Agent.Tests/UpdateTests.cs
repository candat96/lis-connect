using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lis.Core.Agents;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Agent.Tests;

internal sealed class FakeLauncher : IProcessLauncher
{
    public List<(string Exe, string Args, string WorkDir)> Started { get; } = [];

    public Exception? Fails { get; set; }

    public void StartDetached(string exePath, string arguments, string workingDirectory)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        Started.Add((exePath, arguments, workingDirectory));
    }
}

internal sealed class FakeProbe(UpdateProbeResult result) : IUpdateProbe
{
    public int Calls { get; private set; }

    public string? Exe { get; private set; }

    public TimeSpan Timeout { get; private set; }

    public Exception? Throws { get; set; }

    public Task<UpdateProbeResult> RunAsync(string exePath, TimeSpan timeout, CancellationToken ct)
    {
        Calls++;
        Exe = exePath;
        Timeout = timeout;
        return Throws is null ? Task.FromResult(result) : throw Throws;
    }
}

/// <summary>Dịch vụ giả có chỗ cắm: <see cref="OnStart"/> mô phỏng bản mới vừa bật và ghi <c>ket-noi.json</c>.</summary>
internal sealed class HookedServiceControl : IServiceControl
{
    public List<string> Calls { get; } = [];

    public Action<int>? OnStart { get; set; }

    public Exception? StopFails { get; set; }

    public Action? OnStop { get; set; }

    public int Starts => Calls.Count(c => c == "Start");

    public bool Exists() => true;

    public bool IsRunning() => true;

    public void Create(string exePath) { }

    public string? BinPath() => null;

    public void SetBinPath(string exePath) { }

    public void Start()
    {
        Calls.Add("Start");
        OnStart?.Invoke(Starts);
    }

    public void Stop()
    {
        Calls.Add("Stop");
        OnStop?.Invoke();
        if (StopFails is not null)
        {
            throw StopFails;
        }
    }

    public void Delete() { }

    public void Disable() { }

    public bool WaitRunning(TimeSpan timeout) => true;
}

public sealed class UpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-update-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero));
    private readonly Guid _commandId = Guid.NewGuid();

    public UpdateTests()
    {
        Directory.CreateDirectory(InstallDir);
        Directory.CreateDirectory(DataDir);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string InstallDir => Path.Combine(_root, "install");

    private string DataDir => Path.Combine(_root, "data");

    private string Installed => Path.Combine(InstallDir, "LisAgent.exe");

    private string Old => Path.Combine(InstallDir, "LisAgent.old.exe");

    private string Tmp => Path.Combine(InstallDir, "LisAgent.tmp.exe");

    private string New => Path.Combine(InstallDir, "LisAgent.new.exe");

    private AgentPaths Paths => new(DataDir);

    private void Seed()
    {
        File.WriteAllText(Installed, "cu");
        File.WriteAllText(New, "moi");
    }

    private UpdateApplier Applier(IServiceControl service, Action<string, string>? copy = null, Action<string>? deleteAfterExit = null) =>
        new(Installed, New, Paths, service, _clock, TimeSpan.FromMinutes(2), copyFile: copy, deleteAfterExit: deleteAfterExit);

    private void WriteMarker(string version, DateTimeOffset at) =>
        File.WriteAllText(Paths.ConnectedMarker, JsonSerializer.Serialize(new { version, at }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private UpdateReport ReadReport() =>
        JsonSerializer.Deserialize<UpdateReport>(File.ReadAllText(Paths.UpdateReport), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static JsonElement Args(string version, string sha) => JsonSerializer.SerializeToElement(new { version, sha256 = sha });

    private UpdateHandler Handler(string content, FakeLauncher launcher, bool service = true, string current = "0.2.0", FakeLifetime? lifetime = null, IUpdateProbe? probe = null)
    {
        var body = content;
        return new UpdateHandler(
            async (dest, ct) =>
            {
                await File.WriteAllTextAsync(dest, body, ct);
                return (Sha(body), "0.3.0");
            },
            InstallDir, current, service, launcher, probe ?? new FakeProbe(new UpdateProbeResult(false, 0, "0.3.0\n")), Paths, lifetime ?? new FakeLifetime());
    }

    // ---- Handler (phía dịch vụ đang chạy) ----

    [Fact]
    public async Task Sha_sai_khong_dung_tep_dang_chay()
    {
        File.WriteAllText(Installed, "cu");
        var launcher = new FakeLauncher();
        var handler = Handler("tep-la", launcher);

        var ex = await Should.ThrowAsync<AgentCommandException>(() => handler.RunAsync(_commandId, Args("0.3.0", Sha("tep-that")), CancellationToken.None));
        ex.Message.ShouldBe("Tệp cập nhật sai mã kiểm tra — không cài");
        File.Exists(New).ShouldBeFalse();
        File.ReadAllText(Installed).ShouldBe("cu");
        await handler.AfterResultAsync(CancellationToken.None);
        launcher.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sha_dung_thi_chay_tach_roi_sau_khi_co_ket_qua()
    {
        File.WriteAllText(Installed, "cu");
        var launcher = new FakeLauncher();
        var handler = Handler("moi", launcher);

        var data = await handler.RunAsync(_commandId, Args("0.3.0", Sha("moi").ToUpperInvariant()), CancellationToken.None);
        data!.Value.GetProperty("handedOver").GetBoolean().ShouldBeTrue();
        launcher.Started.ShouldBeEmpty(); // chưa: kết quả chưa gửi
        File.ReadAllText(New).ShouldBe("moi");

        await handler.AfterResultAsync(CancellationToken.None);
        launcher.Started.ShouldBe([(New, $"apply-update {_commandId}", Path.GetTempPath())]);
        File.ReadAllText(Installed).ShouldBe("cu");
    }

    [Fact]
    public async Task Da_dung_phien_ban_thi_khong_tai()
    {
        var launcher = new FakeLauncher();
        var downloaded = false;
        var handler = new UpdateHandler((_, _) => { downloaded = true; return Task.FromResult(("x", (string?)null)); }, InstallDir, "0.3.0", true, launcher, new FakeProbe(new UpdateProbeResult(false, 0, "")), Paths, new FakeLifetime());

        var data = await handler.RunAsync(_commandId, Args("0.3.0", "abc"), CancellationToken.None);
        data!.Value.GetProperty("alreadyCurrent").GetBoolean().ShouldBeTrue();
        downloaded.ShouldBeFalse();
    }

    [Fact]
    public async Task Khong_phai_dich_vu_thi_bao_loi_va_khong_tai()
    {
        var launcher = new FakeLauncher();
        var handler = Handler("moi", launcher, service: false);

        var ex = await Should.ThrowAsync<AgentCommandException>(() => handler.RunAsync(_commandId, Args("0.3.0", Sha("moi")), CancellationToken.None));
        ex.Message.ShouldBe("Chỉ cập nhật được agent cài dạng dịch vụ");
        File.Exists(New).ShouldBeFalse();
    }

    [Fact]
    public async Task Khong_bat_duoc_tien_trinh_thi_xoa_tep_moi()
    {
        File.WriteAllText(Installed, "cu");
        var launcher = new FakeLauncher { Fails = new InvalidOperationException("không bật được") };
        var lifetime = new FakeLifetime();
        var handler = Handler("moi", launcher, lifetime: lifetime);

        await handler.RunAsync(_commandId, Args("0.3.0", Sha("moi")), CancellationToken.None);
        await handler.AfterResultAsync(CancellationToken.None);
        File.Exists(New).ShouldBeFalse();
        File.ReadAllText(Installed).ShouldBe("cu");
        ReadReport().ShouldBe(new UpdateReport(_commandId, false, null, "Không bật được tiến trình cập nhật: không bật được"));
        lifetime.Restarts.ShouldBe(1); // khởi động lại để hello kế tiếp mang báo lỗi lên web
    }

    [Fact]
    public async Task Lenh_thieu_tham_so_bi_tu_choi()
    {
        var handler = Handler("moi", new FakeLauncher());
        await Should.ThrowAsync<AgentCommandException>(() => handler.RunAsync(_commandId, null, CancellationToken.None));
        await Should.ThrowAsync<AgentCommandException>(() => handler.RunAsync(_commandId, JsonSerializer.SerializeToElement(new { version = "1" }), CancellationToken.None));
    }

    [Fact]
    public async Task Dispatcher_truyen_ma_lenh_cho_update()
    {
        File.WriteAllText(Installed, "cu");
        var launcher = new FakeLauncher();
        var dispatcher = new CommandDispatcher([Handler("moi", launcher)]);
        var command = new ChannelEnvelope(ChannelProtocol.Command, _commandId, "update", Args("0.3.0", Sha("moi")));

        var result = await dispatcher.RunAsync(command, _ => Task.CompletedTask, CancellationToken.None);
        result.Ok.ShouldBe(true);
        await dispatcher.AfterResultAsync(command, CancellationToken.None);
        launcher.Started.Single().Args.ShouldBe($"apply-update {_commandId}");
    }

    // ---- Tải về ----

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task Tai_ve_tinh_sha_tren_tep_da_ghi_khong_tin_tieu_de()
    {
        var client = new ServerClient(new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("noi-dung")) };
            response.Headers.Add("X-Agent-Sha256", "tieu-de-gia");
            response.Headers.Add("X-Agent-Version", "0.3.0");
            return response;
        })) { BaseAddress = new Uri("http://may-chu/") });

        var (sha, version) = await client.DownloadInstallerAsync(New, TestContext.Current.CancellationToken);
        sha.ShouldBe(Sha("noi-dung"));
        version.ShouldBe("0.3.0");
        File.ReadAllText(New).ShouldBe("noi-dung");
    }

    [Fact]
    public async Task Tai_ve_loi_server_thi_khong_de_lai_tep()
    {
        var client = new ServerClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        { BaseAddress = new Uri("http://may-chu/") });

        await Should.ThrowAsync<HttpRequestException>(() => client.DownloadInstallerAsync(New, TestContext.Current.CancellationToken));
        File.Exists(New).ShouldBeFalse();
    }

    // ---- Bộ áp cập nhật ----

    [Fact]
    public async Task Ap_cap_nhat_thanh_cong()
    {
        Seed();
        var service = new HookedServiceControl { OnStart = _ => WriteMarker("0.3.0", _clock.GetUtcNow()) };
        var deferred = new List<string>();

        var report = await Applier(service, deleteAfterExit: deferred.Add).ApplyAsync(_commandId, "0.3.0", CancellationToken.None);

        report.ShouldBe(new UpdateReport(_commandId, true, "0.3.0", null));
        File.ReadAllText(Installed).ShouldBe("moi");
        File.Exists(Old).ShouldBeFalse();
        File.Exists(New).ShouldBeTrue(); // đang chạy từ nó — chép chứ không di chuyển
        File.Exists(Paths.UpdateReport).ShouldBeFalse(); // thành công không để lại báo cáo
        service.Calls.ShouldBe(["Stop", "Start"]);
        deferred.ShouldBe([New]);
    }

    [Fact]
    public async Task Ap_cap_nhat_thanh_cong_xoa_bao_cao_cu()
    {
        Seed();
        File.WriteAllText(Paths.UpdateReport, "{}");
        var service = new HookedServiceControl { OnStart = _ => WriteMarker("0.3.0", _clock.GetUtcNow()) };

        await Applier(service).ApplyAsync(_commandId, "0.3.0", CancellationToken.None);

        File.Exists(Paths.UpdateReport).ShouldBeFalse();
    }

    // ---- Chạy thử bản mới ----

    [Fact]
    public async Task Chay_thu_dat_thi_giao_viec_va_chay_voi_timeout_20_giay()
    {
        File.WriteAllText(Installed, "cu");
        var launcher = new FakeLauncher();
        var probe = new FakeProbe(new UpdateProbeResult(false, 0, "0.3.0\r\n"));
        var handler = Handler("moi", launcher, probe: probe);

        await handler.RunAsync(_commandId, Args("0.3.0", Sha("moi")), CancellationToken.None);
        await handler.AfterResultAsync(CancellationToken.None);

        probe.Calls.ShouldBe(1);
        probe.Exe.ShouldBe(New);
        probe.Timeout.ShouldBe(TimeSpan.FromSeconds(20));
        launcher.Started.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, 0, "0.9.9", "báo phiên bản")]
    [InlineData(false, 1, "", "thoát với mã 1")]
    [InlineData(true, -1, "", "không phản hồi trong 20 giây")]
    public async Task Chay_thu_khong_dat_thi_xoa_tep_moi_va_bao_loi_khong_giao_viec(bool timedOut, int exit, string output, string reason)
    {
        File.WriteAllText(Installed, "cu");
        var launcher = new FakeLauncher();
        var handler = Handler("moi", launcher, probe: new FakeProbe(new UpdateProbeResult(timedOut, exit, output)));

        var ex = await Should.ThrowAsync<AgentCommandException>(() => handler.RunAsync(_commandId, Args("0.3.0", Sha("moi")), CancellationToken.None));

        ex.Message.ShouldStartWith("Bản mới không chạy được: ");
        ex.Message.ShouldContain(reason);
        File.Exists(New).ShouldBeFalse();
        File.ReadAllText(Installed).ShouldBe("cu");
        await handler.AfterResultAsync(CancellationToken.None);
        launcher.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task Chay_thu_ne_loi_bat_tien_trinh_cung_la_khong_chay_duoc()
    {
        var handler = Handler("moi", new FakeLauncher(), probe: new FakeProbe(new UpdateProbeResult(false, 0, "")) { Throws = new InvalidOperationException("tệp hỏng") });

        var ex = await Should.ThrowAsync<AgentCommandException>(() => handler.RunAsync(_commandId, Args("0.3.0", Sha("moi")), CancellationToken.None));

        ex.Message.ShouldBe("Bản mới không chạy được: không bật được (tệp hỏng)");
        File.Exists(New).ShouldBeFalse();
    }

    [Fact]
    public async Task Ap_cap_nhat_cho_marker_cua_ban_moi_roi_moi_thanh_cong()
    {
        Seed();
        // Marker cũ (phiên bản cũ / trước lúc bắt đầu) không được tính.
        WriteMarker("0.2.0", _clock.GetUtcNow().AddMinutes(-5));
        var service = new HookedServiceControl();
        var task = Applier(service).ApplyAsync(_commandId, "0.3.0", CancellationToken.None);

        for (var i = 0; i < 20 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        task.IsCompleted.ShouldBeFalse();
        WriteMarker("0.3.0", _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(2));
        (await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Ok.ShouldBeTrue();
        File.ReadAllText(Installed).ShouldBe("moi");
    }

    [Fact]
    public async Task Ban_moi_khong_noi_duoc_quay_ve()
    {
        Seed();
        var service = new HookedServiceControl();
        var task = Applier(service).ApplyAsync(_commandId, "0.3.0", CancellationToken.None);

        for (var i = 0; i < 400 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        var report = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        report.Ok.ShouldBeFalse();
        report.Error.ShouldBe("Bản mới không kết nối được máy chủ trong 2 phút — đã quay về bản cũ");
        File.ReadAllText(Installed).ShouldBe("cu");
        File.Exists(Old).ShouldBeFalse();
        service.Starts.ShouldBe(2);
        service.Calls.ShouldBe(["Stop", "Start", "Stop", "Start"]);
        ReadReport().ShouldBe(report);
    }

    [Fact]
    public async Task Chep_tam_truoc_khi_dung_dich_vu()
    {
        Seed();
        string? atStop = null;
        var service = new HookedServiceControl { OnStop = () => atStop = File.Exists(Tmp) ? File.ReadAllText(Tmp) : null, OnStart = _ => WriteMarker("0.3.0", _clock.GetUtcNow()) };

        (await Applier(service).ApplyAsync(_commandId, "0.3.0", CancellationToken.None)).Ok.ShouldBeTrue();
        atStop.ShouldBe("moi"); // bản tạm đã nằm sẵn (đã kiểm SHA) lúc dừng dịch vụ
        File.Exists(Tmp).ShouldBeFalse();
        File.ReadAllText(Installed).ShouldBe("moi");
    }

    [Fact]
    public async Task Chep_tam_loi_truoc_khi_dung_thi_khong_doi_tep_va_bao_loi()
    {
        Seed();
        var service = new HookedServiceControl();
        service.OnStart = _ => File.Exists(Paths.UpdateReport).ShouldBeTrue();

        var report = await Applier(service, copy: (_, to) => { File.WriteAllText(to, "ná"); throw new IOException("đĩa đầy"); })
            .ApplyAsync(_commandId, "0.3.0", CancellationToken.None);

        report.Ok.ShouldBeFalse();
        report.Error!.ShouldContain("đĩa đầy");
        File.ReadAllText(Installed).ShouldBe("cu");
        File.Exists(Old).ShouldBeFalse();
        File.Exists(Tmp).ShouldBeFalse();
        // Không đổi tệp nào; dịch vụ chỉ được dừng + bật lại để agent đang chạy nối lại và gửi báo lỗi (báo lỗi đã nằm trên đĩa trước khi bật).
        service.Calls.ShouldBe(["Stop", "Start"]);
        ReadReport().ShouldBe(report);
    }

    [Fact]
    public async Task Ban_tam_sai_sha_thi_khong_cai()
    {
        Seed();
        var service = new HookedServiceControl();

        var report = await Applier(service, copy: (from, to) => File.WriteAllText(to, "khac")).ApplyAsync(_commandId, "0.3.0", CancellationToken.None);

        report.Ok.ShouldBeFalse();
        File.ReadAllText(Installed).ShouldBe("cu");
        File.Exists(Tmp).ShouldBeFalse();
    }

    [Fact]
    public async Task Replace_loi_thi_doi_ten_hai_buoc()
    {
        Seed();
        var service = new HookedServiceControl { OnStart = _ => WriteMarker("0.3.0", _clock.GetUtcNow()) };
        var applier = new UpdateApplier(Installed, New, Paths, service, _clock, TimeSpan.FromMinutes(2), replaceFile: (_, _, _) => throw new IOException("replace lỗi"));

        (await applier.ApplyAsync(_commandId, "0.3.0", CancellationToken.None)).Ok.ShouldBeTrue();
        File.ReadAllText(Installed).ShouldBe("moi");
        File.Exists(Old).ShouldBeFalse();
    }

    [Fact]
    public async Task Doi_ten_hai_buoc_do_dang_van_tra_ban_cu()
    {
        Seed();
        var service = new HookedServiceControl();
        // Replace lỗi, rồi bước 2 (tmp → exe) cũng lỗi vì tệp tmp bị mất: exe đã sang .old, phải trả về.
        var applier = new UpdateApplier(
            Installed, New, Paths, service, _clock, TimeSpan.FromMinutes(2),
            replaceFile: (tmp, _, _) => { File.Delete(tmp); throw new IOException("replace lỗi"); });

        var report = await applier.ApplyAsync(_commandId, "0.3.0", CancellationToken.None);
        report.Ok.ShouldBeFalse();
        File.ReadAllText(Installed).ShouldBe("cu");
        File.Exists(Old).ShouldBeFalse();
        service.Starts.ShouldBe(1); // chưa kịp bật bản mới; chỉ bật lại bản cũ
    }

    [Fact]
    public async Task Bao_loi_quay_ve_duoc_ghi_truoc_khi_bat_lai_dich_vu()
    {
        Seed();
        var existsAtStart = new List<bool>();
        var service = new HookedServiceControl { OnStart = _ => existsAtStart.Add(File.Exists(Paths.UpdateReport)) };
        var task = Applier(service).ApplyAsync(_commandId, "0.3.0", CancellationToken.None);
        for (var i = 0; i < 400 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        existsAtStart.ShouldBe([false, true]); // lần bật bản mới chưa có báo lỗi; lần bật lại bản cũ đã có
        ReadReport().Ok.ShouldBeFalse();
    }

    [Fact]
    public void Co_old_ma_mat_exe_thi_tra_ve()
    {
        File.WriteAllText(Old, "cu");

        UpdateApplier.RecoverMissingExe(Installed).ShouldBeTrue();
        File.ReadAllText(Installed).ShouldBe("cu");
        File.Exists(Old).ShouldBeFalse();

        File.WriteAllText(Old, "khac");
        UpdateApplier.RecoverMissingExe(Installed).ShouldBeFalse(); // exe còn → không đụng
        File.ReadAllText(Installed).ShouldBe("cu");
    }

    [Fact]
    public async Task Apply_update_tu_khoi_phuc_exe_mat_truoc_khi_lam()
    {
        File.WriteAllText(Old, "cu");
        File.WriteAllText(New, "moi");
        var service = new HookedServiceControl { OnStart = _ => WriteMarker("0.3.0", _clock.GetUtcNow()) };

        (await Applier(service).ApplyAsync(_commandId, "0.3.0", CancellationToken.None)).Ok.ShouldBeTrue();
        File.ReadAllText(Installed).ShouldBe("moi");
    }

    [Fact]
    public async Task Cai_dat_trai_ve_exe_bi_mat_do_cap_nhat_do_dang()
    {
        var installDir = Path.Combine(_root, "ProgramFiles");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "LisAgent.old.exe"), "cu");
        var source = Path.Combine(_root, "src.exe");
        File.WriteAllText(source, "moi");
        var installer = new AgentInstaller(
            Paths, installDir, new FakeServiceControl(), (_, _) => Task.FromResult(new PairingResult(TestCodes.State(), null)), TextWriter.Null);

        await installer.InstallOrRepairAsync(source, TestCodes.Code(), CancellationToken.None);
        File.Exists(Path.Combine(installDir, "LisAgent.exe")).ShouldBeTrue();
    }

    [Fact]
    public void Bao_loi_som_ghi_bao_cao_roi_khoi_dong_lai_dich_vu()
    {
        var service = new HookedServiceControl();
        service.OnStart = _ => File.Exists(Paths.UpdateReport).ShouldBeTrue();

        var report = UpdateApplier.FailEarly(_commandId, "lý do", Paths, service);

        service.Calls.ShouldBe(["Stop", "Start"]);
        ReadReport().ShouldBe(report);
        report.Error.ShouldBe("lý do");
    }

    [Fact]
    public async Task Thieu_tep_ban_moi_thi_bao_loi_va_khoi_dong_lai_dich_vu()
    {
        File.WriteAllText(Installed, "cu");
        var service = new HookedServiceControl();

        var report = await Applier(service).ApplyAsync(_commandId, "0.3.0", CancellationToken.None);

        report.Ok.ShouldBeFalse();
        service.Calls.ShouldBe(["Stop", "Start"]);
        ReadReport().Ok.ShouldBeFalse();
        File.ReadAllText(Installed).ShouldBe("cu");
    }

    [Fact]
    public async Task Dung_dich_vu_loi_thi_khong_doi_tep_va_van_bat_lai()
    {
        Seed();
        var service = new HookedServiceControl { StopFails = new InvalidOperationException("không dừng được") };

        var report = await Applier(service).ApplyAsync(_commandId, "0.3.0", CancellationToken.None);

        report.Ok.ShouldBeFalse();
        report.Error!.ShouldContain("không dừng được");
        File.ReadAllText(Installed).ShouldBe("cu");
        File.Exists(Old).ShouldBeFalse();
        service.Calls.Last().ShouldBe("Start");
    }

    [Fact]
    public async Task Bat_dich_vu_ban_moi_loi_thi_quay_ve()
    {
        Seed();
        var service = new HookedServiceControl { OnStart = n => { if (n == 1) { throw new InvalidOperationException("không bật được"); } } };

        var report = await Applier(service).ApplyAsync(_commandId, "0.3.0", CancellationToken.None);

        report.Ok.ShouldBeFalse();
        File.ReadAllText(Installed).ShouldBe("cu");
        service.Starts.ShouldBe(2);
    }

    [Fact]
    public async Task Huy_giua_luc_cho_thi_quay_ve()
    {
        Seed();
        var service = new HookedServiceControl();
        using var cts = new CancellationTokenSource();
        var task = Applier(service).ApplyAsync(_commandId, "0.3.0", cts.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        cts.Cancel();

        var report = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        report.Ok.ShouldBeFalse();
        File.ReadAllText(Installed).ShouldBe("cu");
        service.Starts.ShouldBe(2);
    }

    [Fact]
    public async Task Gap_tep_old_con_sot_thi_ghi_de()
    {
        Seed();
        File.WriteAllText(Old, "rac-lan-truoc");
        var service = new HookedServiceControl { OnStart = _ => WriteMarker("0.3.0", _clock.GetUtcNow()) };

        (await Applier(service).ApplyAsync(_commandId, "0.3.0", CancellationToken.None)).Ok.ShouldBeTrue();
        File.ReadAllText(Installed).ShouldBe("moi");
        File.Exists(Old).ShouldBeFalse();
    }

    // ---- Dòng lệnh ----

    [Fact]
    public void Apply_update_chi_nhan_guid()
    {
        var id = Guid.NewGuid();
        AgentCommand.Parse(["apply-update", id.ToString()], false, true).ShouldBe(new AgentCommand.ApplyUpdate(id));
        Should.Throw<ArgumentException>(() => AgentCommand.Parse(["apply-update", "x; del *"], false, true));
        Should.Throw<ArgumentException>(() => AgentCommand.Parse(["apply-update"], false, true));
        Should.Throw<ArgumentException>(() => AgentCommand.Parse(["apply-update", id.ToString(), "them"], false, true));
    }
}
