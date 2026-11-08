using System.Globalization;
using System.Text;
using Lis.Core.Ingest;

namespace Lis.Core.Drivers.Hl7;

/// <summary>
/// Driver họ "hl7-mllp" theo Mindray BC-5380/BC-5180 (Phụ lục C sổ tay vận hành — xem
/// <c>docs/drivers/mindray-bc5380.md</c>): cắt khung MLLP <c>&lt;VT&gt;…&lt;FS&gt;&lt;CR&gt;</c>,
/// tách <c>ORU^R01</c> thành kết quả, trả <c>ACK^R01</c> ngay (máy chỉ chờ 10 giây và không gửi
/// lại), nhận câu hỏi chỉ định <c>ORM^O01</c> và dựng <c>ORR^O02</c>.
/// </summary>
public sealed class Hl7MllpDriver : IInstrumentDriver
{
    private const byte StartBlock = 0x0B;
    private const byte EndBlock = 0x1C;
    private const byte CarriageReturn = 0x0D;

    /// <summary>Bản tin có ảnh Base64 dài hàng trăm KB; quá 2 MB coi là rác, bỏ để không phình bộ nhớ.</summary>
    private const int MaxFrameBytes = 2 * 1024 * 1024;

    private static readonly TimeSpan StaleFrame = TimeSpan.FromSeconds(30);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Mã chỉ số phân loại bạch cầu — có trong chỉ định thì yêu cầu máy chạy CBC+5DIFF.</summary>
    private static readonly HashSet<string> DiffCodes =
        ["704-7", "706-2", "751-8", "770-8", "711-2", "713-8", "731-0", "736-9", "742-7", "5905-5", "26477-0", "13046-8", "10000", "10001"];

    /// <summary>
    /// OBX dạng số nhưng không phải kết quả: tuổi (thông tin chạy mẫu), vạch/độ dài histogram,
    /// thông số nội bộ GRAN-X/Y, WBC-MCV. Đưa vào sẽ chỉ làm đầy màn Chưa ghép.
    /// </summary>
    private static bool IsNonResultCode(string code) =>
        code is "30525-0" or "10003" or "10004" or "10005" or "10006"
        || (code.Length == 5 && code.StartsWith("15", StringComparison.Ordinal));

    private readonly List<byte> _frame = [];
    private bool _inFrame;
    private DateTimeOffset _frameStarted;
    private int _controlId;

    public string Family => "hl7-mllp";

    public DriverOutput Receive(ReadOnlySpan<byte> data, DateTimeOffset now)
    {
        var output = new DriverOutput();
        var skipped = 0;
        foreach (var b in data)
        {
            if (!_inFrame)
            {
                if (b == StartBlock)
                {
                    _inFrame = true;
                    _frameStarted = now;
                    _frame.Clear();
                }
                else if (b is not (CarriageReturn or 0x0A))
                {
                    skipped++;
                }

                continue;
            }

            if (b == StartBlock)
            {
                // VT giữa chừng: máy bắt đầu lại bản tin (vd mất ACK rồi gửi bản sau) — bỏ phần dở.
                output.Notes.Add($"Bỏ bản tin dở {_frame.Count} byte (gặp VT mới trước FS)");
                _frame.Clear();
                _frameStarted = now;
                continue;
            }

            if (b == EndBlock)
            {
                // CR sau FS là phần đuôi khung; gặp ở vòng sau khi !_inFrame thì bỏ qua.
                _inFrame = false;
                HandleMessage(Decode(_frame.ToArray()), now, output);
                _frame.Clear();
                continue;
            }

            if (_frame.Count >= MaxFrameBytes)
            {
                output.Notes.Add($"Bản tin vượt {MaxFrameBytes / 1024 / 1024} MB — bỏ");
                _frame.Clear();
                _inFrame = false;
                continue;
            }

            _frame.Add(b);
        }

        if (skipped > 0)
        {
            output.Notes.Add($"Bỏ {skipped} byte ngoài khung MLLP");
        }

        return output;
    }

    public DriverOutput Tick(DateTimeOffset now)
    {
        var output = new DriverOutput();
        if (_inFrame && now - _frameStarted > StaleFrame)
        {
            output.Notes.Add($"Bỏ bản tin dở {_frame.Count} byte (quá {StaleFrame.TotalSeconds:0} giây không thấy FS)");
            _frame.Clear();
            _inFrame = false;
        }

        return output;
    }

