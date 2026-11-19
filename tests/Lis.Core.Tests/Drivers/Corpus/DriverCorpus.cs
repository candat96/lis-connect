using System.Text;
using Lis.Core.Drivers.Astm;
using Lis.Core.Drivers.BeckmanAu;
using Lis.Simulator.Messages;

namespace Lis.Core.Tests.Drivers.Corpus;

/// <summary>Một dòng byte máy gửi (chiều In) cùng cấu hình để dựng driver, và số bản tin kết quả mong đợi.</summary>
/// <param name="SyncPoints">
/// Vị trí byte (sau byte thứ N) mà một khúc không được vắt qua. Dùng cho ca "máy trả lời LIS": byte máy trả trong phiên
/// LIS→máy chỉ có thể tới SAU khi LIS đã gửi ENQ/khung chỉ định, mà LIS chỉ gửi sau khi lớp chạy gọi
/// <c>AnswerQuery</c> — tức sau khi nhận xong khúc chứa byte kết thúc câu hỏi. Chia khúc vắt qua điểm này là điều đường
/// thật không bao giờ tạo ra.
/// </param>
public sealed record CorpusStream(string Name, string Family, string? Model, string? DriverConfigJson, byte[] InBytes, int ExpectedReadings, IReadOnlyList<int>? SyncPoints = null);

/// <summary>
/// Kho dòng byte dùng cho phép thử "đường cũ = đường mới" (thiết kế mục 9). Byte lấy từ các test driver hiện có
/// (<c>Hl7MllpDriverTests</c>, <c>AstmDriverTests</c>, <c>BeckmanAuDriverTests</c>) và từ bộ dựng bản tin của
/// <c>tools/Lis.Simulator</c>. Chỉ có byte MÁY gửi vào LIS; byte máy trả trong phiên LIS→máy (ACK/NAK) nằm xen vào
/// đúng chỗ như trên đường thật. Dữ liệu giả hoàn toàn.
/// </summary>
public static class DriverCorpus
{
    private static readonly DateTimeOffset Stamp = new(2026, 11, 8, 9, 30, 0, TimeSpan.FromHours(7));

    public const string AuClassBBcc = """{"classB":true,"bcc":true}""";
    public const string AuClassASmall = """{"textLength":256}""";
    public const string AuSmallText = """{"classB":true,"bcc":true,"textLength":256}""";

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static readonly byte[] SyncMark = [];

    /// <summary>Điểm đồng bộ trong <see cref="Causal"/>: máy chờ LIS trả lời trước khi gửi tiếp.</summary>
    private static byte[] Sync => SyncMark;

