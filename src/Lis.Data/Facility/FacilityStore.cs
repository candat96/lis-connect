using Dapper;

namespace Lis.Data.Facility;

/// <summary>
/// Thông tin cơ sở (bảng <c>facility</c>, luôn một dòng duy nhất) — dùng cho phiếu in và
/// quy tắc sinh barcode mặc định.
/// </summary>
public record FacilityInfo(
    string Name,
    string? Address,
    string? Phone,
    byte[]? Logo,
    string BarcodePrefix,
    string BarcodeDatePattern,
    int BarcodeSeqDigits,
    string BarcodeMode);

/// <summary>
/// Phần đọc của <see cref="FacilityStore"/> cần cho phiếu in (Task 12) — tách interface để
/// <see cref="Lis.App.Services.ReportPrinter"/> kiểm thử được bằng bản giả, không cần CSDL.
/// </summary>
public interface IFacilityStore
{
    Task<FacilityInfo?> GetAsync();

    Task SaveAsync(FacilityInfo facility);
}

/// <summary>
/// Truy cập thông tin cơ sở (bảng <c>facility</c>, id luôn là 1).
/// </summary>
public sealed class FacilityStore(Db db) : IFacilityStore

{
    public async Task<FacilityInfo?> GetAsync()
    {
        await using var connection = await db.OpenAsync();
        var row = await connection.QuerySingleOrDefaultAsync<FacilityRow>(
            """
            SELECT name, address, phone, logo, barcode_prefix, barcode_date_pattern, barcode_seq_digits, barcode_mode
            FROM facility
            WHERE id = 1
            """);

        return row?.ToFacilityInfo();
    }

    public async Task SaveAsync(FacilityInfo facility)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO facility (id, name, address, phone, logo, barcode_prefix, barcode_date_pattern, barcode_seq_digits, barcode_mode)
            VALUES (1, @Name, @Address, @Phone, @Logo, @BarcodePrefix, @BarcodeDatePattern, @BarcodeSeqDigits, @BarcodeMode)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                address = EXCLUDED.address,
                phone = EXCLUDED.phone,
                logo = EXCLUDED.logo,
                barcode_prefix = EXCLUDED.barcode_prefix,
                barcode_date_pattern = EXCLUDED.barcode_date_pattern,
                barcode_seq_digits = EXCLUDED.barcode_seq_digits,
                barcode_mode = EXCLUDED.barcode_mode
            """,
            facility);
    }

    private sealed record FacilityRow
    {
        public string Name { get; init; } = "";
        public string? Address { get; init; }
        public string? Phone { get; init; }
        public byte[]? Logo { get; init; }
        public string BarcodePrefix { get; init; } = "";
        public string BarcodeDatePattern { get; init; } = "";
        public int BarcodeSeqDigits { get; init; }
        public string BarcodeMode { get; init; } = "";

        public FacilityInfo ToFacilityInfo() =>
            new(Name, Address, Phone, Logo, BarcodePrefix, BarcodeDatePattern, BarcodeSeqDigits, BarcodeMode);
    }
}
