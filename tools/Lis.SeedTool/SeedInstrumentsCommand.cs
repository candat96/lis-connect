using System.Text.Json;
using Dapper;
using Lis.Data;
using Lis.Data.Catalog;

namespace Lis.SeedTool;

/// <summary>Một máy trong danh sách seed. <see cref="Connection"/> null → chưa rõ cách nối.</summary>
internal sealed record SeedInstrument(
    string Name, string Model, string? ProtocolFamily, object? Connection, string? WorkstationCode);

/// <summary>
/// Lệnh "seed-instruments": đưa toàn bộ máy xét nghiệm đã có tài liệu ở <c>docs/drivers/</c> vào
/// bảng <c>instrument</c> của một CSDL dev, để thử màn "Máy &amp; map".
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Nếu CSDL chưa có dịch vụ nào, nhập trước <c>tools/seed/mau-danh-muc.xlsx</c> (qua
/// <see cref="CatalogExcelImporter"/>) — sheet MapMay của tệp này tạo sẵn một số máy kèm map
/// kênh (Cell-Dyn Ruby, e2010, COBAS 8000, CHEM7…).</item>
/// <item>Thêm hoặc cập nhật từng máy theo <b>tên</b> (không phân biệt hoa thường): máy đã có thì
/// chỉ điền model/họ giao thức/kết nối/trạm, giữ nguyên map kênh; máy chưa có thì thêm mới.
/// Chạy lại nhiều lần không sinh trùng.</item>
/// </list>
/// Tham số cổng lấy theo tài liệu từng máy (cài đặt BVĐK 10 nếu có, không thì mặc định của hãng).
/// Chỗ tài liệu không nêu (cổng TCP, cổng COM phía LIS) là giá trị tạm — phải sửa theo máy thật.
/// <c>connection</c> là JSON thô; khuôn chính thức sẽ chốt ở Đợt 2 cùng lớp kết nối.
/// </remarks>
internal static class SeedInstrumentsCommand
{
    private static object Com(string port, int baud, int dataBits = 8, string parity = "None", int stopBits = 1) =>
        new { type = "serial", port, baudRate = baud, dataBits, parity, stopBits };

    private static object TcpServer(int port) => new { type = "tcp", role = "server", port };

    private static object TcpClient(string host, int port) => new { type = "tcp", role = "client", host, port };

    private static readonly SeedInstrument[] Instruments =
    [
        // Ba máy ưu tiên.
        new("Mindray BC-5380", "Mindray BC-5380 / BC-5180", "hl7-mllp", TcpServer(5100), null), // cổng: tài liệu không nêu
        new("Beckman AU480", "Beckman Coulter AU480", "beckman-au", Com("COM1", 9600), null),
        new("Beckman AU680", "Beckman Coulter AU680", "beckman-au", Com("COM1", 9600), null),
        new("Beckman Access 2", "Beckman Coulter Access 2 (UniCel DxI)", "astm-e1394", Com("COM1", 9600), null),

        // Huyết học.
        new("DIRUI BCC-3900", "DIRUI BCC-3900 / BCC-3960", "hl7-mllp", TcpServer(9527), null),
        new("Cell-Dyn Ruby", "Abbott Cell-Dyn Ruby (máy 1)", "astm-e1394", Com("COM4", 9600), "DK10.3"),
        new("Cell-Dyn Ruby 2", "Abbott Cell-Dyn Ruby (máy 2)", "astm-e1394", Com("COM8", 9600), "DK10.2"),
        new("Mindray BC-3200", "Mindray BC-3200", "mindray-bc", Com("COM1", 9600, dataBits: 7), null),
        new("CON500", "CON500", null, null, null),
        new("Nihon Kohden Celltac G", "Nihon Kohden Celltac G (MEK-9100)", null, null, null),

        // Sinh hóa.
        new("DIRUI CS-T240", "DIRUI CS-T240 / CS-T240plus", "astm-e1394", Com("COM2", 19200), null),
        new("Beckman AU2700", "Beckman/Olympus AU2700 (AU5400)", "beckman-au", Com("COM5", 9600), "DK10.1"),
        new("Beckman AU5800", "Beckman Coulter AU5800", "beckman-au", Com("COM1", 9600), null),
        new("Olympus AU640", "Olympus AU640", "beckman-au", Com("COM6", 9600), "DK10.1"),
        new("CHEM7", "Erba CHEM7", null, null, null),
        new("EBA200", "EBA200", null, null, null),
        new("BIOLIS50", "Biolis 50i", null, null, null),

        // Miễn dịch.
        new("e2010", "Roche Elecsys 2010 / cobas e411", "astm-e1394", Com("COM6", 9600), null),
        new("COBAS 8000", "Roche cobas 8000 (data manager)", "astm-e1394", TcpClient("192.168.1.100", 50001), null), // IP tạm
        new("Tosoh AIA-360", "Tosoh AIA-360", "fixed-offset", Com("COM1", 9600), "DK10.3"),

        // HbA1c.
        new("HLC-723GX", "Tosoh HLC-723GX", "tosoh-g8", Com("COM1", 9600), null),
        new("Tosoh HLC-723 G11", "Tosoh HLC-723 G11", "tosoh-g8", Com("COM5", 9600), "DK10.2"),

        // Đông máu.
        new("Sysmex CA-500", "Sysmex CA-500 series", "sysmex-ca", Com("COM7", 9600), "DK10.2"),
        new("Sysmex CA-600", "Sysmex CA-600 series", "sysmex-ca", Com("COM1", 9600), null),

        // Nước tiểu.
        new("DIRUI HC-300", "DIRUI HC-300", "dirui-h", Com("COM1", 9600), null),
        new("ACON Mission U500", "ACON Mission U500 / U120", null, Com("COM1", 9600), null),
        new("CYBOW R-50S", "CYBOW R-50S (DFI)", null, Com("COM1", 38400), null),

        // Điện giải.
        new("ST200", "ST200", null, null, null),
        new("ST200PRO", "ST200PRO", null, null, null),
    ];

    /// <summary>Chỉ số bổ sung (đông máu, nước tiểu) mà danh mục mẫu rút gọn chưa có. Đã có thì bỏ qua.</summary>
    private static readonly (string Code, string Name, string Group, string Specimen, string? Unit, string ValueType, int? Decimals, int Order)[] ExtraServices =
    [
        ("PT", "Thời gian prothrombin (PT)", "Đông máu", "CITRAT", "giây", "so", 1, 200),
        ("PTINR", "PT-INR", "Đông máu", "CITRAT", null, "so", 2, 201),
        ("APTT", "Thời gian thromboplastin từng phần (APTT)", "Đông máu", "CITRAT", "giây", "so", 1, 202),
        ("FIB", "Fibrinogen", "Đông máu", "CITRAT", "g/L", "so", 2, 203),
        ("U-LEU", "Bạch cầu niệu", "Nước tiểu", "NUOC-TIEU", "Leu/µL", "chu", null, 300),
        ("U-NIT", "Nitrit niệu", "Nước tiểu", "NUOC-TIEU", null, "chu", null, 301),
        ("U-PRO", "Protein niệu", "Nước tiểu", "NUOC-TIEU", "g/L", "chu", null, 302),
        ("U-GLU", "Glucose niệu", "Nước tiểu", "NUOC-TIEU", "mmol/L", "chu", null, 303),
        ("U-BLD", "Hồng cầu niệu", "Nước tiểu", "NUOC-TIEU", "Ery/µL", "chu", null, 304),
        ("U-PH", "pH niệu", "Nước tiểu", "NUOC-TIEU", null, "so", 1, 305),
        ("U-SG", "Tỷ trọng niệu", "Nước tiểu", "NUOC-TIEU", null, "so", 3, 306),
        ("U-KET", "Thể ceton niệu", "Nước tiểu", "NUOC-TIEU", "mmol/L", "chu", null, 307),
    ];

    /// <summary>
    /// Map kênh cho các máy chưa có trong danh mục mẫu. Mã kênh lấy theo tài liệu từng máy ở
    /// <c>docs/drivers/</c>; riêng họ Beckman AU mã kênh là số test do từng nơi cài trên máy —
    /// ở đây dùng số minh họa. Dữ liệu dev, phải đối chiếu lại với máy thật.
    /// </summary>
    private static readonly (string Instrument, string Channel, string Service)[] ExtraChannels =
    [
        // Mindray BC-5380 — OBX-3 theo LOINC.
        ("Mindray BC-5380", "6690-2", "WBC"), ("Mindray BC-5380", "789-8", "RBC"), ("Mindray BC-5380", "718-7", "HGB"),
        ("Mindray BC-5380", "4544-3", "HCT"), ("Mindray BC-5380", "787-2", "MCV"), ("Mindray BC-5380", "785-6", "MCH"),
        ("Mindray BC-5380", "786-4", "MCHC"), ("Mindray BC-5380", "777-3", "PLT"), ("Mindray BC-5380", "751-8", "NEU"),
        ("Mindray BC-5380", "731-0", "LYM"), ("Mindray BC-5380", "742-7", "MONO"), ("Mindray BC-5380", "711-2", "EOS"),
        ("Mindray BC-5380", "704-7", "BASO"),

        // DIRUI BCC-3900 — OBX-3 mã số.
        ("DIRUI BCC-3900", "2006", "WBC"), ("DIRUI BCC-3900", "2014", "RBC"), ("DIRUI BCC-3900", "2018", "HGB"),
        ("DIRUI BCC-3900", "2019", "HCT"), ("DIRUI BCC-3900", "2020", "MCV"), ("DIRUI BCC-3900", "2021", "MCH"),
        ("DIRUI BCC-3900", "2022", "MCHC"), ("DIRUI BCC-3900", "2025", "PLT"),

        // Mindray BC-3200 — tên chỉ số trong khối cố định.
        ("Mindray BC-3200", "WBC", "WBC"), ("Mindray BC-3200", "RBC", "RBC"), ("Mindray BC-3200", "HGB", "HGB"),
        ("Mindray BC-3200", "HCT", "HCT"), ("Mindray BC-3200", "MCV", "MCV"), ("Mindray BC-3200", "MCH", "MCH"),
        ("Mindray BC-3200", "MCHC", "MCHC"), ("Mindray BC-3200", "PLT", "PLT"),

        // DIRUI CS-T240 — R-3 thành phần 4.
        ("DIRUI CS-T240", "GLU", "GLU"), ("DIRUI CS-T240", "UREA", "URE"), ("DIRUI CS-T240", "CREA", "CRE"),
        ("DIRUI CS-T240", "AST", "AST"), ("DIRUI CS-T240", "ALT", "ALT"), ("DIRUI CS-T240", "TC", "CHO"),
        ("DIRUI CS-T240", "TG", "TRI"), ("DIRUI CS-T240", "HDL-C", "HDL"), ("DIRUI CS-T240", "GGT", "GGT"),
        ("DIRUI CS-T240", "UA", "UA"), ("DIRUI CS-T240", "ALB", "ALB"), ("DIRUI CS-T240", "CRP", "CRP"),

        // Họ Beckman AU — số test minh họa.
        ("Beckman AU2700", "01", "GLU"), ("Beckman AU2700", "02", "URE"), ("Beckman AU2700", "03", "CRE"),
        ("Beckman AU2700", "04", "AST"), ("Beckman AU2700", "05", "ALT"), ("Beckman AU2700", "06", "CHO"),
        ("Beckman AU2700", "07", "TRI"), ("Beckman AU2700", "08", "HDL"), ("Beckman AU2700", "10", "UA"),
        ("Olympus AU640", "01", "GLU"), ("Olympus AU640", "02", "URE"), ("Olympus AU640", "03", "CRE"),
        ("Olympus AU640", "04", "AST"), ("Olympus AU640", "05", "ALT"), ("Olympus AU640", "11", "GGT"),
        ("Beckman AU480", "01", "GLU"), ("Beckman AU480", "03", "CRE"), ("Beckman AU480", "05", "ALT"),
        ("Beckman AU680", "01", "GLU"), ("Beckman AU680", "03", "CRE"), ("Beckman AU680", "05", "ALT"),
        ("Beckman AU5800", "01", "GLU"), ("Beckman AU5800", "03", "CRE"), ("Beckman AU5800", "05", "ALT"),

        // Beckman Access 2 — mã test ASTM.
        ("Beckman Access 2", "TSH3", "TSH"), ("Beckman Access 2", "FT4", "FT4"), ("Beckman Access 2", "FT3", "FT3"),
        ("Beckman Access 2", "AFP", "AFP"), ("Beckman Access 2", "CEA", "CEA"), ("Beckman Access 2", "OV125Ag", "CA125"),
        ("Beckman Access 2", "GI19-9Ag", "CA199"), ("Beckman Access 2", "BR15-3Ag", "CA153"), ("Beckman Access 2", "Ferritin", "FERRITIN"),
        ("Beckman Access 2", "PSA", "PSA"),

        // Tosoh AIA-360, HLC-723 G11.
        ("Tosoh AIA-360", "TSH", "TSH"), ("Tosoh AIA-360", "FT4", "FT4"), ("Tosoh AIA-360", "AFP", "AFP"),
        ("Tosoh AIA-360", "CEA", "CEA"), ("Tosoh AIA-360", "PSA", "PSA"),
        ("Tosoh HLC-723 G11", "A1C", "A1C"),

        // Sysmex CA-500 / CA-600 — mã thông số 3 chữ số.
        ("Sysmex CA-500", "041", "PT"), ("Sysmex CA-500", "044", "PTINR"), ("Sysmex CA-500", "051", "APTT"), ("Sysmex CA-500", "062", "FIB"),
        ("Sysmex CA-600", "041", "PT"), ("Sysmex CA-600", "044", "PTINR"), ("Sysmex CA-600", "051", "APTT"), ("Sysmex CA-600", "062", "FIB"),

        // Nước tiểu.
        ("DIRUI HC-300", "LEU", "U-LEU"), ("DIRUI HC-300", "NIT", "U-NIT"), ("DIRUI HC-300", "PRO", "U-PRO"),
        ("DIRUI HC-300", "GLU", "U-GLU"), ("DIRUI HC-300", "BLD", "U-BLD"), ("DIRUI HC-300", "pH", "U-PH"),
        ("DIRUI HC-300", "SG", "U-SG"), ("DIRUI HC-300", "KET", "U-KET"),
        ("ACON Mission U500", "LEU", "U-LEU"), ("ACON Mission U500", "NIT", "U-NIT"), ("ACON Mission U500", "PRO", "U-PRO"),
        ("ACON Mission U500", "GLU", "U-GLU"), ("ACON Mission U500", "BLD", "U-BLD"), ("ACON Mission U500", "PH", "U-PH"),
        ("ACON Mission U500", "SG", "U-SG"), ("ACON Mission U500", "KET", "U-KET"),
        ("CYBOW R-50S", "LEU", "U-LEU"), ("CYBOW R-50S", "NIT", "U-NIT"), ("CYBOW R-50S", "PRO", "U-PRO"), ("CYBOW R-50S", "GLU", "U-GLU"),

        // Huyết học khác.
        ("CON500", "WBC", "WBC"), ("CON500", "RBC", "RBC"), ("CON500", "HGB", "HGB"), ("CON500", "PLT", "PLT"),
        ("Nihon Kohden Celltac G", "WBC", "WBC"), ("Nihon Kohden Celltac G", "RBC", "RBC"),
        ("Nihon Kohden Celltac G", "HGB", "HGB"), ("Nihon Kohden Celltac G", "PLT", "PLT"),
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        string? connectionString = null;
        var catalogPath = "tools/seed/mau-danh-muc.xlsx";

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--conn" && i + 1 < args.Length)
            {
                connectionString = args[++i];
            }
            else if (args[i] == "--catalog" && i + 1 < args.Length)
            {
                catalogPath = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Thiếu --conn <chuỗi kết nối>");
            return 1;
        }

        var db = Db.Create(connectionString);

        await using (var connection = await db.OpenAsync())
        {
            var serviceCount = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM service");
            if (serviceCount == 0)
            {
                if (!File.Exists(catalogPath))
                {
                    Console.Error.WriteLine($"CSDL chưa có danh mục và không thấy tệp {catalogPath} (đổi bằng --catalog).");
                    return 1;
                }

                await using var stream = File.OpenRead(catalogPath);
                var report = await new CatalogExcelImporter(db).ImportAsync(stream, dryRun: false);
                if (report.Errors.Count > 0)
                {
                    Console.Error.WriteLine("Nhập danh mục lỗi:");
                    foreach (var error in report.Errors)
                    {
                        Console.Error.WriteLine($"  {error}");
                    }

                    return 1;
                }

                Console.WriteLine($"Đã nhập danh mục {catalogPath}: tạo {report.Created}, cập nhật {report.Updated}.");
            }
        }

        var (created, updated) = await db.InTransactionAsync(async (connection, transaction) =>
        {
            int created = 0, updated = 0;
            foreach (var item in Instruments)
            {
                var parameters = new
                {
                    name = item.Name,
                    model = item.Model,
                    protocolFamily = item.ProtocolFamily,
                    connection = item.Connection is null ? null : JsonSerializer.Serialize(item.Connection),
                    workstationCode = item.WorkstationCode,
                };

                var existingId = await connection.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM instrument WHERE lower(name) = lower(@name) ORDER BY created_at LIMIT 1",
                    parameters, transaction);

                if (existingId is { } id)
                {
                    await connection.ExecuteAsync(
                        """
                        UPDATE instrument SET model = @model, protocol_family = @protocolFamily,
                            connection = @connection::jsonb, workstation_code = @workstationCode
                        WHERE id = @id
                        """,
                        new { parameters.model, parameters.protocolFamily, parameters.connection, parameters.workstationCode, id },
                        transaction);
                    updated++;
                }
                else
                {
                    await connection.ExecuteAsync(
                        """
                        INSERT INTO instrument (name, model, protocol_family, connection, workstation_code, active)
                        VALUES (@name, @model, @protocolFamily, @connection::jsonb, @workstationCode, true)
                        """,
                        parameters, transaction);
                    created++;
                }
            }

            // Máy Cell-Dyn Ruby thứ hai dùng cùng bộ mã kênh với máy thứ nhất: chép map kênh nếu chưa có.
            await connection.ExecuteAsync(
                """
                INSERT INTO instrument_channel (instrument_id, channel_code, service_id, decimals, factor, keep_comparator, send_on_query)
                SELECT dst.id, ic.channel_code, ic.service_id, ic.decimals, ic.factor, ic.keep_comparator, ic.send_on_query
                FROM instrument src
                JOIN instrument_channel ic ON ic.instrument_id = src.id
                CROSS JOIN instrument dst
                WHERE src.name = 'Cell-Dyn Ruby' AND dst.name = 'Cell-Dyn Ruby 2'
                ON CONFLICT (instrument_id, channel_code) DO NOTHING
                """,
                transaction: transaction);

            return (created, updated);
        });

        var (servicesAdded, channelsAdded) = await db.InTransactionAsync(async (connection, transaction) =>
        {
            var servicesAdded = 0;
            foreach (var s in ExtraServices)
            {
                servicesAdded += await connection.ExecuteAsync(
                    """
                    INSERT INTO service (code, name, group_name, specimen_type_id, unit, value_type, decimals, print_order)
                    SELECT @Code, @Name, @Group, (SELECT id FROM specimen_type WHERE code = @Specimen), @Unit, @ValueType, @Decimals, @Order
                    ON CONFLICT (code) DO NOTHING
                    """,
                    new { s.Code, s.Name, s.Group, s.Specimen, s.Unit, s.ValueType, s.Decimals, s.Order }, transaction);
            }

            var channelsAdded = 0;
            foreach (var c in ExtraChannels)
            {
                channelsAdded += await connection.ExecuteAsync(
                    """
                    INSERT INTO instrument_channel (instrument_id, channel_code, service_id)
                    SELECT i.id, @Channel, s.id
                    FROM instrument i JOIN service s ON s.code = @Service
                    WHERE i.name = @Instrument
                    ON CONFLICT (instrument_id, channel_code) DO NOTHING
                    """,
                    new { c.Instrument, c.Channel, c.Service }, transaction);
            }

            return (servicesAdded, channelsAdded);
        });

        Console.WriteLine($"Máy xét nghiệm: thêm {created}, cập nhật {updated} (tổng {Instruments.Length} máy trong danh sách).");
        Console.WriteLine($"Chỉ số bổ sung: thêm {servicesAdded}. Map kênh bổ sung: thêm {channelsAdded}.");
        return 0;
    }
}
