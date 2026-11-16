using Dapper;
using Lis.Core.Results;
using Npgsql;

namespace Lis.Data.Catalog;

/// <summary>Lý do một thao tác sửa danh mục bị từ chối (server web dịch sang mã HTTP).</summary>
public enum CatalogFailure
{
    /// <summary>Không có dịch vụ với id đó.</summary>
    NotFound,

    /// <summary>Mã dịch vụ trùng.</summary>
    DuplicateCode,

    /// <summary>Loại mẫu, máy hoặc chỉ số con trỏ tới bản ghi không tồn tại.</summary>
    MissingReference,

    /// <summary>Vi phạm quy tắc danh mục (vd gói lồng gói).</summary>
    Invalid,

    /// <summary>Dịch vụ đang được dùng nên không xoá được.</summary>
    InUse,
}

/// <summary>Lỗi nghiệp vụ khi sửa danh mục; <see cref="Exception.Message"/> là câu tiếng Việt hiển thị cho người dùng.</summary>
public sealed class CatalogException(CatalogFailure failure, string message) : Exception(message)
{
    public CatalogFailure Failure { get; } = failure;
}

public sealed record DetailChild(Guid Id, string Code, string Name);

public sealed record DetailRange(
    string? Sex, int? AgeFromDays, int? AgeToDays, Guid? InstrumentId,
    double? Low, double? High, double? CriticalLow, double? CriticalHigh, string? TextRange);

public sealed record DetailRule(Guid? InstrumentId, string RawValue, string DisplayValue, string? Flag);

/// <summary>Dịch vụ đầy đủ để mở form sửa: trường của dịch vụ (giữ nguyên null của CSDL), chỉ số con, khoảng tham chiếu, luật định tính.</summary>
public sealed record ServiceDetail(
    Guid Id, string Code, string Name, string? GroupName, Guid? SpecimenTypeId, string? Unit, string ValueType, int? Decimals,
    int? PrintOrder, bool IsPanel, bool AutoApprove, bool Active, string? HisCode,
    IReadOnlyList<DetailChild> Children, IReadOnlyList<DetailRange> Ranges, IReadOnlyList<DetailRule> QualitativeRules);

public sealed record MetaSpecimenType(Guid Id, string Code, string Name);

public sealed record MetaInstrument(Guid Id, string Name);

/// <summary>Dữ liệu cho form sửa danh mục: loại mẫu, các nhóm đang dùng, mọi máy (cho khoảng tham chiếu riêng theo máy).</summary>
public sealed record CatalogMeta(
    IReadOnlyList<MetaSpecimenType> SpecimenTypes, IReadOnlyList<string> Groups, IReadOnlyList<MetaInstrument> Instruments);

// Phần sửa danh mục từ web: ghi/xoá một dịch vụ cùng chỉ số con, khoảng tham chiếu, luật định tính trong MỘT giao dịch.
public sealed partial class CatalogStore
{
    /// <summary>Loại mẫu (theo tên), các nhóm đang dùng, mọi máy (theo tên).</summary>
    public async Task<CatalogMeta> MetaAsync()
    {
        await using var connection = await db.OpenAsync();
        var specimens = await connection.QueryAsync<MetaSpecimenType>("SELECT id, code, name FROM specimen_type ORDER BY name");
        var groups = await connection.QueryAsync<string>(
            "SELECT DISTINCT group_name FROM service WHERE group_name IS NOT NULL AND group_name <> '' ORDER BY 1");
        var instruments = await connection.QueryAsync<MetaInstrument>("SELECT id, name FROM instrument ORDER BY name");
        return new CatalogMeta(specimens.ToList(), groups.ToList(), instruments.ToList());
    }