    public DriverOutput AnswerQuery(DriverQuery query, IReadOnlyList<string> channelCodes, DateTimeOffset now)
    {
        var output = new DriverOutput();
        if (query.Context is not QueryContext context)
        {
            return output;
        }

        if (channelCodes.Count == 0)
        {
            output.Send.Add(Frame(OrrHeader(context, now, "AR")));
            output.Notes.Add($"Không có chỉ định cho mẫu {query.Barcode} — trả AR");
            return output;
        }

        var testMode = channelCodes.Any(c => DiffCodes.Contains(c.Trim())) ? "CBC+5DIFF" : "CBC";
        var barcode = Hl7Message.EscapeText(query.Barcode);
        var text = new StringBuilder(OrrHeader(context, now, "AA"));
        text.Append("PID|1\r");
        text.Append("PV1|1\r");
        text.Append("ORC|AF|").Append(barcode).Append('\r');
        text.Append("OBR|1|").Append(barcode).Append('\r');
        text.Append("OBX|1|IS|08003^Test Mode^99MRC||").Append(testMode).Append("||||||F\r");
        output.Send.Add(Frame(text.ToString()));
        output.Notes.Add($"Trả chỉ định mẫu {query.Barcode}: {testMode}");
        return output;
    }

    public void Reset()
    {
        _frame.Clear();
        _inFrame = false;
    }

    /// <summary>Dữ liệu câu hỏi chỉ định cần giữ để dựng ORR^O02 (MSA-2 = MSH-10 bản tin hỏi).</summary>
    internal sealed record QueryContext(string ControlId, string ProcessingId);

