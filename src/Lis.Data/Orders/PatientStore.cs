using Dapper;
using Npgsql;

namespace Lis.Data.Orders;

/// <summary>Người bệnh mới, nhập tại màn Tiếp nhận (chưa có mã, mã sinh lúc tạo chỉ định).</summary>
public sealed record NewPatient(string FullName, DateOnly? Dob, int? BirthYear, char Sex, string? Address, string? Phone);

/// <summary>Một dòng gợi ý khi tìm người bệnh theo mã, họ tên hoặc số điện thoại.</summary>
public sealed record PatientHit(Guid Id, string Code, string FullName, DateOnly? Dob, int? BirthYear, char Sex, string? Phone);

/// <summary>
/// Truy cập dữ liệu người bệnh (bảng <c>patient</c>): thêm người bệnh mới (trong giao dịch
/// tạo chỉ định — xem <see cref="OrderService"/>) và tìm người bệnh theo mã/họ tên/số điện
/// thoại.
/// </summary>
public sealed class PatientStore(Db db)
{
    /// <summary>
    /// Thêm người bệnh mới vào CSDL, dùng kết nối/giao dịch đang mở của
    /// <see cref="OrderService.CreateAsync"/> — không mở kết nối riêng, để việc tạo người
    /// bệnh và chỉ định cùng nằm trong một giao dịch.
    /// </summary>
    public async Task<Guid> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, NewPatient patient)
    {
        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO patient (code, full_name, dob, birth_year, sex, address, phone)
            VALUES (@code, @fullName, @dob, @birthYear, @sex, @address, @phone)
            RETURNING id
            """,
            new
            {
                code,
                fullName = patient.FullName,
                dob = patient.Dob,
                birthYear = patient.BirthYear,
                sex = patient.Sex.ToString(),
                address = patient.Address,
                phone = patient.Phone,
            },
            transaction);
    }

    /// <summary>
    /// Tìm tối đa 20 người bệnh theo mã, họ tên (không dấu, không phân biệt hoa thường — dùng
    /// <c>unaccent</c>, xem <c>0002_unaccent.sql</c>) hoặc số điện thoại chứa
    /// <paramref name="query"/>.
    /// </summary>
    public async Task<IReadOnlyList<PatientHit>> FindAsync(string query)
    {
        await using var connection = await db.OpenAsync();

        var pattern = $"%{query.Trim()}%";
        var rows = await connection.QueryAsync<PatientRow>(
            """
            SELECT id, code, full_name, dob, birth_year, sex, phone
            FROM patient
            WHERE lower(code) LIKE lower(@pattern)
               OR unaccent(lower(full_name)) LIKE unaccent(lower(@pattern))
               OR phone LIKE @pattern
            ORDER BY full_name
            LIMIT 20
            """,
            new { pattern });

        return rows.Select(row => row.ToHit()).ToList();
    }

    private sealed record PatientRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string FullName { get; init; } = "";
        public DateOnly? Dob { get; init; }
        public int? BirthYear { get; init; }
        public string Sex { get; init; } = "";
        public string? Phone { get; init; }

        public PatientHit ToHit() => new(Id, Code, FullName, Dob, BirthYear, Sex[0], Phone);
    }
}
