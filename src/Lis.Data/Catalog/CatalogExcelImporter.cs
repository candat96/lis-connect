using ClosedXML.Excel;
using Lis.Core.Results;
using Lis.Data.Instruments;

namespace Lis.Data.Catalog;

/// <summary>
/// Kết quả nhập danh mục từ Excel: số dòng được tạo/cập nhật và danh sách lỗi (định dạng
/// "Sheet X dòng Y: mô tả lỗi").
/// </summary>
public record ImportReport(int Created, int Updated, IReadOnlyList<string> Errors);

/// <summary>
/// Nhập danh mục dịch vụ, chỉ số con, khoảng tham chiếu và ánh xạ máy từ workbook Excel.
/// Toàn bộ nhập trong một giao dịch: lỗi nào thì rollback hết.
/// </summary>
public sealed class CatalogExcelImporter(Db db)
{
    private readonly Db _db = db;
    private readonly List<string> _errors = [];
    private int _created = 0;
    private int _updated = 0;

    /// <summary>
    /// Nhập dữ liệu từ workbook. Nếu <paramref name="dryRun"/> là true, kiểm tra nhưng không ghi.
    /// </summary>
    public async Task<ImportReport> ImportAsync(Stream xlsx, bool dryRun)
    {
        _errors.Clear();
        _created = 0;
        _updated = 0;

        try
        {
            var workbook = new XLWorkbook(xlsx);

            if (dryRun)
            {
                // Dry run: kiểm tra nhưng không ghi
                await ImportSheetsAsync(workbook, dryRun: true);
            }
            else
            {
                // Ghi thực: dùng transaction
                var result = await _db.InTransactionAsync(async (conn, tx) =>
                {
                    await ImportSheetsAsync(workbook, dryRun: false);
                    return !_errors.Any();
                });

                if (!result)
                {
                    // Rollback happened, reset counts
                    _created = 0;
                    _updated = 0;
                }
            }
        }
        catch (Exception ex)
        {
            _errors.Add($"Lỗi đọc workbook: {ex.Message}");
            _created = 0;
            _updated = 0;
        }

        return new ImportReport(_created, _updated, _errors.AsReadOnly());
    }

    private async Task ImportSheetsAsync(XLWorkbook workbook, bool dryRun)
    {
        var catalogStore = new CatalogStore(_db);
        var instrumentStore = new InstrumentStore(_db);

        // Lấy danh sách loại mẫu — LoaiMau trong sheet DichVu chấp nhận mã loại mẫu
        // (vd "EDTA", xem specimen_type.code) để tệp mẫu dùng được trên mọi CSDL (id là
        // uuidv7 sinh ngẫu nhiên lúc migrate, không cố định); vẫn chấp nhận GUID trực tiếp
        // để không phá các workbook cũ đã dùng id.
        var specimenTypes = await catalogStore.SpecimenTypesAsync();
        var specimenTypeMap = specimenTypes.ToDictionary(x => x.Code, x => x.Id, StringComparer.OrdinalIgnoreCase);

        // Import DichVu sheet
        var serviceMap = await ImportDichVuAsync(workbook, catalogStore, specimenTypeMap, dryRun);
        if (_errors.Any())
            return;

        // Import ChiSoCon sheet (service children)
        await ImportChiSoConAsync(workbook, catalogStore, serviceMap, dryRun);
        if (_errors.Any())
            return;

        // Import KhoangThamChieu sheet (reference ranges)
        await ImportKhoangThamChieuAsync(workbook, catalogStore, serviceMap, dryRun);
        if (_errors.Any())
            return;

        // Import MapMay sheet (instrument channels)
        await ImportMapMayAsync(workbook, instrumentStore, serviceMap, dryRun);
    }