    /// <summary>Một dịch vụ đầy đủ để sửa; null khi không có.</summary>
    public async Task<ServiceDetail?> DetailAsync(Guid id)
    {
        await using var connection = await db.OpenAsync();
        var s = await connection.QuerySingleOrDefaultAsync<ServiceRow>($"SELECT {ServiceColumns} FROM service WHERE id = @id", new { id });
        if (s is null)
        {
            return null;
        }

        var children = await connection.QueryAsync<DetailChild>(
            "SELECT sv.id, sv.code, sv.name FROM service_child sc JOIN service sv ON sv.id = sc.child_id WHERE sc.parent_id = @id ORDER BY sc.sort_order, sv.code",
            new { id });
        var ranges = await connection.QueryAsync<DetailRangeRow>(
            """
            SELECT sex, age_from_days, age_to_days, instrument_id, low::float8 AS low, high::float8 AS high,
                   critical_low::float8 AS critical_low, critical_high::float8 AS critical_high, text_range
            FROM reference_range WHERE service_id = @id
            ORDER BY instrument_id NULLS FIRST, sex NULLS FIRST, age_from_days NULLS FIRST, created_at
            """,
            new { id });
        var rules = await connection.QueryAsync<DetailRule>(
            "SELECT instrument_id, raw_value, display_value, flag FROM qualitative_rule WHERE service_id = @id ORDER BY raw_value",
            new { id });

        return new ServiceDetail(
            s.Id, s.Code, s.Name, s.GroupName, s.SpecimenTypeId, s.Unit, s.ValueType, s.Decimals, s.PrintOrder,
            s.IsPanel, s.AutoApprove, s.Active, s.HisCode,
            children.ToList(), ranges.Select(r => r.ToDetail()).ToList(), rules.ToList());
    }

    /// <summary>
    /// Thêm (<paramref name="id"/> null) hoặc sửa dịch vụ, rồi đặt lại chỉ số con, khoảng tham chiếu, luật định tính — cùng một giao dịch.
    /// Gói (<see cref="ServiceInfo.IsPanel"/>) không giữ khoảng tham chiếu / luật định tính; luật định tính chỉ giữ khi kiểu kết quả là "dinh-tinh".
    /// Vi phạm → <see cref="CatalogException"/>, giao dịch rollback. Trả id dịch vụ.
    /// </summary>
    public async Task<Guid> SaveWithDetailsAsync(
        Guid? id, ServiceInfo service, IReadOnlyList<ReferenceRange> ranges, IReadOnlyList<QualitativeRule> rules)
    {
        try
        {
            return await db.InTransactionAsync(async (connection, transaction) =>
            {
                if (id is { } existing
                    && await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM service WHERE id = @id", new { id = existing }, transaction) == 0)
                {
                    throw new CatalogException(CatalogFailure.NotFound, "Không tìm thấy dịch vụ");
                }

                Guid serviceId;
                try
                {
                    serviceId = await SaveServiceInTransactionAsync(connection, transaction, service with { Id = id ?? Guid.Empty });
                }
                catch (InvalidOperationException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    // Chỉ lỗi UNIQUE mới là trùng mã; lỗi khác (vd mất kết nối) không được báo nhầm thành "đã tồn tại".
                    throw new CatalogException(CatalogFailure.DuplicateCode, "Mã dịch vụ đã tồn tại");
                }

                var childIds = service.IsPanel ? service.ChildIds.Distinct().ToList() : [];
                if (service.IsPanel)
                {
                    await CheckPanelChildrenAsync(connection, transaction, serviceId, childIds);
                }

                await SetChildrenInTransactionAsync(connection, transaction, serviceId, childIds);

                // Gói không có giá trị riêng nên không giữ khoảng tham chiếu / luật định tính.
                var keepRanges = service.IsPanel ? [] : ranges;
                var keepRules = !service.IsPanel && service.ValueType == "dinh-tinh" ? rules : [];
                await SaveRangesInTransactionAsync(connection, transaction, serviceId, keepRanges);
                await SaveQualitativeRulesInTransactionAsync(connection, transaction, serviceId, keepRules);
                return serviceId;
            });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new CatalogException(CatalogFailure.DuplicateCode, "Mã dịch vụ đã tồn tại");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new CatalogException(CatalogFailure.MissingReference, "Loại mẫu, máy hoặc chỉ số con không tồn tại");
        }
        catch (InvalidOperationException ex)
        {
            // Quy tắc chỉ số con của SetChildrenInTransactionAsync mà CheckPanelChildrenAsync chưa chặn trước.
            throw new CatalogException(CatalogFailure.Invalid, ex.Message);
        }
    }

    /// <summary>Báo lỗi có tên cụ thể cho gói không hợp lệ (tốt hơn câu chung của <see cref="SetChildrenInTransactionAsync"/>).</summary>
    private static async Task CheckPanelChildrenAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid panelId, IReadOnlyList<Guid> childIds)
    {
        if (childIds.Contains(panelId))
        {
            throw new CatalogException(CatalogFailure.Invalid, "Dịch vụ không thể là con của chính nó");
        }

        var parentName = await connection.QueryFirstOrDefaultAsync<string>(
            "SELECT sv.name FROM service_child sc JOIN service sv ON sv.id = sc.parent_id WHERE sc.child_id = @panelId LIMIT 1",
            new { panelId }, transaction);
        if (parentName is not null)
        {
            throw new CatalogException(CatalogFailure.Invalid, $"Dịch vụ này đang là chỉ số con của gói \"{parentName}\" nên không thể thành gói");
        }

        if (childIds.Count > 0)
        {
            var panels = await PanelNamesAmongAsync(connection, transaction, childIds);
            if (panels.Count > 0)
            {
                throw new CatalogException(CatalogFailure.Invalid, $"Chỉ số con không được là một gói khác: {string.Join(", ", panels)}");
            }
        }
    }

