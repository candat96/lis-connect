using Dapper;
using Lis.Core.Results;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Orders;
using Lis.Data.Results;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Results;

/// <summary>
/// Kiểm tra vòng đời kết quả: nhập tay (làm tròn, chọn khoảng tham chiếu theo người bệnh,
/// tính cờ), sửa bắt buộc lý do và giữ lịch sử, duyệt / hủy duyệt, tự duyệt, bản thay thế
/// khi máy gửi lại cho mẫu đã duyệt, và danh sách mẫu theo trạng thái.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ResultServiceTests(PostgresFixture fixture)
{
    private sealed record Env(ResultService Results, OrderService Orders, CatalogStore Catalog, Guid EdtaId, Guid ActorId, Db Db);

    private async Task<Env> CreateEnvAsync(TimeProvider? clock = null)
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);

        await new FacilityStore(db).SaveAsync(new FacilityInfo(
            "Khoa Xét nghiệm — Bệnh viện Thử", null, null, null, "9", "yyMMdd", 3, "per-specimen"));

        var catalog = new CatalogStore(db);
        var edtaId = (await catalog.SpecimenTypesAsync()).First(s => s.Code == "EDTA").Id;

        var actor = await new UserStore(db).CreateAsync("ktv.thu", "Kỹ thuật viên Thử", null, "mat-khau-thu-1", ["ky-thuat-vien"]);

        clock ??= TimeProvider.System;
        return new Env(new ResultService(db, clock), new OrderService(db, clock), catalog, edtaId, actor.Id, db);
    }

    private static async Task<Guid> SaveServiceAsync(
        CatalogStore catalog, string code, string name, Guid specimenTypeId, int? decimals, string? unit,
        bool autoApprove = false, bool isPanel = false, string valueType = "so")
    {
        return await catalog.SaveServiceAsync(new ServiceInfo(
            Guid.Empty, code, name, GroupName: null, specimenTypeId, unit, valueType, decimals,
            PrintOrder: 0, isPanel, autoApprove, Active: true, HisCode: null, ChildIds: []));
    }

    private static ReferenceRange Range(Guid serviceId, char? sex, decimal low, decimal high, Guid? instrumentId = null,
        decimal? criticalLow = null, decimal? criticalHigh = null) =>
        new(Guid.Empty, serviceId, sex, null, null, instrumentId, low, high, criticalLow, criticalHigh, null);

    /// <summary>Tạo một chỉ định cho người bệnh giả và trả về mẫu (duy nhất, loại EDTA) kèm order_item theo dịch vụ.</summary>
    private static async Task<(Guid SampleId, IReadOnlyDictionary<Guid, Guid> ItemByService)> CreateSampleAsync(
        Env env, NewPatient patient, params Guid[] serviceIds)
    {
        var created = await env.Orders.CreateAsync(new NewOrder(null, patient, serviceIds, null, null, null), env.ActorId);
        var sampleId = created.Samples.Single().SampleId;

        await using var connection = await env.Db.OpenAsync();
        var items = await connection.QueryAsync<(Guid ServiceId, Guid Id)>(
            "SELECT service_id, id FROM order_item WHERE sample_id = @sampleId", new { sampleId });

        return (sampleId, items.ToDictionary(i => i.ServiceId, i => i.Id));
    }

    private static NewPatient FemaleAdult(string name = "Trần Thị Mẫu") => new(name, new DateOnly(1990, 3, 15), null, 'F', null, "0900000011");

    private static NewPatient MaleAdult(string name = "Nguyễn Văn Mẫu") => new(name, new DateOnly(1988, 7, 1), null, 'M', null, "0900000012");

    private sealed record WriteOutcome(bool Succeeded, string? Error);

    private sealed record HistoryRow
    {
        public string Action { get; init; } = "";
        public string? OldValue { get; init; }
        public string? NewValue { get; init; }
        public string? Reason { get; init; }
        public Guid? ActorId { get; init; }
    }

    private static async Task<List<HistoryRow>> HistoryAsync(Db db, Guid orderItemId)
    {
        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<HistoryRow>(
            """
            SELECT h.action, h.old_value, h.new_value, h.reason, h.actor_id
            FROM result_history h
            JOIN result r ON r.id = h.result_id
            WHERE r.order_item_id = @orderItemId
            ORDER BY h.at, h.id
            """,
            new { orderItemId });
        return rows.ToList();
    }

    private sealed record StoredResult
    {
        public Guid Id { get; init; }
        public string? Value { get; init; }
        public string? Unit { get; init; }
        public decimal? RefLow { get; init; }
        public decimal? RefHigh { get; init; }
        public string? Flag { get; init; }
        public string? Source { get; init; }
        public Guid? InstrumentId { get; init; }
        public string? RawRef { get; init; }
        public string Status { get; init; } = "";
        public string? PendingValue { get; init; }
        public Guid? PendingInstrumentId { get; init; }
        public string? PendingRawRef { get; init; }
    }

    private static async Task<StoredResult> StoredAsync(Db db, Guid orderItemId)
    {
        await using var connection = await db.OpenAsync();
        return await connection.QuerySingleAsync<StoredResult>(
            """
            SELECT id, value, unit, ref_low, ref_high, flag, source, instrument_id, raw_ref, status,
                   pending_value, pending_instrument_id, pending_raw_ref
            FROM result WHERE order_item_id = @orderItemId
            """,
            new { orderItemId });
    }

    private static async Task<Guid> InsertInstrumentAsync(Db db, string name)
    {
        await using var connection = await db.OpenAsync();
        return await connection.ExecuteScalarAsync<Guid>("INSERT INTO instrument (name) VALUES (@name) RETURNING id", new { name });
    }

    /// <summary>Giả lập máy gửi lại kết quả cho mẫu đã duyệt (Task 11 sẽ ghi các cột pending_* này).</summary>
    private static async Task SetPendingAsync(Db db, Guid orderItemId, string pendingValue, Guid instrumentId, string rawRef)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            """
            UPDATE result SET pending_value = @pendingValue, pending_instrument_id = @instrumentId, pending_raw_ref = @rawRef
            WHERE order_item_id = @orderItemId
            """,
            new { orderItemId, pendingValue, instrumentId, rawRef });
    }

    [Fact]
    public async Task SetManual_RoundsAndFlagsWithPatientRange()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");
        await env.Catalog.SaveRangesAsync(hgbId, [Range(hgbId, 'M', 130, 170), Range(hgbId, 'F', 120, 150)]);

        var (sampleId, items) = await CreateSampleAsync(env, FemaleAdult(), hgbId);

        await env.Results.SetManualAsync(items[hgbId], "118,6", env.ActorId, reason: null);

        var stored = await StoredAsync(env.Db, items[hgbId]);
        stored.Value.ShouldBe("119");
        stored.Unit.ShouldBe("g/L");
        stored.RefLow.ShouldBe(120m);
        stored.RefHigh.ShouldBe(150m);
        stored.Flag.ShouldBe("Low");
        stored.Source.ShouldBe("tay");
        stored.Status.ShouldBe("cho-duyet");

        var sample = await env.Results.GetAsync(sampleId);
        sample.PatientName.ShouldBe("Trần Thị Mẫu");
        sample.Patient.Sex.ShouldBe('F');
        sample.ApprovedAt.ShouldBeNull();
        var row = sample.Rows.Single();
        row.Value.ShouldBe("119");
        row.Flag.ShouldBe(ResultFlag.Low);
        row.RefText.ShouldBe("120 – 150");
        row.Status.ShouldBe("cho-duyet");
        row.Source.ShouldBe("tay");
        row.Depth.ShouldBe(0);
        row.IsPanelHeader.ShouldBeFalse();

        var history = await HistoryAsync(env.Db, items[hgbId]);
        history.Count.ShouldBe(1);
        history[0].Action.ShouldBe("nhan");
        history[0].OldValue.ShouldBeNull();
        history[0].NewValue.ShouldBe("119");
        history[0].ActorId.ShouldBe(env.ActorId);
    }

    [Fact]
    public async Task SetManual_EditRequiresReason_AndKeepsHistory()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");
        var (_, items) = await CreateSampleAsync(env, FemaleAdult(), hgbId);
        await env.Results.SetManualAsync(items[hgbId], "119", env.ActorId, reason: null);

        var ex = await Should.ThrowAsync<ArgumentException>(() => env.Results.SetManualAsync(items[hgbId], "125", env.ActorId, reason: "  "));
        ex.Message.ShouldBe("Nhập lý do sửa kết quả");
        (await StoredAsync(env.Db, items[hgbId])).Value.ShouldBe("119");

        await env.Results.SetManualAsync(items[hgbId], "125", env.ActorId, reason: "Nhập nhầm");

        (await StoredAsync(env.Db, items[hgbId])).Value.ShouldBe("125");
        var history = await HistoryAsync(env.Db, items[hgbId]);
        history.Select(h => h.Action).ShouldBe(["nhan", "sua"]);
        history[1].OldValue.ShouldBe("119");
        history[1].NewValue.ShouldBe("125");
        history[1].Reason.ShouldBe("Nhập nhầm");
    }

    [Fact]
    public async Task SetManual_EmptyOverwrite_RequiresReasonAndKeepsOld()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");
        var (_, items) = await CreateSampleAsync(env, FemaleAdult(), hgbId);
        await env.Results.SetManualAsync(items[hgbId], "119", env.ActorId, reason: null);

        var ex = await Should.ThrowAsync<ArgumentException>(() => env.Results.SetManualAsync(items[hgbId], "", env.ActorId, reason: null));
        ex.Message.ShouldBe("Nhập lý do sửa kết quả");
        (await StoredAsync(env.Db, items[hgbId])).Value.ShouldBe("119");

        await env.Results.SetManualAsync(items[hgbId], "  ", env.ActorId, reason: "Xóa vì nhập nhầm ô");

        var stored = await StoredAsync(env.Db, items[hgbId]);
        stored.Value.ShouldBeNull();
        stored.Flag.ShouldBe("None");
        var history = await HistoryAsync(env.Db, items[hgbId]);
        history.Select(h => h.Action).ShouldBe(["nhan", "sua"]);
        history[1].OldValue.ShouldBe("119");
        history[1].NewValue.ShouldBeNull();
        history[1].Reason.ShouldBe("Xóa vì nhập nhầm ô");
    }

    [Fact]
    public async Task SetManual_Comparator_KeepsSymbol()
    {
        var env = await CreateEnvAsync();
        var crpId = await SaveServiceAsync(env.Catalog, "CRP", "CRP", env.EdtaId, decimals: 1, unit: "mg/L");
        await env.Catalog.SaveRangesAsync(crpId, [Range(crpId, null, 0, 5)]);
        var (sampleId, items) = await CreateSampleAsync(env, MaleAdult(), crpId);

        await env.Results.SetManualAsync(items[crpId], "> 500", env.ActorId, null);

        // FlagCalculator so thẳng số sau dấu so sánh với ngưỡng: 500 > 5 → High.
        var stored = await StoredAsync(env.Db, items[crpId]);
        stored.Value.ShouldBe(">500.0");
        stored.Flag.ShouldBe("High");
        (await env.Results.GetAsync(sampleId)).Rows.Single().Flag.ShouldBe(ResultFlag.High);
    }

    [Fact]
    public async Task SetManual_NoDecimals_NormalisesComma()
    {
        var env = await CreateEnvAsync();
        var gluId = await SaveServiceAsync(env.Catalog, "GLU", "Glucose", env.EdtaId, decimals: null, unit: "mmol/L");
        await env.Catalog.SaveRangesAsync(gluId, [Range(gluId, null, 3.9m, 6.4m)]);
        var (_, items) = await CreateSampleAsync(env, MaleAdult(), gluId);

        await env.Results.SetManualAsync(items[gluId], "4,8", env.ActorId, null);

        var stored = await StoredAsync(env.Db, items[gluId]);
        stored.Value.ShouldBe("4.8");
        stored.Flag.ShouldBe("None");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12..5")]
    [InlineData("***.*")]
    public async Task SetManual_UnparsableNumber_Throws(string raw)
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");
        var (_, items) = await CreateSampleAsync(env, FemaleAdult(), hgbId);
        await env.Results.SetManualAsync(items[hgbId], "119", env.ActorId, null);

        var ex = await Should.ThrowAsync<ArgumentException>(() => env.Results.SetManualAsync(items[hgbId], $" {raw} ", env.ActorId, "Sửa thử"));

        ex.Message.ShouldBe($"Giá trị không hợp lệ: '{raw}'");
        (await StoredAsync(env.Db, items[hgbId])).Value.ShouldBe("119");
        (await HistoryAsync(env.Db, items[hgbId])).Count.ShouldBe(1);
    }

    [Fact]
    public async Task SetManual_TextService_AcceptsAnyText()
    {
        var env = await CreateEnvAsync();
        var noteId = await SaveServiceAsync(env.Catalog, "GHICHU", "Ghi chú", env.EdtaId, decimals: null, unit: null, valueType: "chu");
        var (_, items) = await CreateSampleAsync(env, FemaleAdult(), noteId);

        await env.Results.SetManualAsync(items[noteId], "Mẫu hơi tán huyết", env.ActorId, null);

        (await StoredAsync(env.Db, items[noteId])).Value.ShouldBe("Mẫu hơi tán huyết");
    }

    [Fact]
    public async Task SetManual_Concurrent_SecondWriterNeedsReason()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");
        var (_, items) = await CreateSampleAsync(env, FemaleAdult(), hgbId);

        async Task<WriteOutcome> TryWriteAsync(string value)
        {
            try
            {
                await env.Results.SetManualAsync(items[hgbId], value, env.ActorId, reason: null);
                return new WriteOutcome(true, null);
            }
            catch (ArgumentException ex)
            {
                return new WriteOutcome(false, ex.Message);
            }
        }

        var outcomes = await Task.WhenAll(TryWriteAsync("119"), TryWriteAsync("125"));

        outcomes.Count(o => o.Succeeded).ShouldBe(1);
        outcomes.Single(o => !o.Succeeded).Error.ShouldBe("Nhập lý do sửa kết quả");

        var history = await HistoryAsync(env.Db, items[hgbId]);
        history.Count.ShouldBe(1);
        history[0].Action.ShouldBe("nhan");
        (await StoredAsync(env.Db, items[hgbId])).Value.ShouldBe(history[0].NewValue);
    }

    [Fact]
    public async Task SetManual_ApprovedSample_Throws()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");
        var (sampleId, items) = await CreateSampleAsync(env, FemaleAdult(), hgbId);
        await env.Results.SetManualAsync(items[hgbId], "130", env.ActorId, reason: null);
        await env.Results.ApproveAsync(sampleId, env.ActorId);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => env.Results.SetManualAsync(items[hgbId], "131", env.ActorId, reason: "Sửa thử"));

        ex.Message.ShouldBe("Mẫu đã duyệt — hủy duyệt trước khi sửa");
        (await StoredAsync(env.Db, items[hgbId])).Value.ShouldBe("130");
    }

    [Fact]
    public async Task Approve_NoValues_Throws()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");
        var (sampleId, _) = await CreateSampleAsync(env, FemaleAdult(), hgbId);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => env.Results.ApproveAsync(sampleId, env.ActorId));

        ex.Message.ShouldBe("Chưa có kết quả để duyệt");
        (await env.Results.GetAsync(sampleId)).ApprovedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Unapprove_RequiresReason()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");
        var wbcId = await SaveServiceAsync(env.Catalog, "WBC", "Bạch cầu", env.EdtaId, decimals: 2, unit: "G/L");
        var (sampleId, items) = await CreateSampleAsync(env, FemaleAdult(), hgbId, wbcId);
        await env.Results.SetManualAsync(items[hgbId], "130", env.ActorId, reason: null);

        await env.Results.ApproveAsync(sampleId, env.ActorId);

        var approved = await env.Results.GetAsync(sampleId);
        approved.ApprovedAt.ShouldNotBeNull();
        approved.ApprovedBy.ShouldBe("Kỹ thuật viên Thử");
        approved.Rows.Single(r => r.ServiceId == hgbId).Status.ShouldBe("da-duyet");
        approved.Rows.Single(r => r.ServiceId == wbcId).Status.ShouldBeNull();
        (await HistoryAsync(env.Db, items[hgbId])).Select(h => h.Action).ShouldBe(["nhan", "duyet"]);

        // Duyệt lần nữa không đổi gì (idempotent).
        await env.Results.ApproveAsync(sampleId, env.ActorId);
        (await HistoryAsync(env.Db, items[hgbId])).Count.ShouldBe(2);

        var ex = await Should.ThrowAsync<ArgumentException>(() => env.Results.UnapproveAsync(sampleId, env.ActorId, " "));
        ex.Message.ShouldBe("Nhập lý do hủy duyệt");
        (await env.Results.GetAsync(sampleId)).ApprovedAt.ShouldNotBeNull();

        await env.Results.UnapproveAsync(sampleId, env.ActorId, "Nhầm mẫu");

        var unapproved = await env.Results.GetAsync(sampleId);
        unapproved.ApprovedAt.ShouldBeNull();
        unapproved.ApprovedBy.ShouldBeNull();
        unapproved.Rows.Single(r => r.ServiceId == hgbId).Status.ShouldBe("cho-duyet");
        var history = await HistoryAsync(env.Db, items[hgbId]);
        history.Select(h => h.Action).ShouldBe(["nhan", "duyet", "huy-duyet"]);
        history[2].Reason.ShouldBe("Nhầm mẫu");
        history[2].ActorId.ShouldBe(env.ActorId);
    }

    [Fact]
    public async Task AutoApprove_OnlyWhenAllNormalAndAllowed()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L", autoApprove: true);
        var wbcId = await SaveServiceAsync(env.Catalog, "WBC", "Bạch cầu", env.EdtaId, decimals: 2, unit: "G/L", autoApprove: true);
        var pltId = await SaveServiceAsync(env.Catalog, "PLT", "Tiểu cầu", env.EdtaId, decimals: 0, unit: "G/L", autoApprove: false);
        await env.Catalog.SaveRangesAsync(hgbId, [Range(hgbId, null, 120, 170)]);
        await env.Catalog.SaveRangesAsync(wbcId, [Range(wbcId, null, 4, 10)]);

        // Mọi chỉ số đều cho tự duyệt, đủ giá trị, cờ None → tự duyệt, người duyệt "Tự duyệt".
        var (allNormal, normalItems) = await CreateSampleAsync(env, MaleAdult(), hgbId, wbcId);
        await env.Results.SetManualAsync(normalItems[hgbId], "140", env.ActorId, null);
        await env.Results.SetManualAsync(normalItems[wbcId], "6.5", env.ActorId, null);

        (await env.Results.TryAutoApproveAsync(allNormal)).ShouldBeTrue();
        var approved = await env.Results.GetAsync(allNormal);
        approved.ApprovedAt.ShouldNotBeNull();
        approved.ApprovedBy.ShouldBe("Tự duyệt");
        approved.Rows.ShouldAllBe(r => r.Status == "da-duyet");
        var history = await HistoryAsync(env.Db, normalItems[hgbId]);
        history.Select(h => h.Action).ShouldBe(["nhan", "duyet"]);
        history[1].ActorId.ShouldBeNull();

        // Một chỉ số có cờ High → không tự duyệt.
        var (withHigh, highItems) = await CreateSampleAsync(env, MaleAdult("Lê Văn Mẫu"), hgbId, wbcId);
        await env.Results.SetManualAsync(highItems[hgbId], "140", env.ActorId, null);
        await env.Results.SetManualAsync(highItems[wbcId], "12.1", env.ActorId, null);
        (await env.Results.TryAutoApproveAsync(withHigh)).ShouldBeFalse();
        (await env.Results.GetAsync(withHigh)).ApprovedAt.ShouldBeNull();

        // Thiếu giá trị → không tự duyệt.
        var (missing, missingItems) = await CreateSampleAsync(env, MaleAdult("Phạm Văn Mẫu"), hgbId, wbcId);
        await env.Results.SetManualAsync(missingItems[hgbId], "140", env.ActorId, null);
        (await env.Results.TryAutoApproveAsync(missing)).ShouldBeFalse();

        // Một dịch vụ không cho tự duyệt → không tự duyệt.
        var (notAllowed, notAllowedItems) = await CreateSampleAsync(env, MaleAdult("Hoàng Văn Mẫu"), hgbId, pltId);
        await env.Results.SetManualAsync(notAllowedItems[hgbId], "140", env.ActorId, null);
        await env.Results.SetManualAsync(notAllowedItems[pltId], "250", env.ActorId, null);
        (await env.Results.TryAutoApproveAsync(notAllowed)).ShouldBeFalse();

        // Máy gắn cờ (instrument_flag) → không tự duyệt dù cờ tính ra là None.
        var (flaggedByInstrument, flaggedItems) = await CreateSampleAsync(env, MaleAdult("Vũ Văn Mẫu"), hgbId, wbcId);
        await env.Results.SetManualAsync(flaggedItems[hgbId], "140", env.ActorId, null);
        await env.Results.SetManualAsync(flaggedItems[wbcId], "6.5", env.ActorId, null);
        await using (var connection = await env.Db.OpenAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("UPDATE result SET instrument_flag = 'A' WHERE order_item_id = @id", new { id = flaggedItems[wbcId] });
        }

        (await env.Results.TryAutoApproveAsync(flaggedByInstrument)).ShouldBeFalse();
        (await env.Results.GetAsync(flaggedByInstrument)).ApprovedAt.ShouldBeNull();
    }

    [Fact]
    public async Task TryAutoApprove_WithPending_ReturnsFalse()
    {
        var env = await CreateEnvAsync();
        var instrumentId = await InsertInstrumentAsync(env.Db, "Máy thử XN");
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L", autoApprove: true);
        await env.Catalog.SaveRangesAsync(hgbId, [Range(hgbId, null, 120, 170)]);
        var (sampleId, items) = await CreateSampleAsync(env, MaleAdult(), hgbId);
        await env.Results.SetManualAsync(items[hgbId], "140", env.ActorId, null);
        await SetPendingAsync(env.Db, items[hgbId], "150", instrumentId, "MSG-004");

        (await env.Results.TryAutoApproveAsync(sampleId)).ShouldBeFalse();

        (await env.Results.GetAsync(sampleId)).ApprovedAt.ShouldBeNull();
        (await StoredAsync(env.Db, items[hgbId])).Status.ShouldBe("cho-duyet");
    }

    [Fact]
    public async Task AcceptPending_ReplacesValue_AndUnapproves()
    {
        var env = await CreateEnvAsync();
        var instrumentId = await InsertInstrumentAsync(env.Db, "Máy thử XN");
        var wbcId = await SaveServiceAsync(env.Catalog, "WBC", "Bạch cầu", env.EdtaId, decimals: 1, unit: "G/L");
        // Khoảng chung 4–8; khoảng riêng của máy 4–7 — chấp nhận bản thay thế phải chọn khoảng theo máy.
        await env.Catalog.SaveRangesAsync(wbcId, [Range(wbcId, null, 4, 8), Range(wbcId, null, 4, 7, instrumentId)]);

        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");

        var (sampleId, items) = await CreateSampleAsync(env, MaleAdult(), wbcId, hgbId);
        await env.Results.SetManualAsync(items[wbcId], "6.0", env.ActorId, null);
        await env.Results.SetManualAsync(items[hgbId], "140", env.ActorId, null);
        await env.Results.ApproveAsync(sampleId, env.ActorId);
        await SetPendingAsync(env.Db, items[wbcId], "7.25", instrumentId, "MSG-001");

        var before = await env.Results.GetAsync(sampleId);
        var wbcRow = before.Rows.Single(r => r.ServiceId == wbcId);
        wbcRow.PendingValue.ShouldBe("7.25");
        before.ApprovedAt.ShouldNotBeNull();

        await env.Results.AcceptPendingAsync(wbcRow.ResultId!.Value, env.ActorId);

        var stored = await StoredAsync(env.Db, items[wbcId]);
        stored.Value.ShouldBe("7.3");
        stored.Flag.ShouldBe("High");
        stored.RefHigh.ShouldBe(7m);
        stored.Source.ShouldBe("may");
        stored.InstrumentId.ShouldBe(instrumentId);
        stored.RawRef.ShouldBe("MSG-001");
        stored.Status.ShouldBe("cho-duyet");
        stored.PendingValue.ShouldBeNull();
        stored.PendingInstrumentId.ShouldBeNull();
        stored.PendingRawRef.ShouldBeNull();

        var after = await env.Results.GetAsync(sampleId);
        after.ApprovedAt.ShouldBeNull();
        after.ApprovedBy.ShouldBeNull();

        var history = await HistoryAsync(env.Db, items[wbcId]);
        var replaced = history.Single(h => h.Action == "thay-the");
        replaced.OldValue.ShouldBe("6.0");
        replaced.NewValue.ShouldBe("7.25");
        replaced.ActorId.ShouldBe(env.ActorId);

        // Kết quả bị thay thế đã về cho-duyet trước khi hủy duyệt mẫu nên không có dòng huy-duyet riêng;
        // chỉ số còn lại (đã duyệt) nhận đúng một dòng huy-duyet với lý do cố định.
        history.Count(h => h.Action == "huy-duyet").ShouldBe(0);
        var otherHistory = await HistoryAsync(env.Db, items[hgbId]);
        otherHistory.Select(h => h.Action).ShouldBe(["nhan", "duyet", "huy-duyet"]);
        otherHistory[2].Reason.ShouldBe("Chấp nhận bản thay thế của máy");
        otherHistory[2].ActorId.ShouldBe(env.ActorId);
        (await StoredAsync(env.Db, items[hgbId])).Status.ShouldBe("cho-duyet");
    }

    [Fact]
    public async Task AcceptPending_ErrorSymbol_StoresNoValue()
    {
        var env = await CreateEnvAsync();
        var instrumentId = await InsertInstrumentAsync(env.Db, "Máy thử XN");
        var wbcId = await SaveServiceAsync(env.Catalog, "WBC", "Bạch cầu", env.EdtaId, decimals: 1, unit: "G/L");
        var (sampleId, items) = await CreateSampleAsync(env, MaleAdult(), wbcId);
        await env.Results.SetManualAsync(items[wbcId], "6.0", env.ActorId, null);
        await env.Results.ApproveAsync(sampleId, env.ActorId);
        await SetPendingAsync(env.Db, items[wbcId], "***.*", instrumentId, "MSG-003");
        var resultId = (await StoredAsync(env.Db, items[wbcId])).Id;

        await env.Results.AcceptPendingAsync(resultId, env.ActorId);

        var stored = await StoredAsync(env.Db, items[wbcId]);
        stored.Value.ShouldBeNull();
        stored.Flag.ShouldBe("None");
        stored.RawRef.ShouldBe("MSG-003");
        var replaced = (await HistoryAsync(env.Db, items[wbcId])).Single(h => h.Action == "thay-the");
        replaced.OldValue.ShouldBe("6.0");
        replaced.NewValue.ShouldBe("***.*");

        var today = DateOnly.FromDateTime(DateTime.Now);
        var listed = (await env.Results.ListAsync(today.AddDays(-1), today.AddDays(1), null, null)).Single();
        listed.WithValue.ShouldBe(0);
        listed.Total.ShouldBe(1);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => env.Results.ApproveAsync(sampleId, env.ActorId));
        ex.Message.ShouldBe("Chưa có kết quả để duyệt");
    }

    [Fact]
    public async Task RejectPending_ClearsAndLogs()
    {
        var env = await CreateEnvAsync();
        var instrumentId = await InsertInstrumentAsync(env.Db, "Máy thử XN");
        var wbcId = await SaveServiceAsync(env.Catalog, "WBC", "Bạch cầu", env.EdtaId, decimals: 1, unit: "G/L");
        var (sampleId, items) = await CreateSampleAsync(env, MaleAdult(), wbcId);
        await env.Results.SetManualAsync(items[wbcId], "6.0", env.ActorId, null);
        await env.Results.ApproveAsync(sampleId, env.ActorId);
        await SetPendingAsync(env.Db, items[wbcId], "7.25", instrumentId, "MSG-002");
        var resultId = (await StoredAsync(env.Db, items[wbcId])).Id;

        await env.Results.RejectPendingAsync(resultId, env.ActorId);

        var stored = await StoredAsync(env.Db, items[wbcId]);
        stored.Value.ShouldBe("6.0");
        stored.Status.ShouldBe("da-duyet");
        stored.PendingValue.ShouldBeNull();
        stored.PendingInstrumentId.ShouldBeNull();
        stored.PendingRawRef.ShouldBeNull();
        (await env.Results.GetAsync(sampleId)).ApprovedAt.ShouldNotBeNull();

        var history = await HistoryAsync(env.Db, items[wbcId]);
        var rejected = history.Single(h => h.Action == "bo-thay-the");
        rejected.OldValue.ShouldBe("7.25");
        rejected.NewValue.ShouldBe("6.0");
    }

    [Fact]
    public async Task Get_PanelHeaderThenChildren_InPrintOrder()
    {
        var env = await CreateEnvAsync();
        var gluId = await env.Catalog.SaveServiceAsync(new ServiceInfo(
            Guid.Empty, "GLU", "Glucose", null, env.EdtaId, "mmol/L", "so", 1, PrintOrder: 1, false, false, true, null, []));
        var panelId = await env.Catalog.SaveServiceAsync(new ServiceInfo(
            Guid.Empty, "TPTTBM", "Tổng phân tích tế bào máu", null, env.EdtaId, null, "so", null, PrintOrder: 2, true, false, true, null, []));
        var wbcId = await SaveServiceAsync(env.Catalog, "WBC", "Bạch cầu", env.EdtaId, decimals: 2, unit: "G/L");
        var rbcId = await SaveServiceAsync(env.Catalog, "RBC", "Hồng cầu", env.EdtaId, decimals: 2, unit: "T/L");
        await env.Catalog.SetChildrenAsync(panelId, [wbcId, rbcId]);

        var (sampleId, items) = await CreateSampleAsync(env, MaleAdult(), panelId, gluId);
        await env.Results.SetManualAsync(items[rbcId], "4.5", env.ActorId, null);

        var sample = await env.Results.GetAsync(sampleId);

        sample.Rows.Select(r => (r.ServiceCode, r.Depth, r.IsPanelHeader)).ShouldBe(
        [
            ("GLU", 0, false),
            ("TPTTBM", 0, true),
            ("WBC", 1, false),
            ("RBC", 1, false),
        ]);
        sample.Rows.Single(r => r.ServiceCode == "TPTTBM").Value.ShouldBeNull();
        sample.Rows.Single(r => r.ServiceCode == "WBC").Status.ShouldBeNull();
        sample.Rows.Single(r => r.ServiceCode == "RBC").Value.ShouldBe("4.50");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => env.Results.SetManualAsync(items[panelId], "1", env.ActorId, null));
        ex.Message.ShouldBe("Dịch vụ cha (panel) không có giá trị kết quả");
    }

    [Fact]
    public async Task List_StatusFilters_AndCounts()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");
        var wbcId = await SaveServiceAsync(env.Catalog, "WBC", "Bạch cầu", env.EdtaId, decimals: 2, unit: "G/L");
        await env.Catalog.SaveRangesAsync(wbcId, [Range(wbcId, null, 4, 10)]);

        var (awaitingResults, _) = await CreateSampleAsync(env, MaleAdult("Nguyễn Văn Mẫu"), hgbId, wbcId);

        var (awaitingApproval, approvalItems) = await CreateSampleAsync(env, FemaleAdult("Trần Thị Mẫu"), hgbId, wbcId);
        await env.Results.SetManualAsync(approvalItems[hgbId], "130", env.ActorId, null);
        await env.Results.SetManualAsync(approvalItems[wbcId], "12.5", env.ActorId, null);

        var (approved, approvedItems) = await CreateSampleAsync(env, MaleAdult("Lê Văn Mẫu"), hgbId);
        await env.Results.SetManualAsync(approvedItems[hgbId], "140", env.ActorId, null);
        await env.Results.ApproveAsync(approved, env.ActorId);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-1);
        var to = today.AddDays(1);

        var all = await env.Results.ListAsync(from, to, status: null, query: null);
        all.Select(s => s.SampleId).ShouldBe([approved, awaitingApproval, awaitingResults]);

        var first = all.Single(s => s.SampleId == awaitingResults);
        first.Total.ShouldBe(2);
        first.WithValue.ShouldBe(0);
        first.Approved.ShouldBeFalse();
        first.HasAbnormal.ShouldBeFalse();
        first.SpecimenName.ShouldBe("Máu EDTA (huyết học)");

        var second = all.Single(s => s.SampleId == awaitingApproval);
        second.WithValue.ShouldBe(2);
        second.HasAbnormal.ShouldBeTrue();
        second.HasPending.ShouldBeFalse();

        (await env.Results.ListAsync(from, to, SampleStatus.AwaitingResults, null)).Select(s => s.SampleId).ShouldBe([awaitingResults]);
        (await env.Results.ListAsync(from, to, SampleStatus.AwaitingApproval, null)).Select(s => s.SampleId).ShouldBe([awaitingApproval]);
        (await env.Results.ListAsync(from, to, SampleStatus.Approved, null)).Select(s => s.SampleId).ShouldBe([approved]);

        (await env.Results.ListAsync(from, to, null, "tran thi mau")).Select(s => s.SampleId).ShouldBe([awaitingApproval]);
        var barcode = all.Single(s => s.SampleId == approved).Barcode;
        (await env.Results.ListAsync(from, to, null, $" {barcode} ")).Select(s => s.SampleId).ShouldBe([approved]);
        (await env.Results.ListAsync(from, to, null, barcode[1..])).ShouldBeEmpty();

        (await env.Results.ListAsync(today.AddDays(-10), today.AddDays(-5), null, null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_DateFilter_UsesLocalDay()
    {
        // 23:30 ngày 04/11 giờ Việt Nam = 16:30 UTC cùng ngày; lọc "04/11" theo giờ máy phải thấy,
        // lọc "05/11" không thấy — dù ngày UTC của 23:30+07 vẫn là 04/11, còn 01:30 ngày 05/11
        // giờ Việt Nam (18:30 UTC 04/11) phải thuộc ngày 05/11.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 23, 30, 0, TimeSpan.FromHours(7)));
        clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
        var env = await CreateEnvAsync(clock);
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");

        var (lateEvening, _) = await CreateSampleAsync(env, MaleAdult("Nguyễn Văn Mẫu"), hgbId);
        var (earlyMorning, _) = await CreateSampleAsync(env, MaleAdult("Lê Văn Mẫu"), hgbId);

        // OrderService ghi lab_order.created_at bằng now() của CSDL — đặt lại trực tiếp để kiểm múi giờ.
        await using (var connection = await env.Db.OpenAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                "UPDATE lab_order SET created_at = @at WHERE id = (SELECT order_id FROM sample WHERE id = @sampleId)",
                new { at = new DateTimeOffset(2026, 11, 4, 23, 30, 0, TimeSpan.FromHours(7)).ToUniversalTime(), sampleId = lateEvening });
            await connection.ExecuteAsync(
                "UPDATE lab_order SET created_at = @at WHERE id = (SELECT order_id FROM sample WHERE id = @sampleId)",
                new { at = new DateTimeOffset(2026, 11, 5, 1, 30, 0, TimeSpan.FromHours(7)).ToUniversalTime(), sampleId = earlyMorning });
        }

        var day4 = new DateOnly(2026, 11, 4);
        var day5 = new DateOnly(2026, 11, 5);

        (await env.Results.ListAsync(day4, day4, null, null)).Select(s => s.SampleId).ShouldBe([lateEvening]);
        (await env.Results.ListAsync(day5, day5, null, null)).Select(s => s.SampleId).ShouldBe([earlyMorning]);
        (await env.Results.ListAsync(day4, day5, null, null)).Select(s => s.SampleId).ShouldBe([earlyMorning, lateEvening]);
    }

    [Fact]
    public async Task List_Query_EscapesLikeWildcards()
    {
        var env = await CreateEnvAsync();
        var hgbId = await SaveServiceAsync(env.Catalog, "HGB", "Huyết sắc tố", env.EdtaId, decimals: 0, unit: "g/L");
        await CreateSampleAsync(env, MaleAdult("Nguyễn Văn Mẫu"), hgbId);
        await CreateSampleAsync(env, new NewPatient("Trần 100% Mẫu", null, 1990, 'M', null, "0900000013"), hgbId);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-1);
        var to = today.AddDays(1);

        (await env.Results.ListAsync(from, to, null, "%")).Select(s => s.PatientName).ShouldBe(["Trần 100% Mẫu"]);
        (await env.Results.ListAsync(from, to, null, "100%")).Select(s => s.PatientName).ShouldBe(["Trần 100% Mẫu"]);
        (await env.Results.ListAsync(from, to, null, "_")).ShouldBeEmpty();
        (await env.Results.ListAsync(from, to, null, "van mau")).Select(s => s.PatientName).ShouldBe(["Nguyễn Văn Mẫu"]);
    }
}
