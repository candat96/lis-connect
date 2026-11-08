using System.Globalization;
using System.Text;
using Dapper;
using Lis.Core.Instruments;
using Lis.Data;
using Lis.Data.Instruments;

namespace Lis.SeedTool;

/// <summary>
/// Lệnh "seed-rawlog": sinh log thô GIẢ cho các máy đã khai trong CSDL dev, để thử màn "Log thô"
/// khi chưa có máy thật. Mỗi máy vài phiên/ngày trong <c>--days</c> ngày gần nhất, đúng khung của
/// họ giao thức (ASTM E1381 có ENQ/ACK/checksum, HL7 MLLP có ACK, Beckman AU, dòng chữ DIRUI H),
/// kèm sự kiện kết nối, một phiên rớt kết nối giữa chừng và một phiên rác do sai baud.
/// </summary>
/// <remarks>
/// Mã vạch lấy từ bảng <c>sample</c> nếu có (chạy <c>seed-demo</c> trước để tìm được trên màn Log
/// thô), mã kênh lấy từ map kênh của từng máy. Tên người bệnh luôn là tên giả "Mẫu"/"Thử".
/// </remarks>
internal static class SeedRawLogCommand
{
    private const char Stx = '\u0002';
    private const char Etx = '\u0003';
    private const char Eot = '\u0004';
    private const char Enq = '\u0005';
    private const char Ack = '\u0006';
    private const char Vt = '\u000B';
    private const char Fs = '\u001C';

    private sealed record Target(Guid Id, string Name, string? Model, string? ProtocolFamily, string? ConnectionJson);

    public static async Task<int> RunAsync(string[] args)
    {
        string? connectionString = null;
        var days = 3;
        var root = RawLogStore.DefaultRoot;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--conn" && i + 1 < args.Length)
            {
                connectionString = args[++i];
            }
            else if (args[i] == "--days" && i + 1 < args.Length && int.TryParse(args[i + 1], out var d))
            {
                days = Math.Max(1, d);
                i++;
            }
            else if (args[i] == "--root" && i + 1 < args.Length)
            {
                root = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Thiếu --conn <chuỗi kết nối>");
            return 1;
        }

        var db = Db.Create(connectionString);
        await using var connection = await db.OpenAsync();
        var targets = (await connection.QueryAsync<Target>(
            "SELECT id, name, model, protocol_family AS ProtocolFamily, connection::text AS ConnectionJson FROM instrument WHERE active ORDER BY name")).ToList();
        var channelRows = await connection.QueryAsync<(Guid InstrumentId, string ChannelCode)>(
            "SELECT instrument_id, channel_code FROM instrument_channel ORDER BY channel_code");
        var channels = channelRows.GroupBy(r => r.InstrumentId).ToDictionary(g => g.Key, g => g.Select(r => r.ChannelCode).ToList());
        var barcodes = (await connection.QueryAsync<string>("SELECT barcode FROM sample ORDER BY created_at DESC LIMIT 200")).ToList();
        if (barcodes.Count == 0)
        {
            barcodes = Enumerable.Range(1, 40).Select(n => $"9{DateTime.Now:yyMMdd}{n:000}").ToList();
        }

        var store = new RawLogStore(root);
        var random = new Random(20261108);
        var totalEntries = 0;
        var seeded = new List<string>();

        foreach (var target in targets)
        {
            var family = target.ProtocolFamily;
            var codes = channels.GetValueOrDefault(target.Id) ?? [];
            var settings = ConnectionSettings.Parse(target.ConnectionJson);
            if (settings.Kind == ConnectionKind.None)
            {
                continue; // chưa khai cách nối thì chưa có log
            }

            var count = 0;
            for (var dayOffset = days - 1; dayOffset >= 0; dayOffset--)
            {
                var day = DateTime.Today.AddDays(-dayOffset);
                var clock = new DateTimeOffset(day.AddHours(7).AddMinutes(random.Next(0, 30)));
                var session = $"{day:yyMMdd}-1";
                var log = new LogWriter(store, target.Id, session, clock, random);

                log.Event(settings.Kind switch
                {
                    ConnectionKind.Serial => $"Mở cổng {settings.Describe()}",
                    ConnectionKind.TcpServer => $"Mở cổng TCP {settings.TcpPort}, chờ máy nối vào",
                    _ => $"Nối tới {settings.Host}:{settings.TcpPort}",
                });
                if (settings.Kind == ConnectionKind.TcpServer)
                {
                    log.Event($"Máy nối vào từ 192.168.1.{random.Next(20, 200)}:{random.Next(49152, 65000)}");
                }

                var messages = random.Next(4, 9);
                for (var m = 0; m < messages; m++)
                {
                    log.Pause(random.Next(60, 900));
                    var barcode = barcodes[random.Next(barcodes.Count)];
                    var picked = codes.OrderBy(_ => random.Next()).Take(Math.Min(codes.Count, random.Next(3, 9))).ToList();
                    WriteMessage(log, family, target, barcode, picked, random, withQuery: m == 0);
                }

                // Ngày giữa: rớt kết nối giữa chừng rồi nối lại — để thấy sự kiện lỗi trên màn log.
                if (dayOffset == 1)
                {
                    log.Pause(30);
                    log.In($"{Stx}2R|3|^^^");
                    log.Event(settings.Kind == ConnectionKind.Serial ? "Lỗi: hết thời gian chờ khung tiếp theo (15 s), bỏ bản tin dở" : "Mất kết nối (máy đóng socket)");
                    log.Pause(20);
                    log.Event(settings.Kind == ConnectionKind.Serial ? $"Mở lại cổng {settings.PortName}" : "Máy nối lại");
                }

                log.Pause(random.Next(300, 1200));
                log.Event("Đóng kết nối (hết ca)");
                count += log.Count;
            }

            // Một máy có thêm phiên rác do sai baud — để thử phần "đoán giao thức"/xem hex.
            if (target.Name == "Mindray BC-3200")
            {
                var log = new LogWriter(store, target.Id, $"{DateTime.Today:yyMMdd}-sai-baud", new DateTimeOffset(DateTime.Today.AddHours(6)), random);
                log.Event("Mở cổng COM1 · 19200 8N1 (cấu hình thử sai)");
                var garbage = new byte[48];
                random.NextBytes(garbage);
                log.InBytes(garbage);
                log.Event("Đóng cổng — đổi lại 9600 7N1");
                count += log.Count;
            }

            totalEntries += count;
            seeded.Add($"{target.Name} ({count})");
        }

        Console.WriteLine($"Đã ghi {totalEntries} bản ghi log thô cho {seeded.Count} máy vào {root}");
        Console.WriteLine("  " + string.Join(", ", seeded));
        return 0;
    }