    /// <summary>
    /// Xoá dịch vụ chưa từng được dùng. Đã có trong phiếu chỉ định, đang map kênh máy, hoặc đang là chỉ số con của một gói
    /// thì không xoá — <see cref="CatalogException"/> nêu lý do. Xoá kèm khoảng tham chiếu, luật định tính, khai báo máy chạy được, liên kết chỉ số con của chính nó.
    /// </summary>
    public async Task DeleteServiceAsync(Guid id) =>
        await db.InTransactionAsync(async (connection, transaction) =>
        {
            if (await connection.ExecuteScalarAsync<string?>("SELECT name FROM service WHERE id = @id FOR UPDATE", new { id }, transaction) is null)
            {
                throw new CatalogException(CatalogFailure.NotFound, "Không tìm thấy dịch vụ");
            }

            var used = await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM order_item WHERE service_id = @id", new { id }, transaction);
            if (used > 0)
            {
                throw new CatalogException(
                    CatalogFailure.InUse, $"Dịch vụ đã có trong {used} dòng chỉ định nên không xoá được — hãy chuyển sang \"Ngừng dùng\"");
            }

            var parents = (await connection.QueryAsync<string>(
                "SELECT sv.name FROM service_child sc JOIN service sv ON sv.id = sc.parent_id WHERE sc.child_id = @id ORDER BY sv.name",
                new { id }, transaction)).ToList();
            if (parents.Count > 0)
            {
                throw new CatalogException(
                    CatalogFailure.InUse, $"Đang là chỉ số con của gói \"{string.Join("\", \"", parents)}\" — gỡ khỏi gói trước khi xoá");
            }

            var machines = (await connection.QueryAsync<string>(
                "SELECT DISTINCT i.name FROM instrument_channel ic JOIN instrument i ON i.id = ic.instrument_id WHERE ic.service_id = @id ORDER BY 1",
                new { id }, transaction)).ToList();
            if (machines.Count > 0)
            {
                throw new CatalogException(
                    CatalogFailure.InUse, $"Đang được map kênh ở máy {string.Join(", ", machines)} — xoá kênh đó trước khi xoá dịch vụ");
            }

            await connection.ExecuteAsync("DELETE FROM instrument_service WHERE service_id = @id", new { id }, transaction);
            await connection.ExecuteAsync("DELETE FROM service_child WHERE parent_id = @id", new { id }, transaction);
            await connection.ExecuteAsync("DELETE FROM reference_range WHERE service_id = @id", new { id }, transaction);
            await connection.ExecuteAsync("DELETE FROM qualitative_rule WHERE service_id = @id", new { id }, transaction);
            await connection.ExecuteAsync("DELETE FROM service WHERE id = @id", new { id }, transaction);
            return true;
        });

    private sealed record DetailRangeRow
    {
        public string? Sex { get; init; }
        public int? AgeFromDays { get; init; }
        public int? AgeToDays { get; init; }
        public Guid? InstrumentId { get; init; }
        public double? Low { get; init; }
        public double? High { get; init; }
        public double? CriticalLow { get; init; }
        public double? CriticalHigh { get; init; }
        public string? TextRange { get; init; }

        public DetailRange ToDetail() => new(Sex, AgeFromDays, AgeToDays, InstrumentId, Low, High, CriticalLow, CriticalHigh, TextRange);
    }
}
