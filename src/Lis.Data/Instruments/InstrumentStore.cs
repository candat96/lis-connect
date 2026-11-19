using System.Text.Json;
using Dapper;
using Npgsql;

namespace Lis.Data.Instruments;

/// <summary>Một máy xét nghiệm trong danh mục. <c>driver_config</c>/<c>connection</c> là JSON thô (đợt 2 thay bằng form theo họ giao thức).</summary>
public sealed record InstrumentInfo(
    Guid Id,
    string Name,
    string? Model,
    string? ProtocolFamily,
    string? DriverConfigJson,
    string? ConnectionJson,
    string? WorkstationCode,
    bool Active);

/// <summary>Ánh xạ một kênh đo của máy sang một chỉ số. <see cref="Decimals"/> null → theo số lẻ của chỉ số; <see cref="Factor"/> null → không nhân.</summary>
public sealed record ChannelMap(
    Guid Id,
    Guid InstrumentId,
    string ChannelCode,
    Guid ServiceId,
    int? Decimals,
    decimal? Factor,
    bool KeepComparator,
    bool SendOnQuery);

/// <summary>Một mã kênh máy đã gửi mà chưa map (từ Chưa gắn mẫu): số lần gặp, giá trị và đơn vị gần nhất.</summary>
public sealed record UnmappedChannel(string ChannelCode, int Count, string LastValue, string? LastUnit, DateTimeOffset LastAt);

/// <summary>Một dịch vụ đã khai là máy chạy được; <see cref="ChannelCode"/> null khi chưa biết mã kênh.</summary>
public sealed record DeclaredService(Guid ServiceId, string Code, string Name, string? ChannelCode);

/// <summary>Một dòng map kênh kèm mã/tên chỉ số để hiển thị.</summary>
public sealed record ChannelMapItem(ChannelMap Map, string ServiceCode, string ServiceName);

/// <summary>Lý do một thao tác sửa máy / map kênh bị từ chối (server web dịch sang mã HTTP).</summary>
public enum InstrumentFailure
{
    /// <summary>Không có máy với id đó.</summary>
    NotFound,

    /// <summary>Mã kênh đã map cho chỉ số khác trên cùng máy.</summary>
    DuplicateChannel,

    /// <summary>Không có chỉ số với id đó.</summary>
    ServiceNotFound,

    /// <summary>Kênh máy chỉ map tới chỉ số lá, không map tới dịch vụ cha (panel).</summary>
    PanelService,
}

/// <summary>
/// Lỗi nghiệp vụ khi sửa máy / map kênh; <see cref="Exception.Message"/> là câu tiếng Việt hiển thị cho người dùng.
/// Là <see cref="InvalidOperationException"/> như trước nên bên gọi cũ bắt kiểu đó vẫn chạy.
/// </summary>
public sealed class InstrumentException(InstrumentFailure failure, string message) : InvalidOperationException(message)
{
    public InstrumentFailure Failure { get; } = failure;
}

/// <summary>Kết quả xoá một máy.</summary>
public enum InstrumentDeleteResult
{
    Deleted,

    NotFound,

    /// <summary>Máy đã có kết quả / log tham chiếu tới — không xoá được, chỉ ngừng dùng.</summary>
    InUse,

    /// <summary>Máy còn bản tin thô chưa xử lý xong (chờ giải mã, đang giải mã, lỗi, chỉ lưu, cần xem lại) — không xoá được.</summary>
    InUseRawMessages,
}

/// <summary>Danh mục máy và map kênh mà màn "Máy &amp; map" cần. Cài đặt thật là <see cref="InstrumentStore"/>; kiểm thử ViewModel dùng bản giả.</summary>
public interface IInstrumentStore
{
    Task<IReadOnlyList<InstrumentInfo>> ListAsync();

    /// <summary>Thêm (khi <see cref="InstrumentInfo.Id"/> rỗng) hoặc cập nhật máy. Trả về id. JSON không hợp lệ → <see cref="ArgumentException"/>.</summary>
    Task<Guid> SaveAsync(InstrumentInfo instrument);