    private static string Decode(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private void HandleMessage(string text, DateTimeOffset now, DriverOutput output)
    {
        Hl7Message? message;
        try
        {
            message = Hl7Message.Parse(text);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            message = null;
        }

        if (message is null)
        {
            output.Notes.Add("Bản tin không bắt đầu bằng MSH hợp lệ — trả AR");
            output.Send.Add(Frame(Ack(now, "", "P", "AR", "200", "ACK")));
            return;
        }

        var msh = message.Msh;
        var controlId = message.Value(msh, 10);
        var processingId = message.Comp(msh, 11, 1);
        var type = message.Comp(msh, 9, 1);
        var trigger = message.Comp(msh, 9, 2);

        if (controlId.Length == 0)
        {
            output.Notes.Add("Bản tin thiếu MSH-10 — trả AE 101");
            output.Send.Add(Frame(Ack(now, controlId, processingId, "AE", "101", AckType(type))));
            return;
        }

        switch (type)
        {
            case "ORU":
                HandleResult(message, processingId, output);
                output.Send.Add(Frame(Ack(now, controlId, processingId, "AA", null, "ACK^R01")));
                break;

            case "ORM":
                HandleQuery(message, controlId, processingId, now, output);
                break;

            default:
                output.Notes.Add($"Không hỗ trợ bản tin {type}^{trigger} — trả AR 200");
                output.Send.Add(Frame(Ack(now, controlId, processingId, "AR", "200", AckType(type))));
                break;
        }
    }

    private static string AckType(string type) => type == "ORM" ? "ORR^O02" : "ACK^R01";

    /// <summary>
    /// ORU^R01 → một <see cref="DriverReading"/> cho mỗi mã mẫu (OBR-3) theo thứ tự xuất hiện: OBR
    /// đếm máy + OBR soi tay cùng mã gộp làm một; QC (MSH-11 = Q/T) mỗi OBR là một điểm QC riêng.
    /// MSH-11 = D (cài đặt QC) không có kết quả.
    /// </summary>
    private static void HandleResult(Hl7Message message, string processingId, DriverOutput output)
    {
        if (processingId == "D")
        {
            output.Notes.Add("Bản tin cài đặt QC (MSH-11 = D) — chỉ ACK");
            return;
        }

        var isQc = processingId is "Q" or "T";
        var readings = new List<(string Barcode, List<ReadingItem> Items)>();
        List<ReadingItem>? current = null;

        foreach (var segment in message.Segments)
        {
            switch (segment[0])
            {
                case "OBR":
                {
                    var barcode = message.Value(segment, 3).Trim();
                    var existing = isQc ? -1 : readings.FindIndex(r => r.Barcode == barcode);
                    if (existing >= 0)
                    {
                        current = readings[existing].Items;
                    }
                    else
                    {
                        current = [];
                        readings.Add((barcode, current));
                    }

                    break;
                }

                case "OBX" when current is not null:
                {
                    var item = ToItem(message, segment);
                    if (item is not null)
                    {
                        current.Add(item);
                    }

                    break;
                }
            }
        }

        if (readings.Count == 0)
        {
            output.Notes.Add("Bản tin ORU không có OBR — không có kết quả");
            return;
        }

        foreach (var (barcode, items) in readings)
        {
            output.Readings.Add(new DriverReading(barcode, isQc, items, message.Text));
        }
    }

    /// <summary>
    /// Chỉ lấy OBX kết quả: NM (số) và ST (hình thái, nhóm máu). Bỏ ED (ảnh/biểu đồ), IS (thông
    /// tin chạy mẫu, cờ cảnh báo T/F), mã không phải kết quả, và giá trị rỗng / "***.*" (máy không
    /// có kết quả — không được lưu thành 0).
    /// </summary>
    private static ReadingItem? ToItem(Hl7Message message, string[] segment)
    {
        var valueType = Hl7Message.Field(segment, 2).Trim();
        if (valueType is not ("NM" or "ST"))
        {
            return null;
        }

        var code = message.Comp(segment, 3, 1).Trim();
        if (code.Length == 0 || IsNonResultCode(code))
        {
            return null;
        }

        var value = message.Value(segment, 5).Trim();
        if (value.Length == 0 || value.All(c => c is '*' or '.'))
        {
            return null;
        }

        var unit = message.Value(segment, 6).Trim();
        var flag = message.Value(segment, 8).Trim();
        return new ReadingItem(code, value, unit.Length == 0 ? null : unit, flag.Length == 0 ? null : flag);
    }

    private void HandleQuery(Hl7Message message, string controlId, string processingId, DateTimeOffset now, DriverOutput output)
    {
        var context = new QueryContext(controlId, processingId.Length == 0 ? "P" : processingId);
        var orc = message.Segments.FirstOrDefault(s => s[0] == "ORC");
        var barcode = orc is null ? "" : message.Value(orc, 3).Trim();

        // Autoloader đọc barcode lỗi gửi "Invalid" — tài liệu yêu cầu trả AR ngay, không cần tra.
        if (barcode.Length == 0 || barcode.Equals("Invalid", StringComparison.OrdinalIgnoreCase))
        {
            output.Notes.Add(barcode.Length == 0 ? "Câu hỏi chỉ định thiếu ORC-3 — trả AR" : "Máy đọc barcode lỗi (Invalid) — trả AR");
            output.Send.Add(Frame(OrrHeader(context, now, "AR")));
            return;
        }

        output.Queries.Add(new DriverQuery(barcode, controlId, context));
    }

    private string Ack(DateTimeOffset now, string controlId, string processingId, string code, string? error, string messageType)
    {
        var msa = $"MSA|{code}|{Hl7Message.EscapeText(controlId)}";
        if (error is not null)
        {
            msa += $"||||{error}";
        }

        return Header(now, messageType, processingId.Length == 0 ? "P" : processingId) + msa + "\r";
    }

    private string OrrHeader(QueryContext context, DateTimeOffset now, string code) =>
        Header(now, "ORR^O02", context.ProcessingId) + $"MSA|{code}|{Hl7Message.EscapeText(context.ControlId)}\r";

    /// <summary>MSH của LIS — luôn dùng phân cách chuẩn "|^~\&amp;" như bản tin mẫu của hãng.</summary>
    private string Header(DateTimeOffset now, string messageType, string processingId)
    {
        _controlId++;
        var timestamp = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return $"MSH|^~\\&|LIS||||{timestamp}||{messageType}|{_controlId.ToString(CultureInfo.InvariantCulture)}|{processingId}|2.3.1||||||UNICODE\r";
    }

    private static byte[] Frame(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var frame = new byte[body.Length + 3];
        frame[0] = StartBlock;
        body.CopyTo(frame, 1);
        frame[^2] = EndBlock;
        frame[^1] = CarriageReturn;
        return frame;
    }
}
