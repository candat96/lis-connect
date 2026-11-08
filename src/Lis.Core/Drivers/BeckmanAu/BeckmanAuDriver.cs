using System.Text;
using Lis.Core.Ingest;

namespace Lis.Core.Drivers.BeckmanAu;

/// <summary>
/// Thông tin một câu hỏi chỉ định <c>R␣</c>/<c>RH</c>/<c>Rh</c> — các trường cố định phải chép nguyên
/// vào bản tin trả lời <c>S␣</c>/<c>SH</c>/<c>Sh</c> (docs/drivers/beckman-au.md mục 5.3). Các chuỗi giữ
/// nguyên độ rộng như máy gửi (không trim).
/// </summary>
public sealed record AuQueryContext(
    string Code, string Rack, string Cup, string SampleType, string SampleNo, string SampleIdRaw, DateTimeOffset ReceivedAt);

/// <summary>
/// Driver giao thức "AU Online" của Beckman Coulter (AU480/AU680, cùng họ AU2700/AU5800) — không phải
/// ASTM. Bản tin <c>[Start][mã phân biệt 2][System No. 0/2][dữ liệu cột cố định][End][BCC 0/1]</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Kết quả: phiên <c>DB</c> → <c>D␣</c>/<c>DH</c>/<c>d␣</c>/<c>dH</c>/<c>DQ</c> → <c>DE</c>; bản tin
/// nhiều khối (<c>0</c>…<c>9</c>, cuối <c>E</c>) được gom theo mẫu rồi mới tạo <see cref="DriverReading"/>.
/// <c>DA</c> (chuẩn) và <c>DR</c> (reagent blank) bỏ qua, chỉ ghi chú.</item>
/// <item>Mã kênh = Online Test No. <b>đúng như máy gửi</b> (giữ số 0 đầu, vd "01" hoặc "001") — map kênh
/// trong LIS phải khai đúng số chữ số đang đặt ở "Online Test No. Digit".</item>
/// <item>Giá trị: bỏ dấu cách/số 0 đệm ("␣␣5.42" → "5.42", "0123.4" → "123.4"). Toàn số 9 (vượt định
/// dạng), toàn dấu cách, hoặc cờ <c>/</c> (chưa đo) → không tạo kênh, ghi chú — không bao giờ lưu 999999.</item>
/// <item>Độ dài phần kết quả không khớp hồ sơ cấu hình → bỏ cả bản tin (cột lệch thì giá trị sẽ vào nhầm
/// kênh); bản thô vẫn nằm trong log thô để chỉnh hồ sơ.</item>
/// <item>Class B: ACK mỗi khung đúng, NAK khung sai BCC; bản tin LIS gửi chờ ACK, NAK thì gửi lại theo Retry.</item>
/// <item>Hỏi chỉ định: tạo <see cref="DriverQuery"/>; lớp chạy phải gọi <see cref="AnswerQuery"/> trong T1
/// (~2 s) — trả muộn máy báo 6031 và bỏ qua.</item>
/// </list>
/// </remarks>
public sealed class BeckmanAuDriver : IInstrumentDriver
{
    private const byte Ack = 0x06;
    private const byte Nak = 0x15;
    private const byte Etb = 0x17;

    private readonly BeckmanAuOptions _options;

    // Tách khung.
    private readonly List<byte> _frame = [];
    private bool _inFrame;
    private bool _awaitBcc;
    private byte _terminator;
    private DateTimeOffset _frameStart;
    private int _garbage;

    // Kết quả nhiều khối đang gom.
    private PendingResult? _pending;

    // Bản tin LIS gửi đi.
    private readonly Queue<byte[]> _outQueue = new();
    private byte[]? _awaitingAck;
    private DateTimeOffset _awaitingSince;
    private int _retriesLeft;
    private DateTimeOffset _nextClassASend;

    public BeckmanAuDriver(BeckmanAuOptions? options = null)
    {
        _options = options ?? BeckmanAuOptions.Au480;
        _options.Validate();
    }

    public string Family => "beckman-au";

    public BeckmanAuOptions Options => _options;

