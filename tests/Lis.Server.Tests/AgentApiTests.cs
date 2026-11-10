using System.Net;
using Dapper;
using Lis.Agent;
using Lis.Core.Ingest;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Instruments;
using Lis.Data.Orders;
using Lis.Server;
using Lis.Server.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>Một container PostgreSQL cho cả lớp test: CSDL master + CSDL từng phòng khám tạo trong đó.</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string MasterConnection => WithDatabase("lis_master_test");

    /// <summary>Mẫu chuỗi kết nối phòng khám (<c>{db}</c> = tên CSDL).</summary>
    public string TenantTemplate => WithDatabase("{db}");

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private string WithDatabase(string database) =>
        new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = "DBNAME", Pooling = false }
            .ConnectionString.Replace("DBNAME", database, StringComparison.Ordinal);
}

/// <summary>
/// Đường agent → server → CSDL phòng khám: xác thực khoá, chỉ thấy máy của máy trạm mình, gửi kết
/// quả vào đúng phiếu, gửi lại không ghi trùng, máy hỏi chỉ định, và cách ly giữa hai phòng khám.
/// Dùng chính <see cref="ServerClient"/> / <see cref="HttpReadingSink"/> của agent để gọi.
/// </summary>
public sealed class AgentApiTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.FromHours(7));

    private sealed record ClinicEnv(Clinic Clinic, Db Db, Guid InstrumentId, string Barcode, Guid GluItemId, string AgentKey);

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Master:ConnectionString", fixture.MasterConnection);
            builder.UseSetting("Tenants:ConnectionTemplate", fixture.TenantTemplate);
        });

    /// <summary>Tạo một phòng khám (CSDL riêng) có máy "AU thử" gán cho máy trạm XN-01, một mẫu chỉ định Glucose, và một agent.</summary>
    private async Task<ClinicEnv> NewClinicAsync(string code)
    {
        var master = new MasterStore(fixture.MasterConnection);
        var tenants = new TenantRegistry(fixture.TenantTemplate, TimeProvider.System);
        await master.EnsureSchemaAsync();
        (await AdminCommands.AddClinicAsync(code, $"Phòng khám {code}", $"lis_{code}", master, tenants)).ShouldBe(0);
        var clinic = (await master.FindClinicAsync(code))!;

        var db = Db.Create(tenants.ConnectionStringFor(clinic));
        await new FacilityStore(db).SaveAsync(new FacilityInfo("Phòng xét nghiệm Thử", null, null, null, "9", "yyMMdd", 3, "single"));
        var catalog = new CatalogStore(db);
        var edta = (await catalog.SpecimenTypesAsync()).First(s => s.Code == "EDTA").Id;
        var glu = await catalog.SaveServiceAsync(new ServiceInfo(
            Guid.Empty, "GLU", "Glucose", null, edta, "mmol/L", "so", 1, 0, false, false, true, null, []));
        var actor = await new UserStore(db).CreateAsync("ktv.thu", "Kỹ thuật viên Thử", null, "mat-khau-thu-1", ["ky-thuat-vien"]);

        var instruments = new InstrumentStore(db);
        var instrumentId = await instruments.SaveAsync(new InstrumentInfo(Guid.Empty, "AU thử", "AU480", "beckman-au", null, null, "XN-01", true));
        await instruments.SaveAsync(new InstrumentInfo(Guid.Empty, "Máy phòng khác", "X", "astm-e1394", null, null, "XN-02", true));
        await instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, instrumentId, "001", glu, null, null, false, true));

        var created = await new OrderService(db, TimeProvider.System).CreateAsync(
            new NewOrder(null, new NewPatient("Nguyễn Văn Mẫu", null, 1980, 'M', null, null), [glu], null, null, null), actor.Id);
        var sample = created.Samples[0];
        await using var connection = await db.OpenAsync(TestContext.Current.CancellationToken);
        var itemId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM order_item WHERE sample_id = @SampleId", new { sample.SampleId });

        var (_, key) = await master.AddAgentAsync(clinic.Id, "Agent thử", "XN-01");
        return new ClinicEnv(clinic, db, instrumentId, sample.Barcode, itemId, key);
    }

    private static ServerClient Client(WebApplicationFactory<Program> factory, string agentKey)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add(ServerClient.KeyHeader, agentKey);
        return new ServerClient(http);
    }

    private static async Task<string?> StoredValueAsync(Db db, Guid orderItemId)
    {
        await using var connection = await db.OpenAsync();
        return await connection.ExecuteScalarAsync<string?>("SELECT value FROM result WHERE order_item_id = @orderItemId", new { orderItemId });
    }

    [Fact]
    public async Task Agent_SendsReading_StoredInItsClinic_AndResendIsNotDuplicated()
    {
        var env = await NewClinicAsync("pka");
        await using var factory = Factory();
        var client = Client(factory, env.AgentKey);

        var me = await client.MeAsync(TestContext.Current.CancellationToken);
        me.ShouldBe(new AgentMe("pka", "Phòng khám pka", "Agent thử", "XN-01"));

        // Chỉ thấy máy của máy trạm mình.
        var instruments = await client.InstrumentsAsync(TestContext.Current.CancellationToken);
        instruments.ShouldHaveSingleItem().Name.ShouldBe("AU thử");

        var reading = new InstrumentReading(env.InstrumentId, env.Barcode, false, At, "MSG-1", [new ReadingItem("001", "5.6", null, null)]);
        var sink = new HttpReadingSink(client);
        var outcome = await sink.IngestAsync(reading);
        outcome.Stored.ShouldBe(1);
        (await StoredValueAsync(env.Db, env.GluItemId)).ShouldBe("5.6");

        // Agent gửi lại đúng bản tin (mất mạng lúc chờ phản hồi) → server báo đã nhận, không ghi lần hai.
        var again = await client.SendReadingAsync(reading, TestContext.Current.CancellationToken);
        again.Duplicate.ShouldBeTrue();
        again.Stored.ShouldBe(1);
        await using var connection = await env.Db.OpenAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM result_history")).ShouldBe(1);

        // Máy hỏi chỉ định: mẫu đã có kết quả GLU → không còn kênh nào cần chạy.
        (await client.OrderQueryAsync(env.InstrumentId, env.Barcode, TestContext.Current.CancellationToken)).ShouldBeEmpty();

        await client.SendStatusAsync([new InstrumentStatusReport(env.InstrumentId, "Connected", "Đã kết nối", At, 1, 0)], TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OrderQuery_ReturnsChannelsStillToRun()
    {
        var env = await NewClinicAsync("pkq");
        await using var factory = Factory();

        var channels = await Client(factory, env.AgentKey).OrderQueryAsync(env.InstrumentId, env.Barcode, TestContext.Current.CancellationToken);

        channels.ShouldBe(["001"]);
    }

    [Fact]
    public async Task WrongKey_Unauthorized_AndOtherClinicsInstrument_Forbidden()
    {
        var a = await NewClinicAsync("pkb");
        var b = await NewClinicAsync("pkc");
        await using var factory = Factory();

        var bad = await Should.ThrowAsync<HttpRequestException>(() => Client(factory, a.AgentKey + "x").MeAsync());
        bad.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Agent phòng khám B gửi kết quả cho máy của phòng khám A → bị từ chối, CSDL A không đổi.
        var reading = new InstrumentReading(a.InstrumentId, a.Barcode, false, At, "MSG-X", [new ReadingItem("001", "9.9", null, null)]);
        var forbidden = await Should.ThrowAsync<HttpRequestException>(() => Client(factory, b.AgentKey).SendReadingAsync(reading));
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await StoredValueAsync(a.Db, a.GluItemId)).ShouldBeNull();
    }

    [Fact]
    public async Task Logs_StoredForOwnInstruments_OthersDropped()
    {
        var env = await NewClinicAsync("pkl");
        await using var factory = Factory();
        var client = Client(factory, env.AgentKey);

        await client.SendLogsAsync(
        [
            new AgentLogLine(env.InstrumentId, At, "in", "phien-1", "024142430D", null),
            new AgentLogLine(env.InstrumentId, At.AddSeconds(1), "event", "phien-1", null, "Đã kết nối"),
            new AgentLogLine(null, At.AddSeconds(2), "agent", null, null, "Agent khởi động"),
            // Máy không thuộc agent này và chiều lạ → bỏ.
            new AgentLogLine(Guid.NewGuid(), At, "in", null, "00", null),
            new AgentLogLine(env.InstrumentId, At, "la", null, "00", null),
        ], TestContext.Current.CancellationToken);

        await using var connection = await env.Db.OpenAsync(TestContext.Current.CancellationToken);
        var rows = (await connection.QueryAsync<(string Direction, string? Hex, string? Note)>(
            "SELECT direction, hex, note FROM agent_log ORDER BY at")).ToList();
        rows.ShouldBe([("in", "024142430D", null), ("event", null, "Đã kết nối"), ("agent", null, "Agent khởi động")]);
    }
}