    Task<IReadOnlyList<ChannelMapItem>> ChannelsAsync(Guid instrumentId);

    /// <summary>Kênh máy đã gửi nhưng chưa map (dòng Chưa gắn mẫu lý do "kênh chưa map", chưa xử lý), mới nhất trước.</summary>
    Task<IReadOnlyList<UnmappedChannel>> UnmappedChannelsAsync(Guid instrumentId) => Task.FromResult<IReadOnlyList<UnmappedChannel>>([]);

    /// <summary>Thêm (khi <see cref="ChannelMap.Id"/> rỗng) hoặc cập nhật map kênh. Trùng kênh → <see cref="InvalidOperationException"/> nêu rõ chỉ số đã map.</summary>
    Task<Guid> SaveChannelAsync(ChannelMap channel);

    /// <summary>Các dịch vụ đã khai là máy này chạy được (kèm mã kênh nếu đã map), theo mã.</summary>
    Task<IReadOnlyList<DeclaredService>> DeclaredServicesAsync(Guid instrumentId) => Task.FromResult<IReadOnlyList<DeclaredService>>([]);

    /// <summary>Thêm / bỏ một dịch vụ khỏi danh sách máy chạy được.</summary>
    Task SetDeclaredAsync(Guid instrumentId, Guid serviceId, bool declared) => Task.CompletedTask;

    Task DeleteChannelAsync(Guid channelId);
}

/// <summary>
/// Truy cập bảng <c>instrument</c> và <c>instrument_channel</c>. Cột jsonb đi qua tham số text
/// có ép kiểu trong SQL (<c>@x::jsonb</c>) và đọc về dạng text.
/// </summary>
public sealed class InstrumentStore(Db db) : IInstrumentStore
{
    public async Task<IReadOnlyList<DeclaredService>> DeclaredServicesAsync(Guid instrumentId)
    {
        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<DeclaredService>(
            """
            SELECT sv.id AS ServiceId, sv.code AS Code, sv.name AS Name,
                   (SELECT string_agg(ic.channel_code, ', ' ORDER BY ic.channel_code) FROM instrument_channel ic
                    WHERE ic.instrument_id = x.instrument_id AND ic.service_id = sv.id) AS ChannelCode
            FROM instrument_service x
            JOIN service sv ON sv.id = x.service_id
            WHERE x.instrument_id = @instrumentId
            ORDER BY sv.code
            """,
            new { instrumentId });
        return rows.ToList();
    }