    public DriverOutput Receive(ReadOnlySpan<byte> data, DateTimeOffset now)
    {
        var output = new DriverOutput();
        foreach (var b in data)
        {
            if (_awaitBcc)
            {
                _awaitBcc = false;
                var expected = ComputeBcc(_frame, _terminator);
                CompleteFrame(output, now, bccOk: b == expected, receivedBcc: b, expectedBcc: expected);
                continue;
            }

            if (!_inFrame)
            {
                if (b == _options.StartCode)
                {
                    FlushGarbage(output);
                    _inFrame = true;
                    _frame.Clear();
                    _frameStart = now;
                }
                else if (b == Ack || b == Nak)
                {
                    FlushGarbage(output);
                    HandleResponse(b == Ack, output, now);
                }
                else
                {
                    _garbage++;
                }

                continue;
            }

            if (b == _options.StartCode)
            {
                output.Notes.Add($"Bỏ khung dở {_frame.Count} byte: gặp mã bắt đầu mới trước mã kết thúc");
                _frame.Clear();
                _frameStart = now;
            }
            else if (b == _options.EndCode || b == Etb)
            {
                _terminator = b;
                if (_options.Bcc)
                {
                    _awaitBcc = true;
                }
                else
                {
                    CompleteFrame(output, now, bccOk: true, receivedBcc: 0, expectedBcc: 0);
                }
            }
            else if (_frame.Count >= _options.TextLength + 16)
            {
                output.Notes.Add($"Bỏ khung dài quá {_options.TextLength} byte (thiếu mã kết thúc?)");
                _inFrame = false;
                _frame.Clear();
            }
            else
            {
                _frame.Add(b);
            }
        }

        FlushGarbage(output);
        return output;
    }

    public DriverOutput Tick(DateTimeOffset now)
    {
        var output = new DriverOutput();
        if ((_inFrame || _awaitBcc) && now - _frameStart > _options.FrameTimeout)
        {
            output.Notes.Add($"Bỏ khung nhận dở {_frame.Count} byte: quá {_options.FrameTimeout.TotalSeconds:0.#} s không thấy mã kết thúc");
            _inFrame = false;
            _awaitBcc = false;
            _frame.Clear();
        }

        if (_pending is not null && now - _pending.StartedAt > _options.MultiBlockTimeout)
        {
            output.Notes.Add($"Bỏ kết quả mẫu {_pending.Describe()}: không nhận được khối cuối (E)");
            _pending = null;
        }

        if (_options.ClassB)
        {
            // T4 (~2 s): máy không ACK/NAK → coi như mất, chuyển bản tin kế.
            if (_awaitingAck is not null && now - _awaitingSince > _options.AnswerTimeout * 2)
            {
                output.Notes.Add("Máy không trả ACK/NAK cho bản tin LIS gửi — bỏ qua");
                _awaitingAck = null;
                SendNextClassB(output, now);
            }
        }
        else if (_outQueue.Count > 0 && now >= _nextClassASend)
        {
            output.Send.Add(_outQueue.Dequeue());
            _nextClassASend = now + _options.ClassAInterval;
        }

        return output;
    }

    public DriverOutput AnswerQuery(DriverQuery query, IReadOnlyList<string> channelCodes, DateTimeOffset now)
    {
        var output = new DriverOutput();
        if (query.Context is not AuQueryContext ctx)
        {
            output.Notes.Add("Câu hỏi chỉ định không phải của driver AU — bỏ qua");
            return output;
        }

        if (now - ctx.ReceivedAt > _options.AnswerTimeout)
        {
            output.Notes.Add($"Trả lời chỉ định mẫu {ctx.SampleIdRaw.Trim()} muộn {(now - ctx.ReceivedAt).TotalSeconds:0.0} s (> T1 {_options.AnswerTimeout.TotalSeconds:0.#} s) — máy có thể đã bỏ qua");
        }

        var tests = new List<string>();
        foreach (var raw in channelCodes)
        {
            var code = raw.Trim();
            if (code.Length == 0 || !code.All(char.IsAsciiDigit) || code.Length > _options.TestNoDigits)
            {
                output.Notes.Add($"Bỏ mã kênh '{raw}' khi trả chỉ định: không phải Online Test No. {_options.TestNoDigits} chữ số");
                continue;
            }

            tests.Add(code.PadLeft(_options.TestNoDigits, '0'));
        }

        tests = tests.Distinct().ToList();
        List<string> texts;
        if (tests.Count == 0 && _options.NoOrderReply == AuNoOrderReply.ResponseStop)
        {
            texts = ["SE"];
        }
        else
        {
            texts = BuildRequisition(ctx, tests);
        }

        Enqueue(texts.Select(BuildFrame).ToList(), output, now);
        return output;
    }

