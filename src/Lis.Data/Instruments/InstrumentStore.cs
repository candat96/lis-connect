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

/// <summary>Một dòng map kênh kèm mã/tên chỉ số để hiển thị.</summary>
public sealed record ChannelMapItem(ChannelMap Map, string ServiceCode, string ServiceName);

/// <summary>Danh mục máy và map kênh mà màn "Máy &amp; map" cần. Cài đặt thật là <see cref="InstrumentStore"/>; kiểm thử ViewModel dùng bản giả.</summary>
public interface IInstrumentStore
{
    Task<IReadOnlyList<InstrumentInfo>> ListAsync();

    /// <summary>Thêm (khi <see cref="InstrumentInfo.Id"/> rỗng) hoặc cập nhật máy. Trả về id. JSON không hợp lệ → <see cref="ArgumentException"/>.</summary>
    Task<Guid> SaveAsync(InstrumentInfo instrument);

    Task<IReadOnlyList<ChannelMapItem>> ChannelsAsync(Guid instrumentId);

    /// <summary>Thêm (khi <see cref="ChannelMap.Id"/> rỗng) hoặc cập nhật map kênh. Trùng kênh → <see cref="InvalidOperationException"/> nêu rõ chỉ số đã map.</summary>
    Task<Guid> SaveChannelAsync(ChannelMap channel);

    Task DeleteChannelAsync(Guid channelId);
}

/// <summary>
/// Truy cập bảng <c>instrument</c> và <c>instrument_channel</c>. Cột jsonb đi qua tham số text
/// có ép kiểu trong SQL (<c>@x::jsonb</c>) và đọc về dạng text.
/// </summary>
public sealed class InstrumentStore(Db db) : IInstrumentStore
{
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

        await using var connection = await db.OpenAsync();

        if (instrument.Id == Guid.Empty)
        {
            return await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO instrument (name, model, protocol_family, driver_config, connection, workstation_code, active)
                VALUES (@name, @model, @protocolFamily, @driverConfig::jsonb, @connection::jsonb, @workstationCode, @active)
                RETURNING id
                """,
                parameters);
        }

        var updated = await connection.ExecuteAsync(
            """
            UPDATE instrument SET
                name = @name, model = @model, protocol_family = @protocolFamily,
                driver_config = @driverConfig::jsonb, connection = @connection::jsonb,
                workstation_code = @workstationCode, active = @active
            WHERE id = @id
            """,
            parameters);

        if (updated == 0)
        {
            throw new InvalidOperationException("Không tìm thấy máy");
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
        if (channelCode.Length == 0)
        {
            throw new ArgumentException("Nhập mã kênh");
        }

        if (channel.Factor is <= 0)
        {
            throw new ArgumentException("Hệ số phải lớn hơn 0");
        }

        try
        {
            return await db.InTransactionAsync(async (connection, transaction) =>
            {
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
                    ?? throw new InvalidOperationException("Không tìm thấy chỉ số");
                if (isPanel)
                {
                    throw new InvalidOperationException("Kênh máy chỉ map tới chỉ số lá, không map tới dịch vụ cha (panel)");
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
            });
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

    private static InvalidOperationException DuplicateChannel(string channelCode, string serviceName) =>
        new($"Mã kênh {channelCode} đã map cho chỉ số {serviceName}");

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