    public async Task SetDeclaredAsync(Guid instrumentId, Guid serviceId, bool declared)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            declared
                ? "INSERT INTO instrument_service (instrument_id, service_id) VALUES (@instrumentId, @serviceId) ON CONFLICT DO NOTHING"
                : "DELETE FROM instrument_service WHERE instrument_id = @instrumentId AND service_id = @serviceId",
            new { instrumentId, serviceId });
    }

    public async Task<IReadOnlyList<UnmappedChannel>> UnmappedChannelsAsync(Guid instrumentId)
    {
        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<UnmappedRow>(
            """
            SELECT DISTINCT ON (u.channel_code)
                u.channel_code AS ChannelCode,
                (count(*) OVER (PARTITION BY u.channel_code))::int AS Count,
                u.value AS LastValue,
                u.unit AS LastUnit,
                u.received_at AS LastAt
            FROM unmatched_result u
            WHERE u.instrument_id = @instrumentId
              AND u.reason = 'kenh-chua-map'
              AND u.resolved_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM instrument_channel ic WHERE ic.instrument_id = u.instrument_id AND ic.channel_code = u.channel_code)
            ORDER BY u.channel_code, u.received_at DESC
            """,
            new { instrumentId });
        return rows.OrderByDescending(r => r.LastAt)
            .Select(r => new UnmappedChannel(r.ChannelCode, r.Count, r.LastValue, r.LastUnit, r.LastAt)).ToList();
    }

    public async Task<IReadOnlyList<InstrumentInfo>> ListAsync()
    {
        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<InstrumentRow>(
            """
            SELECT id, name, model, protocol_family, driver_config::text AS driver_config, connection::text AS connection,
                   workstation_code, active
            FROM instrument
            ORDER BY name
            """);
        return rows.Select(row => row.ToInfo()).ToList();
    }

    public async Task<Guid> SaveAsync(InstrumentInfo instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument.Name))
        {
            throw new ArgumentException("Nhập tên máy");
        }

        await using var connection = await db.OpenAsync();
        return await SaveInTransactionAsync(connection, null, instrument);
    }

    /// <summary>
    /// Như <see cref="SaveAsync"/> nhưng dùng kết nối/giao dịch của bên gọi — để các việc nhiều
    /// bước (vd nhập Excel ở <see cref="Catalog.CatalogExcelImporter"/>) ghi chung một giao
    /// dịch. Không kiểm tra <see cref="InstrumentInfo.Name"/> trống — bên gọi tự kiểm trước.
    /// </summary>
    internal static async Task<Guid> SaveInTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, InstrumentInfo instrument)
    {
        var parameters = new
        {
            id = instrument.Id,
            name = instrument.Name.Trim(),
            model = NullIfBlank(instrument.Model),
            protocolFamily = NullIfBlank(instrument.ProtocolFamily),
            driverConfig = ValidJsonOrNull(instrument.DriverConfigJson),
            connection = ValidJsonOrNull(instrument.ConnectionJson),
            workstationCode = NullIfBlank(instrument.WorkstationCode),
            active = instrument.Active,
        };

        if (instrument.Id == Guid.Empty)
        {
            return await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO instrument (name, model, protocol_family, driver_config, connection, workstation_code, active)
                VALUES (@name, @model, @protocolFamily, @driverConfig::jsonb, @connection::jsonb, @workstationCode, @active)
                RETURNING id
                """,
                parameters, transaction);
        }

        var updated = await connection.ExecuteAsync(
            """
            UPDATE instrument SET
                name = @name, model = @model, protocol_family = @protocolFamily,
                driver_config = @driverConfig::jsonb, connection = @connection::jsonb,
                workstation_code = @workstationCode, active = @active
            WHERE id = @id
            """,
            parameters, transaction);

        if (updated == 0)
        {
            throw new InstrumentException(InstrumentFailure.NotFound, "Không tìm thấy máy");
        }

        return instrument.Id;
    }

    public async Task<IReadOnlyList<ChannelMapItem>> ChannelsAsync(Guid instrumentId)
    {
        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<ChannelRow>(
            $"""
            {ChannelSelect}
            WHERE ic.instrument_id = @instrumentId
            ORDER BY ic.channel_code
            """,
            new { instrumentId });
        return rows.Select(row => new ChannelMapItem(row.ToMap(), row.ServiceCode, row.ServiceName)).ToList();
    }

    public async Task<Guid> SaveChannelAsync(ChannelMap channel)
    {
        var channelCode = channel.ChannelCode.Trim();

        try
        {
            return await db.InTransactionAsync(async (connection, transaction) =>
                await SaveChannelInTransactionAsync(connection, transaction, channel with { ChannelCode = channelCode }));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await using var connection = await db.OpenAsync();
            var serviceName = await connection.ExecuteScalarAsync<string?>(
                """
                SELECT sv.name FROM instrument_channel ic JOIN service sv ON sv.id = ic.service_id
                WHERE ic.instrument_id = @instrumentId AND ic.channel_code = @channelCode
                """,
                new { channel.InstrumentId, channelCode });
            throw DuplicateChannel(channelCode, serviceName ?? "?");
        }
    }

    /// <summary>
    /// Như <see cref="SaveChannelAsync"/> nhưng dùng kết nối/giao dịch của bên gọi — để các việc
    /// nhiều bước (vd nhập Excel ở <see cref="Catalog.CatalogExcelImporter"/>) ghi chung một
    /// giao dịch. Kiểm tra <see cref="ChannelMap.ChannelCode"/> trống và <see cref="ChannelMap.Factor"/>
    /// ngay ở đây (không phải chỉ ở <see cref="SaveChannelAsync"/>) để cả hai đường gọi đều được
    /// kiểm, không trùng lặp kiểm tra ở hai nơi.
    /// </summary>
    internal static async Task<Guid> SaveChannelInTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ChannelMap channel)
    {
        var channelCode = channel.ChannelCode.Trim();
        if (channelCode.Length == 0)
        {
            throw new ArgumentException("Nhập mã kênh");
        }

        if (channel.Factor is <= 0)
        {
            throw new ArgumentException("Hệ số phải lớn hơn 0");
        }

        // Kiểm tra trùng trong cùng giao dịch để báo tên chỉ số đã map; ràng buộc UNIQUE
        // (instrument_id, channel_code) vẫn là chốt chặn cuối khi hai máy trạm lưu cùng lúc.
        var existingServiceName = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT sv.name
            FROM instrument_channel ic
            JOIN service sv ON sv.id = ic.service_id
            WHERE ic.instrument_id = @instrumentId AND ic.channel_code = @channelCode AND ic.id <> @id
            """,
            new { channel.InstrumentId, channelCode, channel.Id }, transaction);

        if (existingServiceName is not null)
        {
            throw DuplicateChannel(channelCode, existingServiceName);
        }

        var isPanel = await connection.ExecuteScalarAsync<bool?>(
            "SELECT is_panel FROM service WHERE id = @serviceId", new { channel.ServiceId }, transaction)
            ?? throw new InstrumentException(InstrumentFailure.ServiceNotFound, "Không tìm thấy chỉ số");
        if (isPanel)
        {
            throw new InstrumentException(InstrumentFailure.PanelService, "Kênh máy chỉ map tới chỉ số lá, không map tới dịch vụ cha (panel)");
        }

        var parameters = new
        {
            channel.Id,
            channel.InstrumentId,
            channelCode,
            channel.ServiceId,
            channel.Decimals,
            channel.Factor,
            channel.KeepComparator,
            channel.SendOnQuery,
        };

        if (channel.Id == Guid.Empty)
        {
            return await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO instrument_channel (instrument_id, channel_code, service_id, decimals, factor, keep_comparator, send_on_query)
                VALUES (@InstrumentId, @channelCode, @ServiceId, @Decimals, @Factor, @KeepComparator, @SendOnQuery)
                RETURNING id
                """,
                parameters, transaction);
        }

        await connection.ExecuteAsync(
            """
            UPDATE instrument_channel SET
                channel_code = @channelCode, service_id = @ServiceId, decimals = @Decimals, factor = @Factor,
                keep_comparator = @KeepComparator, send_on_query = @SendOnQuery
            WHERE id = @Id
            """,
            parameters, transaction);
        return channel.Id;
    }

    /// <summary>
    /// Xoá máy cùng map kênh, danh sách chỉ số khai báo và các bản tin thô đã xong (<c>da-giai-ma</c>, <c>khong-co-ket-qua</c>) của nó
    /// trong một giao dịch. Còn bản tin thô ở trạng thái khác thì <see cref="InstrumentDeleteResult.InUseRawMessages"/>.
    /// </summary>
    public async Task<InstrumentDeleteResult> DeleteAsync(Guid instrumentId)
    {
        try
        {
            return await db.InTransactionAsync(async (connection, transaction) =>
            {
                var unfinished = await connection.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM raw_message
                        WHERE instrument_id = @instrumentId AND decode_status NOT IN ('da-giai-ma', 'khong-co-ket-qua'))
                    """,
                    new { instrumentId }, transaction);
                if (unfinished)
                {
                    return InstrumentDeleteResult.InUseRawMessages;
                }

                await connection.ExecuteAsync(
                    "DELETE FROM raw_message WHERE instrument_id = @instrumentId AND decode_status IN ('da-giai-ma', 'khong-co-ket-qua')",
                    new { instrumentId }, transaction);
                await connection.ExecuteAsync("DELETE FROM instrument_service WHERE instrument_id = @instrumentId", new { instrumentId }, transaction);
                await connection.ExecuteAsync("DELETE FROM instrument_channel WHERE instrument_id = @instrumentId", new { instrumentId }, transaction);
                var deleted = await connection.ExecuteAsync("DELETE FROM instrument WHERE id = @instrumentId", new { instrumentId }, transaction);
                return deleted == 0 ? InstrumentDeleteResult.NotFound : InstrumentDeleteResult.Deleted;
            });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return InstrumentDeleteResult.InUse;
        }
    }

    public async Task DeleteChannelAsync(Guid channelId)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync("DELETE FROM instrument_channel WHERE id = @channelId", new { channelId });
    }

    /// <summary>Tra map của một kênh máy trong giao dịch của bên gọi; null khi kênh chưa map.</summary>
    internal static async Task<ChannelMap?> FindChannelAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid instrumentId, string channelCode)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ChannelRow>(
            $"""
            {ChannelSelect}
            WHERE ic.instrument_id = @instrumentId AND ic.channel_code = @channelCode
            """,
            new { instrumentId, channelCode }, transaction);
        return row?.ToMap();
    }

    private const string ChannelSelect = """
        SELECT ic.id, ic.instrument_id, ic.channel_code, ic.service_id, ic.decimals, ic.factor, ic.keep_comparator, ic.send_on_query,
               sv.code AS service_code, sv.name AS service_name
        FROM instrument_channel ic
        JOIN service sv ON sv.id = ic.service_id
        """;

    private static InstrumentException DuplicateChannel(string channelCode, string serviceName) =>
        new(InstrumentFailure.DuplicateChannel, $"Mã kênh {channelCode} đã map cho chỉ số {serviceName}");

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>JSON thô người dùng gõ: trống → NULL; không phân tích được → lỗi dễ hiểu trước khi chạm CSDL.</summary>
    private static string? ValidJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return json.Trim();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("JSON không hợp lệ", ex);
        }
    }

    private sealed record InstrumentRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? Model { get; init; }
        public string? ProtocolFamily { get; init; }
        public string? DriverConfig { get; init; }
        public string? Connection { get; init; }
        public string? WorkstationCode { get; init; }
        public bool Active { get; init; }

        public InstrumentInfo ToInfo() => new(Id, Name, Model, ProtocolFamily, DriverConfig, Connection, WorkstationCode, Active);
    }

    /// <summary>Dapper không gán timestamptz vào tham số <see cref="DateTimeOffset"/> của hàm tạo record — đi qua thuộc tính.</summary>
    private sealed record UnmappedRow
    {
        public string ChannelCode { get; init; } = "";
        public int Count { get; init; }
        public string LastValue { get; init; } = "";
        public string? LastUnit { get; init; }
        public DateTimeOffset LastAt { get; init; }
    }

    private sealed record ChannelRow
    {
        public Guid Id { get; init; }
        public Guid InstrumentId { get; init; }
        public string ChannelCode { get; init; } = "";
        public Guid ServiceId { get; init; }
        public int? Decimals { get; init; }
        public decimal? Factor { get; init; }
        public bool KeepComparator { get; init; }
        public bool SendOnQuery { get; init; }
        public string ServiceCode { get; init; } = "";
        public string ServiceName { get; init; } = "";

        public ChannelMap ToMap() => new(Id, InstrumentId, ChannelCode, ServiceId, Decimals, Factor, KeepComparator, SendOnQuery);
    }
}
