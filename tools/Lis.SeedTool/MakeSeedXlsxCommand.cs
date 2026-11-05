using ClosedXML.Excel;

namespace Lis.SeedTool;

/// <summary>Một dòng của sheet DichVu.</summary>
internal sealed record SeedService(
    string Code, string Name, string? Group, string SpecimenCode, string? Unit, string ValueType,
    int PrintOrder, bool IsPanel, bool AutoApprove, string? HisCode = null);

/// <summary>Một dòng của sheet MapMay.</summary>
internal sealed record SeedChannel(string Instrument, string Channel, string ServiceCode);

/// <summary>Một dòng của sheet KhoangThamChieu.</summary>
internal sealed record SeedRange(
    string Code, char? Sex, int? AgeFrom, int? AgeTo, string? AgeUnit, decimal? Low, decimal? High);

/// <summary>
/// Lệnh "make-seed-xlsx": dựng <c>tools/seed/mau-danh-muc.xlsx</c> theo đúng khuôn mà
/// <c>CatalogExcelImporter</c> đọc (xem <c>src/Lis.Data/Catalog/CatalogExcelImporter.cs</c>).
/// </summary>
/// <remarks>
/// Nguồn dữ liệu theo brief là <c>~/Downloads/lis-tham-khao/lis-server/seed/danhmuc-dichvu.sql</c>
/// và <c>khao-sat/seed/anh-xa-ma-may.json</c>. Hai tệp này không đọc được trong môi trường chạy
/// công cụ này (thư mục <c>~/Downloads</c> bị hệ điều hành chặn quyền đọc cho tiến trình này —
/// EPERM). Danh mục dưới đây là tập rút gọn, dựng lại từ tài liệu đã có trong repo
/// (<c>docs/drivers/cac-may-astm-bvdk10.md</c> mục 6 "Bảng mã chỉ số") để tệp mẫu vẫn nhập được
/// và minh họa đúng luật bỏ map trùng/sai. Khi có lại quyền đọc hai tệp gốc, nên chạy lại lệnh
/// này sau khi nạp đủ dữ liệu từ đó.
/// </remarks>
internal static class MakeSeedXlsxCommand
{
    private const string Edta = "EDTA";
    private const string Serum = "SERUM";

