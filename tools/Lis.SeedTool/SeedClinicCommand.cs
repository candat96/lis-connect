using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Dapper;
using Lis.Data;
using Lis.Data.Catalog;

namespace Lis.SeedTool;

/// <summary>
/// Lệnh "seed-clinic": khởi tạo CSDL cho một phòng khám từ danh mục HIS (bản xuất JSON phpMyAdmin
/// của <c>DM_DICHVU_KYTHUAT</c> + <c>RLS_DV_KYTHUAT</c>) và tệp profile máy
/// (<c>tools/clinics/&lt;pk&gt;/clinic.json</c>).
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Đọc dịch vụ còn dùng (TRANG_THAI=1, chưa xoá, chưa khoá); quan hệ cha → con còn hiệu lực.
/// Chỉ số con mà mọi dịch vụ cha đều đã ngừng thì bỏ (vd combo test nhanh đã xoá).</item>
/// <item>Dựng workbook đúng khuôn <see cref="CatalogExcelImporter"/> (DichVu, ChiSoCon, MapMay),
/// lưu ra <c>--out</c> để xem lại, rồi nhập vào CSDL (nhập lại được nhiều lần).</item>
/// <item>Tạo/cập nhật máy theo tên, kết nối để trống ("Chưa cấu hình") — cài tại chỗ.</item>
/// <item><c>--reset</c>: xoá trước toàn bộ danh mục, máy, người bệnh, chỉ định, kết quả, QC
/// (giữ tài khoản, cơ sở, máy trạm, cài đặt). Chỉ dùng khi khởi tạo/làm lại CSDL.</item>
/// </list>
/// </remarks>
internal static class SeedClinicCommand
{
    private sealed record HisService(
        string Id, string Code, string Name, string LoaiDv, string? LoaiChiSo, string? KieuChiSo, int? Stt, bool Alive);

    private sealed record ClinicProfile(string Clinic, List<ProfileInstrument> Instruments);

    /// <param name="Services">Chỉ số máy chạy được mà chưa biết mã kênh (vd AU480) — phần mềm tự dò mã kênh theo barcode.</param>
    private sealed record ProfileInstrument(
        string Name, string Model, string ProtocolFamily, string? Note, List<ProfileChannel> Channels, List<string>? Services = null);

    private sealed record ProfileChannel(string Code, string Service);

    internal sealed record CatalogRow(
        string Code, string Name, string Group, string Specimen, string ValueType, int? Decimals, int PrintOrder, bool IsPanel, string HisCode);

    private static readonly Dictionary<string, (string Group, int Order)> Groups = new()
    {
        ["36"] = ("Huyết học", 1),
        ["35"] = ("Sinh hoá", 2),
        ["46"] = ("Miễn dịch", 3),
        ["56"] = ("Miễn dịch", 3),
        ["38"] = ("Nước tiểu", 4),
        ["55"] = ("Test nhanh", 5),
        ["37"] = ("Vi sinh", 6),
        ["45"] = ("Vi sinh", 6),
        ["39"] = ("Tế bào – Giải phẫu bệnh", 7),
        ["41"] = ("Sinh học phân tử – Di truyền", 8),
        ["42"] = ("Sinh học phân tử – Di truyền", 8),
    };

    private static readonly Regex Coagulation = new(@"\b(PT|APTT|INR|Fibrinogen|D-dimer|Lupus|LA)\b", RegexOptions.IgnoreCase);