    private async Task<Dictionary<string, Guid>> ImportDichVuAsync(
        XLWorkbook workbook,
        CatalogStore catalogStore,
        IReadOnlyDictionary<string, Guid> specimenTypeMap,
        bool dryRun)
    {
        var serviceMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        if (!workbook.Worksheets.TryGetWorksheet("DichVu", out var sheet))
            return serviceMap;

        var rowNum = 1;
        foreach (var row in sheet.Rows().Skip(1))
        {
            rowNum++;
            try
            {
                var code = GetCellValue(row, 1)?.Trim();
                var name = GetCellValue(row, 2)?.Trim();
                var groupName = GetCellValue(row, 3)?.Trim();
                var specimenTypeIdStr = GetCellValue(row, 4)?.Trim();
                var unit = GetCellValue(row, 5)?.Trim();
                var valueTypeStr = GetCellValue(row, 6)?.Trim();
                var decimalsStr = GetCellValue(row, 7)?.Trim();
                var printOrderStr = GetCellValue(row, 8)?.Trim();
                var isPanelStr = GetCellValue(row, 9)?.Trim();
                var autoApproveStr = GetCellValue(row, 10)?.Trim();
                var hisCode = GetCellValue(row, 11)?.Trim();

                if (string.IsNullOrEmpty(code))
                    throw new InvalidOperationException("thiếu Mã");

                if (string.IsNullOrEmpty(name))
                    throw new InvalidOperationException("thiếu Tên");

                if (string.IsNullOrEmpty(specimenTypeIdStr))
                    throw new InvalidOperationException("LoaiMau không hợp lệ");

                if (!Guid.TryParse(specimenTypeIdStr, out var specimenTypeId)
                    && !specimenTypeMap.TryGetValue(specimenTypeIdStr, out specimenTypeId))
                    throw new InvalidOperationException("LoaiMau không hợp lệ");

                if (string.IsNullOrEmpty(valueTypeStr) || !IsValidValueType(valueTypeStr))
                    throw new InvalidOperationException("KieuKetQua không hợp lệ");

                var decimals = string.IsNullOrEmpty(decimalsStr) ? null : (int?)int.Parse(decimalsStr);
                var printOrder = string.IsNullOrEmpty(printOrderStr) ? 0 : int.Parse(printOrderStr);
                var isPanel = string.IsNullOrEmpty(isPanelStr) ? false : bool.Parse(isPanelStr);
                var autoApprove = string.IsNullOrEmpty(autoApproveStr) ? false : bool.Parse(autoApproveStr);

                var service = new ServiceInfo(
                    Guid.Empty,
                    code,
                    name,
                    groupName,
                    specimenTypeId,
                    unit,
                    valueTypeStr,
                    decimals,
                    printOrder,
                    isPanel,
                    autoApprove,
                    true,
                    hisCode,
                    []
                );

                if (!dryRun)
                {
                    var serviceId = await catalogStore.SaveServiceAsync(service);
                    serviceMap[code] = serviceId;
                }
                else
                {
                    // Dry run: generate a temporary ID for validation
                    serviceMap[code] = Guid.NewGuid();
                }
                _created++;
            }
            catch (Exception ex)
            {
                _errors.Add($"Sheet DichVu dòng {rowNum}: {ex.Message}");
            }

            if (_errors.Any())
                break;
        }

        return serviceMap;
    }

    private async Task ImportChiSoConAsync(
        XLWorkbook workbook,
        CatalogStore catalogStore,
        Dictionary<string, Guid> serviceMap,
        bool dryRun)
    {
        if (!workbook.Worksheets.TryGetWorksheet("ChiSoCon", out var sheet))
            return;

        var rowNum = 1;
        foreach (var row in sheet.Rows().Skip(1))
        {
            rowNum++;
            try
            {
                var parentCode = GetCellValue(row, 1)?.Trim();
                var childCode = GetCellValue(row, 2)?.Trim();

                if (string.IsNullOrEmpty(parentCode) || !serviceMap.TryGetValue(parentCode, out var parentId))
                    throw new InvalidOperationException($"MaCha '{parentCode}' không tìm thấy");

                if (string.IsNullOrEmpty(childCode) || !serviceMap.TryGetValue(childCode, out var childId))
                    throw new InvalidOperationException($"MaCon '{childCode}' không tìm thấy");

                if (!dryRun)
                {
                    await catalogStore.SetChildrenAsync(parentId, [childId]);
                }
                _updated++;
            }
            catch (Exception ex)
            {
                _errors.Add($"Sheet ChiSoCon dòng {rowNum}: {ex.Message}");
            }

            if (_errors.Any())
                break;
        }
    }

    private async Task ImportKhoangThamChieuAsync(
        XLWorkbook workbook,
        CatalogStore catalogStore,
        Dictionary<string, Guid> serviceMap,
        bool dryRun)
    {
        if (!workbook.Worksheets.TryGetWorksheet("KhoangThamChieu", out var sheet))
            return;

        var rowNum = 1;
        var rangesByService = new Dictionary<Guid, List<ReferenceRange>>();

        foreach (var row in sheet.Rows().Skip(1))
        {
            rowNum++;
            try
            {
                var code = GetCellValue(row, 1)?.Trim();
                var sex = GetCellValue(row, 2)?.Trim();
                var ageFromStr = GetCellValue(row, 3)?.Trim();
                var ageToStr = GetCellValue(row, 4)?.Trim();
                var ageUnit = GetCellValue(row, 5)?.Trim();
                var lowStr = GetCellValue(row, 6)?.Trim();
                var highStr = GetCellValue(row, 7)?.Trim();
                var criticalLowStr = GetCellValue(row, 8)?.Trim();
                var criticalHighStr = GetCellValue(row, 9)?.Trim();
                var textRange = GetCellValue(row, 10)?.Trim();

                if (string.IsNullOrEmpty(code) || !serviceMap.TryGetValue(code, out var serviceId))
                    continue;

                int? ageFromDays = null;
                int? ageToDays = null;

                if (!string.IsNullOrEmpty(ageFromStr) && int.TryParse(ageFromStr, out var ageFrom))
                {
                    ageFromDays = ConvertAgeToDays(ageFrom, ageUnit ?? "nam");
                }

                if (!string.IsNullOrEmpty(ageToStr) && int.TryParse(ageToStr, out var ageTo))
                {
                    ageToDays = ConvertAgeToDays(ageTo, ageUnit ?? "nam");
                }

                decimal? low = string.IsNullOrEmpty(lowStr) ? null : decimal.Parse(lowStr);
                decimal? high = string.IsNullOrEmpty(highStr) ? null : decimal.Parse(highStr);
                decimal? criticalLow = string.IsNullOrEmpty(criticalLowStr) ? null : decimal.Parse(criticalLowStr);
                decimal? criticalHigh = string.IsNullOrEmpty(criticalHighStr) ? null : decimal.Parse(criticalHighStr);

                var range = new ReferenceRange(
                    Guid.Empty,
                    serviceId,
                    string.IsNullOrEmpty(sex) ? null : sex[0],
                    ageFromDays,
                    ageToDays,
                    null,
                    low,
                    high,
                    criticalLow,
                    criticalHigh,
                    textRange
                );

                if (!rangesByService.ContainsKey(serviceId))
                    rangesByService[serviceId] = [];

                rangesByService[serviceId].Add(range);
            }
            catch (Exception ex)
            {
                _errors.Add($"Sheet KhoangThamChieu dòng {rowNum}: {ex.Message}");
            }

            if (_errors.Any())
                break;
        }

        if (!dryRun)
        {
            foreach (var (serviceId, ranges) in rangesByService)
            {
                await catalogStore.SaveRangesAsync(serviceId, ranges);
                _updated++;
            }
        }
        else if (rangesByService.Any())
        {
            _updated += rangesByService.Count;
        }
    }

