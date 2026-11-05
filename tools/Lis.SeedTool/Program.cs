namespace Lis.SeedTool;

/// <summary>
/// Công cụ dòng lệnh: <c>make-seed-xlsx</c> dựng tệp danh mục mẫu, <c>seed-demo</c> tạo dữ
/// liệu giả (người bệnh, chỉ định, kết quả) trên một CSDL dev.
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
    }
}
