using System.Text;
using Lis.Core.Drivers;
using Lis.Core.Drivers.Astm;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Drivers;

/// <summary>
/// Driver ASTM E1381/E1394 (Beckman Access 2): khung + checksum, ACK/NAK, frame trùng, ETB, hết giờ,
/// đọc kết quả + cờ ở record C, QC, câu hỏi chỉ định và phiên LIS gửi trả lời, tranh chấp đường truyền.
/// Dữ liệu giả hoàn toàn.
/// </summary>
public class AstmDriverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 15, 0, TimeSpan.FromHours(7));

    private const byte Enq = 0x05, Ack = 0x06, Nak = 0x15, Eot = 0x04;

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

    /// <summary>Đóng khung các record thành frame đánh số từ 1 (dùng bộ đóng khung đã được kiểm với ví dụ tài liệu).</summary>
    private static List<byte[]> Frames(IEnumerable<string> records)
    {
        var frames = new List<byte[]>();
        var fn = 1;
        foreach (var record in records)
            frames.AddRange(AstmFraming.BuildFrames(record, fn, out fn));
        return frames;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>Máy giả gửi trọn một phiên ENQ → frame → EOT; trả về tất cả đầu ra của driver.</summary>
    private static List<DriverOutput> SendSession(IInstrumentDriver driver, IEnumerable<byte[]> frames, DateTimeOffset? at = null)
    {
        var now = at ?? T0;
        var outputs = new List<DriverOutput> { driver.Receive([Enq], now) };
        foreach (var frame in frames)
            outputs.Add(driver.Receive(frame, now));
        outputs.Add(driver.Receive([Eot], now));
        return outputs;
    }

    private static List<byte[]> AllSent(IEnumerable<DriverOutput> outputs) => outputs.SelectMany(o => o.Send).ToList();

    private static string Text(byte[] frame) => Encoding.Latin1.GetString(frame);

    [Theory]
    [InlineData("1H|\\^&|\r\u0003", "61")]
    [InlineData("4L|1|F\r\u0003", "FF")]
    [InlineData("1H|\\^&|||ACCESS^500001|||||LIS||P|1|20111231235959\r\u0003", "20")]
    [InlineData("1H|\\^&\r\u0003", "E5")]
    [InlineData("5R|1|^^^WBC|7.5|10^3/uL\u0017", "31")]
    [InlineData("6|4.0 to 10.0|N||F\r\u0003", "3E")]
    [InlineData("3O|1|2611030001|^12^3|^^^TSH3^1\\^^^FT4II^1|||||||||||Serum||||||||||F\r\u0003", "A1")]
    [InlineData("2Q|1|^2611030001||ALL||||||||O\r\u0003", "08")]
    public void Checksum_MatchesDocumentExamples(string body, string expected)
    {
        AstmFraming.Checksum(Ascii(body)).ShouldBe(expected);
    }

    [Fact]
    public void BuildFrame_MatchesDocumentFrameBytes()
    {
        Text(AstmFraming.BuildFrames("L|1|F", 7, out var next)[0]).ShouldBe("\u00027L|1|F\r\u000302\r\n");
        next.ShouldBe(0);
    }

    [Fact]
    public void Access2_ResultSession_AcksEveryFrame_AndProducesOneReadingWithCommentFlags()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);

        var outputs = SendSession(driver, Frames(AccessResult));

        AllSent(outputs).Select(b => b.Single()).ShouldBe([Ack, Ack, Ack, Ack, Ack, Ack, Ack, Ack]);
        var reading = outputs.SelectMany(o => o.Readings).Single();
        reading.Barcode.ShouldBe("2611030001");
        reading.IsQc.ShouldBeFalse();
        reading.Items.Count.ShouldBe(2);
        reading.Items[0].ShouldBe(new Lis.Core.Ingest.ReadingItem("TSH3", "2.15", "uIU/mL", null));
        reading.Items[1].ShouldBe(new Lis.Core.Ingest.ReadingItem("FT4II", "28.4", "pmol/L", "H;ORH"));
        reading.RawText.Split('\r').ShouldBe(AccessResult[..^1]);
    }

    [Fact]
    public void ByteByByteDelivery_GivesSameResult()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        var stream = new List<byte> { 0x11, (byte)'x', Enq }; // rác trước ENQ bị bỏ qua
        foreach (var frame in Frames(AccessResult))
            stream.AddRange(frame);
        stream.Add(Eot);

        var outputs = stream.Select(b => driver.Receive([b], T0)).ToList();

        AllSent(outputs).Count.ShouldBe(8);
        outputs.SelectMany(o => o.Readings).Single().Items.Count.ShouldBe(2);
    }

    [Fact]
    public void LongRecord_SplitWithEtb_IsReassembled()
    {
        var longRange = new string('9', 300);
        string[] records = [AccessResult[0], AccessResult[2], $"R|1|^^^TSH3^1|2.15|uIU/mL|0.38 to {longRange}^Normal|N||F", "L|1|F"];
        var frames = Frames(records);
        frames.Count.ShouldBe(5); // record R dài 300+ ký tự → 2 frame (ETB + ETX)
        frames[2][^5].ShouldBe((byte)0x17);

        var reading = SendSession(new AstmDriver(AstmDriverOptions.Access2), frames).SelectMany(o => o.Readings).Single();

        reading.Items.Single().RawValue.ShouldBe("2.15");
    }

    [Fact]
    public void EtbSplitInsideField_IsJoinedBeforeParsing()
    {
        var text = Encoding.ASCII.GetBytes("R|1|^^^TSH3^1|2.15|uI");
        var rest = Encoding.ASCII.GetBytes("U/mL||N||F\r");
        byte[][] frames =
        [
            .. Frames([AccessResult[0], AccessResult[2]]),
            AstmFraming.BuildFrame(3, text, last: false),
            AstmFraming.BuildFrame(4, rest, last: true),
            .. AstmFraming.BuildFrames("L|1|F", 5, out _),
        ];

        var reading = SendSession(new AstmDriver(AstmDriverOptions.Access2), frames).SelectMany(o => o.Readings).Single();

        reading.Items.Single().Unit.ShouldBe("uIU/mL");
    }

    [Fact]
    public void BadChecksum_IsNaked_ThenRetransmissionAccepted()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        var frames = Frames(AccessResult);
        var corrupted = (byte[])frames[3].Clone();
        corrupted[10] ^= 0x01; // hỏng một byte trong text

        driver.Receive([Enq], T0).Send.Single().ShouldBe([Ack]);
        foreach (var f in frames.Take(3))
            driver.Receive(f, T0).Send.Single().ShouldBe([Ack]);

        var bad = driver.Receive(corrupted, T0);
        bad.Send.Single().ShouldBe([Nak]);
        bad.Notes.Single().ShouldContain("Checksum sai");

        foreach (var f in frames.Skip(3))
            driver.Receive(f, T0).Send.Single().ShouldBe([Ack]);
        var end = driver.Receive([Eot], T0);

        end.Readings.Single().Items.Select(i => i.ChannelCode).ShouldBe(["TSH3", "FT4II"]);
    }

    [Fact]
    public void DuplicateFrame_IsAckedButNotRecordedTwice()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        var frames = Frames(AccessResult);
        frames.Insert(4, frames[3]); // ACK của frame R thứ nhất bị mất → máy gửi lại

        var outputs = SendSession(driver, frames);

        outputs.SelectMany(o => o.Notes).ShouldContain(n => n.Contains("trùng"));
        outputs.SelectMany(o => o.Readings).Single().Items.Count.ShouldBe(2);
    }

    [Fact]
    public void WrongFrameNumber_IsNaked()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        driver.Receive([Enq], T0);

        driver.Receive(AstmFraming.BuildFrames(AccessResult[0], 3, out _)[0], T0).Send.Single().ShouldBe([Nak]);
    }

    [Fact]
    public void FrameNumbers_WrapFrom7To0()
    {
        string[] records =
        [
            AccessResult[0], AccessResult[1], AccessResult[2],
            .. Enumerable.Range(1, 8).Select(i => $"R|{i}|^^^T{i}^1|{i}.0|U||N||F"),
            "L|1|F",
        ];
        var frames = Frames(records);
        frames[7][1].ShouldBe((byte)'0');

        var reading = SendSession(new AstmDriver(AstmDriverOptions.Access2), frames).SelectMany(o => o.Readings).Single();

        reading.Items.Count.ShouldBe(8);
    }

    [Fact]
    public void SilenceMidSession_AbandonsPartialMessage()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        var frames = Frames(AccessResult);
        driver.Receive([Enq], T0);
        foreach (var f in frames.Take(4))
            driver.Receive(f, T0);

        driver.Tick(T0.AddSeconds(29)).IsEmpty.ShouldBeTrue();
        var timeout = driver.Tick(T0.AddSeconds(31));

        timeout.Notes.Single().ShouldContain("bỏ tin dở");
        // Về rảnh: frame/EOT lạc sau đó bị bỏ qua, không sinh kết quả.
        driver.Receive(frames[4], T0.AddSeconds(32)).IsEmpty.ShouldBeTrue();
        driver.Receive([Eot], T0.AddSeconds(32)).Readings.ShouldBeEmpty();
    }

    [Fact]
    public void QcOrder_IsMarkedQc()
    {
        string[] records =
        [
            AccessResult[0],
            @"O|1|QCLOT123||^^^TSH3^1|||||||Q||||Serum||||||||||F",
            "R|1|^^^TSH3^1|0.51|uIU/mL||N||F",
            "L|1|F",
        ];

        var reading = SendSession(new AstmDriver(AstmDriverOptions.Access2), Frames(records)).SelectMany(o => o.Readings).Single();

        reading.IsQc.ShouldBeTrue();
        reading.Barcode.ShouldBe("QCLOT123");
    }

    [Fact]
    public void Access2_SkipsNonFinalAndEmptyResults_AndKeepsQualitativeInterpretation()
    {
        string[] records =
        [
            AccessResult[0],
            "P|1|BNTHU0002",
            @"O|1|2611030002||^^^Rub-IgG^1|||||||||||Serum||||||||||F",
            "R|1|^^^Rub-IgG^1|0.24^Non-React.|S/CO||N||F",
            "R|2|^^^AFP^1||||||X",
            "R|3|^^^CEA2^1|Cancelled|||||X",
            "L|1|F",
        ];

        var reading = SendSession(new AstmDriver(AstmDriverOptions.Access2), Frames(records)).SelectMany(o => o.Readings).Single();

        reading.Items.Single().ShouldBe(new Lis.Core.Ingest.ReadingItem("Rub-IgG", "0.24", "S/CO", "Non-React."));
    }

    [Fact]
    public void MultipleOrdersInOneSession_GiveOneReadingEach()
    {
        string[] records =
        [
            AccessResult[0],
            "P|1|BN1", "O|1|S001||^^^TSH3^1", "R|1|^^^TSH3^1|1.1|uIU/mL||N||F",
            "P|2|BN2", "O|1|S002||^^^TSH3^1", "R|1|^^^TSH3^1|2.2|uIU/mL||N||F",
            "L|1|F",
        ];

        var readings = SendSession(new AstmDriver(AstmDriverOptions.Access2), Frames(records)).SelectMany(o => o.Readings).ToList();

        readings.Select(r => r.Barcode).ShouldBe(["S001", "S002"]);
        readings[1].RawText.ShouldContain("P|2|BN2");
        readings[1].RawText.ShouldNotContain("S001");
    }

    [Fact]
    public void RejectedOrder_WithErrorComment_GivesNoteNotReading()
    {
        string[] records = [AccessResult[0], "P|1|BN1", "O|1|S009||^^^TSH3|||||||||||||||||||||X", "C|1|I|Sample already exists|G", "L|1|F"];

        var outputs = SendSession(new AstmDriver(AstmDriverOptions.Access2), Frames(records));

        outputs.SelectMany(o => o.Readings).ShouldBeEmpty();
        outputs.SelectMany(o => o.Notes).ShouldContain(n => n.Contains("Sample already exists"));
    }

    [Fact]
    public void EscapedDelimiters_AreDecoded()
    {
        string[] records = [AccessResult[0], "O|1|A&S&B||^^^X", "R|1|^^^X|1&F&2|U||N||F", "L|1|F"];

        var reading = SendSession(new AstmDriver(AstmDriverOptions.Generic), Frames(records)).SelectMany(o => o.Readings).Single();

        reading.Barcode.ShouldBe("A^B");
        reading.Items.Single().RawValue.ShouldBe("1|2");
    }

    private static readonly string[] AccessQuery =
    [
        @"H|\^&|||ACCESS^500001|||||LIS||P|1|20261103090000",
        "Q|1|^2611030001||ALL||||||||O",
        "L|1|F",
    ];

    /// <summary>Máy giả ACK mọi thứ LIS gửi cho tới EOT; trả về các khối LIS đã gửi (không tính ENQ đầu).</summary>
    private static List<byte[]> AckUntilEot(IInstrumentDriver driver, DateTimeOffset now)
    {
        var sent = new List<byte[]>();
        for (var i = 0; i < 20; i++)
        {
            var output = driver.Receive([Ack], now);
            sent.AddRange(output.Send);
            if (output.Send.Count == 0 || output.Send[^1].SequenceEqual(new[] { Eot }))
                break;
        }

        return sent;
    }

    [Fact]
    public void HostQuery_ProducesQuery_AndAnswerIsSentAsNewSessionAfterAcks()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        var query = SendSession(driver, Frames(AccessQuery)).SelectMany(o => o.Queries).Single();
        query.Barcode.ShouldBe("2611030001");

        var answer = driver.AnswerQuery(query, ["TSH3", "FT4II"], T0);
        answer.Send.Single().ShouldBe([Enq]);
        answer.Notes.Single().ShouldContain("TSH3, FT4II");

        var sent = AckUntilEot(driver, T0);

        sent.Count.ShouldBe(5);
        sent[^1].ShouldBe([Eot]);
        var texts = sent.Take(4).Select(Text).ToList();
        texts[0].ShouldStartWith("\u00021H|\\^&|||LIS|||||ACCESS^500001||P|1|20261103091500\r\u0003");
        texts[1].ShouldStartWith("\u00022P|1\r\u0003");
        texts[2].ShouldStartWith("\u00023O|1|2611030001||^^^TSH3\\^^^FT4II|R||||||A\r\u0003");
        texts[3].ShouldStartWith("\u00024L|1|F\r\u0003");
        foreach (var frame in sent.Take(4))
        {
            var body = frame.AsSpan(1, frame.Length - 5);
            Text(frame)[^4..^2].ShouldBe(AstmFraming.Checksum(body));
        }
    }

    [Fact]
    public void HostQuery_NoOrders_AnswersHeaderAndTerminatorOnly()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        var query = SendSession(driver, Frames(AccessQuery)).SelectMany(o => o.Queries).Single();

        driver.AnswerQuery(query, [], T0).Send.Single().ShouldBe([Enq]);
        var sent = AckUntilEot(driver, T0);

        sent.Count.ShouldBe(3);
        Text(sent[1]).ShouldStartWith("\u00022L|1|F\r\u0003");
    }

    [Fact]
    public void GenericOptions_NoOrders_UsesTerminationCodeI_AndAnswerReportTypeO()
    {
        var driver = new AstmDriver();
        var query = SendSession(driver, Frames(AccessQuery)).SelectMany(o => o.Queries).Single();

        driver.AnswerQuery(query, [], T0);
        Text(AckUntilEot(driver, T0)[1]).ShouldStartWith("\u00022L|1|I\r");

        driver.AnswerQuery(query, ["GLU"], T0);
        var order = Text(AckUntilEot(driver, T0)[2]);
        order.ShouldStartWith("\u00023O|1|2611030001||^^^GLU|R||||||A||||||||||||||O\r");
    }

    [Fact]
    public void FrameNak_IsRetransmittedWithSameNumber()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        var query = SendSession(driver, Frames(AccessQuery)).SelectMany(o => o.Queries).Single();
        driver.AnswerQuery(query, ["TSH3"], T0);

        var first = driver.Receive([Ack], T0).Send.Single();
        var again = driver.Receive([Nak], T0).Send.Single();

        again.ShouldBe(first);
    }

    [Fact]
    public void Contention_LisYieldsToInstrument_ThenResendsEnqAfterInstrumentEot()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        var query = SendSession(driver, Frames(AccessQuery)).SelectMany(o => o.Queries).Single();
        driver.AnswerQuery(query, ["TSH3"], T0).Send.Single().ShouldBe([Enq]);

        // Máy cũng gửi ENQ cùng lúc → LIS phải ACK máy và nhận tin của máy trước.
        var yield = driver.Receive([Enq], T0);
        yield.Send.Single().ShouldBe([Ack]);
        yield.Notes.Single().ShouldContain("Tranh chấp");

        var outputs = Frames(AccessResult).Select(f => driver.Receive(f, T0)).ToList();
        AllSent(outputs).ShouldAllBe(b => b.Length == 1 && b[0] == Ack);
        var end = driver.Receive([Eot], T0.AddSeconds(1));

        end.Readings.Single().Barcode.ShouldBe("2611030001");
        end.Send.Single().ShouldBe([Enq]); // máy nhả đường → LIS xin gửi lại ngay
        AckUntilEot(driver, T0.AddSeconds(1)).Count.ShouldBe(5);
    }

    [Fact]
    public void EnqNak_RetriesAfterTenSeconds()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        var query = SendSession(driver, Frames(AccessQuery)).SelectMany(o => o.Queries).Single();
        driver.AnswerQuery(query, ["TSH3"], T0);

        driver.Receive([Nak], T0).Notes.Single().ShouldContain("NAK cho ENQ");
        driver.Tick(T0.AddSeconds(9)).Send.ShouldBeEmpty();
        driver.Tick(T0.AddSeconds(10)).Send.Single().ShouldBe([Enq]);
    }

    [Fact]
    public void NoReplyToFrame_SendsEotAfter15Seconds_AndRetriesWholeMessageLater()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        var query = SendSession(driver, Frames(AccessQuery)).SelectMany(o => o.Queries).Single();
        driver.AnswerQuery(query, ["TSH3"], T0);
        driver.Receive([Ack], T0); // frame 1 đã gửi, máy im

        driver.Tick(T0.AddSeconds(14)).Send.ShouldBeEmpty();
        var timeout = driver.Tick(T0.AddSeconds(15));
        timeout.Send.Single().ShouldBe([Eot]);

        driver.Tick(T0.AddSeconds(25)).Send.Single().ShouldBe([Enq]);
        var resent = driver.Receive([Ack], T0.AddSeconds(25)).Send.Single();
        Text(resent).ShouldStartWith("\u00021H|"); // gửi lại từ đầu, frame 1
    }

    [Fact]
    public void QueuedMessages_AreSentOneAfterAnother()
    {
        var driver = new AstmDriver(AstmDriverOptions.Access2);
        var queries = SendSession(driver, Frames([AccessQuery[0], "Q|1|^S1||ALL||||||||O", "Q|2|^S2||ALL||||||||O", "L|1|F"]))
            .SelectMany(o => o.Queries).ToList();
        queries.Select(q => q.Barcode).ShouldBe(["S1", "S2"]);

        driver.AnswerQuery(queries[0], ["TSH3"], T0).Send.Single().ShouldBe([Enq]);
        driver.AnswerQuery(queries[1], [], T0).Send.ShouldBeEmpty(); // chờ tin trước gửi xong

        for (var i = 0; i < 4; i++)
            driver.Receive([Ack], T0).Send.Single()[0].ShouldBe((byte)0x02); // H, P, O, L

        // ACK frame cuối: EOT kết thúc tin 1 và ENQ mở ngay phiên tin 2 trong cùng đầu ra.
        driver.Receive([Ack], T0).Send.ShouldBe([[Eot], [Enq]]);
        var second = AckUntilEot(driver, T0);
        Text(second[1]).ShouldStartWith("\u00022L|1|F\r");
    }
}
