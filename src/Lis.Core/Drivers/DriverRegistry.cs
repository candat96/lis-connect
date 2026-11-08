using System.Text.Json;
using Lis.Core.Drivers.Astm;
using Lis.Core.Drivers.BeckmanAu;
using Lis.Core.Drivers.Hl7;

namespace Lis.Core.Drivers;

/// <summary>
/// Tạo driver theo họ giao thức của máy (<c>instrument.protocol_family</c>), kèm cấu hình riêng
/// (<c>instrument.driver_config</c>). Họ chưa có driver → null (máy vẫn khai báo được, chỉ chưa tự
/// nhận kết quả).
/// </summary>
/// <remarks>
/// <c>driver_config</c> của máy Beckman AU là JSON các tuỳ chọn <see cref="BeckmanAuOptions"/>
/// (vd <c>{"classB":true,"testNoDigits":3,"sampleIdLength":13}</c>) — phải khớp màn Online trên
/// máy, khoá nào không ghi thì lấy mặc định nhà máy AU480. Họ ASTM chọn bộ cài đặt sẵn theo
/// model (Access → <see cref="AstmDriverOptions.Access2"/>).
/// </remarks>
public static class DriverRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyCollection<string> SupportedFamilies { get; } = ["hl7-mllp", "astm-e1394", "beckman-au"];

    /// <summary>Tạo driver. Cấu hình driver JSON sai → <see cref="ArgumentException"/>.</summary>
    public static IInstrumentDriver? Create(string? family, string? model = null, string? driverConfigJson = null)
    {
        switch (family?.Trim().ToLowerInvariant())
        {
            case "hl7-mllp":
                return new Hl7MllpDriver();

            case "astm-e1394":
                return new AstmDriver(model?.Contains("Access", StringComparison.OrdinalIgnoreCase) == true
                    ? AstmDriverOptions.Access2
                    : AstmDriverOptions.Generic);

            case "beckman-au":
                return new BeckmanAuDriver(ParseOptions<BeckmanAuOptions>(driverConfigJson) ?? BeckmanAuOptions.Au480);

            default:
                return null;
        }
    }

    private static T? ParseOptions<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() is "{}" or "null")
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Cấu hình driver không đúng: {ex.Message}", ex);
        }
    }
}
