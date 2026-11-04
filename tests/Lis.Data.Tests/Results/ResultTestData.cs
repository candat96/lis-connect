using Dapper;
using Lis.Core.Results;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Orders;
using Lis.Data.Results;

namespace Lis.Data.Tests.Results;

/// <summary>
/// Môi trường kiểm thử cho kết quả: CSDL riêng đã migrate, cơ sở giả, một kỹ thuật viên giả
/// và các service cần để tạo chỉ định/ghi kết quả. Dùng chung cho kiểm thử
/// <see cref="ResultService"/> (Task 10) và nhận kết quả máy / Chưa ghép (Task 11).
/// </summary>
internal sealed record ResultTestEnv(
    ResultService Results, OrderService Orders, CatalogStore Catalog, Guid EdtaId, Guid ActorId, Db Db, TimeProvider Clock)
{
    public static async Task<ResultTestEnv> CreateAsync(PostgresFixture fixture, TimeProvider? clock = null)
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);

        await new FacilityStore(db).SaveAsync(new FacilityInfo(
            "Khoa Xét nghiệm — Bệnh viện Thử", null, null, null, "9", "yyMMdd", 3, "per-specimen"));

        var catalog = new CatalogStore(db);
        var edtaId = (await catalog.SpecimenTypesAsync()).First(s => s.Code == "EDTA").Id;

        var actor = await new UserStore(db).CreateAsync("ktv.thu", "Kỹ thuật viên Thử", null, "mat-khau-thu-1", ["ky-thuat-vien"]);

        clock ??= TimeProvider.System;
        return new ResultTestEnv(new ResultService(db, clock), new OrderService(db, clock), catalog, edtaId, actor.Id, db, clock);
    }
}

/// <summary>Hàm tiện ích tạo dữ liệu giả và đọc thẳng bảng <c>result</c> / <c>result_history</c>.</summary>
internal static class ResultTestData
{
    public static async Task<Guid> SaveServiceAsync(
        CatalogStore catalog, string code, string name, Guid specimenTypeId, int? decimals, string? unit,
        bool autoApprove = false, bool isPanel = false, string valueType = "so")
    {
        return await catalog.SaveServiceAsync(new ServiceInfo(
            Guid.Empty, code, name, GroupName: null, specimenTypeId, unit, valueType, decimals,
            PrintOrder: 0, isPanel, autoApprove, Active: true, HisCode: null, ChildIds: []));
    }

    public static ReferenceRange Range(Guid serviceId, char? sex, decimal low, decimal high, Guid? instrumentId = null,
        decimal? criticalLow = null, decimal? criticalHigh = null) =>
        new(Guid.Empty, serviceId, sex, null, null, instrumentId, low, high, criticalLow, criticalHigh, null);

    /// <summary>Tạo một chỉ định cho người bệnh giả và trả về mẫu (duy nhất, loại EDTA) kèm order_item theo dịch vụ.</summary>
    public static async Task<(Guid SampleId, IReadOnlyDictionary<Guid, Guid> ItemByService)> CreateSampleAsync(
        ResultTestEnv env, NewPatient patient, params Guid[] serviceIds)
    {
        var created = await env.Orders.CreateAsync(new NewOrder(null, patient, serviceIds, null, null, null), env.ActorId);
        var sampleId = created.Samples.Single().SampleId;

        await using var connection = await env.Db.OpenAsync();
        var items = await connection.QueryAsync<(Guid ServiceId, Guid Id)>(
            "SELECT service_id, id FROM order_item WHERE sample_id = @sampleId", new { sampleId });

        return (sampleId, items.ToDictionary(i => i.ServiceId, i => i.Id));
    }

    public static async Task<string> BarcodeAsync(Db db, Guid sampleId)
    {
        await using var connection = await db.OpenAsync();
        return await connection.ExecuteScalarAsync<string>("SELECT barcode FROM sample WHERE id = @sampleId", new { sampleId })
            ?? throw new InvalidOperationException("Không tìm thấy mẫu");
    }

    public static NewPatient FemaleAdult(string name = "Trần Thị Mẫu") => new(name, new DateOnly(1990, 3, 15), null, 'F', null, "0900000011");

    public static NewPatient MaleAdult(string name = "Nguyễn Văn Mẫu") => new(name, new DateOnly(1988, 7, 1), null, 'M', null, "0900000012");

    public sealed record HistoryRow
    {
        public string Action { get; init; } = "";
        public string? OldValue { get; init; }
        public string? NewValue { get; init; }
        public string? Reason { get; init; }
        public Guid? ActorId { get; init; }
    }

    public static async Task<List<HistoryRow>> HistoryAsync(Db db, Guid orderItemId)
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

    public sealed record StoredResult
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
        public string? InstrumentFlag { get; init; }
        public string Status { get; init; } = "";
        public string? PendingValue { get; init; }
        public Guid? PendingInstrumentId { get; init; }
        public string? PendingRawRef { get; init; }
    }

    public static async Task<StoredResult> StoredAsync(Db db, Guid orderItemId)
    {
        await using var connection = await db.OpenAsync();
        return await connection.QuerySingleAsync<StoredResult>(
            """
            SELECT id, value, unit, ref_low, ref_high, flag, source, instrument_id, raw_ref, instrument_flag, status,
                   pending_value, pending_instrument_id, pending_raw_ref
            FROM result WHERE order_item_id = @orderItemId
            """,
            new { orderItemId });
    }

    public static async Task<StoredResult?> StoredOrNullAsync(Db db, Guid orderItemId)
    {
        await using var connection = await db.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<StoredResult>(
            "SELECT id, value, status FROM result WHERE order_item_id = @orderItemId", new { orderItemId });
    }

    public static async Task<Guid> InsertInstrumentAsync(Db db, string name)
    {
        await using var connection = await db.OpenAsync();
        return await connection.ExecuteScalarAsync<Guid>("INSERT INTO instrument (name) VALUES (@name) RETURNING id", new { name });
    }

    /// <summary>Giả lập máy gửi lại kết quả cho mẫu đã duyệt bằng cách ghi thẳng các cột pending_*.</summary>
    public static async Task SetPendingAsync(Db db, Guid orderItemId, string pendingValue, Guid instrumentId, string rawRef)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            """
            UPDATE result SET pending_value = @pendingValue, pending_instrument_id = @instrumentId, pending_raw_ref = @rawRef
            WHERE order_item_id = @orderItemId
            """,
            new { orderItemId, pendingValue, instrumentId, rawRef });
    }
}