    // Bảng chỉ để reset CSDL — thứ tự không quan trọng vì TRUNCATE ... CASCADE cùng một câu.
    private static readonly string[] ResetTables =
    [
        "unmatched_result", "result_history", "result", "qc_result", "order_item", "sample", "lab_order", "patient",
        "barcode_counter", "instrument_channel", "instrument", "qualitative_rule", "reference_range", "service_child", "service",
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        string? connectionString = null, dmPath = null, rlsPath = null, profilePath = null, outPath = null;
        var reset = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--conn" when i + 1 < args.Length: connectionString = args[++i]; break;
                case "--his-dm" when i + 1 < args.Length: dmPath = args[++i]; break;
                case "--his-rls" when i + 1 < args.Length: rlsPath = args[++i]; break;
                case "--profile" when i + 1 < args.Length: profilePath = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--reset": reset = true; break;
            }
        }

        if (connectionString is null || dmPath is null || rlsPath is null || profilePath is null)
        {
            Console.Error.WriteLine("Cần --conn, --his-dm, --his-rls, --profile");
            return 1;
        }

        var profile = JsonSerializer.Deserialize<ClinicProfile>(File.ReadAllText(profilePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Profile rỗng");
        outPath ??= Path.Combine(Path.GetDirectoryName(profilePath)!, "danh-muc.xlsx");

        var (rows, children, skipped) = BuildCatalog(ReadPhpMyAdminTable(dmPath), ReadPhpMyAdminTable(rlsPath));
        Console.WriteLine($"Danh mục HIS: {rows.Count} dịch vụ ({rows.Count(r => r.IsPanel)} dịch vụ có chỉ số con, {children.Count} quan hệ cha→con); bỏ {skipped} dịch vụ ngừng dùng.");

        var codes = rows.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var instrument in profile.Instruments)
        {
            foreach (var channel in instrument.Channels)
            {
                if (!codes.TryGetValue(channel.Service, out var row))
                    problems.Add($"{instrument.Name} kênh {channel.Code}: không có dịch vụ {channel.Service} trong danh mục");
                else if (row.IsPanel)
                    problems.Add($"{instrument.Name} kênh {channel.Code}: {channel.Service} là dịch vụ có chỉ số con — phải map vào chỉ số lá");
            }
        }

        if (problems.Count > 0)
        {
            problems.ForEach(p => Console.Error.WriteLine("  " + p));
            return 1;
        }

        // Chỉ số được máy trả kết quả là số → kiểu kết quả "so" dù bên HIS khai nhập văn bản.
        var mapped = profile.Instruments.SelectMany(i => i.Channels.Select(c => c.Service).Concat(i.Services ?? []))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        rows = rows.Select(r => mapped.Contains(r.Code) && r.ValueType == "chu" ? r with { ValueType = "so" } : r).ToList();

        WriteWorkbook(outPath, rows, children, profile);
        Console.WriteLine($"Đã ghi {outPath}");

        var db = Db.Create(connectionString);
        Migrator.Upgrade(connectionString);

        if (reset)
        {
            await using var connection = await db.OpenAsync();
            await connection.ExecuteAsync($"TRUNCATE {string.Join(", ", ResetTables)} CASCADE");
            Console.WriteLine("Đã xoá dữ liệu cũ (danh mục, máy, người bệnh, chỉ định, kết quả, QC).");
        }

        await db.InTransactionAsync(async (connection, transaction) =>
        {
            foreach (var instrument in profile.Instruments)
            {
                var updated = await connection.ExecuteAsync(
                    "UPDATE instrument SET model = @Model, protocol_family = @ProtocolFamily WHERE lower(name) = lower(@Name)",
                    instrument, transaction);
                if (updated == 0)
                {
                    await connection.ExecuteAsync(
                        "INSERT INTO instrument (name, model, protocol_family, active) VALUES (@Name, @Model, @ProtocolFamily, true)",
                        instrument, transaction);
                }
            }

            return true;
        });

        await using (var stream = File.OpenRead(outPath))
        {
            var report = await new CatalogExcelImporter(db).ImportAsync(stream, dryRun: false);
            if (report.Errors.Count > 0)
            {
                Console.Error.WriteLine("Nhập danh mục lỗi:");
                foreach (var error in report.Errors)
                    Console.Error.WriteLine("  " + error);
                return 1;
            }

            Console.WriteLine($"Đã nhập danh mục: tạo {report.Created}, cập nhật {report.Updated}.");
        }

        // Dịch vụ máy chạy được mà chưa biết mã kênh.
        await using (var connection = await db.OpenAsync())
        {
            foreach (var instrument in profile.Instruments.Where(i => i.Services is { Count: > 0 }))
            {
                var serviceCodes = instrument.Services!.ToArray();
                var added = await connection.ExecuteAsync(
                    """
                    INSERT INTO instrument_service (instrument_id, service_id)
                    SELECT i.id, s.id FROM instrument i JOIN service s ON s.code = ANY(@codes) AND NOT s.is_panel
                    WHERE i.name = @Name ON CONFLICT DO NOTHING
                    """,
                    new { codes = serviceCodes, instrument.Name });
                Console.WriteLine($"  Máy {instrument.Name}: khai {added}/{serviceCodes.Length} dịch vụ chạy được (mã kênh tự dò theo barcode).");
            }
        }

        // Đơn vị + khoảng tham chiếu mặc định của phòng khám (nếu có) — HIS không xuất phần này.
        var rangesSql = Path.Combine(Path.GetDirectoryName(profilePath)!, "don-vi-tham-chieu.sql");
        if (File.Exists(rangesSql))
        {
            await using var connection = await db.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand(await File.ReadAllTextAsync(rangesSql), connection);
            await command.ExecuteNonQueryAsync();
            Console.WriteLine($"Đã áp đơn vị + khoảng tham chiếu mặc định từ {rangesSql}.");
        }

        foreach (var instrument in profile.Instruments)
        {
            Console.WriteLine($"  Máy {instrument.Name} ({instrument.ProtocolFamily}): {instrument.Channels.Count} kênh. {instrument.Note}");
        }

        return 0;
    }

    /// <summary>Bản xuất JSON của phpMyAdmin: mảng [header, database, {type:"table", data:[...]}].</summary>
    private static List<Dictionary<string, string?>> ReadPhpMyAdminTable(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var table = document.RootElement.EnumerateArray().First(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("data", out _));
        return table.GetProperty("data").EnumerateArray()
            .Select(row => row.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString()))
            .ToList();
    }

    internal static (List<CatalogRow> Rows, List<(string Parent, string Child, int Order)> Children, int Skipped) BuildCatalog(
        List<Dictionary<string, string?>> dm, List<Dictionary<string, string?>> rls)
    {
        static string Clean(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

        var services = dm.Select(d => new HisService(
                d["ID"]!, Clean(d["MA_DICHVU"]), Clean(d["TEN_DICHVU"]), d.GetValueOrDefault("LOAI_DV_ID") ?? "",
                d.GetValueOrDefault("LOAI_CHI_SO"), d.GetValueOrDefault("KIEU_CHI_SO"),
                int.TryParse(d.GetValueOrDefault("STT_DV"), out var stt) ? stt : null,
                d.GetValueOrDefault("TRANG_THAI") == "1" && string.IsNullOrEmpty(d.GetValueOrDefault("NGAY_XOA")) && d.GetValueOrDefault("KHOA") != "1"))
            .Where(s => s.Code.Length > 0)
            .ToDictionary(s => s.Id);

        var relations = rls
            .Where(r => r.GetValueOrDefault("TRANG_THAI") == "1" && services.ContainsKey(r["DV_CHA_ID"]!) && services.ContainsKey(r["DV_CON_ID"]!))
            .Select(r => (Parent: services[r["DV_CHA_ID"]!], Child: services[r["DV_CON_ID"]!], Id: int.TryParse(r["ID"], out var id) ? id : 0))
            .ToList();

        var childIds = relations.Select(r => r.Child.Id).ToHashSet();
        var liveRelations = relations.Where(r => r.Parent.Alive && r.Child.Alive).ToList();
        var liveChildIds = liveRelations.Select(r => r.Child.Id).ToHashSet();
        var panelIds = liveRelations.Select(r => r.Parent.Id).ToHashSet();
        var parentOf = liveRelations.GroupBy(r => r.Child.Id).ToDictionary(g => g.Key, g => g.First().Parent);

        // Bỏ: dịch vụ ngừng dùng; chỉ số con mà mọi dịch vụ cha đều đã ngừng.
        var kept = services.Values.Where(s => s.Alive && (!childIds.Contains(s.Id) || liveChildIds.Contains(s.Id))).ToList();
        var duplicate = kept.GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Mã dịch vụ {duplicate.Key} trùng trong danh mục HIS");

        string GroupOf(HisService s) => s.Code.Equals("XN37", StringComparison.OrdinalIgnoreCase) ? "Nước tiểu" : Groups.GetValueOrDefault(s.LoaiDv, (Group: "Khác", Order: 9)).Group;
        int GroupOrder(HisService s) => GroupOf(s) == "Nước tiểu" ? 4 : Groups.GetValueOrDefault(s.LoaiDv, (Group: "Khác", Order: 9)).Order;

        string SpecimenOf(HisService s)
        {
            if (parentOf.TryGetValue(s.Id, out var parent))
                return SpecimenOf(parent);
            return GroupOf(s) switch
            {
                "Huyết học" => Coagulation.IsMatch(s.Name) ? "CITRAT" : "EDTA",
                "Sinh hoá" => s.Name.Contains("HbA1c", StringComparison.OrdinalIgnoreCase) ? "EDTA" : "SERUM",
                "Miễn dịch" => "SERUM",
                "Nước tiểu" => "NUOC-TIEU",
                _ => "KHAC",
            };
        }

        static string ValueTypeOf(HisService s) => s.KieuChiSo switch
        {
            "SO_THAP_PHAN" or "SO_NGUYEN" => "so",
            "MOT_LUA_CHON" or "NHIEU_LUA_CHON" => "dinh-tinh",
            _ => "chu",
        };

        static string NaturalKey(string code) => Regex.Replace(code, @"\d+", m => m.Value.PadLeft(8, '0'));

        var ordered = kept
            .OrderBy(GroupOrder)
            .ThenBy(s => NaturalKey(parentOf.TryGetValue(s.Id, out var p) ? p.Code : s.Code), StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => parentOf.ContainsKey(s.Id) ? 1 : 0)
            .ThenBy(s => s.Stt ?? int.MaxValue)
            .ThenBy(s => NaturalKey(s.Code), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = ordered.Select((s, i) => new CatalogRow(
                s.Code, s.Name, GroupOf(s), SpecimenOf(s), panelIds.Contains(s.Id) ? "chu" : ValueTypeOf(s),
                s.KieuChiSo == "SO_NGUYEN" ? 0 : null, (i + 1) * 10, panelIds.Contains(s.Id), s.Code))
            .ToList();

        var children = liveRelations
            .GroupBy(r => r.Parent.Id)
            .SelectMany(g => g.OrderBy(r => r.Child.Stt ?? int.MaxValue).ThenBy(r => r.Id)
                .Select((r, i) => (r.Parent.Code, r.Child.Code, i + 1)))
            .ToList();

        return (rows, children, services.Count - kept.Count);
    }

    private static void WriteWorkbook(
        string path, List<CatalogRow> rows, List<(string Parent, string Child, int Order)> children, ClinicProfile profile)
    {
        using var workbook = new XLWorkbook();

        var dv = workbook.Worksheets.Add("DichVu");
        string[] dvHeaders = ["Ma", "Ten", "Nhom", "LoaiMau", "DonVi", "KieuKetQua", "SoLe", "ThuTuIn", "LaPanel", "TuDuyet", "MaHIS"];
        for (var c = 0; c < dvHeaders.Length; c++)
            dv.Cell(1, c + 1).Value = dvHeaders[c];
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            dv.Cell(r + 2, 1).Value = row.Code;
            dv.Cell(r + 2, 2).Value = row.Name;
            dv.Cell(r + 2, 3).Value = row.Group;
            dv.Cell(r + 2, 4).Value = row.Specimen;
            dv.Cell(r + 2, 6).Value = row.ValueType;
            if (row.Decimals is { } decimals)
                dv.Cell(r + 2, 7).Value = decimals;
            dv.Cell(r + 2, 8).Value = row.PrintOrder;
            dv.Cell(r + 2, 9).Value = row.IsPanel;
            dv.Cell(r + 2, 10).Value = false;
            dv.Cell(r + 2, 11).Value = row.HisCode;
        }

        var cs = workbook.Worksheets.Add("ChiSoCon");
        cs.Cell(1, 1).Value = "MaCha";
        cs.Cell(1, 2).Value = "MaCon";
        cs.Cell(1, 3).Value = "ThuTu";
        for (var r = 0; r < children.Count; r++)
        {
            cs.Cell(r + 2, 1).Value = children[r].Parent;
            cs.Cell(r + 2, 2).Value = children[r].Child;
            cs.Cell(r + 2, 3).Value = children[r].Order;
        }

        var map = workbook.Worksheets.Add("MapMay");
        string[] mapHeaders = ["TenMay", "MaKenh", "MaChiSo", "SoLe", "HeSo", "GiuDauSoSanh", "GuiKhiMayHoi"];
        for (var c = 0; c < mapHeaders.Length; c++)
            map.Cell(1, c + 1).Value = mapHeaders[c];
        var mapRow = 2;
        foreach (var instrument in profile.Instruments)
        {
            foreach (var channel in instrument.Channels)
            {
                map.Cell(mapRow, 1).Value = instrument.Name;
                map.Cell(mapRow, 2).Value = channel.Code;
                map.Cell(mapRow, 3).Value = channel.Service;
                map.Cell(mapRow, 7).Value = true; // GuiKhiMayHoi: máy hỏi chỉ định thì gửi kênh này
                mapRow++;
            }
        }

        foreach (var sheet in workbook.Worksheets)
        {
            sheet.Row(1).Style.Font.Bold = true;
            sheet.Columns().AdjustToContents(1, 200);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        workbook.SaveAs(path);
    }
}
