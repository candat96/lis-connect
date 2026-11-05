using Dapper;
using Lis.Core.Ingest;
using Lis.Core.Results;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Results;
using Lis.Data.Tests.Results;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Lis.Data.Tests.Results.ResultTestData;

namespace Lis.Data.Tests.Ingest;

/// <summary>
/// Kiểm tra nhận kết quả từ máy: ghép barcode chính xác (bỏ ký tự thừa, không khớp đuôi),
/// map kênh + hệ số + định tính, các lý do vào Chưa ghép, QC, bản thay thế khi mẫu đã duyệt,
/// và gán tay / bỏ qua từ màn Chưa ghép.
/// </summary>
[Collection(PostgresCollection.Name)]
public class IngestServiceTests(PostgresFixture fixture)
{
    private sealed record IngestEnv(ResultTestEnv Base, IngestService Ingest, UnmatchedService Unmatched, InstrumentStore Instruments, Guid InstrumentId)
    {
        public Db Db => Base.Db;
    }

    private async Task<IngestEnv> CreateEnvAsync(TimeProvider? clock = null)
    {
        var baseEnv = await ResultTestEnv.CreateAsync(fixture, clock);
        var instruments = new InstrumentStore(baseEnv.Db);
        var instrumentId = await instruments.SaveAsync(new InstrumentInfo(
            Guid.Empty, "Máy thử XN-1000", "XN-1000", "astm", null, null, "XN-01", true));
        return new IngestEnv(baseEnv, new IngestService(baseEnv.Db, baseEnv.Clock), new UnmatchedService(baseEnv.Db, baseEnv.Clock), instruments, instrumentId);
    }

    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 4, 9, 15, 0, TimeSpan.FromHours(7));

    private static InstrumentReading Reading(Guid instrumentId, string barcode, params ReadingItem[] items) =>
        new(instrumentId, barcode, IsQc: false, ReceivedAt, "MSG-001", items);

    private static ReadingItem Item(string channel, string value, string? unit = null, string? flag = null) => new(channel, value, unit, flag);

    private static Task<Guid> MapAsync(IngestEnv env, string channel, Guid serviceId, int? decimals = null, decimal? factor = null, bool keepComparator = false) =>
        env.Instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, env.InstrumentId, channel, serviceId, decimals, factor, keepComparator, false));

    private sealed record UnmatchedRow
    {
        public string Barcode { get; init; } = "";
        public string ChannelCode { get; init; } = "";
        public string Value { get; init; } = "";
        public string Reason { get; init; } = "";
        public DateTimeOffset? ResolvedAt { get; init; }
        public Guid? ResolvedItemId { get; init; }
        public string? DismissReason { get; init; }
    }

    private static async Task<List<UnmatchedRow>> UnmatchedRowsAsync(Db db)
    {
        await using var connection = await db.OpenAsync();
        return (await connection.QueryAsync<UnmatchedRow>(
            "SELECT barcode, channel_code, value, reason, resolved_at, resolved_item_id, dismiss_reason FROM unmatched_result ORDER BY channel_code")).ToList();
    }

    [Fact]
    public async Task Ingest_MapsChannel_RoundsAndFlags()
    {
        var env = await CreateEnvAsync();
        var wbcId = await SaveServiceAsync(env.Base.Catalog, "WBC", "Bạch cầu", env.Base.EdtaId, decimals: 2, unit: "G/L");
        await env.Base.Catalog.SaveRangesAsync(wbcId, [Range(wbcId, null, 4, 10)]);
        await MapAsync(env, "WBC#", wbcId, decimals: 1);
        var (sampleId, items) = await CreateSampleAsync(env.Base, MaleAdult(), wbcId);
        var barcode = await BarcodeAsync(env.Db, sampleId);

        var outcome = await env.Ingest.IngestAsync(Reading(env.InstrumentId, barcode, Item("WBC#", "12.46", "10^9/L", "H")));

        outcome.ShouldBe(new IngestOutcome(Stored: 1, PendingReplacement: 0, Unmatched: 0, Qc: 0));

        var stored = await StoredAsync(env.Db, items[wbcId]);
        stored.Value.ShouldBe("12.5");
        stored.Unit.ShouldBe("10^9/L");
        stored.Flag.ShouldBe("High");
        stored.RefLow.ShouldBe(4m);
        stored.RefHigh.ShouldBe(10m);
        stored.Source.ShouldBe("may");
        stored.InstrumentId.ShouldBe(env.InstrumentId);
        stored.RawRef.ShouldBe("MSG-001");
        stored.InstrumentFlag.ShouldBe("H");
        stored.Status.ShouldBe("cho-duyet");

        var history = await HistoryAsync(env.Db, items[wbcId]);
        history.Count.ShouldBe(1);
        history[0].Action.ShouldBe("nhan");
        history[0].NewValue.ShouldBe("12.5");
        history[0].Reason.ShouldBe("Máy gửi");
        history[0].ActorId.ShouldBeNull();

        (await env.Base.Results.GetAsync(sampleId)).Rows.Single().Flag.ShouldBe(ResultFlag.High);
    }

    [Fact]
    public async Task Ingest_TrimsBarcode_ButNeverSuffixMatches()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Base.Catalog, "HGB", "Huyết sắc tố", env.Base.EdtaId, decimals: 0, unit: "g/L");
        await MapAsync(env, "HGB", hgbId);
        var (_, items) = await CreateSampleAsync(env.Base, FemaleAdult(), hgbId);

        // Barcode máy gửi: " 9261103001\r" (khoảng trắng + CR) → vẫn khớp đúng mẫu.
        await using (var connection = await env.Db.OpenAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("UPDATE sample SET barcode = '9261103001'");
        }

        var matched = await env.Ingest.IngestAsync(Reading(env.InstrumentId, " 9261103001\r", Item("HGB", "132")));
        matched.Stored.ShouldBe(1);
        matched.Unmatched.ShouldBe(0);
        (await StoredAsync(env.Db, items[hgbId])).Value.ShouldBe("132");

        // Chỉ trùng đuôi ("261103001") → không khớp, vào Chưa ghép với lý do không có barcode.
        var suffix = await env.Ingest.IngestAsync(Reading(env.InstrumentId, "261103001", Item("HGB", "140")));
        suffix.Stored.ShouldBe(0);
        suffix.Unmatched.ShouldBe(1);
        (await StoredAsync(env.Db, items[hgbId])).Value.ShouldBe("132");

        var unmatched = (await UnmatchedRowsAsync(env.Db)).Single();
        unmatched.Barcode.ShouldBe("261103001");
        unmatched.Reason.ShouldBe("khong-co-barcode");
        unmatched.Value.ShouldBe("140");
    }

    [Fact]
    public async Task Ingest_UnknownChannel_Unmatched()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Base.Catalog, "HGB", "Huyết sắc tố", env.Base.EdtaId, decimals: 0, unit: "g/L");
        await MapAsync(env, "HGB", hgbId);
        var (sampleId, items) = await CreateSampleAsync(env.Base, FemaleAdult(), hgbId);
        var barcode = await BarcodeAsync(env.Db, sampleId);

        var outcome = await env.Ingest.IngestAsync(Reading(env.InstrumentId, barcode, Item("HGB", "130"), Item("MCV", "88.1", "fL")));

        outcome.ShouldBe(new IngestOutcome(1, 0, 1, 0));
        (await StoredAsync(env.Db, items[hgbId])).Value.ShouldBe("130");
        var unmatched = (await UnmatchedRowsAsync(env.Db)).Single();
        unmatched.ChannelCode.ShouldBe("MCV");
        unmatched.Reason.ShouldBe("kenh-chua-map");
        unmatched.Barcode.ShouldBe(barcode);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var listed = (await env.Unmatched.ListOpenAsync(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4))).Single();
        listed.InstrumentName.ShouldBe("Máy thử XN-1000");
        listed.ChannelCode.ShouldBe("MCV");
        listed.Value.ShouldBe("88.1");
        listed.Unit.ShouldBe("fL");
        listed.Reason.ShouldBe("kenh-chua-map");
        listed.ServiceCode.ShouldBeNull();
        (await env.Unmatched.ListOpenAsync(today.AddDays(-30), today.AddDays(-20))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Ingest_NotOrdered_Unmatched()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Base.Catalog, "HGB", "Huyết sắc tố", env.Base.EdtaId, decimals: 0, unit: "g/L");
        var pltId = await SaveServiceAsync(env.Base.Catalog, "PLT", "Tiểu cầu", env.Base.EdtaId, decimals: 0, unit: "G/L");
        await MapAsync(env, "HGB", hgbId);
        await MapAsync(env, "PLT", pltId);
        var (sampleId, items) = await CreateSampleAsync(env.Base, FemaleAdult(), hgbId);
        var barcode = await BarcodeAsync(env.Db, sampleId);

        var outcome = await env.Ingest.IngestAsync(Reading(env.InstrumentId, barcode, Item("HGB", "130"), Item("PLT", "250")));

        outcome.ShouldBe(new IngestOutcome(1, 0, 1, 0));
        (await StoredAsync(env.Db, items[hgbId])).Value.ShouldBe("130");
        var unmatched = (await UnmatchedRowsAsync(env.Db)).Single();
        unmatched.ChannelCode.ShouldBe("PLT");
        unmatched.Reason.ShouldBe("khong-co-chi-dinh");

        var listed = (await env.Unmatched.ListOpenAsync(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4))).Single();
        listed.ServiceCode.ShouldBe("PLT");
        listed.ServiceName.ShouldBe("Tiểu cầu");
    }

    [Fact]
    public async Task Ingest_ApprovedResult_BecomesPendingReplacement()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Base.Catalog, "HGB", "Huyết sắc tố", env.Base.EdtaId, decimals: 0, unit: "g/L");
        await MapAsync(env, "HGB", hgbId);
        var (sampleId, items) = await CreateSampleAsync(env.Base, FemaleAdult(), hgbId);
        var barcode = await BarcodeAsync(env.Db, sampleId);
        await env.Base.Results.SetManualAsync(items[hgbId], "130", env.Base.ActorId, null);
        await env.Base.Results.ApproveAsync(sampleId, env.Base.ActorId);

        var outcome = await env.Ingest.IngestAsync(Reading(env.InstrumentId, barcode, Item("HGB", "135")));

        outcome.ShouldBe(new IngestOutcome(0, 1, 0, 0));
        var stored = await StoredAsync(env.Db, items[hgbId]);
        stored.Value.ShouldBe("130");
        stored.Status.ShouldBe("da-duyet");
        stored.Source.ShouldBe("tay");
        stored.PendingValue.ShouldBe("135");
        stored.PendingInstrumentId.ShouldBe(env.InstrumentId);
        stored.PendingRawRef.ShouldBe("MSG-001");

        var sample = await env.Base.Results.GetAsync(sampleId);
        sample.ApprovedAt.ShouldNotBeNull();
        sample.Rows.Single().PendingValue.ShouldBe("135");

        var history = await HistoryAsync(env.Db, items[hgbId]);
        history.Select(h => h.Action).ShouldBe(["nhan", "duyet", "thay-the"]);
        history[2].OldValue.ShouldBe("130");
        history[2].NewValue.ShouldBe("135");
        history[2].Reason.ShouldBe("Máy gửi lại kết quả cho mẫu đã duyệt");
        history[2].ActorId.ShouldBeNull();
    }

    [Fact]
    public async Task Ingest_UnapprovedExisting_OverwritesWithHistory()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Base.Catalog, "HGB", "Huyết sắc tố", env.Base.EdtaId, decimals: 0, unit: "g/L");
        await MapAsync(env, "HGB", hgbId);
        var (sampleId, items) = await CreateSampleAsync(env.Base, FemaleAdult(), hgbId);
        var barcode = await BarcodeAsync(env.Db, sampleId);
        await env.Base.Results.SetManualAsync(items[hgbId], "130", env.Base.ActorId, null);

        var outcome = await env.Ingest.IngestAsync(Reading(env.InstrumentId, barcode, Item("HGB", "135")));

        outcome.ShouldBe(new IngestOutcome(1, 0, 0, 0));
        var stored = await StoredAsync(env.Db, items[hgbId]);
        stored.Value.ShouldBe("135");
        stored.Source.ShouldBe("may");
        stored.PendingValue.ShouldBeNull();
        stored.Status.ShouldBe("cho-duyet");

        var history = await HistoryAsync(env.Db, items[hgbId]);
        history.Select(h => h.Action).ShouldBe(["nhan", "nhan"]);
        history[1].OldValue.ShouldBe("130");
        history[1].NewValue.ShouldBe("135");
        history[1].Reason.ShouldBe("Máy gửi");
        history[1].ActorId.ShouldBeNull();
    }

    [Fact]
    public async Task Ingest_Qc_GoesToQcTable_NotResults()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Base.Catalog, "HGB", "Huyết sắc tố", env.Base.EdtaId, decimals: 0, unit: "g/L");
        await MapAsync(env, "HGB", hgbId);
        var (sampleId, items) = await CreateSampleAsync(env.Base, FemaleAdult(), hgbId);
        var barcode = await BarcodeAsync(env.Db, sampleId);

        var outcome = await env.Ingest.IngestAsync(
            new InstrumentReading(env.InstrumentId, barcode, IsQc: true, ReceivedAt, "QC-001", [Item("HGB", "131", "g/L"), Item("WBC", "6.1")]));

        outcome.ShouldBe(new IngestOutcome(0, 0, 0, 2));
        (await StoredOrNullAsync(env.Db, items[hgbId])).ShouldBeNull();
        (await UnmatchedRowsAsync(env.Db)).ShouldBeEmpty();

        await using var connection = await env.Db.OpenAsync(TestContext.Current.CancellationToken);
        var qc = (await connection.QueryAsync<(string ChannelCode, string Value, string? Unit, string? RawRef, Guid InstrumentId)>(
            "SELECT channel_code, value, unit, raw_ref, instrument_id FROM qc_result ORDER BY channel_code")).ToList();
        qc.ShouldBe([("HGB", "131", "g/L", "QC-001", env.InstrumentId), ("WBC", "6.1", null, "QC-001", env.InstrumentId)]);
    }

    [Fact]
    public async Task Ingest_FactorAndQualitative()
    {
        var env = await CreateEnvAsync();
        var hctId = await SaveServiceAsync(env.Base.Catalog, "HCT", "Hematocrit", env.Base.EdtaId, decimals: 1, unit: "%");
        var hbsId = await SaveServiceAsync(env.Base.Catalog, "HBSAG", "HBsAg", env.Base.EdtaId, decimals: null, unit: null, valueType: "dinh-tinh");
        await env.Base.Catalog.SaveQualitativeRulesAsync(hbsId,
        [
            new QualitativeRule(null, hbsId, "neg", "Âm tính", null),
            new QualitativeRule(null, hbsId, "pos", "Dương tính", ResultFlag.Abnormal),
        ]);
        await MapAsync(env, "HCT", hctId, factor: 100);
        await MapAsync(env, "HBS", hbsId);
        var (sampleId, items) = await CreateSampleAsync(env.Base, MaleAdult(), hctId, hbsId);
        var barcode = await BarcodeAsync(env.Db, sampleId);

        var outcome = await env.Ingest.IngestAsync(Reading(env.InstrumentId, barcode, Item("HCT", "0.405"), Item("HBS", "neg")));

        outcome.Stored.ShouldBe(2);
        (await StoredAsync(env.Db, items[hctId])).Value.ShouldBe("40.5");
        var hbs = await StoredAsync(env.Db, items[hbsId]);
        hbs.Value.ShouldBe("Âm tính");
        hbs.Flag.ShouldBe("None");
    }

    [Fact]
    public async Task Ingest_Comparator_KeptOnlyWhenMapSays()
    {
        var env = await CreateEnvAsync();
        var crpId = await SaveServiceAsync(env.Base.Catalog, "CRP", "CRP", env.Base.EdtaId, decimals: 1, unit: "mg/L");
        var ferId = await SaveServiceAsync(env.Base.Catalog, "FER", "Ferritin", env.Base.EdtaId, decimals: 0, unit: "ng/mL");
        await MapAsync(env, "CRP", crpId, keepComparator: true);
        await MapAsync(env, "FER", ferId, keepComparator: false);
        var (sampleId, items) = await CreateSampleAsync(env.Base, MaleAdult(), crpId, ferId);
        var barcode = await BarcodeAsync(env.Db, sampleId);

        await env.Ingest.IngestAsync(Reading(env.InstrumentId, barcode, Item("CRP", ">500"), Item("FER", ">2000")));

        (await StoredAsync(env.Db, items[crpId])).Value.ShouldBe(">500.0");
        (await StoredAsync(env.Db, items[ferId])).Value.ShouldBe("2000");
    }

    [Fact]
    public async Task Ingest_AllNormal_AutoApproves()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Base.Catalog, "HGB", "Huyết sắc tố", env.Base.EdtaId, decimals: 0, unit: "g/L", autoApprove: true);
        await env.Base.Catalog.SaveRangesAsync(hgbId, [Range(hgbId, null, 120, 170)]);
        await MapAsync(env, "HGB", hgbId);
        var (sampleId, items) = await CreateSampleAsync(env.Base, MaleAdult(), hgbId);
        var barcode = await BarcodeAsync(env.Db, sampleId);

        await env.Ingest.IngestAsync(Reading(env.InstrumentId, barcode, Item("HGB", "140")));

        var sample = await env.Base.Results.GetAsync(sampleId);
        sample.ApprovedAt.ShouldNotBeNull();
        sample.ApprovedBy.ShouldBe("Tự duyệt");
        (await StoredAsync(env.Db, items[hgbId])).Status.ShouldBe("da-duyet");
    }

    [Fact]
    public async Task Ingest_InstrumentFlag_BlocksAutoApprove()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Base.Catalog, "HGB", "Huyết sắc tố", env.Base.EdtaId, decimals: 0, unit: "g/L", autoApprove: true);
        await env.Base.Catalog.SaveRangesAsync(hgbId, [Range(hgbId, null, 120, 170)]);
        await MapAsync(env, "HGB", hgbId);
        var (sampleId, items) = await CreateSampleAsync(env.Base, MaleAdult(), hgbId);
        var barcode = await BarcodeAsync(env.Db, sampleId);

        await env.Ingest.IngestAsync(Reading(env.InstrumentId, barcode, Item("HGB", "140", flag: "*")));

        var stored = await StoredAsync(env.Db, items[hgbId]);
        stored.Value.ShouldBe("140");
        stored.Flag.ShouldBe("None");
        stored.InstrumentFlag.ShouldBe("*");
        stored.Status.ShouldBe("cho-duyet");
        (await env.Base.Results.GetAsync(sampleId)).ApprovedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Assign_FromUnmatched_WritesResult()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Base.Catalog, "HGB", "Huyết sắc tố", env.Base.EdtaId, decimals: 0, unit: "g/L");
        await env.Base.Catalog.SaveRangesAsync(hgbId, [Range(hgbId, 'F', 120, 150)]);
        var (sampleId, items) = await CreateSampleAsync(env.Base, FemaleAdult(), hgbId);
        var barcode = await BarcodeAsync(env.Db, sampleId);

        // Kênh "HB" chưa map → vào Chưa ghép; gán tay dùng số lẻ của chỉ số, không có hệ số.
        await env.Ingest.IngestAsync(Reading(env.InstrumentId, barcode, Item("HB", "118.6", "g/L")));
        var open = (await env.Unmatched.ListOpenAsync(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4))).Single();

        var leafItems = await env.Unmatched.SampleItemsAsync($" {barcode} ");
        leafItems.Single().ShouldBe(new SampleLeafItem(items[hgbId], "HGB", "Huyết sắc tố", null));
        var missing = await Should.ThrowAsync<InvalidOperationException>(() => env.Unmatched.SampleItemsAsync("0000000000"));
        missing.Message.ShouldBe("Không tìm thấy mẫu có barcode 0000000000");

        await env.Unmatched.AssignAsync(open.Id, items[hgbId], env.Base.ActorId);

        var stored = await StoredAsync(env.Db, items[hgbId]);
        stored.Value.ShouldBe("119");
        stored.Flag.ShouldBe("Low");
        stored.Source.ShouldBe("may");
        stored.InstrumentId.ShouldBe(env.InstrumentId);
        stored.RawRef.ShouldBe("MSG-001");
        var history = (await HistoryAsync(env.Db, items[hgbId])).Single();
        history.Action.ShouldBe("nhan");
        history.Reason.ShouldBe("Gán tay từ Chưa ghép");
        history.ActorId.ShouldBe(env.Base.ActorId);

        var row = (await UnmatchedRowsAsync(env.Db)).Single();
        row.ResolvedAt.ShouldNotBeNull();
        row.ResolvedItemId.ShouldBe(items[hgbId]);
        (await env.Unmatched.ListOpenAsync(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4))).ShouldBeEmpty();

        var again = await Should.ThrowAsync<InvalidOperationException>(() => env.Unmatched.AssignAsync(open.Id, items[hgbId], env.Base.ActorId));
        again.Message.ShouldBe("Dòng này đã được xử lý");
    }

    [Fact]
    public async Task Dismiss_RequiresReason()
    {
        var env = await CreateEnvAsync();
        await env.Ingest.IngestAsync(Reading(env.InstrumentId, "", Item("HGB", "130")));
        var open = (await env.Unmatched.ListOpenAsync(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4))).Single();
        open.Reason.ShouldBe("khong-co-barcode");

        var ex = await Should.ThrowAsync<ArgumentException>(() => env.Unmatched.DismissAsync(open.Id, env.Base.ActorId, "  "));
        ex.Message.ShouldBe("Nhập lý do bỏ qua");
        (await env.Unmatched.ListOpenAsync(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4))).Count.ShouldBe(1);

        await env.Unmatched.DismissAsync(open.Id, env.Base.ActorId, "Mẫu QC gửi nhầm");

        var row = (await UnmatchedRowsAsync(env.Db)).Single();
        row.ResolvedAt.ShouldNotBeNull();
        row.ResolvedItemId.ShouldBeNull();
        row.DismissReason.ShouldBe("Mẫu QC gửi nhầm");
        (await env.Unmatched.ListOpenAsync(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4))).ShouldBeEmpty();
    }

    [Fact]
    public async Task ListOpen_DateFilter_UsesLocalDay()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 23, 30, 0, TimeSpan.FromHours(7)));
        clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
        var env = await CreateEnvAsync(clock);

        // 23:30 ngày 04/11 giờ Việt Nam thuộc ngày 04/11; 01:30 ngày 05/11 thuộc ngày 05/11.
        await env.Ingest.IngestAsync(new InstrumentReading(env.InstrumentId, "", false, new DateTimeOffset(2026, 11, 4, 23, 30, 0, TimeSpan.FromHours(7)), null, [Item("A", "1")]));
        await env.Ingest.IngestAsync(new InstrumentReading(env.InstrumentId, "", false, new DateTimeOffset(2026, 11, 5, 1, 30, 0, TimeSpan.FromHours(7)), null, [Item("B", "2")]));

        var day4 = new DateOnly(2026, 11, 4);
        var day5 = new DateOnly(2026, 11, 5);
        (await env.Unmatched.ListOpenAsync(day4, day4)).Select(u => u.ChannelCode).ShouldBe(["A"]);
        (await env.Unmatched.ListOpenAsync(day5, day5)).Select(u => u.ChannelCode).ShouldBe(["B"]);
        (await env.Unmatched.ListOpenAsync(day4, day5)).Select(u => u.ChannelCode).ShouldBe(["B", "A"]);
    }
}
