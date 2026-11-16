using System.Net;
using System.Net.Http.Json;
using Dapper;
using Lis.Agent;
using Lis.Core.Ingest;
using Lis.Core.Results;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Instruments;
using Lis.Data.Orders;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests.Api;

/// <summary>
/// Kết quả máy gửi và kết quả nhập tay phải chọn cùng khoảng tham chiếu theo tuổi, bất kể múi giờ của máy chủ:
/// đồng hồ gốc của server thử ở UTC (như container Docker), phòng khám ở UTC+7 (<c>LocalUtcOffsetMinutes</c> = 420).
/// </summary>
public sealed class ClinicClockTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Password = "mat-khau-thu-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Stored(decimal? RefLow, decimal? RefHigh, string? Flag, string Source);

    [Fact]
    public async Task Ket_qua_may_va_nhap_tay_chon_cung_khoang_tham_chieu_khi_may_chu_o_UTC()
    {
        // 23:00 UTC ngày 15/11 = 06:00 ngày 16/11 giờ Việt Nam: chỉ định tạo lúc này, bệnh nhân sinh 14/11.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 23, 0, 0, TimeSpan.Zero));
        clock.LocalTimeZone.ShouldBe(TimeZoneInfo.Utc);
        await using var host = await ApiHost.StartAsync(fixture, clock: clock);
        var clinic = await host.AddClinicAsync("pkgiomay", "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");
        var tenant = host.TenantOf(clinic);
        await new FacilityStore(tenant.Db).SaveAsync(new FacilityInfo("Phòng khám Thử", null, null, null, "9", "yyMMdd", 4, "per-specimen"));
        var edta = (await tenant.Catalog.SpecimenTypesAsync()).First(s => s.Code == "EDTA").Id;
        var gluId = await tenant.Catalog.SaveServiceAsync(new ServiceInfo(
            Guid.Empty, "GLU", "Glucose Thử", null, edta, "mmol/L", "so", 1, 0, false, false, true, null, []));
        // Tuổi tại ngày chỉ định giờ Việt Nam (16/11) là 2 ngày → khoảng 2+; theo ngày UTC (15/11) mới 1 ngày → khoảng 0–1.
        await tenant.Catalog.SaveRangesAsync(gluId,
        [
            new ReferenceRange(Guid.Empty, gluId, null, 0, 1, null, 1m, 2m, null, null, null),
            new ReferenceRange(Guid.Empty, gluId, null, 2, null, null, 10m, 20m, null, null, null),
        ]);
        var instruments = tenant.Instruments;
        var instrumentId = await instruments.SaveAsync(new InstrumentInfo(Guid.Empty, "AU thử", "AU480", "beckman-au", null, null, "XN-01", true));
        await instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, instrumentId, "001", gluId, null, null, false, true));
        var agentKey = await host.AddAgentKeyAsync(clinic, "Agent thử", "XN-01");
        var actor = (await tenant.Users.ListAsync()).Single(u => u.Username == "admin").Id;

        var orders = new OrderService(tenant.Db, clock);
        var baby = new DateOnly(2026, 11, 14);
        var manual = await orders.CreateAsync(new NewOrder(null, new NewPatient("Bé Mẫu Tay", baby, null, 'M', null, null), [gluId], null, null, null), actor);
        var machine = await orders.CreateAsync(new NewOrder(null, new NewPatient("Bé Mẫu Máy", baby, null, 'M', null, null), [gluId], null, null, null), actor);

        // created_at do CSDL đặt (now()) — ghi thành 06:00 giờ Việt Nam = 23:00 UTC ngày hôm trước cho cả hai chỉ định.
        await using (var connection = await tenant.Db.OpenAsync(Ct))
        {
            await connection.ExecuteAsync("UPDATE lab_order SET created_at = @at", new { at = clock.GetUtcNow() });
        }

        async Task<Guid> ItemOf(Guid sampleId)
        {
            await using var connection = await tenant.Db.OpenAsync(Ct);
            return await connection.ExecuteScalarAsync<Guid>("SELECT id FROM order_item WHERE sample_id = @sampleId", new { sampleId });
        }

        async Task<Stored> StoredOf(Guid itemId)
        {
            await using var connection = await tenant.Db.OpenAsync(Ct);
            return await connection.QuerySingleAsync<Stored>(
                "SELECT ref_low AS RefLow, ref_high AS RefHigh, flag AS Flag, source AS Source FROM result WHERE order_item_id = @itemId", new { itemId });
        }

        // Nhập tay qua web.
        var web = await host.LoginAsync("pkgiomay", "admin", Password);
        var manualItem = await ItemOf(manual.Samples[0].SampleId);
        (await web.PutAsJsonAsync($"/api/results/items/{manualItem}", new { value = "5" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Máy gửi qua /agent/v1/readings, cùng giá trị.
        var reading = new InstrumentReading(instrumentId, machine.Samples[0].Barcode, false, clock.GetUtcNow(), "MSG-GIO", [new ReadingItem("001", "5", null, null)]);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent/v1/readings") { Content = JsonContent.Create(reading) };
        request.Headers.Add(ServerClient.KeyHeader, agentKey);
        (await host.Anonymous.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var byHand = await StoredOf(manualItem);
        var byMachine = await StoredOf(await ItemOf(machine.Samples[0].SampleId));
        byHand.Source.ShouldBe("tay");
        byMachine.Source.ShouldBe("may");
        byHand.RefLow.ShouldBe(10m);
        byHand.RefHigh.ShouldBe(20m);
        byMachine.RefLow.ShouldBe(byHand.RefLow);
        byMachine.RefHigh.ShouldBe(byHand.RefHigh);
        byMachine.Flag.ShouldBe(byHand.Flag);
    }
}
