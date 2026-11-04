using Dapper;
using Lis.Core.Results;
using Npgsql;

namespace Lis.Data.Catalog;

/// <summary>
/// Một dịch vụ/chỉ số xét nghiệm trong danh mục. <see cref="IsPanel"/> đánh dấu dịch vụ cha
/// (gói), khi đó <see cref="ChildIds"/> là danh sách chỉ số con theo đúng thứ tự in
/// (<c>service_child.sort_order</c>). Dịch vụ không phải panel luôn có <see cref="ChildIds"/>
/// rỗng.
/// </summary>
public sealed record ServiceInfo(
    Guid Id,
    string Code,
    string Name,
    string? GroupName,
    Guid SpecimenTypeId,
    string? Unit,
    string ValueType,
    int? Decimals,
    int PrintOrder,
    bool IsPanel,
    bool AutoApprove,
    bool Active,
    string? HisCode,
    IReadOnlyList<Guid> ChildIds);

/// <summary>
/// Truy cập danh mục dịch vụ xét nghiệm: dịch vụ/chỉ số, quan hệ dịch vụ cha (panel) - chỉ số
/// con, khoảng tham chiếu và quy đổi định tính. Xem bảng <c>service</c>, <c>service_child</c>,
/// <c>reference_range</c>, <c>qualitative_rule</c> trong <c>0001_khoi_tao.sql</c>.
/// </summary>
public sealed class CatalogStore(Db db)
{
    private const string ServiceColumns = """
        id, code, name, group_name, specimen_type_id, unit, value_type, decimals,
        print_order, is_panel, auto_approve, active, his_code
        """;

    /// <summary>Liệt kê toàn bộ dịch vụ, kèm danh sách chỉ số con của từng panel, theo mã.</summary>
    public async Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(bool activeOnly)
    {
        await using var connection = await db.OpenAsync();

        var rows = (await connection.QueryAsync<ServiceRow>(
            $"SELECT {ServiceColumns} FROM service" +
            (activeOnly ? " WHERE active = true" : "") +
            " ORDER BY code")).ToList();

        var childrenByParent = await LoadChildrenByParentAsync(connection, rows.Select(r => r.Id).ToArray());

        return rows.Select(row => row.ToServiceInfo(childrenByParent)).ToList();
    }

    /// <summary>Tìm dịch vụ theo mã, không phân biệt hoa thường. Kèm danh sách chỉ số con.</summary>
    public async Task<ServiceInfo?> FindByCodeAsync(string code)
    {
        await using var connection = await db.OpenAsync();

        var row = await connection.QuerySingleOrDefaultAsync<ServiceRow>(
            $"SELECT {ServiceColumns} FROM service WHERE lower(code) = lower(@code)",
            new { code });

        if (row is null)
        {
            return null;
        }

        var childrenByParent = await LoadChildrenByParentAsync(connection, [row.Id]);
        return row.ToServiceInfo(childrenByParent);
    }

    /// <summary>
    /// Thêm dịch vụ mới (khi <see cref="ServiceInfo.Id"/> rỗng) hoặc cập nhật dịch vụ đã có.
    /// Không đụng tới quan hệ cha-con — xem <see cref="SetChildrenAsync"/>.
    /// </summary>
    public async Task<Guid> SaveServiceAsync(ServiceInfo s)
    {
        await using var connection = await db.OpenAsync();

        try
        {
            if (s.Id == Guid.Empty)
            {
                return await connection.ExecuteScalarAsync<Guid>(
                    """
                    INSERT INTO service
                        (code, name, group_name, specimen_type_id, unit, value_type, decimals,
                         print_order, is_panel, auto_approve, active, his_code)
                    VALUES
                        (@Code, @Name, @GroupName, @SpecimenTypeId, @Unit, @ValueType, @Decimals,
                         @PrintOrder, @IsPanel, @AutoApprove, @Active, @HisCode)
                    RETURNING id
                    """,
                    s);
            }

            await connection.ExecuteAsync(
                """
                UPDATE service SET
                    code = @Code, name = @Name, group_name = @GroupName, specimen_type_id = @SpecimenTypeId,
                    unit = @Unit, value_type = @ValueType, decimals = @Decimals, print_order = @PrintOrder,
                    is_panel = @IsPanel, auto_approve = @AutoApprove, active = @Active, his_code = @HisCode
                WHERE id = @Id
                """,
                s);
            return s.Id;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException("Mã dịch vụ đã tồn tại", ex);
        }
    }

