using Lis.Core.Drivers.Astm;
using Lis.Core.Drivers.BeckmanAu;
using Lis.Core.Instruments;
using Lis.Simulator.Links;
using Lis.Simulator.Messages;

namespace Lis.Simulator.Sessions;

/// <summary>Kết quả một lần giả lập: thành công hay không, câu tóm tắt, và các dòng LIS trả về (record/khung văn bản).</summary>
public sealed record SessionResult(bool Ok, string Message, IReadOnlyList<string> Lines);

/// <summary>Các thời gian chờ của phiên giả lập; test đặt ngắn.</summary>
public sealed record SessionTimings
{
    /// <summary>Chờ LIS trả ACK/NAK/ACK^R01.</summary>
    public TimeSpan Reply { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Chờ LIS trả lời câu hỏi chỉ định (LIS còn phải tra CSDL).</summary>
    public TimeSpan QueryAnswer { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>AU Class A: nghỉ giữa các bản tin vì máy thật không gửi dồn.</summary>
    public TimeSpan ClassAGap { get; init; } = TimeSpan.FromMilliseconds(150);
}

/// <summary>
/// Hội thoại của MÁY với LIS theo từng giao thức: dựng bản tin (lớp <c>Messages</c>), gửi đúng thủ
/// tục (ACK/ENQ/EOT…), chờ và đọc trả lời, ghi từng bước qua <c>log</c>. Chạy được trên mọi
/// <see cref="IMachineLink"/>, nên test dùng đúng đường chạy thật.
/// </summary>
public sealed class MachineSession(IMachineLink link, Action<string> log, SessionTimings? timings = null)
{
    private readonly SessionTimings _t = timings ?? new SessionTimings();
    private int _controlId;

    // ---------------- HL7 / MLLP ----------------

    public async Task<SessionResult> SendHl7ResultAsync(string barcode, IReadOnlyList<SimResult> results, bool isQc, CancellationToken ct)
    {
        var text = Hl7ResultBuilder.BuildOru(barcode, results, DateTimeOffset.Now, ++_controlId, isQc);
        await WriteAsync(Hl7ResultBuilder.Frame(text), "Gửi ORU^R01", ct);

        var (reply, ok) = await ReadUntilAsync(b => b.Contains(Hl7ResultBuilder.EndBlock), _t.Reply, ct);
        if (!ok)
        {
            return Fail("LIS không trả ACK trong thời hạn (máy thật sẽ coi là mất bản tin).");
        }

        var code = Hl7ResultBuilder.ParseAckCode(reply.ToArray());
        return code == "AA"
            ? Success($"LIS xác nhận bản tin (MSA|AA). Đã gửi {results.Count} kênh cho mẫu {barcode}.")
            : Fail($"LIS trả MSA|{code ?? "?"} — không nhận bản tin.");
    }

    // ---------------- ASTM E1381/E1394 ----------------

    public async Task<SessionResult> SendAstmResultAsync(string barcode, IReadOnlyList<SimResult> results, bool isQc, CancellationToken ct)
    {
        var records = AstmMessageBuilder.BuildResultRecords(barcode, results, DateTimeOffset.Now, isQc);
        foreach (var r in records)
        {
            log($"  record: {r}");
        }

        return await SendAstmRecordsAsync(records, $"Đã gửi {results.Count} kênh cho mẫu {barcode}.", ct);
    }

    private async Task<SessionResult> SendAstmRecordsAsync(IReadOnlyList<string> records, string successMessage, CancellationToken ct)
    {
        await WriteAsync([AstmFraming.Enq], "Xin gửi (ENQ)", ct);
        var answer = await ReadControlAsync(_t.Reply, ct);
        if (answer != AstmFraming.Ack)
        {
            return Fail(answer == AstmFraming.Nak ? "LIS từ chối ENQ (NAK)." : "LIS không trả ACK cho ENQ.");
        }

        var fn = 1;
        var count = 0;
        foreach (var record in records)
        {
            foreach (var frame in AstmFraming.BuildFrames(record, fn, out fn))
            {
                count++;
                await WriteAsync(frame, $"Khung {count}", ct);
                var reply = await ReadControlAsync(_t.Reply, ct);
                if (reply != AstmFraming.Ack)
                {
                    await WriteAsync([AstmFraming.Eot], "Bỏ phiên (EOT)", ct);
                    return Fail(reply == AstmFraming.Nak ? $"LIS NAK khung {count}." : $"LIS không trả lời khung {count}.");
                }
            }
        }

        await WriteAsync([AstmFraming.Eot], "Kết thúc phiên (EOT)", ct);
        return Success(successMessage);
    }

    public async Task<SessionResult> QueryAstmAsync(string barcode, CancellationToken ct)
    {
        var sent = await SendAstmRecordsAsync(AstmMessageBuilder.BuildQueryRecords(barcode, DateTimeOffset.Now), "", ct);
        if (!sent.Ok)
        {
            return sent;
        }

        // LIS trả lời bằng phiên riêng: ENQ → khung… → EOT; máy ACK từng bước.
        var receiver = new AstmReceiver();
        var deadline = DateTimeOffset.Now + _t.QueryAnswer;
        while (!receiver.Finished)
        {
            var data = await ReadBeforeAsync(deadline, ct);
            if (data.Length == 0)
            {
                return Fail("LIS không trả lời câu hỏi chỉ định trong thời hạn.");
            }

            foreach (var reply in receiver.Feed(data))
            {
                await WriteAsync(reply, reply[0] == AstmFraming.Ack ? "ACK" : "NAK", ct);
            }
        }

        foreach (var r in receiver.Records)
        {
            log($"  record nhận: {r}");
        }

        var order = receiver.Records.FirstOrDefault(r => r.StartsWith("O|", StringComparison.Ordinal));
        return Success(order is null ? "LIS trả lời: không có chỉ định cho mẫu này." : $"LIS trả chỉ định: {order}", receiver.Records);
    }

    // ---------------- Beckman AU ----------------

    public async Task<SessionResult> SendAuResultAsync(
        BeckmanAuOptions options, string barcode, IReadOnlyList<SimResult> results, bool isQc, CancellationToken ct)
    {
        var frames = AuMessageBuilder.BuildResultFrames(options, barcode, results, isQc);
        for (var i = 0; i < frames.Count; i++)
        {
            await WriteAsync(frames[i], $"Khung {i + 1}/{frames.Count}", ct);
            if (options.ClassB)
            {
                var reply = await ReadControlAsync(_t.Reply, ct);
                if (reply != 0x06)
                {
                    return Fail(reply == 0x15 ? $"LIS NAK khung {i + 1} (kiểm tra BCC/cấu hình)." : $"LIS không ACK khung {i + 1} (Class B).");
                }
            }
            else if (_t.ClassAGap > TimeSpan.Zero)
            {
                await Task.Delay(_t.ClassAGap, ct);
            }
        }

        // Class A: máy không chờ trả lời; đọc nốt cho đủ log nếu LIS có gửi gì.
        return Success($"Đã gửi {results.Count} kênh cho mẫu {barcode} ({(options.ClassB ? "Class B" : "Class A")}).");
    }

    public async Task<SessionResult> QueryAuAsync(BeckmanAuOptions options, string barcode, CancellationToken ct)
    {
        await WriteAsync(AuMessageBuilder.BuildQueryFrame(options, barcode), "Hỏi chỉ định (R␣)", ct);

        var reader = new AuFrameReader(options);
        var deadline = DateTimeOffset.Now + _t.QueryAnswer;
        var acked = 0;
        while (true)
        {
            var data = await ReadBeforeAsync(deadline, ct);
            if (data.Length == 0)
            {
                return Fail("LIS không trả lời câu hỏi chỉ định trong thời hạn (máy thật báo lỗi 6031).");
            }

            reader.Feed(data);
            if (options.ClassB)
            {
                // Mỗi khung LIS gửi, máy trả ACK.
                for (; acked < reader.Frames.Count; acked++)
                {
                    await WriteAsync([0x06], "ACK", ct);
                }
            }

            if (reader.Frames.Count > 0 && IsLastReplyBlock(options, reader.Frames[^1]))
            {
                break;
            }
        }

        foreach (var f in reader.Frames)
        {
            log($"  khung nhận: {f}");
        }

        var first = reader.Frames[0];
        return first.StartsWith("SE", StringComparison.Ordinal)
            ? Success("LIS trả SE: không có chỉ định cho mẫu này.", reader.Frames)
            : Success($"LIS trả chỉ định ({reader.Frames.Count} khung): {string.Join(" | ", reader.Frames)}", reader.Frames);
    }

    private static bool IsLastReplyBlock(BeckmanAuOptions o, string frame)
    {
        if (frame.StartsWith("SE", StringComparison.Ordinal))
        {
            return true;
        }

        var index = AuMessageBuilder.ReplyBlockIndex(o);
        return frame.Length > index && frame[index] == 'E';
    }

    // ---------------- Tiện ích ----------------

    private async Task WriteAsync(byte[] data, string label, CancellationToken ct)
    {
        log($"→ LIS  [{label}] {RawBytes.ToReadable(data).TrimEnd()}");
        await link.WriteAsync(data, ct);
    }

    private async Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        var data = await link.ReadAsync(timeout, ct);
        if (data.Length > 0)
        {
            log($"← LIS  {RawBytes.ToReadable(data).TrimEnd()}");
        }

        return data;
    }

    /// <summary>
    /// Đọc trong thời gian còn lại tới <paramref name="deadline"/>. Đã quá hạn vẫn đọc một lần với
    /// thời hạn 0 để lấy byte đã nằm sẵn — máy tải nặng có thể treo luồng quá hạn trong khi LIS đã
    /// trả lời; chỉ coi là "không trả lời" khi lần đọc đó rỗng.
    /// </summary>
    private Task<byte[]> ReadBeforeAsync(DateTimeOffset deadline, CancellationToken ct)
    {
        var remaining = deadline - DateTimeOffset.Now;
        return ReadAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct);
    }

    /// <summary>Đọc một lần và trả byte điều khiển đầu tiên thuộc ACK/NAK/ENQ/EOT; 0 nếu hết giờ.</summary>
    private async Task<byte> ReadControlAsync(TimeSpan timeout, CancellationToken ct)
    {
        var data = await ReadAsync(timeout, ct);
        foreach (var b in data)
        {
            if (b is 0x06 or 0x15 or 0x05 or 0x04)
            {
                return b;
            }
        }

        return 0;
    }

    private async Task<(List<byte> Bytes, bool Ok)> ReadUntilAsync(Func<List<byte>, bool> done, TimeSpan timeout, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var deadline = DateTimeOffset.Now + timeout;
        while (!done(buffer))
        {
            var data = await ReadBeforeAsync(deadline, ct);
            if (data.Length == 0)
            {
                return (buffer, false);
            }

            buffer.AddRange(data);
        }

        return (buffer, true);
    }

    private SessionResult Success(string message, IReadOnlyList<string>? lines = null) => new(true, message, lines ?? []);

    private SessionResult Fail(string message) => new(false, message, []);
}
