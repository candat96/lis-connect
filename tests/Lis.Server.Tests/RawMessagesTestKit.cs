using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Instruments;
using Lis.Data.Orders;
using Lis.Data.RawMessages;
using Lis.Server.Tenancy;
using Npgsql;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>
/// Một phòng khám thử cho các test bản tin thô: máy HL7 "BC-01" gán cho máy trạm XN-01 (kênh 6690-2 → WBC, 718-7 → HGB),
/// một máy của máy trạm khác, một mẫu có chỉ định WBC + HGB, một agent, tài khoản quản trị / kỹ thuật viên / trưởng khoa / tiếp nhận.
/// Mọi dữ liệu là dữ liệu giả.
/// </summary>
internal sealed class RawEnv : IAsyncDisposable
{
    public const string Password = "mat-khau-thu-1";
    public static readonly DateTimeOffset CompletedAt = new(2026, 11, 19, 8, 30, 0, TimeSpan.FromHours(7));

    private readonly bool _ownsHost;

    private RawEnv(ApiHost host, bool ownsHost, ServerFixture fixture)
    {
        Host = host;
        _ownsHost = ownsHost;
        Fixture = fixture;
    }

    public ApiHost Host { get; }

    public ServerFixture Fixture { get; }

    public Clinic Clinic { get; private set; } = null!;

    public TenantServices Tenant => Host.TenantOf(Clinic);

    public Db Db => Tenant.Db;

    public Guid InstrumentId { get; private set; }

    public Guid OtherInstrumentId { get; private set; }

    public Guid WbcItemId { get; private set; }

    public Guid HgbItemId { get; private set; }

    public string Barcode { get; private set; } = "";

    public string AgentKey { get; private set; } = "";

    public Guid AgentId { get; private set; }

