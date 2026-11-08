namespace Lis.SeedTool;

/// <summary>
/// Công cụ dòng lệnh: <c>make-seed-xlsx</c> dựng tệp danh mục mẫu, <c>seed-demo</c> tạo dữ
/// liệu giả (người bệnh, chỉ định, kết quả), <c>seed-instruments</c> đưa danh sách máy xét
/// nghiệm trong <c>docs/drivers/</c> vào một CSDL dev, <c>seed-rawlog</c> sinh log thô giả để thử màn
/// "Log thô".
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var rest = args.Skip(1).ToArray();
        switch (args[0])
        {
            case "make-seed-xlsx":
                return MakeSeedXlsxCommand.Run(rest);

            case "seed-demo":
                return await SeedDemoCommand.RunAsync(rest);

            case "seed-instruments":
                return await SeedInstrumentsCommand.RunAsync(rest);

            case "seed-rawlog":
                return await SeedRawLogCommand.RunAsync(rest);

            case "seed-clinic":
                return await SeedClinicCommand.RunAsync(rest);

            default:
                Console.Error.WriteLine($"Lệnh không rõ: {args[0]}");
                PrintUsage();
                return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Dùng:");
        Console.WriteLine("  Lis.SeedTool make-seed-xlsx [--out tools/seed/mau-danh-muc.xlsx]");
        Console.WriteLine("  Lis.SeedTool seed-demo --conn <chuỗi kết nối> [--days 3]");
        Console.WriteLine("  Lis.SeedTool seed-instruments --conn <chuỗi kết nối> [--catalog tools/seed/mau-danh-muc.xlsx]");
        Console.WriteLine("  Lis.SeedTool seed-clinic --conn <chuỗi kết nối> --his-dm <DM_DICHVU_KYTHUAT.json> --his-rls <RLS_DV_KYTHUAT.json> --profile tools/clinics/<pk>/clinic.json [--out <xlsx>] [--reset]");
        Console.WriteLine("  Lis.SeedTool seed-rawlog --conn <chuỗi kết nối> [--days 3] [--root <thư mục log>]");
    }
}