    private async Task ImportMapMayAsync(
        XLWorkbook workbook,
        InstrumentStore instrumentStore,
        Dictionary<string, Guid> serviceMap,
        bool dryRun)
    {
        if (!workbook.Worksheets.TryGetWorksheet("MapMay", out var sheet))
            return;

        var rowNum = 1;
        var instrumentMap = new Dictionary<string, Guid>();

        foreach (var row in sheet.Rows().Skip(1))
        {
            rowNum++;
            try
            {
                var instrumentName = GetCellValue(row, 1)?.Trim();
                var channelCode = GetCellValue(row, 2)?.Trim();
                var serviceCode = GetCellValue(row, 3)?.Trim();
                var decimalsStr = GetCellValue(row, 4)?.Trim();
                var factorStr = GetCellValue(row, 5)?.Trim();
                var keepComparatorStr = GetCellValue(row, 6)?.Trim();
                var sendOnQueryStr = GetCellValue(row, 7)?.Trim();

                if (string.IsNullOrEmpty(instrumentName))
                    throw new InvalidOperationException("thiếu TenMay");

                if (string.IsNullOrEmpty(channelCode))
                    throw new InvalidOperationException("thiếu MaKenh");

                if (string.IsNullOrEmpty(serviceCode) || !serviceMap.TryGetValue(serviceCode, out var serviceId))
                    throw new InvalidOperationException("MaChiSo không tìm thấy");

                // Lấy hoặc tạo máy
                Guid instrumentId;
                if (!instrumentMap.TryGetValue(instrumentName, out instrumentId))
                {
                    var instrument = new InstrumentInfo(Guid.Empty, instrumentName, null, null, null, null, null, true);
                    if (!dryRun)
                    {
                        instrumentId = await instrumentStore.SaveAsync(instrument);
                    }
                    else
                    {
                        instrumentId = Guid.NewGuid();
                    }
                    instrumentMap[instrumentName] = instrumentId;
                }

                int? decimals = string.IsNullOrEmpty(decimalsStr) ? null : (int?)int.Parse(decimalsStr);
                decimal? factor = string.IsNullOrEmpty(factorStr) ? null : (decimal?)decimal.Parse(factorStr);
                bool keepComparator = string.IsNullOrEmpty(keepComparatorStr) ? false : bool.Parse(keepComparatorStr);
                bool sendOnQuery = string.IsNullOrEmpty(sendOnQueryStr) ? false : bool.Parse(sendOnQueryStr);

                var channel = new ChannelMap(Guid.Empty, instrumentId, channelCode, serviceId, decimals, factor, keepComparator, sendOnQuery);
                if (!dryRun)
                {
                    await instrumentStore.SaveChannelAsync(channel);
                }
                _created++;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("channel already mapped") || ex.Message.Contains("Kênh"))
            {
                _errors.Add($"Sheet MapMay dòng {rowNum}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _errors.Add($"Sheet MapMay dòng {rowNum}: {ex.Message}");
            }

            if (_errors.Any())
                break;
        }
    }

    private static string? GetCellValue(IXLRow row, int columnIndex)
    {
        var lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
        if (columnIndex < 1 || columnIndex > lastCol)
            return null;

        var cell = row.Cell(columnIndex);
        if (cell.IsEmpty())
            return null;

        return cell.GetString()?.Trim();
    }

    private static bool IsValidValueType(string valueType) =>
        valueType == "so" || valueType == "dinh-tinh" || valueType == "chu";

    private static int ConvertAgeToDays(int age, string unit) =>
        unit switch
        {
            "nam" => age * 365,
            "thang" => age * 30,
            "ngay" => age,
            _ => age,
        };
}
