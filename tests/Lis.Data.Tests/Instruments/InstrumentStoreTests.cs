using Lis.Data.Catalog;
using Lis.Data.Instruments;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Instruments;

/// <summary>Kiểm tra danh mục máy và map kênh: lưu/cập nhật máy (JSON thô phải hợp lệ), map kênh và lỗi trùng kênh dễ hiểu.</summary>
[Collection(PostgresCollection.Name)]
public class InstrumentStoreTests(PostgresFixture fixture)
{
    private sealed record Env(InstrumentStore Instruments, CatalogStore Catalog, Guid EdtaId);

    private async Task<Env> CreateEnvAsync()
    {
        var db = Db.Create(await fixture.NewDatabaseAsync());
        var catalog = new CatalogStore(db);
        var edtaId = (await catalog.SpecimenTypesAsync()).First(s => s.Code == "EDTA").Id;
        return new Env(new InstrumentStore(db), catalog, edtaId);
    }

    private static Task<Guid> SaveServiceAsync(CatalogStore catalog, string code, string name, Guid specimenTypeId) =>
        catalog.SaveServiceAsync(new ServiceInfo(Guid.Empty, code, name, null, specimenTypeId, "G/L", "so", 2, 0, false, false, true, null, []));

    [Fact]
    public async Task SaveInstrument_InsertThenUpdate_RoundTripsJson()
    {
        var env = await CreateEnvAsync();

        var id = await env.Instruments.SaveAsync(new InstrumentInfo(
            Guid.Empty, "Máy thử XN-1000", "XN-1000", "astm", """{"frame": "lis2a2"}""", """{"port": "COM3", "baud": 9600}""", "XN-01", true));

        var listed = (await env.Instruments.ListAsync()).Single();
        listed.Id.ShouldBe(id);
        listed.Name.ShouldBe("Máy thử XN-1000");
        listed.ProtocolFamily.ShouldBe("astm");
        listed.DriverConfigJson.ShouldNotBeNull();
        listed.DriverConfigJson.ShouldContain("lis2a2");
        listed.ConnectionJson.ShouldNotBeNull();
        listed.ConnectionJson.ShouldContain("COM3");
        listed.WorkstationCode.ShouldBe("XN-01");
        listed.Active.ShouldBeTrue();

        await env.Instruments.SaveAsync(listed with { Name = "Máy thử XN-2000", DriverConfigJson = "  ", Active = false });

        var updated = (await env.Instruments.ListAsync()).Single();
        updated.Id.ShouldBe(id);
        updated.Name.ShouldBe("Máy thử XN-2000");
        updated.DriverConfigJson.ShouldBeNull();
        updated.Active.ShouldBeFalse();

        // Máy ngừng dùng vẫn nằm trong danh mục, nhưng không còn trong danh sách chọn của Danh mục.
        (await env.Catalog.InstrumentsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveInstrument_InvalidJson_Throws()
    {
        var env = await CreateEnvAsync();

        var ex = await Should.ThrowAsync<ArgumentException>(() => env.Instruments.SaveAsync(new InstrumentInfo(
            Guid.Empty, "Máy thử", null, "hl7", "{không phải json", null, null, true)));

        ex.Message.ShouldBe("JSON không hợp lệ");
        (await env.Instruments.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveInstrument_RequiresName()
    {
        var env = await CreateEnvAsync();

        var ex = await Should.ThrowAsync<ArgumentException>(() => env.Instruments.SaveAsync(new InstrumentInfo(
            Guid.Empty, "  ", null, null, null, null, null, true)));

        ex.Message.ShouldBe("Nhập tên máy");
    }

    [Fact]
    public async Task SaveChannel_Duplicate_FriendlyError()
    {
        var env = await CreateEnvAsync();
        var instrumentId = await env.Instruments.SaveAsync(new InstrumentInfo(Guid.Empty, "Máy thử XN-1000", null, "astm", null, null, null, true));
        var wbcId = await SaveServiceAsync(env.Catalog, "WBC", "Bạch cầu", env.EdtaId);
        var rbcId = await SaveServiceAsync(env.Catalog, "RBC", "Hồng cầu", env.EdtaId);

        var channelId = await env.Instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, instrumentId, " WBC# ", wbcId, 1, 0.001m, true, true));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => env.Instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, instrumentId, "WBC#", rbcId, null, null, false, false)));
        ex.Message.ShouldBe("Mã kênh WBC# đã map cho chỉ số Bạch cầu");

        var channels = await env.Instruments.ChannelsAsync(instrumentId);
        var only = channels.Single();
        only.Map.ShouldBe(new ChannelMap(channelId, instrumentId, "WBC#", wbcId, 1, 0.001m, true, true));
        only.ServiceCode.ShouldBe("WBC");
        only.ServiceName.ShouldBe("Bạch cầu");

        // Cập nhật chính dòng đó (cùng id) không bị coi là trùng.
        await env.Instruments.SaveChannelAsync(only.Map with { ServiceId = rbcId, Decimals = null });
        (await env.Instruments.ChannelsAsync(instrumentId)).Single().ServiceCode.ShouldBe("RBC");

        var empty = await Should.ThrowAsync<ArgumentException>(
            () => env.Instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, instrumentId, " ", rbcId, null, null, false, false)));
        empty.Message.ShouldBe("Nhập mã kênh");

        await env.Instruments.DeleteChannelAsync(channelId);
        (await env.Instruments.ChannelsAsync(instrumentId)).ShouldBeEmpty();
    }
}
