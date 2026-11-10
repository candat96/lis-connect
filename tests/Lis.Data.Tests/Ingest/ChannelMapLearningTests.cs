using Dapper;
using Lis.Core.Ingest;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Tests.Results;
using Shouldly;
using Xunit;
using static Lis.Data.Tests.Results.ResultTestData;

namespace Lis.Data.Tests.Ingest;

/// <summary>
/// Gợi ý map kênh từ barcode: máy gửi số test lạ cho nhiều mẫu → suy ra số nào là chỉ số nào
/// theo chỉ định từng mẫu; xác nhận thì lưu map và đưa kết quả cũ vào phiếu.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ChannelMapLearningTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 9, 15, 0, TimeSpan.FromHours(7));

    [Fact]
    public async Task Suggest_FromOrders_ThenConfirm_AppliesOldResults()
    {
        var env = await ResultTestEnv.CreateAsync(fixture);
        var instruments = new InstrumentStore(env.Db);
        var instrumentId = await instruments.SaveAsync(new InstrumentInfo(Guid.Empty, "AU thử", "AU480", "beckman-au", null, null, "AU-01", true));
        var unmatched = new UnmatchedService(env.Db, env.Clock);
        var ingest = new IngestService(env.Db, env.Clock);
        var learning = new ChannelMapLearning(env.Db, instruments, unmatched);

        var glu = await SaveServiceAsync(env.Catalog, "GLU", "Glucose", env.EdtaId, decimals: 1, unit: "mmol/L");
        var ast = await SaveServiceAsync(env.Catalog, "AST", "AST", env.EdtaId, decimals: 0, unit: "U/L");
        var alt = await SaveServiceAsync(env.Catalog, "ALT", "ALT", env.EdtaId, decimals: 0, unit: "U/L");

        // Mẫu 1: GLU, AST, ALT. Mẫu 2: chỉ AST. Mẫu 3: chỉ GLU.
        var (s1, items1) = await CreateSampleAsync(env, MaleAdult(), glu, ast, alt);
        var (s2, _) = await CreateSampleAsync(env, FemaleAdult(), ast);
        var (s3, _) = await CreateSampleAsync(env, MaleAdult("Trần Văn Thử"), glu);

        async Task SendAsync(Guid sampleId, params (string Code, string Value)[] items) =>
            await ingest.IngestAsync(new InstrumentReading(
                instrumentId, await BarcodeAsync(env.Db, sampleId), IsQc: false, At, "AU-MSG",
                items.Select(i => new ReadingItem(i.Code, i.Value, null, null)).ToList()));

        await SendAsync(s1, ("001", "5.6"), ("002", "32"), ("003", "28"));
        await SendAsync(s2, ("002", "25"));
        await SendAsync(s3, ("001", "6.0"));

        var hints = await learning.SuggestAsync(instrumentId);

        hints.Single(h => h.ChannelCode == "001").Candidates.ShouldHaveSingleItem().Code.ShouldBe("GLU");
        hints.Single(h => h.ChannelCode == "002").Candidates.ShouldHaveSingleItem().Code.ShouldBe("AST");
        hints.Single(h => h.ChannelCode == "003").Candidates.ShouldHaveSingleItem().Code.ShouldBe("ALT");
        hints.Single(h => h.ChannelCode == "002").SampleCount.ShouldBe(2);

        var applied = await learning.ConfirmAsync(instrumentId, "002", ast, env.ActorId);

        applied.ShouldBe(2);
        (await StoredAsync(env.Db, items1[ast])).Value.ShouldBe("32");
        (await instruments.ChannelsAsync(instrumentId)).ShouldHaveSingleItem().Map.ChannelCode.ShouldBe("002");

        await using var connection = await env.Db.OpenAsync(TestContext.Current.CancellationToken);
        var open = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM unmatched_result WHERE channel_code = '002' AND resolved_at IS NULL");
        open.ShouldBe(0);

        // Đã map 002 → không còn trong gợi ý.
        (await learning.SuggestAsync(instrumentId)).Select(h => h.ChannelCode).ShouldBe(["001", "003"]);
    }

    [Fact]
    public async Task AutoLearn_DeclaredServices_MapsAfterTwoAgreeingSamples()
    {
        var env = await ResultTestEnv.CreateAsync(fixture);
        var instruments = new InstrumentStore(env.Db);
        var instrumentId = await instruments.SaveAsync(new InstrumentInfo(Guid.Empty, "AU tự dò", "AU480", "beckman-au", null, null, "AU-02", true));
        var ingest = new IngestService(env.Db, env.Clock);
        var learning = new ChannelMapLearning(env.Db, instruments, new UnmatchedService(env.Db, env.Clock));

        var glu = await SaveServiceAsync(env.Catalog, "GLU2", "Glucose", env.EdtaId, decimals: 1, unit: "mmol/L");
        var wbc = await SaveServiceAsync(env.Catalog, "WBC2", "Bạch cầu", env.EdtaId, decimals: 2, unit: "G/L");

        // Mẫu chung barcode: GLU (máy sinh hoá) + WBC (máy khác). Chưa khai → không tự map, và gợi ý còn mơ hồ.
        var (s1, items1) = await CreateSampleAsync(env, MaleAdult(), glu, wbc);
        var (s2, items2) = await CreateSampleAsync(env, FemaleAdult(), glu, wbc);

        async Task SendAsync(Guid sampleId, string value) =>
            await ingest.IngestAsync(new InstrumentReading(
                instrumentId, await BarcodeAsync(env.Db, sampleId), IsQc: false, At, "AU-MSG", [new ReadingItem("007", value, null, null)]));

        await SendAsync(s1, "5.6");
        (await learning.AutoLearnAsync(instrumentId)).ShouldBeEmpty();
        (await learning.SuggestAsync(instrumentId)).Single().Candidates.Count.ShouldBe(2);

        // Khai máy chỉ chạy GLU → ứng viên chỉ còn GLU; mới 1 mẫu nên chưa tự map.
        await instruments.SetDeclaredAsync(instrumentId, glu, declared: true);
        (await instruments.DeclaredServicesAsync(instrumentId)).ShouldHaveSingleItem().ChannelCode.ShouldBeNull();
        (await learning.AutoLearnAsync(instrumentId)).ShouldBeEmpty();

        // Mẫu thứ hai cùng khớp → tự map, cả hai kết quả vào phiếu.
        await SendAsync(s2, "6.1");
        var notes = await learning.AutoLearnAsync(instrumentId);

        notes.ShouldHaveSingleItem().ShouldBe("Tự map kênh 007 → GLU2 Glucose (khớp ở 2 mẫu), đưa 2 kết quả vào phiếu");
        (await StoredAsync(env.Db, items1[glu])).Value.ShouldBe("5.6");
        (await StoredAsync(env.Db, items2[glu])).Value.ShouldBe("6.1");
        (await instruments.DeclaredServicesAsync(instrumentId)).Single().ChannelCode.ShouldBe("007");
    }
}
