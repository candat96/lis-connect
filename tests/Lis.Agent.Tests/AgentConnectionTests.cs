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
    public void Server_trong_tep_appsettings_khong_thang_cau_hinh_da_ghep_noi()
    {
        var file = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(file, """{ "Server": { "Url": "http://tep-cu.thu:1", "AgentKey": "KHOA-TEP-CU" } }""");
        var full = new ConfigurationBuilder().AddJsonFile(file).Build();
        full["Server:AgentKey"].ShouldBe("KHOA-TEP-CU"); // tệp có thật — nhưng chỉ cấu hình biến môi trường được đưa vào Resolve

        var env = AgentConnection.EnvironmentConfiguration([KeyValuePair.Create("LISAGENT_DataDirectory", (string?)"x"), KeyValuePair.Create("PATH", (string?)"y")]);
        var connection = AgentConnection.Resolve(env, Store(true), out _);
        connection!.AgentKey.ShouldBe("KHOA-DA-GHEP");
        connection.Source.ShouldBe("cấu hình đã ghép nối");

        var withEnv = AgentConnection.EnvironmentConfiguration([
            KeyValuePair.Create("LISAGENT_Server__Url", (string?)"http://env.thu:1"), KeyValuePair.Create("LISAGENT_Server__AgentKey", (string?)"KHOA-ENV")]);
        AgentConnection.Resolve(withEnv, Store(true), out _)!.Source.ShouldBe("biến môi trường");
    }

    [Fact]
    public void Khong_co_gi_thi_null_va_cau_huong_dan()
    {
        var connection = AgentConnection.Resolve(Config(), Store(false), out var problem);
        connection.ShouldBeNull();
        problem.ShouldBe("Agent chưa được ghép nối — chạy tệp cài tải từ mục Agent trên web");
    }
}