    public void Reset()
    {
        _frame.Clear();
        _inFrame = false;
        _awaitBcc = false;
        _garbage = 0;
        _pending = null;
        _outQueue.Clear();
        _awaitingAck = null;
    }

    // ---------------- Tách khung ----------------

    private static byte ComputeBcc(List<byte> text, byte terminator)
    {
        byte bcc = 0;
        foreach (var b in text)
        {
            bcc ^= b;
        }

        return (byte)(bcc ^ terminator);
    }

    private void FlushGarbage(DriverOutput output)
    {
        if (_garbage > 0)
        {
            output.Notes.Add($"Bỏ {_garbage} byte ngoài khung");
            _garbage = 0;
        }
    }

    private void CompleteFrame(DriverOutput output, DateTimeOffset now, bool bccOk, byte receivedBcc, byte expectedBcc)
    {
        _inFrame = false;
        var text = Encoding.Latin1.GetString(_frame.ToArray());
        _frame.Clear();

        if (!bccOk)
        {
            output.Notes.Add($"BCC sai (nhận {receivedBcc:X2}, tính {expectedBcc:X2}) — bỏ bản tin \"{Shorten(text)}\"{(_options.ClassB ? ", đã NAK" : "")}");
            if (_options.ClassB)
            {
                output.Send.Add([Nak]);
            }

            return;
        }

        if (_options.ClassB)
        {
            output.Send.Add([Ack]);
        }

        HandleMessage(text, output, now);
    }

    private void HandleResponse(bool ack, DriverOutput output, DateTimeOffset now)
    {
        if (!_options.ClassB || _awaitingAck is null)
        {
            if (!ack)
            {
                output.Notes.Add("Máy gửi NAK nhưng LIS không có bản tin nào đang chờ xác nhận");
            }

            return;
        }

        if (ack)
        {
            _awaitingAck = null;
            SendNextClassB(output, now);
            return;
        }

        if (_retriesLeft > 0)
        {
            _retriesLeft--;
            output.Notes.Add("Máy NAK bản tin LIS gửi — gửi lại");
            output.Send.Add(_awaitingAck);
            _awaitingSince = now;
        }
        else
        {
            output.Notes.Add("Máy NAK bản tin LIS gửi, hết số lần gửi lại — bỏ");
            _awaitingAck = null;
            SendNextClassB(output, now);
        }
    }

    // ---------------- Bản tin máy gửi ----------------

    /// <summary>ETB chỉ báo khối giữa; thứ tự khối đã có ở Data classification No. nên xử lý như mã kết thúc.</summary>
    private void HandleMessage(string text, DriverOutput output, DateTimeOffset now)
    {
        if (text.Length < 2)
        {
            output.Notes.Add($"Bản tin quá ngắn: \"{Shorten(text)}\"");
            return;
        }

        var code = text[..2];
        switch (code)
        {
            case "DB":
            case "DE":
                if (_pending is not null)
                {
                    output.Notes.Add($"Bỏ kết quả mẫu {_pending.Describe()}: phiên {code} khi chưa nhận khối cuối (E)");
                    _pending = null;
                }

                break;
            case "RB":
            case "RE":
                break;
            case "D ":
            case "d ":
            case "DH":
            case "dH":
            case "DQ":
                ParseResult(code, text, output, now);
                break;
            case "DA":
                output.Notes.Add("Bỏ qua bản tin chuẩn (DA)");
                break;
            case "DR":
                output.Notes.Add("Bỏ qua bản tin reagent blank (DR)");
                break;
            case "R ":
            case "RH":
            case "Rh":
                ParseQuery(code, text, output, now);
                break;
            default:
                output.Notes.Add($"Mã phân biệt không rõ '{code}' — bỏ qua \"{Shorten(text)}\"");
                break;
        }
    }

