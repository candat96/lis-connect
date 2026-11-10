using System.Text.Json;
using Lis.Core.Drivers;
using Lis.Core.Drivers.BeckmanAu;
using Lis.Simulator;
using Lis.Simulator.Links;
using Lis.Simulator.Messages;
using Lis.Simulator.Sessions;

const string Usage = """
Bộ giả lập máy xét nghiệm — đóng vai MÁY nói chuyện với LIS.

Cách dùng:
  Lis.Simulator <lệnh> <kết nối> [tuỳ chọn]

Lệnh:
  replay --file x.listrace [--session s] [--speed 1|0]
      Phát lại các bản ghi máy → LIS trong tệp .listrace, giữ khoảng thời gian gốc
      (--speed 0 = không chờ), in byte LIS trả về.
  send --family hl7-mllp|astm-e1394|beckman-au --barcode 1234567890 --result WBC=7.5 --result HGB=135 [--qc]
      Dựng bản tin kết quả và gửi đúng thủ tục máy. Mã kênh viết đúng như máy gửi
      (AU: số Online Test No., vd --result 01=5.42).
  query --family astm-e1394|beckman-au --barcode 1234567890
      Hỏi chỉ định như máy, in câu trả lời của LIS.

Kết nối (chọn một):
  --connect host:port     LIS là TCP server
  --listen port           LIS là TCP client, giả lập chờ LIS nối vào
  --com COM3 --baud 9600 [--data-bits 8] [--parity None|Even|Odd] [--stop-bits 1|2]

Riêng Beckman AU (phải khớp màn Online trên máy / cấu hình driver của LIS):
  --class-b  --bcc  --test-digits 2|3  --sample-id-length N  --au-config '{"classB":true,...}'
""";

if (args.Length == 0 || args.Contains("--help") || args[0] is "-h" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

void Log(string line) => Console.WriteLine(line);

try
{
    var cl = CommandLine.Parse(args);
    switch (cl.Command)
    {
        case "replay":
        {
            var path = cl.Get("file") ?? throw new ArgumentException("Thiếu --file <x.listrace>");
            var entries = TraceReplayer.Load(path);
            Log($"Đọc {entries.Count} bản ghi từ {path}.");
            await using var link = await LinkFactory.OpenAsync(cl, Log, cts.Token);
            var stats = await new TraceReplayer(link, Log).RunAsync(entries, cl.Get("session"), cl.GetDouble("speed", 1), cts.Token);
            Log($"Xong: gửi {stats.BlocksSent} khối ({stats.BytesSent} byte), nhận {stats.BytesReceived} byte từ LIS.");
            return stats.BlocksSent > 0 ? 0 : 1;
        }

        case "send":
        {
            var family = Required(cl, "family");
            var barcode = Required(cl, "barcode");
            var results = cl.GetAll("result").Select(SimResult.Parse).ToList();
            if (results.Count == 0)
            {
                throw new ArgumentException("Cần ít nhất một --result MÃ=GIÁ_TRỊ");
            }

            var auOptions = family == "beckman-au" ? ParseAuOptions(cl) : null;
            await using var link = await LinkFactory.OpenAsync(cl, Log, cts.Token);
            var session = new MachineSession(link, Log);
            var qc = cl.Has("qc");
            var result = family switch
            {
                "hl7-mllp" => await session.SendHl7ResultAsync(barcode, results, qc, cts.Token),
                "astm-e1394" => await session.SendAstmResultAsync(barcode, results, qc, cts.Token),
                "beckman-au" => await session.SendAuResultAsync(auOptions!, barcode, results, qc, cts.Token),
                _ => throw new ArgumentException($"Họ giao thức không hỗ trợ: {family} (có: {string.Join(", ", DriverRegistry.SupportedFamilies)})"),
            };
            Log((result.Ok ? "THÀNH CÔNG: " : "THẤT BẠI: ") + result.Message);
            return result.Ok ? 0 : 1;
        }

        case "query":
        {
            var family = Required(cl, "family");
            var barcode = Required(cl, "barcode");
            var auOptions = family == "beckman-au" ? ParseAuOptions(cl) : null;
            if (family is not ("astm-e1394" or "beckman-au"))
            {
                throw new ArgumentException("query chỉ hỗ trợ astm-e1394 và beckman-au");
            }

            await using var link = await LinkFactory.OpenAsync(cl, Log, cts.Token);
            var session = new MachineSession(link, Log);
            var result = family == "astm-e1394"
                ? await session.QueryAstmAsync(barcode, cts.Token)
                : await session.QueryAuAsync(auOptions!, barcode, cts.Token);
            Log((result.Ok ? "THÀNH CÔNG: " : "THẤT BẠI: ") + result.Message);
            return result.Ok ? 0 : 1;
        }

        default:
            Console.Error.WriteLine($"Lệnh không biết: {cl.Command}\n");
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex) when (ex is ArgumentException or IOException or System.Net.Sockets.SocketException or JsonException or FormatException)
{
    Console.Error.WriteLine($"Lỗi: {ex.Message}");
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Đã dừng.");
    return 130;
}

static string Required(CommandLine cl, string name) =>
    cl.Get(name) ?? throw new ArgumentException($"Thiếu --{name}");

static BeckmanAuOptions ParseAuOptions(CommandLine cl)
{
    var options = BeckmanAuOptions.Au480;
    if (cl.Get("au-config") is { } json)
    {
        options = JsonSerializer.Deserialize<BeckmanAuOptions>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("--au-config rỗng");
    }

    options = options with
    {
        ClassB = options.ClassB || cl.Has("class-b"),
        Bcc = options.Bcc || cl.Has("bcc"),
        TestNoDigits = cl.GetInt("test-digits", options.TestNoDigits),
        SampleIdLength = cl.GetInt("sample-id-length", options.SampleIdLength),
    };
    options.Validate();
    return options;
}