    public static async Task<RawEnv> CreateAsync(ServerFixture fixture, string code, ApiHost? sharedHost = null)
    {
        var host = sharedHost ?? await ApiHost.StartAsync(fixture);
        var env = new RawEnv(host, sharedHost is null, fixture);
        var clinic = await host.AddClinicAsync(code, $"Phòng khám Thử {code}");
        env.Clinic = clinic;
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        await host.AddUserAsync(clinic, "truongkhoa", Password, "truong-khoa");
        await host.AddUserAsync(clinic, "tiepnhan", Password, "tiep-nhan");

        var db = env.Db;
        await new FacilityStore(db).SaveAsync(new FacilityInfo("Phòng xét nghiệm Thử", null, null, null, "9", "yyMMdd", 3, "single"));
        var catalog = new CatalogStore(db);
        var edta = (await catalog.SpecimenTypesAsync()).First(s => s.Code == "EDTA").Id;
        var wbc = await catalog.SaveServiceAsync(new ServiceInfo(Guid.Empty, "WBC", "Bạch cầu", null, edta, "G/L", "so", 2, 0, false, false, true, null, []));
        var hgb = await catalog.SaveServiceAsync(new ServiceInfo(Guid.Empty, "HGB", "Huyết sắc tố", null, edta, "g/L", "so", 0, 0, false, false, true, null, []));
        var actor = await new UserStore(db).CreateAsync("ktv.thu", "Kỹ thuật viên Thử", null, Password, ["ky-thuat-vien"]);

        var instruments = new InstrumentStore(db);
        env.InstrumentId = await instruments.SaveAsync(new InstrumentInfo(Guid.Empty, "Máy thử BC-01", "BC-5380", "hl7-mllp", null, null, "XN-01", true));
        env.OtherInstrumentId = await instruments.SaveAsync(new InstrumentInfo(Guid.Empty, "Máy phòng khác", "BC-5380", "hl7-mllp", null, null, "XN-02", true));
        await instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, env.InstrumentId, "6690-2", wbc, null, null, false, false));
        await instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, env.InstrumentId, "718-7", hgb, null, null, false, false));

        var created = await new OrderService(db, TimeProvider.System).CreateAsync(
            new NewOrder(null, new NewPatient("Nguyễn Văn Mẫu", null, 1980, 'M', null, null), [wbc, hgb], null, null, null), actor.Id);
        env.Barcode = created.Samples[0].Barcode;
        await using (var connection = await db.OpenAsync())
        {
            var items = (await connection.QueryAsync<(Guid Id, Guid ServiceId)>(
                "SELECT id, service_id FROM order_item WHERE sample_id = @SampleId", new { created.Samples[0].SampleId })).ToList();
            env.WbcItemId = items.Single(i => i.ServiceId == wbc).Id;
            env.HgbItemId = items.Single(i => i.ServiceId == hgb).Id;
        }

        var (agent, key) = await host.AddAgentWithKeyAsync(clinic, "Agent thử", "XN-01");
        env.AgentKey = key;
        env.AgentId = agent.Id;
        return env;
    }

    public Task<HttpClient> LoginAsync(string username) => Host.LoginAsync(Clinic.Code, username, Password);

    public async Task<Guid> UserIdAsync(string username) =>
        (await Tenant.Users.ListAsync()).Single(u => u.Username == username).Id;

    /// <summary>Bản tin HL7-MLLP: mỗi OBR là một mẫu (barcode ở OBR-3).</summary>
    public static byte[] Hl7(string barcode, string wbc = "9.81", string hgb = "132")
    {
        var text = new StringBuilder();
        text.Append("MSH|^~\\&|Analyzer|Mindray|||20261119083000||ORU^R01|1|P|2.3.1||||||UNICODE\r");
        text.Append("PID|1||BN0001^^^^MR||Mẫu^Thử||19900804000000|Female\r");
        text.Append($"OBR|1||{barcode}|00001^Automated Count^99MRC||20261119082000|20261119083000\r");
        text.Append($"OBX|1|NM|6690-2^WBC^LN||{wbc}|10*9/L|4.00-10.00|N|||F\r");
        text.Append($"OBX|2|NM|718-7^HGB^LN||{hgb}|g/L|110-150|N|||F\r");
        return [0x0B, .. Encoding.UTF8.GetBytes(text.ToString()), 0x1C, 0x0D];
    }

    public object AgentMessage(Guid id, byte[]? bytes = null, Guid? instrumentId = null, long seq = 1, string session = "phien-thu") => new
    {
        id,
        instrumentId = instrumentId ?? InstrumentId,
        seq,
        session,
        startedAt = CompletedAt.AddSeconds(-5),
        completedAt = CompletedAt.AddSeconds(seq),
        bytes = Convert.ToBase64String(bytes ?? Hl7(Barcode)),
        partial = false,
    };

    /// <summary>Gửi lô bản tin bằng khoá agent; trả phản hồi nguyên văn.</summary>
    public async Task<HttpResponseMessage> PostAgentAsync(string? key, params object[] messages)
    {
        var client = Host.AgentClient(key ?? AgentKey);
        return await client.PostAsJsonAsync("/agent/v1/raw-messages", new { messages }, TestContext.Current.CancellationToken);
    }

    public static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>Lưu thẳng một bản tin vào kho (không giải mã) rồi trả id.</summary>
    public async Task<Guid> StoreAsync(byte[]? bytes = null, DateTimeOffset? completedAt = null, long seq = 1)
    {
        var id = Guid.NewGuid();
        var at = completedAt ?? CompletedAt;
        (await Tenant.RawMessages.InsertIfAbsentAsync(
            new RawMessageInsert(id, InstrumentId, AgentId, seq, "phien-thu", at.AddSeconds(-5), at, bytes ?? Hl7(Barcode), false),
            TestContext.Current.CancellationToken)).ShouldNotBeNull();
        return id;
    }

    public async Task<T> Scalar<T>(string sql, object? args = null)
    {
        await using var connection = await Db.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    public async Task Exec(string sql, object? args = null)
    {
        await using var connection = await Db.OpenAsync();
        await connection.ExecuteAsync(sql, args);
    }

    public Task<string?> ValueOfAsync(Guid itemId) =>
        Scalar<string?>("SELECT value FROM result WHERE order_item_id = @itemId", new { itemId });

    public async ValueTask DisposeAsync()
    {
        if (_ownsHost)
        {
            await Host.DisposeAsync();
        }
    }
}
