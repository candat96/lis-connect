using Dapper;
using Lis.Core.Auth;
using Lis.Core.Ingest;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Ingest;
using Lis.Data.Orders;
using Microsoft.Extensions.Time.Testing;

namespace Lis.SeedTool;

/// <summary>
/// Lệnh "seed-demo": tạo ~30 người bệnh giả (tên chứa "Mẫu"/"Thử", SĐT 09000000xx), chỉ định
/// ngẫu nhiên qua <see cref="OrderService"/>, và với khoảng một nửa số mẫu, kết quả giả qua
/// <see cref="IngestService"/> — dùng để thử luồng trên CSDL dev đã nhập danh mục
/// (<c>mau-danh-muc.xlsx</c>). Không bao giờ dùng dữ liệu người bệnh thật (xem global-constraints).
/// </summary>
internal static class SeedDemoCommand
{
    private static readonly string[] FamilyNames = ["Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Phan", "Vũ", "Đặng"];
    private static readonly string[] MiddleTokens = ["Văn Mẫu", "Thị Mẫu", "Văn Thử", "Thị Thử"];

    public static async Task<int> RunAsync(string[] args)
    {
        string? connectionString = null;
        var days = 3;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--conn" && i + 1 < args.Length)
            {
                connectionString = args[++i];
            }
            else if (args[i] == "--days" && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedDays))
            {
                days = Math.Max(1, parsedDays);
                i++;
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Thiếu --conn <chuỗi kết nối>");
            return 1;
        }

        var db = Db.Create(connectionString);
        var random = new Random(20261103);

        var users = await new UserStore(db).ListAsync();
        var actor = users.FirstOrDefault(u => u.Roles.Contains(Roles.QuanTri)) ?? users.FirstOrDefault();
        if (actor is null)
        {
            Console.Error.WriteLine("CSDL chưa có tài khoản nào — tạo tài khoản quản trị đầu tiên trước khi seed-demo.");
            return 1;
        }

        var catalog = new CatalogStore(db);
        var services = await catalog.ListServicesAsync(activeOnly: true);
        var leafServices = services.Where(s => !s.IsPanel).ToList();
        if (leafServices.Count == 0)
        {
            Console.Error.WriteLine("Danh mục chưa có dịch vụ nào — nhập mau-danh-muc.xlsx trước khi seed-demo.");
            return 1;
        }

        var channelsByService = await LoadChannelsByServiceAsync(db);

        var createdSamples = new List<(CreatedSample Sample, IReadOnlyList<Guid> ServiceIds)>();

        for (var i = 1; i <= 30; i++)
        {
            var dayOffset = random.Next(days);
            var clock = new FakeTimeProvider();
            clock.SetUtcNow(DateTimeOffset.UtcNow.AddDays(-dayOffset).Date.AddHours(8).ToUniversalTime());

            var orders = new OrderService(db, clock);

            var sex = random.Next(2) == 0 ? 'M' : 'F';
            var patient = new NewPatient(
                BuildPatientName(random, sex),
                null,
                1950 + random.Next(1, 76),
                sex,
                "Địa chỉ mẫu, Quận Thử",
                $"09000000{i % 100:D2}");

            var pickCount = random.Next(1, 4);
            var pickedServiceIds = leafServices.OrderBy(_ => random.Next()).Take(pickCount).Select(s => s.Id).ToList();

            try
            {
                var newOrder = new NewOrder(null, patient, pickedServiceIds, "BS. Thử", "Khoa Khám bệnh", "Khám sức khỏe định kỳ (dữ liệu giả)");
                var result = await orders.CreateAsync(newOrder, actor.Id);

                foreach (var sample in result.Samples)
                {
                    createdSamples.Add((sample, pickedServiceIds));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Bỏ qua người bệnh #{i}: {ex.Message}");
            }
        }

        Console.WriteLine($"Đã tạo {createdSamples.Count} mẫu cho tối đa 30 người bệnh giả.");

        var ingest = new IngestService(db, TimeProvider.System);
        var ingestedCount = 0;

        // Khoảng một nửa số mẫu nhận kết quả giả từ máy.
        var targeted = createdSamples.Where((_, index) => index % 2 == 0).ToList();

        foreach (var (sample, _) in targeted)
        {
            var items = BuildFakeReadingItems(sample, channelsByService, random, out var instrumentId);
            if (items.Count == 0 || instrumentId is null)
            {
                continue;
            }

            try
            {
                await ingest.IngestAsync(new InstrumentReading(instrumentId.Value, sample.Barcode, false, DateTimeOffset.UtcNow, null, items));
                ingestedCount++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Bỏ qua kết quả giả cho mẫu {sample.Barcode}: {ex.Message}");
            }
        }

        Console.WriteLine($"Đã gửi kết quả giả cho {ingestedCount}/{targeted.Count} mẫu được chọn.");
        return 0;
    }

    private static string BuildPatientName(Random random, char sex)
    {
        var family = FamilyNames[random.Next(FamilyNames.Length)];
        var middle = sex == 'M' ? "Văn Mẫu" : "Thị Thử";
        return $"{family} {middle} {random.Next(1, 999)}";
    }

    private static async Task<Dictionary<Guid, List<(Guid InstrumentId, string ChannelCode)>>> LoadChannelsByServiceAsync(Db db)
    {
        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<(Guid ServiceId, Guid InstrumentId, string ChannelCode)>(
            "SELECT service_id AS ServiceId, instrument_id AS InstrumentId, channel_code AS ChannelCode FROM channel_map");

        var map = new Dictionary<Guid, List<(Guid InstrumentId, string ChannelCode)>>();
        foreach (var row in rows)
        {
            if (!map.TryGetValue(row.ServiceId, out var list))
            {
                list = [];
                map[row.ServiceId] = list;
            }

            list.Add((row.InstrumentId, row.ChannelCode));
        }

        return map;
    }

    private static List<ReadingItem> BuildFakeReadingItems(
        CreatedSample sample,
        Dictionary<Guid, List<(Guid InstrumentId, string ChannelCode)>> channelsByService,
        Random random,
        out Guid? instrumentId)
    {
        instrumentId = null;
        var items = new List<ReadingItem>();

        // CreatedSample không giữ ServiceId — ghép theo tên dịch vụ đã trả về khi tạo mẫu không
        // khả thi ở đây (không có map tên->id), nên quét toàn bộ channelsByService và chọn ngẫu
        // nhiên một vài kênh từ MỘT máy duy nhất (một InstrumentReading chỉ của một máy) để mô
        // phỏng máy gửi kết quả — đủ cho mục đích thử luồng, không cần khớp tuyệt đối dịch vụ.
        if (channelsByService.Count == 0)
        {
            return items;
        }

        var allChannels = channelsByService.SelectMany(kv => kv.Value.Select(v => (v.InstrumentId, v.ChannelCode)))
            .Distinct()
            .ToList();
        if (allChannels.Count == 0)
        {
            return items;
        }

        var chosenInstrument = allChannels[random.Next(allChannels.Count)].InstrumentId;
        instrumentId = chosenInstrument;

        var channelsOfInstrument = allChannels.Where(c => c.InstrumentId == chosenInstrument).ToList();
        var count = Math.Min(channelsOfInstrument.Count, random.Next(1, 4));

        foreach (var (_, channelCode) in channelsOfInstrument.OrderBy(_ => random.Next()).Take(count))
        {
            var value = (random.Next(10, 1000) / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            items.Add(new ReadingItem(channelCode, value, null, null));
        }

        return items;
    }
}