    private static void WriteMessage(LogWriter log, string? family, Target target, string barcode, List<string> codes, Random random, bool withQuery)
    {
        switch (family)
        {
            case "astm-e1394":
                if (withQuery)
                {
                    // Máy hỏi chỉ định (host query) trước khi chạy mẫu.
                    AstmTransfer(log, target, [$"H|\\^&|||{Model(target)}|||||||P|1|{Ts(log.Now)}", $"Q|1|^{barcode}||^^^ALL||||||||O", "L|1|N"], fromInstrument: true);
                    log.Pause(1);
                    AstmTransfer(log, target, [$"H|\\^&|||LIS Connect|||||||P|1|{Ts(log.Now)}", "P|1", $"O|1|{barcode}||{string.Join("\\", codes.Select(c => "^^^" + c))}|R||||||A", "L|1|N"], fromInstrument: false);
                    log.Pause(random.Next(120, 400));
                }

                var records = new List<string>
                {
                    $"H|\\^&|||{Model(target)}|||||||P|1|{Ts(log.Now)}",
                    "P|1||||Nguyen^Van Mau", // máy ASTM thường chỉ gửi ASCII không dấu
                    $"O|1|{barcode}||^^^ALL|R||||||||||||||||||||F",
                };
                records.AddRange(codes.Select((code, i) => $"R|{i + 1}|^^^{code}|{Value(code, random)}|||N||F||||{Ts(log.Now)}"));
                records.Add("L|1|N");
                AstmTransfer(log, target, records, fromInstrument: true);
                break;

            case "hl7-mllp":
                var id = random.Next(1000, 9999);
                var obx = codes.Select((code, i) => $"OBX|{i + 1}|NM|{code}^^LN||{Value(code, random)}||||||F");
                var oru = string.Join("\r", new[]
                {
                    $"MSH|^~\\&|{Model(target)}|Lab|LIS||{Ts(log.Now)}||ORU^R01|{id}|P|2.3.1||||||UNICODE",
                    "PID|1||BN0001^^^^MR||Tran^Thi Thu",
                    $"OBR|1||{barcode}|00001^Automated Count^99MRC|||{Ts(log.Now)}",
                }.Concat(obx)) + "\r";
                log.In($"{Vt}{oru}{Fs}\r");
                log.Pause(0.2);
                log.Out($"{Vt}MSH|^~\\&|LIS||{Model(target)}|Lab|{Ts(log.Now)}||ACK^R01|{id}|P|2.3.1\rMSA|AA|{id}\r{Fs}\r");
                break;

            case "beckman-au":
                log.In($"{Stx}DB{Etx}");
                log.Out($"{Ack}");
                var sampleNo = random.Next(1, 999).ToString("0000");
                var tests = string.Concat(codes.Select(code => $"{code}{Value(code, random),9} "));
                log.Pause(0.5);
                log.In($"{Stx}D 0101{sampleNo}  {barcode,-20}E{tests}{Etx}");
                log.Out($"{Ack}");
                log.Pause(0.5);
                log.In($"{Stx}DE{Etx}");
                log.Out($"{Ack}");
                break;

            case "dirui-h":
                var lines = codes.Select(code => $" {code,-4}{Value(code, random),10}");
                log.In($"{Stx}\r\nDate:{log.Now:yyyy-MM-dd}  {log.Now:HH:mm}\r\nNo.{random.Next(1, 999):0000}\r\nID:{barcode}\r\n{string.Join("\r\n", lines)}\r\n{Etx}");
                break;

            case "sysmex-ca":
                goto case "astm-e1394";

            case "tosoh-g8":
                log.In($"{Stx}D|{barcode,-20}|01|{random.Next(1, 99):000}|{5 + random.NextDouble() * 4:0.0}|{Etx}{(char)random.Next(0x30, 0x7F)}");
                log.Out($"{Ack}");
                log.Pause(0.2);
                log.In($"{Eot}");
                break;

            default:
                // Họ chưa rõ / fixed-offset / văn bản: máy đẩy nội dung bản in.
                var text = $"{Model(target)}\r\nSample ID: {barcode}\r\n{string.Join("\r\n", codes.Select(code => $"{code,-6} {Value(code, random)}"))}\r\n\r\n";
                log.In(text);
                break;
        }
    }