    private static CorpusStream Causal(string name, string family, string? model, string? config, int expected, params byte[][] parts)
    {
        var bytes = new List<byte>();
        var syncs = new List<int>();
        foreach (var part in parts)
        {
            if (ReferenceEquals(part, SyncMark))
            {
                syncs.Add(bytes.Count);
            }
            else
            {
                bytes.AddRange(part);
            }
        }

        return new CorpusStream(name, family, model, config, [.. bytes], expected, syncs);
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static SimResult R(string code, string value) => new(code, value);

    public static IEnumerable<CorpusStream> All()
    {
        foreach (var s in Hl7()) yield return s;
        foreach (var s in Astm()) yield return s;
        foreach (var s in Au()) yield return s;
        yield return new CorpusStream(
            "chi-nghe-byte-bat-ky", "chi-nghe", null, null,
            Cat(Hl7ResultBuilder.Frame(Hl7ResultBuilder.BuildOru("BC0001", [R("WBC", "7.5")], Stamp, 1, false)), [0x05, 0x02, 0x00, 0xFF]), 0);
    }

    // ------------------------------------------------------------------ HL7 MLLP

    private const string Mindray =
        "MSH|^~\\&|Analyzer|Mindray|||20080617143943||ORU^R01|1|P|2.3.1||||||UNICODE\r" +
        "PID|1||BN0001^^^^MR||Mẫu^Thử||19900804000000|Female\r" +
        "PV1|1||nk^^001\r" +
        "OBR|1||2611080001|00001^Automated Count^99MRC||20080508140600|20080508150616|||Kỹ thuật viên||||20080508150000||||||||||HM||||||||Mindray\r" +
        "OBX|1|IS|08001^Take Mode^99MRC||O||||||F\r" +
        "OBX|5|NM|30525-0^Age^LN||18|yr|||||F\r" +
        "OBX|6|NM|6690-2^WBC^LN||9.81|10*9/L|4.00-10.00|N|||F||E\r" +
        "OBX|21|NM|789-8^RBC^LN||4.53|10*12/L|3.50-5.00|N|||F\r" +
        "OBX|22|NM|718-7^HGB^LN||65|g/L|110-150|L|||F\r" +
        "OBX|29|NM|777-3^PLT^LN||212|10*9/L|100-300|H~A|||F\r" +
        "OBX|38|ED|15008^WBC Histogram. BMP^99MRC||^Image^BMP^Base64^Qk0AAAA=||||||F\r" +
        "OBR|2||2611080001|00002^Manual Count^99MRC\r" +
        "OBX|1|ST|6742-1^RBC Morphology^LN||Hồng cầu nhỏ\\F\\nhược sắc||||||F\r";

    private const string MindrayQc =
        "MSH|^~\\&|Analyzer|Mindray|||20261108090000||ORU^R01|7|Q|2.3.1||||||UNICODE\r" +
        "PID|1||LOT123^^^^MR\r" +
        "OBR|1||QC01|00006^XR QCR^99MRC\r" +
        "OBX|1|NM|6690-2^WBC^LN||7.10|10*9/L|||||F\r" +
        "PID|2||LOT123^^^^MR\r" +
        "OBR|2||QC01|00006^XR QCR^99MRC\r" +
        "OBX|1|NM|6690-2^WBC^LN||7.30|10*9/L|||||F\r";

    private const string MindrayQuery =
        "MSH|^~\\&|Analyzer|Mindray|||20081120174836||ORM^O01|4|P|2.3.1||||||UNICODE\r" +
        "ORC|RF||2611030001||IP\r";

    // Máy trả ACK cho ORR^O02 của LIS (phiên LIS→máy), cũng là một khung MLLP.
    private const string MachineAck = "MSH|^~\\&|Analyzer|Mindray|||20081120174837||ACK^O02|11|P|2.3.1\rMSA|AA|4\r";

    private static byte[] Oru(string barcode, int controlId, bool qc = false, params SimResult[] results) =>
        Hl7ResultBuilder.Frame(Hl7ResultBuilder.BuildOru(barcode, results.Length == 0 ? [R("6690-2", "7.5"), R("718-7", "128")] : results, Stamp, controlId, qc));

    private static IEnumerable<CorpusStream> Hl7()
    {
        const string f = "hl7-mllp";
        var mindray = Hl7ResultBuilder.Frame(Mindray);
        yield return new($"hl7-mot-ket-qua-mindray", f, null, null, mindray, 1);
        yield return new("hl7-mot-ket-qua-simulator", f, null, null, Oru("2611080002", 2), 1);
        yield return new("hl7-nhieu-mau-lien-nhau", f, null, null, Cat(Oru("2611080003", 3), Oru("2611080004", 4), Oru("2611080005", 5, false, R("6690-2", "11.2"))), 3);
        yield return new("hl7-qc", f, null, null, Hl7ResultBuilder.Frame(MindrayQc), 2);
        yield return new("hl7-qc-simulator-roi-mau", f, null, null, Cat(Oru("QC02", 6, true), Oru("2611080006", 7)), 2);
        yield return Causal("hl7-cau-hoi-chi-dinh-va-ack-may-tra", f, null, null, 1,
            Hl7ResultBuilder.Frame(MindrayQuery), Sync, Hl7ResultBuilder.Frame(MachineAck), Oru("2611030001", 8));
        yield return Causal("hl7-cau-hoi-roi-ack-co-ca-CR-LF", f, null, null, 2,
            Hl7ResultBuilder.Frame(MindrayQuery), Sync, [0x0D, 0x0A], Hl7ResultBuilder.Frame(MachineAck), Oru("2611030002", 9), Oru("2611030003", 10));
        yield return new("hl7-bo-do-VT-giua-khung", f, null, null,
            Cat([0x0B], Ascii("MSH|^~\\&|A|M|||1||ORU^R01|1|P|2.3.1\rOBR|1||DO0001\rOBX|1|NM|6690-2^WBC^LN||5\r"), Oru("2611080007", 11)), 1);
        yield return new("hl7-bo-do-o-cuoi-khong-co-FS", f, null, null,
            Cat(Oru("2611080008", 12), [0x0B], Ascii("MSH|^~\\&|A|M|||1||ORU^R01|2|P|2.3.1\rOBR|1||DO0002\r")), 1);
        yield return new("hl7-khung-hong-khong-MSH", f, null, null,
            Cat(Hl7ResultBuilder.Frame("PID|1||X\r"), Oru("2611080009", 13)), 1);
        yield return new("hl7-rac-truoc-luot-dau", f, null, null,
            Cat([0x41, 0x00, 0xFF, 0x0D, 0x0A], Oru("2611080010", 14), [0x13, 0x11], Oru("2611080011", 15)), 2);
        yield return new("hl7-byte-Latin1", f, null, null,
            Cat([0x0B], Encoding.ASCII.GetBytes("MSH|^~\\&|A|M|||1||ORU^R01|1|P|2.3.1\rOBR|1||B1\rOBX|1|ST|6742-1^M^LN||"), [0xE9, 0xFF], Ascii("\r"), [0x1C, 0x0D]), 1);
    }

    // ------------------------------------------------------------------ ASTM E1381/E1394

    private static readonly string[] AccessResult =
    [
        @"H|\^&|||ACCESS^500001|||||LIS||P|1|20261103091500",
        "P|1|BNTHU0001",
        @"O|1|2611030001|^12^3|^^^TSH3^1\^^^FT4II^1|||||||||||Serum||||||||||F",
        "R|1|^^^TSH3^1|2.15|uIU/mL||N||F||||20261103091200",
        "R|2|^^^FT4II^1|28.4|pmol/L||H||F||||20261103091310",
        "C|1|I|ORH|I",
        "L|1|F",
    ];

    private static readonly string[] AccessQuery =
    [
        @"H|\^&|||ACCESS^500001|||||LIS||P|1|20261103091500",
        "Q|1|^2611030001||ALL||||||||O",
        "L|1|F",
    ];

    private const byte Enq = 0x05, Ack = 0x06, Nak = 0x15, Eot = 0x04;

    private static List<byte[]> Frames(IEnumerable<string> records, int first = 1)
    {
        var frames = new List<byte[]>();
        var fn = first;
        foreach (var record in records)
        {
            frames.AddRange(AstmFraming.BuildFrames(record, fn, out fn));
        }

        return frames;
    }

    /// <summary>Máy gửi trọn một phiên ENQ → frame → EOT (không có byte LIS trả).</summary>
    private static byte[] Session(IEnumerable<string> records) => Cat([[Enq], .. Frames(records), [Eot]]);

    private static IEnumerable<CorpusStream> Astm()
    {
        const string f = "astm-e1394";
        var now = Stamp;
        var one = AstmMessageBuilder.BuildResultRecords("2611090001", [R("WBC", "7.5"), R("HGB", "128")], now, false);
        var two = AstmMessageBuilder.BuildResultRecords("2611090002", [R("GLU", "5.4")], now, false);
        var three = AstmMessageBuilder.BuildResultRecords("2611090003", [R("GLU", "6.1"), R("UREA", "4.2"), R("CREA", "80")], now, false);
        var qc = AstmMessageBuilder.BuildResultRecords("LOTQC1", [R("GLU", "5.0")], now, true);
        var query = AstmMessageBuilder.BuildQueryRecords("2611090004", now);

        yield return new("astm-mot-ket-qua", f, null, null, Session(one), 1);
        yield return new("astm-nhieu-mau-lien-nhau", f, null, null, Cat(Session(one), Session(two), Session(three)), 3);
        yield return new("astm-qc", f, null, null, Cat(Session(qc), Session(two)), 2);

        // Máy hỏi chỉ định; LIS trả lời (ENQ, H, L); máy ACK ENQ rồi ACK từng frame (2 frame: H, L), sau đó gửi kết quả.
        // Trên đường thật các ACK này đi cùng chiều In nhưng không thuộc phiên nhận nào.
        yield return Causal("astm-cau-hoi-chi-dinh-va-ack-may-tra", f, null, null, 1,
            Session(query), Sync, [Ack, Ack, Ack], Session(two));
        // Máy NAK một frame của LIS (LIS gửi lại), rồi ACK tiếp; một ACK thừa ở cuối.
        yield return Causal("astm-cau-hoi-nak-frame-roi-ack", f, null, null, 2,
            Session(query), Sync, [Ack, Nak, Ack, Ack, Ack], Session(one), Session(two));
        // Tranh chấp đường truyền KHÔNG đồng bộ: máy gửi ENQ đúng lúc LIS đang chờ trả lời ENQ của mình (tuỳ chỗ chia khúc,
        // ENQ tới lúc LIS còn rảnh hoặc đã chờ; cả hai cách đều phải ra đúng một kết quả). Xem AstmLink: ENQ ở WaitEnqReply.
        yield return new("astm-tranh-chap-ENQ-khong-dong-bo", f, null, null, Cat(Session(query), Session(two)), 1);
        // Máy gửi lại ENQ sau tranh chấp (tưởng ENQ đầu chưa được ACK): lượt bỏ dở chỉ có 1 byte ENQ.
        yield return new("astm-ENQ-gui-lai-sau-tranh-chap-bo-do-1-byte", f, null, null, Cat(Session(query), [Enq], Session(two)), 1);
        // ENQ rồi EOT ngay: thăm dò đường truyền, phiên rỗng.
        yield return new("astm-ENQ-EOT-rong", f, null, null, Cat([Enq, Eot], Session(one), [Enq, Eot]), 1);
        // Máy NAK ENQ của LIS (bận): LIS lùi 10 s, máy gửi tiếp kết quả của nó.
        yield return Causal("astm-cau-hoi-nak-ENQ-may-gui-tiep", f, null, null, 2,
            Session(query), Sync, [Nak], Session(one), Session(two));

        // ENQ giữa phiên: máy mở lại phiên, phần đã nhận bị bỏ. Kết quả chỉ ra ở phiên sau.
        var halfFrames = Frames(three);
        yield return new("astm-ENQ-giua-phien", f, null, null,
            Cat([Enq], halfFrames[0], halfFrames[1], Session(two)), 1);
        yield return new("astm-ENQ-giua-phien-hai-lan", f, null, null,
            Cat([Enq], halfFrames[0], [Enq], halfFrames[0], halfFrames[1], Session(one)), 1);

        // Bỏ dở ở cuối dòng: ENQ + vài frame, không có EOT (máy rớt kết nối / hết giờ).
        yield return new("astm-bo-do-o-cuoi-khong-EOT", f, null, null,
            Cat(Session(one), [Enq], halfFrames[0], halfFrames[1], halfFrames[2]), 1);

        // Sai checksum: máy gửi lại frame đúng sau NAK.
        var badFrames = Frames(two);
        // two = H, P, O, R, L → frame 0..4; làm hỏng frame R (chỉ số 3).
        var corrupt = (byte[])badFrames[3].Clone();
        corrupt[^4] = corrupt[^4] == (byte)'0' ? (byte)'1' : (byte)'0';
        yield return new("astm-sai-checksum-roi-gui-lai", f, null, null,
            Cat([Enq], badFrames[0], badFrames[1], badFrames[2], corrupt, [Nak], badFrames[3], badFrames[4], [Eot]), 1);
        // Sai checksum mà máy không gửi lại (bỏ phiên): record R mất → không được sinh kết quả sai cho mẫu đó.
        yield return new("astm-sai-checksum-khong-gui-lai", f, null, null,
            Cat([Enq], badFrames[0], badFrames[1], badFrames[2], corrupt, [Nak], [Eot], Session(one)), 1);

        // Rác trước lượt đầu: ký tự điều khiển XON/XOFF, CR/LF thừa, byte lạ, rồi rác giữa hai phiên.
        yield return new("astm-rac-truoc-luot-dau", f, null, null,
            Cat([0x11, (byte)'x', 0x0D, 0x0A, 0xFF], Session(one), [0x13, 0x0A], Session(two)), 2);

        // Record dài tách ETB/ETX.
        var longRecords = new List<string>(two);
        longRecords.Insert(longRecords.Count - 1, "C|1|I|" + new string('K', 600) + "|I");
        yield return new("astm-record-dai-nhieu-frame-ETB", f, null, null, Session(longRecords), 1);

        // Frame trùng (máy gửi lại vì mất ACK).
        var dupFrames = Frames(two);
        yield return new("astm-frame-trung-ACK-bi-mat", f, null, null,
            Cat([Enq], dupFrames[0], dupFrames[1], dupFrames[1], dupFrames[2], dupFrames[3], [Eot]), 1);

        // Beckman Access 2.
        const string access = "Access 2";
        yield return new("astm-access2-mot-ket-qua", f, access, null, Session(AccessResult), 1);
        yield return new("astm-access2-nhieu-mau", f, access, null,
            Cat(Session(AccessResult), Session(AccessResult.Select(r => r.Replace("2611030001", "2611030002"))), Session(AccessResult.Select(r => r.Replace("2611030001", "2611030003")))), 3);
        yield return Causal("astm-access2-cau-hoi-chi-dinh-va-ack-may-tra", f, access, null, 1,
            Session(AccessQuery), Sync, [Ack, Ack, Ack, Ack], Session(AccessResult));
        yield return new("astm-access2-ENQ-giua-phien", f, access, null,
            Cat([Enq], Frames(AccessResult)[0], Frames(AccessResult)[1], Frames(AccessResult)[2], Session(AccessResult)), 1);
        yield return new("astm-access2-sai-checksum-roi-gui-lai", f, access, null, BadChecksumAccess(), 1);
        yield return new("astm-access2-rac-truoc-luot-dau", f, access, null, Cat([0x11, 0x13, 0x0D, 0x0A, 0x7F], Session(AccessResult)), 1);
        yield return new("astm-access2-qc", f, access, null,
            Session(AccessResult.Select(r => r.StartsWith("O|", StringComparison.Ordinal) ? r.Replace("||||||||||F", "||||||||Q|F") : r)), 1);
    }

    private static byte[] BadChecksumAccess()
    {
        var frames = Frames(AccessResult);
        var corrupt = (byte[])frames[3].Clone();
        corrupt[^4] = corrupt[^4] == (byte)'0' ? (byte)'1' : (byte)'0';
        return Cat([[Enq], frames[0], frames[1], frames[2], corrupt, [Nak], frames[3], .. frames.Skip(4), [Eot]]);
    }

    // ------------------------------------------------------------------ Beckman AU

    private static readonly BeckmanAuOptions ClassBBcc = BeckmanAuOptions.Au480 with { ClassB = true, Bcc = true };
    private static readonly BeckmanAuOptions SmallText = ClassBBcc with { TextLength = 256 };

    private static byte[] AuSample(BeckmanAuOptions o, string barcode, bool qc, params SimResult[] results) =>
        Cat([.. AuMessageBuilder.BuildResultFrames(o, barcode, results, qc)]);

    /// <summary>Dựng lại khung Class A (không BCC) với văn bản bị cắt 3 ký tự cuối: độ dài phần kết quả không chia hết cho độ dài một kênh.</summary>
    public static byte[] ShortenAuFrame(BeckmanAuOptions o, byte[] frame)
    {
        var text = Encoding.ASCII.GetString(frame, 1, frame.Length - 2);
        return AuMessageBuilder.BuildFrame(o, text[..^3]);
    }

    private static IEnumerable<CorpusStream> Au()
    {
        const string f = "beckman-au";
        var many = Enumerable.Range(1, 55).Select(i => R(i.ToString("00"), $"{i}.5")).ToArray();
        var three = new[] { R("01", "5.42"), R("03", "86.0"), R("08", "25.0") };

        yield return new("au-classB-mot-ket-qua", f, null, AuClassBBcc, AuSample(ClassBBcc, "2611030001", false, three), 1);
        yield return new("au-classA-mot-ket-qua", f, null, null,
            Cat([0xFF, 0x0D, 0x0A], AuSample(BeckmanAuOptions.Au480, "2611030002", false, three), [0x41, 0x42]), 1);
        yield return new("au-nhieu-mau-lien-nhau", f, null, AuClassBBcc,
            Cat(AuSample(ClassBBcc, "2611030003", false, three), AuSample(ClassBBcc, "2611030004", false, R("01", "7.7")), AuSample(ClassBBcc, "2611030005", false, three)), 3);
        yield return new("au-qc", f, null, AuClassBBcc,
            Cat(AuSample(ClassBBcc, "QCLOT1", true, R("01", "5.1")), AuSample(ClassBBcc, "2611030006", false, three)), 2);

        // Kết quả nhiều khối (>= 3 khối, 55 kênh, Text Length 256).
        yield return new("au-nhieu-khoi-3-khoi-tro-len", f, null, AuSmallText, AuSample(SmallText, "2611030007", false, many), 1);
        yield return new("au-nhieu-khoi-roi-mau-khac", f, null, AuSmallText,
            Cat(AuSample(SmallText, "2611030008", false, many), AuSample(SmallText, "2611030009", false, three)), 2);

        // Hỏi chỉ định R␣; LIS trả S␣/SE; máy ACK bản tin LIS gửi, rồi gửi kết quả.
        var query = AuMessageBuilder.BuildQueryFrame(ClassBBcc, "2611030010");
        yield return Causal("au-cau-hoi-chi-dinh-va-ack-may-tra", f, null, AuClassBBcc, 1,
            query, Sync, [0x06], AuSample(ClassBBcc, "2611030010", false, three));
        yield return Causal("au-cau-hoi-nak-roi-ack", f, null, AuClassBBcc, 1,
            query, Sync, [0x15, 0x06, 0x06], AuSample(ClassBBcc, "2611030011", false, three), query, Sync, [0x06]);

        // Bỏ dở: khối đầu của mẫu nhiều khối rồi mẫu khác (mẫu dở bị bỏ), và khung dở không có ETX.
        var manyFrames = AuMessageBuilder.BuildResultFrames(SmallText, "2611030012", many, false);
        yield return new("au-bo-do-mau-nhieu-khoi-roi-mau-khac", f, null, AuSmallText,
            Cat(manyFrames[0], manyFrames[1], manyFrames[2], AuSample(SmallText, "2611030013", false, three)), 1);
        yield return new("au-bo-do-o-cuoi-thieu-khoi-E", f, null, AuSmallText,
            Cat(AuSample(SmallText, "2611030014", false, three), manyFrames[0], manyFrames[1], manyFrames[2]), 1);
        var half = AuMessageBuilder.BuildFrame(ClassBBcc, "D 001203 01232611030015    E01  5.42");
        yield return new("au-bo-do-khung-khong-ETX-roi-mau-day-du", f, null, AuClassBBcc,
            Cat(half[..(half.Length - 2)], AuSample(ClassBBcc, "2611030016", false, three)), 1);

        // Sai BCC (Class B: LIS NAK, máy gửi lại).
        var bad = (byte[])AuMessageBuilder.BuildResultFrames(ClassBBcc, "2611030017", three, false)[1].Clone();
        bad[^1] ^= 0x01;
        var good = AuMessageBuilder.BuildResultFrames(ClassBBcc, "2611030017", three, false);
        yield return new("au-sai-BCC-roi-gui-lai", f, null, AuClassBBcc,
            Cat(good[0], bad, good[1], good[2]), 1); // NAK do LIS gửi (Send), không có trong dòng byte máy gửi vào
        yield return new("au-sai-BCC-khong-gui-lai", f, null, AuClassBBcc,
            Cat(good[0], bad, good[2], AuSample(ClassBBcc, "2611030018", false, three)), 1);

        // Class A (tắt BCC), mẫu nhiều khối bị hỏng giữa chừng. Ghi lại hành vi HIỆN CÓ (rủi ro đã biết, không đổi ở task này):
        // 1) mất hẳn khối giữa: driver không kiểm thứ tự khối nên vẫn phát reading THIẾU kênh và không phát hiện được gì;
        // 2) khối giữa bị cắt ngắn: cả mẫu bị bỏ (Discarded) nhưng khối E đến sau vẫn mở lại mẫu và phát reading chỉ có kênh của khối E.
        // Cả hai đường cho cùng kết quả (phép thử đối chiếu). Test riêng: RawMessageEquivalenceTests.Au_classA_nhieu_khoi_hong_*.
        var classA = BeckmanAuOptions.Au480 with { TextLength = 256 };
        var aFrames = AuMessageBuilder.BuildResultFrames(classA, "2611030021", many, false);
        yield return new("au-classA-nhieu-khoi-mat-khoi-giua", f, null, AuClassASmall, Cat(aFrames[0], aFrames[1], aFrames[3], aFrames[4]), 1);
        yield return new("au-classA-nhieu-khoi-khoi-giua-hong", f, null, AuClassASmall, Cat(aFrames[0], aFrames[1], ShortenAuFrame(classA, aFrames[2]), aFrames[3], aFrames[4]), 1);

        // Rác trước lượt đầu và giữa các mẫu.
        yield return new("au-rac-truoc-luot-dau", f, null, AuClassBBcc,
            Cat([0x00, 0xFF, 0x41, 0x0D, 0x0A], AuSample(ClassBBcc, "2611030019", false, three), [0x11, 0x13], AuSample(ClassBBcc, "2611030020", false, three)), 2);
    }
}