    private void ParseResult(string code, string text, DriverOutput output, DateTimeOffset now)
    {
        var o = _options;
        var pos = 2 + (o.SystemNo ? 2 : 0);
        var fixedLength = pos + o.HeadLength + 4 + 1;
        if (text.Length < fixedLength)
        {
            output.Notes.Add($"Bản tin {code.Trim()} ngắn hơn phần cố định theo cấu hình ({text.Length} < {fixedLength}) — kiểm tra Rack/Sample ID digits: \"{Shorten(text)}\"");
            return;
        }

        var rack = text.Substring(pos, o.RackDigits);
        pos += o.RackDigits;
        var cup = o.CupPosition ? text.Substring(pos, 2) : "";
        pos += o.CupPosition ? 2 : 0;
        pos += 1; // sample type
        var sampleNo = text.Substring(pos, 4);
        pos += 4;
        var sampleId = text.Substring(pos, o.SampleIdLength);
        pos += o.SampleIdLength + 4; // Dummy (D␣/d␣/DQ) hoặc Original sample No. (DH/dH)
        var block = text[pos];
        var variable = text[(pos + 1)..];

        if (block is not ((>= '0' and <= '9') or 'E'))
        {
            output.Notes.Add($"Data classification No. '{block}' không hợp lệ — cấu hình độ dài trường có thể sai: \"{Shorten(text)}\"");
            _pending = null;
            return;
        }

        var key = $"{code}|{rack}|{cup}|{sampleNo}|{sampleId}";
        var first = false;
        if (_pending is null || _pending.Key != key)
        {
            if (_pending is not null)
            {
                output.Notes.Add($"Bỏ kết quả mẫu {_pending.Describe()}: chưa nhận khối cuối (E) đã sang mẫu khác");
                _pending = null;
            }

            if (block is not ('0' or 'E'))
            {
                output.Notes.Add($"Khối {block} đến khi chưa có khối đầu — bỏ: \"{Shorten(text)}\"");
                return;
            }

            first = true;
            var isQc = code == "DQ";
            var barcode = sampleId.Trim();
            if (isQc && barcode.Length == 0)
            {
                barcode = sampleNo.Trim();
            }

            _pending = new PendingResult(key, code, barcode, sampleNo, isQc, now);
        }

        if (first)
        {
            var prefix = code switch
            {
                "D " or "d " => o.PatientBlockLength + (o.LoadingTime ? 14 : 0),
                "DQ" => (o.LoadingTime ? 14 : 0) + o.ControlNoDigits,
                _ => o.LoadingTime ? 14 : 0,
            };

            if (variable.Length < prefix)
            {
                output.Notes.Add($"Phần biến đổi ngắn hơn thông tin BN theo cấu hình — bỏ: \"{Shorten(text)}\"");
                _pending = null;
                return;
            }

            variable = variable[prefix..];
        }

        if (variable.Length % o.ItemLength != 0)
        {
            output.Notes.Add($"Độ dài phần kết quả {variable.Length} không chia hết cho {o.ItemLength} (mã XN {o.TestNoDigits} + thuốc thử {o.ReagentInfoLength} + kết quả {o.ResultDigits} + cờ {o.FlagLength}) — cấu hình không khớp máy, bỏ cả mẫu {_pending.Describe()}");
            _pending = null;
            return;
        }

        var items = new List<ReadingItem>();
        for (var i = 0; i < variable.Length; i += o.ItemLength)
        {
            var testNo = variable.Substring(i, o.TestNoDigits);
            var result = variable.Substring(i + o.TestNoDigits + o.ReagentInfoLength, o.ResultDigits);
            var flag = variable.Substring(i + o.TestNoDigits + o.ReagentInfoLength + o.ResultDigits, o.FlagLength);
            if (!testNo.All(char.IsAsciiDigit))
            {
                output.Notes.Add($"Online Test No. '{testNo}' không phải số — cấu hình không khớp máy, bỏ cả mẫu {_pending.Describe()}");
                _pending = null;
                return;
            }

            var flags = ParseFlags(flag);
            if (flags is not null && flags.Contains('/'))
            {
                output.Notes.Add($"Mẫu {_pending.Describe()} kênh {testNo}: chưa đo (cờ /) — bỏ");
                continue;
            }

            if (result.Trim().Length == 0)
            {
                output.Notes.Add($"Mẫu {_pending.Describe()} kênh {testNo}: không có giá trị — bỏ");
                continue;
            }

            if (result == new string('9', o.ResultDigits))
            {
                output.Notes.Add($"Mẫu {_pending.Describe()} kênh {testNo}: giá trị '{result}' vượt số chữ số (digit over) — bỏ, cần nhập tay");
                continue;
            }

            items.Add(new ReadingItem(testNo, NormalizeValue(result), null, flags));
        }

        _pending.Items.AddRange(items);
        _pending.Raw.Add(Readable(text));

        if (block == 'E')
        {
            var p = _pending;
            _pending = null;
            if (p.Barcode.Length == 0)
            {
                output.Notes.Add($"Kết quả mẫu số {p.SampleNo.Trim()} không có Sample ID — vào Chưa ghép");
            }

            output.Readings.Add(new DriverReading(p.Barcode, p.IsQc, p.Items, string.Join("\n", p.Raw)));
        }
    }