    /// <summary>
    /// Đặt lại toàn bộ danh sách chỉ số con của một panel, theo đúng thứ tự truyền vào.
    /// Dịch vụ cha không thể tự làm con của mình; chỉ số con không thể là panel (không cho
    /// lồng nhiều cấp).
    /// </summary>
    public async Task SetChildrenAsync(Guid panelId, IReadOnlyList<Guid> childIds)
    {
        if (childIds.Contains(panelId))
        {
            throw new InvalidOperationException("Dịch vụ không thể là con của chính nó");
        }

        await using var connection = await db.OpenAsync();

        if (childIds.Count > 0)
        {
            var panelAmongChildren = await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM service WHERE id = ANY(@childIds) AND is_panel = true",
                new { childIds = childIds.ToArray() });

            if (panelAmongChildren > 0)
            {
                throw new InvalidOperationException("Chỉ số con không được là một dịch vụ cha (panel)");
            }
        }

        await db.InTransactionAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync("DELETE FROM service_child WHERE parent_id = @panelId", new { panelId }, tx);

            var sortOrder = 0;
            foreach (var childId in childIds)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO service_child (parent_id, child_id, sort_order) VALUES (@panelId, @childId, @sortOrder)",
                    new { panelId, childId, sortOrder }, tx);
                sortOrder++;
            }

            return true;
        });
    }

    /// <summary>Khoảng tham chiếu của một chỉ số, theo giới tính rồi tuổi.</summary>
    public async Task<IReadOnlyList<ReferenceRange>> RangesAsync(Guid serviceId)
    {
        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<RangeRow>(
            """
            SELECT id, service_id, sex, age_from_days, age_to_days, instrument_id,
                   low, high, critical_low, critical_high, text_range
            FROM reference_range
            WHERE service_id = @serviceId
            ORDER BY sex NULLS FIRST, age_from_days NULLS FIRST
            """,
            new { serviceId });
        return rows.Select(row => row.ToReferenceRange()).ToList();
    }

    /// <summary>Thay toàn bộ khoảng tham chiếu của một chỉ số bằng danh sách truyền vào.</summary>
    public async Task SaveRangesAsync(Guid serviceId, IReadOnlyList<ReferenceRange> ranges)
    {
        await db.InTransactionAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync("DELETE FROM reference_range WHERE service_id = @serviceId", new { serviceId }, tx);

            foreach (var range in ranges)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO reference_range
                        (service_id, sex, age_from_days, age_to_days, instrument_id,
                         low, high, critical_low, critical_high, text_range)
                    VALUES
                        (@serviceId, @sex, @ageFromDays, @ageToDays, @instrumentId,
                         @low, @high, @criticalLow, @criticalHigh, @textRange)
                    """,
                    new
                    {
                        serviceId,
                        sex = range.Sex?.ToString(),
                        ageFromDays = range.AgeFromDays,
                        ageToDays = range.AgeToDays,
                        instrumentId = range.InstrumentId,
                        low = range.Low,
                        high = range.High,
                        criticalLow = range.CriticalLow,
                        criticalHigh = range.CriticalHigh,
                        textRange = range.TextRange,
                    },
                    tx);
            }

            return true;
        });
    }

    /// <summary>Quy tắc quy đổi định tính của một chỉ số.</summary>
    public async Task<IReadOnlyList<QualitativeRule>> QualitativeRulesAsync(Guid serviceId)
    {
        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<QualitativeRuleRow>(
            "SELECT instrument_id, service_id, raw_value, display_value, flag FROM qualitative_rule WHERE service_id = @serviceId",
            new { serviceId });
        return rows.Select(row => row.ToRule()).ToList();
    }

    /// <summary>Thay toàn bộ quy tắc quy đổi định tính của một chỉ số bằng danh sách truyền vào.</summary>
    public async Task SaveQualitativeRulesAsync(Guid serviceId, IReadOnlyList<QualitativeRule> rules)
    {
        await db.InTransactionAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync("DELETE FROM qualitative_rule WHERE service_id = @serviceId", new { serviceId }, tx);

            foreach (var rule in rules)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO qualitative_rule (instrument_id, service_id, raw_value, display_value, flag)
                    VALUES (@instrumentId, @serviceId, @rawValue, @displayValue, @flag)
                    """,
                    new
                    {
                        instrumentId = rule.InstrumentId,
                        serviceId,
                        rawValue = rule.RawValue,
                        displayValue = rule.DisplayValue,
                        // result.flag/qualitative_rule.flag lưu tên hằng của enum ResultFlag (vd "Abnormal"), không lưu số.
                        flag = rule.Flag?.ToString(),
                    },
                    tx);
            }

            return true;
        });
    }

    /// <summary>Danh sách loại mẫu bệnh phẩm, theo thứ tự hiển thị.</summary>
    public async Task<IReadOnlyList<(Guid Id, string Code, string Name)>> SpecimenTypesAsync()
    {
        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<SpecimenTypeRow>(
            "SELECT id, code, name FROM specimen_type ORDER BY sort_order");
        return rows.Select(row => (row.Id, row.Code, row.Name)).ToList();
    }

    private static async Task<IReadOnlyDictionary<Guid, List<Guid>>> LoadChildrenByParentAsync(
        NpgsqlConnection connection, IReadOnlyCollection<Guid> parentIds)
    {
        if (parentIds.Count == 0)
        {
            return new Dictionary<Guid, List<Guid>>();
        }

        var rows = await connection.QueryAsync<ChildRow>(
            "SELECT parent_id, child_id FROM service_child WHERE parent_id = ANY(@parentIds) ORDER BY parent_id, sort_order",
            new { parentIds = parentIds.ToArray() });

        var result = new Dictionary<Guid, List<Guid>>();
        foreach (var row in rows)
        {
            if (!result.TryGetValue(row.ParentId, out var list))
            {
                list = [];
                result[row.ParentId] = list;
            }

            list.Add(row.ChildId);
        }

        return result;
    }

    private sealed class ServiceRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? GroupName { get; init; }
        public Guid? SpecimenTypeId { get; init; }
        public string? Unit { get; init; }
        public string ValueType { get; init; } = "";
        public int? Decimals { get; init; }
        public int? PrintOrder { get; init; }
        public bool IsPanel { get; init; }
        public bool AutoApprove { get; init; }
        public bool Active { get; init; }
        public string? HisCode { get; init; }

        public ServiceInfo ToServiceInfo(IReadOnlyDictionary<Guid, List<Guid>> childrenByParent)
        {
            IReadOnlyList<Guid> childIds = childrenByParent.TryGetValue(Id, out var list) ? list : [];
            return new ServiceInfo(
                Id, Code, Name, GroupName, SpecimenTypeId ?? Guid.Empty, Unit, ValueType, Decimals,
                PrintOrder ?? 0, IsPanel, AutoApprove, Active, HisCode, childIds);
        }
    }

    private sealed record ChildRow
    {
        public Guid ParentId { get; init; }
        public Guid ChildId { get; init; }
    }

    private sealed class RangeRow
    {
        public Guid Id { get; init; }
        public Guid ServiceId { get; init; }
        public string? Sex { get; init; }
        public int? AgeFromDays { get; init; }
        public int? AgeToDays { get; init; }
        public Guid? InstrumentId { get; init; }
        public decimal? Low { get; init; }
        public decimal? High { get; init; }
        public decimal? CriticalLow { get; init; }
        public decimal? CriticalHigh { get; init; }
        public string? TextRange { get; init; }

        public ReferenceRange ToReferenceRange() => new(
            Id, ServiceId, Sex is { Length: > 0 } ? Sex[0] : null, AgeFromDays, AgeToDays, InstrumentId,
            Low, High, CriticalLow, CriticalHigh, TextRange);
    }

    private sealed class QualitativeRuleRow
    {
        public Guid? InstrumentId { get; init; }
        public Guid ServiceId { get; init; }
        public string RawValue { get; init; } = "";
        public string DisplayValue { get; init; } = "";
        public string? Flag { get; init; }

        public QualitativeRule ToRule() => new(
            InstrumentId, ServiceId, RawValue, DisplayValue,
            Flag is null ? null : Enum.Parse<ResultFlag>(Flag));
    }

    private sealed class SpecimenTypeRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
    }
}
