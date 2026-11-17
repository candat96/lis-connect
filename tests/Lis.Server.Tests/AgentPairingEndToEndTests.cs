using System.Net.Http.Json;
using System.Text.Json;
using Lis.Agent;
using Lis.Core.Agents;
using Lis.Data.Instruments;
using Lis.Data.Local;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>Đường đi thật: web tạo agent → mã → agent đổi mã → lưu mã hoá → chạy bằng cấu hình đã ghép nối.</summary>
public sealed class AgentPairingEndToEndTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tao_agent_tren_web_doi_ma_luu_roi_chay_bang_khoa_da_ghep()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync("pke2e", "Phòng khám Thử đầu cuối");
        await host.AddUserAsync(clinic, "admin", "mat-khau-thu-1", "quan-tri");
        var admin = await host.LoginAsync("pke2e", "admin", "mat-khau-thu-1");

        var created = await admin.PostAsJsonAsync("/api/agents", new { name = "Agent Thử E2E" }, Ct);
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var agentId = Guid.Parse(body.GetProperty("id").GetString()!);
        var codeText = body.GetProperty("pairing").GetProperty("code").GetString()!;

        // Máy gán cho agent vừa tạo (mã máy trạm = id agent dạng N) và một máy của máy trạm khác.
        var instruments = host.TenantOf(clinic).Instruments;
        await instruments.SaveAsync(new InstrumentInfo(Guid.Empty, "AU thử E2E", "AU480", "beckman-au", null, null, agentId.ToString("N"), true));
        await instruments.SaveAsync(new InstrumentInfo(Guid.Empty, "Máy khác", "X", "astm-e1394", null, null, "XN-KHAC", true));

        PairingCode.TryFind(codeText, out var code).ShouldBeTrue();
        var root = Path.Combine(Path.GetTempPath(), "lis-agent-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AgentPaths(root);
            var store = new PairingStore(paths, new DevKeySecretProtector(root), TimeProvider.System);
            using var http = host.AgentClient();
            var state = await PairingFlow.PairAsync(code!, new PairingClient(http), store, TimeProvider.System, Ct);

            state.State.ClinicCode.ShouldBe("pke2e");
            state.State.ClinicId.ShouldBe(clinic.Id);
            state.State.AgentId.ShouldBe(agentId);
            state.State.AgentName.ShouldBe("Agent Thử E2E");
            state.Warning.ShouldNotBeNull(); // http:// → cảnh báo
            store.Load()!.Value.State.ShouldBe(state.State);

            var connection = AgentConnection.Resolve(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), store, out var problem);
            problem.ShouldBeNull();
            connection.ShouldNotBeNull();
            connection.Source.ShouldBe("cấu hình đã ghép nối");

            var client = new ServerClient(host.AgentClient(connection.AgentKey));
            var listed = await client.InstrumentsAsync(Ct);
            listed.ShouldHaveSingleItem().Name.ShouldBe("AU thử E2E");
            (await client.MeAsync(Ct)).ClinicCode.ShouldBe("pke2e");

            // Mã đã dùng: đổi lại bị từ chối bằng câu tiếng Việt của server.
            var again = await Should.ThrowAsync<PairingException>(() =>
                PairingFlow.PairAsync(code!, new PairingClient(host.AgentClient()), store, TimeProvider.System, Ct));
            again.Message.ShouldContain("dùng");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