    public static int Run(string[] args)
    {
        var outPath = "tools/seed/mau-danh-muc.xlsx";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
            {
                outPath = args[++i];
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var (services, children) = BuildServices();
        var ranges = BuildRanges();
        var (channels, droppedNotes) = BuildChannels(services);

        var workbook = new XLWorkbook();
        WriteDichVu(workbook, services);
        WriteChiSoCon(workbook, children);
        WriteKhoangThamChieu(workbook, ranges);
        WriteMapMay(workbook, channels);
        WriteGhiChu(workbook, droppedNotes);

        workbook.SaveAs(outPath);
        Console.WriteLine($"Đã ghi {outPath} ({services.Count} dịch vụ, {channels.Count} map máy, {droppedNotes.Count} dòng bỏ).");
        return 0;
    }

    private static (List<SeedService> Services, List<(string Parent, string Child, int Order)> Children) BuildServices()
    {
        var services = new List<SeedService>
        {
            // Panel huyết học (Cell-Dyn Ruby, BC-3200) — EDTA.
            new("TPTTBM", "Tổng phân tích tế bào máu", "Huyết học", Edta, null, "so", 0, IsPanel: true, AutoApprove: false),
            new("WBC", "Bạch cầu", "Huyết học", Edta, "10^9/L", "so", 1, false, false),
            new("RBC", "Hồng cầu", "Huyết học", Edta, "10^12/L", "so", 2, false, false),
            new("HGB", "Huyết sắc tố", "Huyết học", Edta, "g/L", "so", 3, false, false),
            new("HCT", "Hematocrit", "Huyết học", Edta, "%", "so", 4, false, false),
            new("MCV", "Thể tích trung bình hồng cầu", "Huyết học", Edta, "fL", "so", 5, false, false),
            new("MCH", "Lượng Hb trung bình hồng cầu", "Huyết học", Edta, "pg", "so", 6, false, false),
            new("MCHC", "Nồng độ Hb trung bình hồng cầu", "Huyết học", Edta, "g/L", "so", 7, false, false),
            new("PLT", "Tiểu cầu", "Huyết học", Edta, "10^9/L", "so", 8, false, false),
            new("NEU", "Bạch cầu trung tính", "Huyết học", Edta, "10^9/L", "so", 9, false, false),
            new("LYM", "Bạch cầu lympho", "Huyết học", Edta, "10^9/L", "so", 10, false, false),
            new("MONO", "Bạch cầu mono", "Huyết học", Edta, "10^9/L", "so", 11, false, false),
            new("EOS", "Bạch cầu eosin", "Huyết học", Edta, "10^9/L", "so", 12, false, false),
            new("BASO", "Bạch cầu basophil", "Huyết học", Edta, "10^9/L", "so", 13, false, false),
            new("A1C", "HbA1c", "Huyết học", Edta, "%", "so", 14, false, false),

            // Miễn dịch (e2010, COBAS 8000) — SERUM.
            new("TSH", "TSH", "Miễn dịch", Serum, "uIU/mL", "so", 20, false, false),
            new("FT3", "FT3", "Miễn dịch", Serum, "pmol/L", "so", 21, false, false),
            new("FT4", "FT4", "Miễn dịch", Serum, "pmol/L", "so", 22, false, false),
            new("AFP", "Alpha-fetoprotein", "Miễn dịch", Serum, "ng/mL", "so", 23, false, false),
            new("CEA", "CEA", "Miễn dịch", Serum, "ng/mL", "so", 24, false, false),
            new("CA125", "CA 125", "Miễn dịch", Serum, "U/mL", "so", 25, false, false),
            new("CA153", "CA 15-3", "Miễn dịch", Serum, "U/mL", "so", 26, false, false),
            new("CA199", "CA 19-9", "Miễn dịch", Serum, "U/mL", "so", 27, false, false),
            new("CA724", "CA 72-4", "Miễn dịch", Serum, "U/mL", "so", 28, false, false),
            new("FERRITIN", "Ferritin", "Miễn dịch", Serum, "ng/mL", "so", 29, false, false),
            new("PSA", "PSA toàn phần", "Miễn dịch", Serum, "ng/mL", "so", 30, false, false),
            new("BETAHCG", "Beta-hCG", "Miễn dịch", Serum, "mIU/mL", "so", 31, false, false),
            new("TROPT", "Troponin T-hs", "Miễn dịch", Serum, "ng/L", "so", 32, false, false),
            new("PCT", "Procalcitonin", "Miễn dịch", Serum, "ng/mL", "so", 33, false, false),
            new("ANTIHBS", "Anti-HBs", "Miễn dịch", Serum, "mIU/mL", "so", 34, false, false),
            new("HBEAG", "HBeAg", "Miễn dịch", Serum, "COI", "so", 35, false, false),
            new("HBSAG", "HBsAg", "Miễn dịch", Serum, "COI", "so", 36, false, false),

            // Sinh hóa (CHEM7, EBA200, BIOLIS50) — SERUM.
            new("GLU", "Glucose", "Sinh hóa", Serum, "mmol/L", "so", 40, false, false),
            new("URE", "Ure", "Sinh hóa", Serum, "mmol/L", "so", 41, false, false),
            new("CRE", "Creatinin", "Sinh hóa", Serum, "umol/L", "so", 42, false, false),
            new("AST", "AST (SGOT)", "Sinh hóa", Serum, "U/L", "so", 43, false, false),
            new("ALT", "ALT (SGPT)", "Sinh hóa", Serum, "U/L", "so", 44, false, false),
            new("CHO", "Cholesterol toàn phần", "Sinh hóa", Serum, "mmol/L", "so", 45, false, false),
            new("TRI", "Triglycerid", "Sinh hóa", Serum, "mmol/L", "so", 46, false, false),
            new("HDL", "HDL-Cholesterol", "Sinh hóa", Serum, "mmol/L", "so", 47, false, false),
            new("GGT", "GGT", "Sinh hóa", Serum, "U/L", "so", 48, false, false),
            new("CRP", "CRP", "Sinh hóa", Serum, "mg/L", "so", 49, false, false),
            new("UA", "Acid uric", "Sinh hóa", Serum, "umol/L", "so", 50, false, false),
            new("ALB", "Albumin", "Sinh hóa", Serum, "g/L", "so", 51, false, false),

            // Điện giải (ST200, ST200PRO) — SERUM.
            new("NA", "Natri", "Điện giải", Serum, "mmol/L", "so", 60, false, false),
            new("K", "Kali", "Điện giải", Serum, "mmol/L", "so", 61, false, false),
            new("CL", "Clo", "Điện giải", Serum, "mmol/L", "so", 62, false, false),
            new("ICA", "Calci ion hóa", "Điện giải", Serum, "mmol/L", "so", 63, false, false),
        };

        var children = new List<(string Parent, string Child, int Order)>
        {
            ("TPTTBM", "WBC", 1), ("TPTTBM", "RBC", 2), ("TPTTBM", "HGB", 3), ("TPTTBM", "HCT", 4),
            ("TPTTBM", "MCV", 5), ("TPTTBM", "MCH", 6), ("TPTTBM", "MCHC", 7), ("TPTTBM", "PLT", 8),
            ("TPTTBM", "NEU", 9), ("TPTTBM", "LYM", 10), ("TPTTBM", "MONO", 11), ("TPTTBM", "EOS", 12),
            ("TPTTBM", "BASO", 13),
        };

        return (services, children);
    }

    private static List<SeedRange> BuildRanges() =>
    [
        new("WBC", null, 18, 100, "nam", 4.0m, 10.0m),
        new("RBC", 'M', 18, 100, "nam", 4.2m, 5.9m),
        new("RBC", 'F', 18, 100, "nam", 3.8m, 5.2m),
        new("HGB", 'M', 18, 100, "nam", 130m, 170m),
        new("HGB", 'F', 18, 100, "nam", 120m, 150m),
        new("PLT", null, 18, 100, "nam", 150m, 400m),
        new("GLU", null, 0, 120, "nam", 3.9m, 6.4m),
        new("CRE", 'M', 18, 100, "nam", 62m, 106m),
        new("CRE", 'F', 18, 100, "nam", 44m, 80m),
        new("TSH", null, 18, 100, "nam", 0.27m, 4.2m),
        new("NA", null, 0, 120, "nam", 135m, 145m),
        new("K", null, 0, 120, "nam", 3.5m, 5.1m),
    ];

    /// <summary>
    /// Map máy theo mục 6 <c>docs/drivers/cac-may-astm-bvdk10.md</c>, chỉ giữ các mã đã có dịch
    /// vụ ở <see cref="BuildServices"/>. Bỏ: (a) mọi (máy, kênh) trỏ tới nhiều dịch vụ — mơ hồ,
    /// (b) các dòng "sai hiển nhiên" liệt kê ở mục 8.3 của tài liệu trên.
    /// </summary>
    private static (List<SeedChannel> Kept, List<string> Dropped) BuildChannels(List<SeedService> services)
    {
        var validCodes = services.Select(s => s.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<SeedChannel>
        {
            // Cell-Dyn Ruby (InsID 16/17) — huyết học 5 thành phần, đơn vị K/µL, g/dL (xem mục 8.8:
            // đơn vị khác BC-3200/CON500 → không gộp, nên BC-3200/CON500 không map ở đây).
            new("Cell-Dyn Ruby", "WBC", "WBC"), new("Cell-Dyn Ruby", "RBC", "RBC"),
            new("Cell-Dyn Ruby", "HGB", "HGB"), new("Cell-Dyn Ruby", "HCT", "HCT"),
            new("Cell-Dyn Ruby", "MCV", "MCV"), new("Cell-Dyn Ruby", "MCH", "MCH"),
            new("Cell-Dyn Ruby", "MCHC", "MCHC"), new("Cell-Dyn Ruby", "PLT", "PLT"),
            new("Cell-Dyn Ruby", "NEU", "NEU"), new("Cell-Dyn Ruby", "LYM", "LYM"),
            new("Cell-Dyn Ruby", "MONO", "MONO"), new("Cell-Dyn Ruby", "EOS", "EOS"),
            new("Cell-Dyn Ruby", "BASO", "BASO"),

            // HLC-723GX (InsID 5).
            new("HLC-723GX", "A1C", "A1C"),

            // e2010 (InsID 2) — mã "3" (Cortisol sáng/chiều, trùng) và "200"→"hc12" (dòng rác)
            // CHỦ Ý không đưa vào candidates — xem BuildAmbiguousAndWrongNotes.
            new("e2010", "9", "TSH"), new("e2010", "15", "FT4"), new("e2010", "411", "FT3"),
            new("e2010", "19", "AFP"), new("e2010", "301", "CEA"), new("e2010", "100", "CA125"),
            new("e2010", "332", "CA153"), new("e2010", "351", "CA199"), new("e2010", "360", "CA724"),
            new("e2010", "381", "FERRITIN"), new("e2010", "11", "PSA"), new("e2010", "761", "BETAHCG"),
            new("e2010", "222", "TROPT"), new("e2010", "510", "PCT"), new("e2010", "410", "ANTIHBS"),
            new("e2010", "440", "HBEAG"), new("e2010", "13", "HBSAG"),

            // COBAS 8000 (InsID 19) — mã "89" (Cortisol, trùng) không đưa vào.
            new("COBAS 8000", "627", "TSH"), new("COBAS 8000", "302", "FT4"), new("COBAS 8000", "813", "FT3"),
            new("COBAS 8000", "134", "AFP"), new("COBAS 8000", "49", "CEA"), new("COBAS 8000", "53", "CA125"),
            new("COBAS 8000", "52", "CA153"), new("COBAS 8000", "54", "CA199"), new("COBAS 8000", "307", "CA724"),
            new("COBAS 8000", "67", "FERRITIN"), new("COBAS 8000", "499", "PSA"), new("COBAS 8000", "148", "BETAHCG"),
            new("COBAS 8000", "576", "TROPT"), new("COBAS 8000", "580", "PCT"), new("COBAS 8000", "765", "ANTIHBS"),
            new("COBAS 8000", "515", "HBSAG"),

            // CHEM7 (InsID 7) — "GHB" (nồng độ cồn, sai) và "AMY"/"PRO" (Amylase/Protein toàn
            // phần, không có dịch vụ tương ứng trong danh mục rút gọn này) không đưa vào.
            new("CHEM7", "ALB", "ALB"), new("CHEM7", "CHO", "CHO"), new("CHEM7", "CRE", "CRE"),
            new("CHEM7", "CRP", "CRP"), new("CHEM7", "GGT", "GGT"), new("CHEM7", "GLU", "GLU"),
            new("CHEM7", "TRI", "TRI"), new("CHEM7", "URE", "URE"), new("CHEM7", "UAC", "UA"),
            new("CHEM7", "SGOT", "AST"), new("CHEM7", "SGPT", "ALT"),

            // EBA200 (InsID 9) — "GLU" map 2 dòng (mơ hồ) và "AMY2" (Amylase, không có dịch vụ
            // tương ứng) không đưa vào.
            new("EBA200", "CHO2", "CHO"), new("EBA200", "CRE", "CRE"),
            new("EBA200", "CRP", "CRP"), new("EBA200", "GGT2", "GGT"), new("EBA200", "HDL", "HDL"),
            new("EBA200", "TRI2", "TRI"), new("EBA200", "URE", "URE"), new("EBA200", "URIC", "UA"),
            new("EBA200", "GOT4", "AST"),
            // GPT2 ở EBA200 đúng nghĩa là ALT thật (khác CON500 GPT2, xem ghi chú) nên vẫn giữ.
            new("EBA200", "GPT2", "ALT"),

            // BIOLIS50 (InsID 11) — bỏ dấu "*" khi map. "AMY*" (Amylase) không có dịch vụ tương ứng.
            new("BIOLIS50", "ALB*", "ALB"), new("BIOLIS50", "CHO*", "CHO"),
            new("BIOLIS50", "CRE*", "CRE"), new("BIOLIS50", "CRP*", "CRP"), new("BIOLIS50", "GGT*", "GGT"),
            new("BIOLIS50", "GLU*", "GLU"), new("BIOLIS50", "GOT*", "AST"), new("BIOLIS50", "GPT*", "ALT"),
            new("BIOLIS50", "HDL", "HDL"), new("BIOLIS50", "TRI*", "TRI"), new("BIOLIS50", "UA*", "UA"),
            new("BIOLIS50", "URE*", "URE"),

            // ST200 / ST200PRO (InsID 13/14) — điện giải.
            new("ST200", "Na", "NA"), new("ST200", "K", "K"), new("ST200", "Cl", "CL"), new("ST200", "iCa", "ICA"),
            new("ST200PRO", "Na", "NA"), new("ST200PRO", "K", "K"), new("ST200PRO", "Cl", "CL"), new("ST200PRO", "iCa", "ICA"),

            // CON500 (InsID 8) — "GPT2"→ALT và "EO_"→P-LCR là map sai hiển nhiên (mục 8.3 tài
            // liệu) — CHỦ Ý không đưa GPT2/EO_ của CON500 vào, dù EBA200 có GPT2 hợp lệ (khác máy).
        };

        var kept = candidates.Where(c => validCodes.Contains(c.ServiceCode)).ToList();

        // Luật bắt buộc: một (máy, kênh) chỉ được trỏ tới một dịch vụ — bỏ hết các kênh trùng.
        return FilterAmbiguous(kept);
    }

    private static (List<SeedChannel> Kept, List<string> Dropped) FilterAmbiguous(List<SeedChannel> candidates)
    {
        var dropped = new List<string>();

        var groups = candidates.GroupBy(c => (c.Instrument, c.Channel)).ToList();
        var ambiguousKeys = groups.Where(g => g.Select(c => c.ServiceCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        foreach (var group in groups.Where(g => ambiguousKeys.Contains(g.Key)))
        {
            var services = string.Join(", ", group.Select(c => c.ServiceCode));
            dropped.Add($"MapMay {group.Key.Instrument} kênh {group.Key.Channel}: trỏ tới nhiều dịch vụ ({services}) — mơ hồ, bỏ hết.");
        }

        dropped.Add("MapMay CON500 kênh GPT2 → ALT: map sai hiển nhiên (máy huyết học không có ALT) — docs/drivers/cac-may-astm-bvdk10.md mục 8.3.");
        dropped.Add("MapMay CON500 kênh EO_ → P-LCR: map sai hiển nhiên — docs/drivers/cac-may-astm-bvdk10.md mục 8.3.");
        dropped.Add("MapMay e2010 kênh 200 → \"hc12\": dòng rác, không tên/không đơn vị — docs/drivers/cac-may-astm-bvdk10.md mục 1.");
        dropped.Add("MapMay CHEM7 kênh GHB → nồng độ cồn: không phải Glucose/HbA1c, map sai hiển nhiên — docs/drivers/cac-may-astm-bvdk10.md mục 8.3.");
        dropped.Add("MapMay e2010 kênh 3 và COBAS 8000 kênh 89 (Cortisol sáng/chiều, trùng mã): không đưa vào danh mục mẫu vì mơ hồ.");
        dropped.Add("MapMay EBA200 kênh GLU: trỏ 2 dòng khác nhau (\"120GLUS\" sau ăn và một dòng không tên) — mơ hồ, bỏ hết.");
        dropped.Add("Không map BC-3200/CON500 cho các chỉ số huyết học (WBC/RBC/HGB...): đơn vị khác Cell-Dyn Ruby (g/L so với g/dL) nên phải dùng bộ mã dịch vụ khác, không gộp — docs/drivers/cac-may-astm-bvdk10.md mục 8.8. Không đưa dịch vụ huyết học thứ hai vào tệp mẫu rút gọn này.");

        var kept = groups.Where(g => !ambiguousKeys.Contains(g.Key)).SelectMany(g => g).ToList();
        return (kept, dropped);
    }

    private static void WriteDichVu(XLWorkbook workbook, List<SeedService> services)
    {
        var sheet = workbook.Worksheets.Add("DichVu");
        string[] headers = ["Ma", "Ten", "Nhom", "LoaiMau", "DonVi", "KieuKetQua", "SoLe", "ThuTuIn", "LaPanel", "TuDuyet", "MaHIS"];
        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }

        var row = 2;
        foreach (var s in services)
        {
            sheet.Cell(row, 1).Value = s.Code;
            sheet.Cell(row, 2).Value = s.Name;
            sheet.Cell(row, 3).Value = s.Group;
            sheet.Cell(row, 4).Value = s.SpecimenCode;
            sheet.Cell(row, 5).Value = s.Unit;
            sheet.Cell(row, 6).Value = s.ValueType;
            sheet.Cell(row, 8).Value = s.PrintOrder;
            sheet.Cell(row, 9).Value = s.IsPanel;
            sheet.Cell(row, 10).Value = s.AutoApprove;
            if (s.HisCode is not null)
            {
                sheet.Cell(row, 11).Value = s.HisCode;
            }

            row++;
        }
    }

    private static void WriteChiSoCon(XLWorkbook workbook, List<(string Parent, string Child, int Order)> children)
    {
        var sheet = workbook.Worksheets.Add("ChiSoCon");
        sheet.Cell(1, 1).Value = "MaCha";
        sheet.Cell(1, 2).Value = "MaCon";
        sheet.Cell(1, 3).Value = "ThuTu";

        var row = 2;
        foreach (var (parent, child, order) in children)
        {
            sheet.Cell(row, 1).Value = parent;
            sheet.Cell(row, 2).Value = child;
            sheet.Cell(row, 3).Value = order;
            row++;
        }
    }

    private static void WriteKhoangThamChieu(XLWorkbook workbook, List<SeedRange> ranges)
    {
        var sheet = workbook.Worksheets.Add("KhoangThamChieu");
        string[] headers = ["Ma", "Gioi", "TuoiTu", "TuoiDen", "DonViTuoi", "Thap", "Cao", "NguyHiemThap", "NguyHiemCao", "Chu"];
        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }

        var row = 2;
        foreach (var r in ranges)
        {
            sheet.Cell(row, 1).Value = r.Code;
            if (r.Sex is not null)
            {
                sheet.Cell(row, 2).Value = r.Sex.Value.ToString();
            }

            sheet.Cell(row, 3).Value = r.AgeFrom;
            sheet.Cell(row, 4).Value = r.AgeTo;
            sheet.Cell(row, 5).Value = r.AgeUnit;
            sheet.Cell(row, 6).Value = r.Low;
            sheet.Cell(row, 7).Value = r.High;
            row++;
        }
    }

    private static void WriteMapMay(XLWorkbook workbook, List<SeedChannel> channels)
    {
        var sheet = workbook.Worksheets.Add("MapMay");
        string[] headers = ["TenMay", "MaKenh", "MaChiSo", "SoLe", "HeSo", "GiuDauSoSanh", "GuiKhiMayHoi"];
        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }

        var row = 2;
        foreach (var c in channels)
        {
            sheet.Cell(row, 1).Value = c.Instrument;
            sheet.Cell(row, 2).Value = c.Channel;
            sheet.Cell(row, 3).Value = c.ServiceCode;
            row++;
        }
    }

    private static void WriteGhiChu(XLWorkbook workbook, List<string> notes)
    {
        var sheet = workbook.Worksheets.Add("GhiChu");
        sheet.Cell(1, 1).Value = "Dòng đã bỏ / lý do";

        var row = 2;
        sheet.Cell(row++, 1).Value =
            "Danh mục này dựng lại từ docs/drivers/cac-may-astm-bvdk10.md (không đọc được " +
            "~/Downloads/lis-tham-khao/... trong môi trường sinh tệp — EPERM) — xem Lis.SeedTool/MakeSeedXlsxCommand.cs.";

        foreach (var note in notes)
        {
            sheet.Cell(row++, 1).Value = note;
        }
    }
}
