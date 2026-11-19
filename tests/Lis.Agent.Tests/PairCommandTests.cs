using Lis.Core.Agents;
using Shouldly;
using Xunit;

namespace Lis.Agent.Tests;

public sealed class PairCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-pair-test-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _error = new();
    private int _calls;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
        else if (File.Exists(_root))
        {
            File.Delete(_root);
        }
    }

    private PairCommand Command(IServiceControl? service) =>
        new(new AgentPaths(_root), service, (_, _) =>
        {
            _calls++;
            return Task.FromResult(new PairingResult(TestCodes.State(), null));
        }, new StringWriter(), _error);

    [Fact]
    public async Task Da_cai_dich_vu_thi_tu_choi_va_khong_goi_may_chu()
    {
        var exit = await Command(new FakeServiceControl(exists: true)).RunAsync(TestCodes.Code().Encode(), CancellationToken.None);

        exit.ShouldBe(2);
        _calls.ShouldBe(0);
        _error.ToString().ShouldContain("chọn Ghép nối lại");
    }

    [Fact]
    public async Task Thu_muc_du_lieu_khong_ghi_duoc_thi_khong_goi_may_chu()
    {
        File.WriteAllText(_root, "la-tep-khong-phai-thu-muc");

        var exit = await Command(null).RunAsync(TestCodes.Code().Encode(), CancellationToken.None);

        exit.ShouldBe(1);
        _calls.ShouldBe(0);
        _error.ToString().ShouldContain("Không ghi được thư mục dữ liệu");
    }

    [Fact]
    public async Task Ghi_duoc_thi_ghep_noi_binh_thuong()
    {
        var exit = await Command(new FakeServiceControl()).RunAsync(TestCodes.Code().Encode(), CancellationToken.None);

        exit.ShouldBe(0);
        _calls.ShouldBe(1);
        Directory.GetFiles(_root).ShouldBeEmpty();
    }

    [Fact]
    public async Task Ma_khong_hop_le_thi_ma_2_khong_goi_may_chu()
    {
        (await Command(null).RunAsync("linh tinh", CancellationToken.None)).ShouldBe(2);
        _calls.ShouldBe(0);
    }
}
