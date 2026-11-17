using Lis.Data.Local;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Agent.Tests;

public sealed class PairingStoreTests : IDisposable
{
    private const string Key = "KHOA-AGENT-THU-RAT-BI-MAT-0123456789";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-agent-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 17, 3, 4, 5, TimeSpan.Zero));

    public PairingStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private AgentPaths Paths => new(_root);

    private PairingStore Store() => new(Paths, new DevKeySecretProtector(_root), _clock);

    private PairingState State(Guid clinicId, string code = "pkthu") =>
        new("http://may-chu.thu:5080", clinicId, code, "Phòng khám Thử", Guid.NewGuid(), "Agent Thử", _clock.GetUtcNow());

    private void Seed(string dir, string file)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), "x");
    }

    [Fact]
    public void Luu_roi_doc_lai_dung_va_tep_khong_chua_khoa_dang_ro()
    {
        var state = State(Guid.NewGuid());
        var store = Store();
        store.Save(state, Key);

        var loaded = store.Load();
        loaded.ShouldNotBeNull();
        loaded.Value.State.ShouldBe(state);
        loaded.Value.AgentKey.ShouldBe(Key);

        var raw = File.ReadAllText(Paths.Pairing);
        raw.ShouldNotContain(Key);
        raw.ShouldNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Key)));
        Directory.GetFiles(_root, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Chua_ghep_noi_thi_Load_null()
    {
        Store().Load().ShouldBeNull();
    }

    [Fact]
    public void Tep_hong_thi_Load_null()
    {
        File.WriteAllText(Paths.Pairing, "{ khong phai json");
        Store().Load().ShouldBeNull();

        Store().Save(State(Guid.NewGuid()), Key);
        var raw = File.ReadAllText(Paths.Pairing);
        File.WriteAllText(Paths.Pairing, raw.Replace("\"", "\"", StringComparison.Ordinal)[..^5] + "!!!}");
        Store().Load().ShouldBeNull();
    }

    [Fact]
    public void Giai_ma_loi_thi_Load_null()
    {
        Store().Save(State(Guid.NewGuid()), Key);
        // Khoá bảo vệ khác (thư mục khác) → không giải mã được.
        var other = Path.Combine(_root, "khac");
        Directory.CreateDirectory(other);
        new PairingStore(Paths, new DevKeySecretProtector(other), _clock).Load().ShouldBeNull();
    }

    [Fact]
    public void Doi_phong_kham_thi_cho_gui_cu_vao_cach_ly()
    {
        var store = Store();
        store.Save(State(Guid.NewGuid(), "pkcu"), Key);
        Seed(Paths.Pending, "ket-qua-1.json");

        store.Save(State(Guid.NewGuid(), "pkmoi"), "KHOA-MOI");

        (Directory.Exists(Paths.Pending) && Directory.GetFileSystemEntries(Paths.Pending).Length > 0).ShouldBeFalse();
        var moved = Path.Combine(Paths.Quarantine, "20261117-030405-pkcu", "cho-gui", "ket-qua-1.json");
        File.Exists(moved).ShouldBeTrue();
        store.Load()!.Value.State.ClinicCode.ShouldBe("pkmoi");
    }

    [Fact]
    public void Cung_phong_kham_thi_giu_cho_gui()
    {
        var clinic = Guid.NewGuid();
        var store = Store();
        store.Save(State(clinic), Key);
        Seed(Paths.Pending, "ket-qua-1.json");

        store.Save(State(clinic), "KHOA-MOI");

        File.Exists(Path.Combine(Paths.Pending, "ket-qua-1.json")).ShouldBeTrue();
        Directory.Exists(Paths.Quarantine).ShouldBeFalse();
    }

    [Fact]
    public void Luon_xoa_bo_dem_agent_va_danh_sach_may()
    {
        File.WriteAllText(Paths.IdentityCache, "{}");
        File.WriteAllText(Paths.InstrumentsCache, "[]");
        Store().Save(State(Guid.NewGuid()), Key);
        File.Exists(Paths.IdentityCache).ShouldBeFalse();
        File.Exists(Paths.InstrumentsCache).ShouldBeFalse();

        File.WriteAllText(Paths.IdentityCache, "{}");
        File.WriteAllText(Paths.InstrumentsCache, "[]");
        var clinic = Guid.NewGuid();
        Store().Save(State(clinic), Key);
        File.Exists(Paths.IdentityCache).ShouldBeFalse();
        File.Exists(Paths.InstrumentsCache).ShouldBeFalse();
    }

    [Fact]
    public void Delete_xoa_tep_ghep_noi()
    {
        var store = Store();
        store.Save(State(Guid.NewGuid()), Key);
        store.Delete();
        File.Exists(Paths.Pairing).ShouldBeFalse();
        store.Load().ShouldBeNull();
    }
}
