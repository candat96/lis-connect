using Lis.Data.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Agent.Tests;

public sealed class AgentConnectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-agent-test-" + Guid.NewGuid().ToString("N"));

    public AgentConnectionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private PairingStore Store(bool paired)
    {
        var store = new PairingStore(new AgentPaths(_root), new DevKeySecretProtector(_root), new FakeTimeProvider());
        if (paired)
        {
            store.Save(new PairingState("http://da-ghep.thu:5080", Guid.NewGuid(), "pkthu", "Phòng khám Thử", Guid.NewGuid(), "Agent Thử", DateTimeOffset.UnixEpoch), "KHOA-DA-GHEP");
        }

        return store;
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

    [Fact]
    public void Bien_moi_truong_du_hai_thi_thang_tep()
    {
        var connection = AgentConnection.Resolve(Config(("Server:Url", "http://env.thu:1"), ("Server:AgentKey", "KHOA-ENV")), Store(true), out var problem);
        problem.ShouldBeNull();
        connection.ShouldBe(new AgentConnection("http://env.thu:1", "KHOA-ENV", "biến môi trường"));
    }

    [Theory]
    [InlineData("Server:Url")]
    [InlineData("Server:AgentKey")]
    public void Thieu_mot_bien_thi_dung_tep(string only)
    {
        var connection = AgentConnection.Resolve(Config((only, "gia-tri")), Store(true), out var problem);
        problem.ShouldBeNull();
        connection!.AgentKey.ShouldBe("KHOA-DA-GHEP");
        connection.ServerUrl.ShouldBe("http://da-ghep.thu:5080");
        connection.Source.ShouldBe("cấu hình đã ghép nối");
    }

    [Fact]
    public void Khong_co_gi_thi_null_va_cau_huong_dan()
    {
        var connection = AgentConnection.Resolve(Config(), Store(false), out var problem);
        connection.ShouldBeNull();
        problem.ShouldBe("Agent chưa được ghép nối — chạy tệp cài tải từ mục Agent trên web");
    }
}