    private void ParseQuery(string code, string text, DriverOutput output, DateTimeOffset now)
    {
        var o = _options;
        var pos = 2 + (o.SystemNo ? 2 : 0);
        if (text.Length < pos + o.HeadLength)
        {
            output.Notes.Add($"Câu hỏi {code.Trim()} ngắn hơn cấu hình ({text.Length} < {pos + o.HeadLength}) — không trả lời được: \"{Shorten(text)}\"");
            return;
        }

        var rack = text.Substring(pos, o.RackDigits);
        pos += o.RackDigits;
        var cup = o.CupPosition ? text.Substring(pos, 2) : "";
        pos += o.CupPosition ? 2 : 0;
        var type = text.Substring(pos, 1);
        pos += 1;
        var sampleNo = text.Substring(pos, 4);
        pos += 4;
        var sampleId = text.Substring(pos, o.SampleIdLength);

        var context = new AuQueryContext(code, rack, cup, type, sampleNo, sampleId, now);
        output.Queries.Add(new DriverQuery(sampleId.Trim(), new object(), context));
    }

    // ---------------- Bản tin LIS gửi ----------------

    /// <summary>
    /// Dựng <c>S␣</c>/<c>SH</c>/<c>Sh</c> (chia khối theo Text Length). Phần cố định chép nguyên từ câu hỏi;
    /// <c>S␣</c> có Dummy 4 dấu cách, <c>SH</c>/<c>Sh</c> có Original sample No. — câu hỏi <c>RH</c>/<c>Rh</c>
    /// chỉ có một số mẫu 4 chữ số nên dùng lại số đó cho cả hai chỗ <i>(suy luận — cần đối chiếu máy thật)</i>.
    /// </summary>
    private List<string> BuildRequisition(AuQueryContext ctx, List<string> tests)
    {
        var o = _options;
        var replyCode = ctx.Code switch { "RH" => "SH", "Rh" => "Sh", _ => "S " };
        var tail = replyCode == "S " ? "    " : ctx.SampleNo;
        var head = replyCode + ctx.Rack + ctx.Cup + ctx.SampleType + ctx.SampleNo + ctx.SampleIdRaw + tail;
        var withDiluent = replyCode != "S " || o.DilutionInfo;
        var patient = replyCode == "S " ? new string(' ', o.PatientBlockLength) : "";
        var itemTexts = tests.Select(t => withDiluent ? t + "0" : t).ToList();

        var budget = o.TextLength - 2 - (o.Bcc ? 1 : 0) - head.Length - 1;
        var blocks = new List<string>();
        var current = new StringBuilder(patient);
        foreach (var item in itemTexts)
        {
            if (current.Length + item.Length > budget)
            {
                blocks.Add(current.ToString());
                current.Clear();
            }

            current.Append(item);
        }

        blocks.Add(current.ToString());
        if (blocks.Count > 11)
        {
            throw new InvalidOperationException("Chỉ định quá dài: vượt 11 khối");
        }

        return blocks.Select((body, i) => head + (i == blocks.Count - 1 ? 'E' : (char)('0' + i)) + body).ToList();
    }