    /// <summary>Một lần truyền ASTM E1381: ENQ/ACK, mỗi record một khung có số khung + checksum, ACK từng khung, EOT.</summary>
    private static void AstmTransfer(LogWriter log, Target target, List<string> records, bool fromInstrument)
    {
        void Send(string text)
        {
            if (fromInstrument)
            {
                log.In(text);
            }
            else
            {
                log.Out(text);
            }
        }

        void Reply(string text)
        {
            if (fromInstrument)
            {
                log.Out(text);
            }
            else
            {
                log.In(text);
            }
        }

        Send($"{Enq}");
        log.Pause(0.05);
        Reply($"{Ack}");
        for (var i = 0; i < records.Count; i++)
        {
            var frameNumber = (char)('0' + ((i + 1) % 8));
            var body = $"{frameNumber}{records[i]}\r{Etx}";
            var checksum = Encoding.Latin1.GetBytes(body).Sum(b => b) % 256;
            log.Pause(0.05);
            Send($"{Stx}{body}{checksum:X2}\r\n");
            log.Pause(0.02);
            Reply($"{Ack}");
        }

        log.Pause(0.05);
        Send($"{Eot}");
    }

    private static string Model(Target target) => (target.Model ?? target.Name).Split(' ', 2).Last().Split('/')[0].Trim();

    private static string Ts(DateTimeOffset time) => time.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private static string Value(string code, Random random) => code.ToUpperInvariant() switch
    {
        "NIT" or "U-NIT" => random.Next(5) == 0 ? "Pos" : "Neg",
        "LEU" or "PRO" or "BLD" or "KET" => new[] { "Neg", "Neg", "Trace", "1+" }[random.Next(4)],
        "PH" => (5 + random.Next(0, 6) * 0.5).ToString("0.0", CultureInfo.InvariantCulture),
        "SG" => (1.005 + random.Next(0, 6) * 0.005).ToString("0.000", CultureInfo.InvariantCulture),
        _ => (random.NextDouble() * 150 + 1).ToString("0.0", CultureInfo.InvariantCulture),
    };

    /// <summary>Ghi tuần tự các bản ghi của một phiên, đồng hồ giả tăng dần.</summary>
    private sealed class LogWriter(RawLogStore store, Guid instrumentId, string session, DateTimeOffset start, Random random)
    {
        public DateTimeOffset Now { get; private set; } = start;

        public int Count { get; private set; }

        public void Pause(double seconds) => Now = Now.AddSeconds(seconds).AddMilliseconds(random.Next(0, 40));

        public void Event(string note) => Append(RawLogDirection.Event, [], note);

        public void In(string text) => Append(RawLogDirection.In, Encoding.Latin1.GetBytes(text));

        public void InBytes(byte[] bytes) => Append(RawLogDirection.In, bytes);

        public void Out(string text) => Append(RawLogDirection.Out, Encoding.Latin1.GetBytes(text));

        private void Append(RawLogDirection direction, byte[] bytes, string? note = null)
        {
            store.Append(instrumentId, new RawLogEntry(Now, direction, session, bytes, note));
            Count++;
        }
    }
}