    private byte[] BuildFrame(string text)
    {
        var body = Encoding.Latin1.GetBytes(text);
        var frame = new List<byte>(body.Length + 3) { _options.StartCode };
        frame.AddRange(body);
        frame.Add(_options.EndCode);
        if (_options.Bcc)
        {
            frame.Add(ComputeBcc([.. body], _options.EndCode));
        }

        return [.. frame];
    }

    private void Enqueue(List<byte[]> frames, DriverOutput output, DateTimeOffset now)
    {
        if (_options.ClassB)
        {
            foreach (var f in frames)
            {
                _outQueue.Enqueue(f);
            }

            if (_awaitingAck is null)
            {
                SendNextClassB(output, now);
            }

            return;
        }

        // Class A: bản tin đầu gửi ngay, các khối sau cách nhau T5.
        for (var i = 0; i < frames.Count; i++)
        {
            if (i == 0 && _outQueue.Count == 0)
            {
                output.Send.Add(frames[i]);
                _nextClassASend = now + _options.ClassAInterval;
            }
            else
            {
                _outQueue.Enqueue(frames[i]);
            }
        }
    }

    private void SendNextClassB(DriverOutput output, DateTimeOffset now)
    {
        if (_outQueue.Count == 0)
        {
            return;
        }

        _awaitingAck = _outQueue.Dequeue();
        _awaitingSince = now;
        _retriesLeft = _options.Retry;
        output.Send.Add(_awaitingAck);
    }

    // ---------------- Tiện ích ----------------

    /// <summary>Cờ 2 ký tự: mỗi ký tự là một cờ, bỏ dấu cách ("H␣" → "H", "Fp" → "Fp"). Cờ 8 ký tự: 4 cờ × 2, nối bằng dấu cách.</summary>
    private string? ParseFlags(string raw)
    {
        string result;
        if (_options.FlagLength == 2)
        {
            result = raw.Replace(" ", "");
        }
        else
        {
            result = string.Join(' ', Enumerable.Range(0, raw.Length / 2)
                .Select(i => raw.Substring(i * 2, 2).Trim())
                .Where(f => f.Length > 0));
        }

        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// "␣␣5.42" → "5.42"; "0123.4" → "123.4"; "-␣1234" → "-1234"; "0.1234" giữ nguyên. Chuỗi không
    /// phải số (vd mã LIH có thể có dạng khác) thì chỉ bỏ dấu cách. Lưu ý mã LIH 3 mức toàn chữ số
    /// ("010000") cũng bị bỏ số 0 đầu — không map kênh LIH vào chỉ số kiểu số.
    /// </summary>
    public static string NormalizeValue(string raw)
    {
        var s = raw.Replace(" ", "");
        var negative = s.StartsWith('-');
        var digits = negative ? s[1..] : s;
        if (digits.Length == 0 || !digits.All(c => char.IsAsciiDigit(c) || c == '.') || digits.Count(c => c == '.') > 1)
        {
            return s;
        }

        digits = digits.TrimStart('0');
        if (digits.Length == 0 || digits[0] == '.')
        {
            digits = "0" + digits;
        }

        return (negative ? "-" : "") + digits;
    }

    private string Readable(string text)
    {
        static string Ctl(byte b) => b switch { 0x02 => "<STX>", 0x03 => "<ETX>", 0x17 => "<ETB>", _ => $"<x{b:X2}>" };
        return Ctl(_options.StartCode) + text + Ctl(_terminator);
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private sealed class PendingResult(string key, string code, string barcode, string sampleNo, bool isQc, DateTimeOffset startedAt)
    {
        public string Key { get; } = key;

        public string Code { get; } = code;

        public string Barcode { get; } = barcode;

        public string SampleNo { get; } = sampleNo;

        public bool IsQc { get; } = isQc;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public List<ReadingItem> Items { get; } = [];

        public List<string> Raw { get; } = [];

        public string Describe() => Barcode.Length > 0 ? Barcode : $"số {SampleNo.Trim()}";
    }
}
