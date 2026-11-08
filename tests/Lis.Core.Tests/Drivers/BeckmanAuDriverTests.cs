using System.Text;
using Lis.Core.Drivers;
using Lis.Core.Drivers.BeckmanAu;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Drivers;

/// <summary>
/// Driver AU Online (AU480): tách khung STX/ETX(+BCC), Class A/B, phiên DB/D␣/DE, nhiều khối, QC,
/// hỏi chỉ định R␣ → S␣/SE. Bản tin mẫu theo docs/drivers/beckman-au.md mục 7 (dữ liệu giả).
/// </summary>
public class BeckmanAuDriverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.FromHours(7));

    /// <summary>Cấu hình tham chiếu của tài liệu mục 7: Class B, BCC bật, Rack 4, Sample ID 10, test 2, kết quả 6, cờ 2.</summary>
    private static readonly BeckmanAuOptions DocReference = BeckmanAuOptions.Au480 with { ClassB = true, Bcc = true };

    private const string ResultText = "D 001203 01232611030001    E01  5.42  03  86.0H 08  25.0  ";

    private static byte Bcc(string text, byte end = 0x03)
    {
        byte bcc = end;
        foreach (var b in Encoding.ASCII.GetBytes(text))
        {
            bcc ^= b;
        }

        return bcc;
    }

    private static byte[] Frame(string text, bool bcc = true)
    {
        var bytes = new List<byte> { 0x02 };
        bytes.AddRange(Encoding.ASCII.GetBytes(text));
        bytes.Add(0x03);
        if (bcc)
        {
            bytes.Add(Bcc(text));
        }

        return [.. bytes];
    }

    private static string Text(byte[] frame) => Encoding.ASCII.GetString(frame, 1, frame.Length - 2 - (frame[^1] == 0x03 ? 0 : 1)).TrimEnd('\u0003');

    [Fact]
    public void BccMatchesDocumentExamples()
    {
        Bcc("DB").ShouldBe((byte)0x05);
        Bcc("DE").ShouldBe((byte)0x02);
        Bcc("SE").ShouldBe((byte)0x15);
        Bcc(ResultText).ShouldBe((byte)0x72);
        Bcc("R 001203 01232611030001").ShouldBe((byte)0x57);
        Bcc("S 001203 01232611030001    E010308").ShouldBe((byte)0x19);
    }

    [Fact]
    public void ClassB_ResultSession_AcksEachMessage_AndParsesItems()
    {
        var driver = new BeckmanAuDriver(DocReference);

        var db = driver.Receive(Frame("DB"), T0);
        var d = driver.Receive(Frame(ResultText), T0);
        var de = driver.Receive(Frame("DE"), T0);

        db.Send.ShouldBe([new byte[] { 0x06 }]);
        de.Send.ShouldBe([new byte[] { 0x06 }]);
        d.Send.ShouldBe([new byte[] { 0x06 }]);
        var reading = d.Readings.ShouldHaveSingleItem();
        reading.Barcode.ShouldBe("2611030001");
        reading.IsQc.ShouldBeFalse();
        reading.Items.Select(i => (i.ChannelCode, i.RawValue, i.InstrumentFlag)).ShouldBe(
            [("01", "5.42", null), ("03", "86.0", "H"), ("08", "25.0", null)]);
        reading.Items.ShouldAllBe(i => i.Unit == null);
        reading.RawText.ShouldBe("<STX>" + ResultText + "<ETX>");
        de.Readings.ShouldBeEmpty();
    }

    [Fact]
    public void ByteByByte_WithBccEqualToStx_StillFramesCorrectly()
    {
        // "DE" có BCC = 0x02 (trùng STX) — bộ tách khung phải đọc nó là BCC, không phải khung mới.
        var driver = new BeckmanAuDriver(DocReference);
        var stream = Frame("DB").Concat(Frame(ResultText)).Concat(Frame("DE")).ToArray();

        var readings = new List<DriverReading>();
        var acks = 0;
        foreach (var b in stream)
        {
            var output = driver.Receive([b], T0);
            readings.AddRange(output.Readings);
            acks += output.Send.Count(s => s is [0x06]);
            output.Notes.ShouldBeEmpty();
        }

        readings.ShouldHaveSingleItem().Items.Count.ShouldBe(3);
        acks.ShouldBe(3);
    }

    [Fact]
    public void ClassA_NoAck_AndGarbageBetweenFramesIsIgnored()
    {
        var driver = new BeckmanAuDriver(BeckmanAuOptions.Au480);
        var data = new byte[] { 0xFF, 0x0D, 0x0A }.Concat(Frame("DB", bcc: false)).Concat(new byte[] { 0x41, 0x42 })
            .Concat(Frame(ResultText, bcc: false)).ToArray();

        var output = driver.Receive(data, T0);

        output.Send.ShouldBeEmpty();
        output.Readings.ShouldHaveSingleItem().Barcode.ShouldBe("2611030001");
        output.Notes.ShouldBe(["Bỏ 3 byte ngoài khung", "Bỏ 2 byte ngoài khung"]);
    }

    [Fact]
    public void ClassB_BadBcc_SendsNak_AndDropsMessage()
    {
        var driver = new BeckmanAuDriver(DocReference);
        var frame = Frame(ResultText);
        frame[^1] ^= 0x01;

        var output = driver.Receive(frame, T0);

        output.Send.ShouldBe([new byte[] { 0x15 }]);
        output.Readings.ShouldBeEmpty();
        output.Notes.ShouldHaveSingleItem().ShouldStartWith("BCC sai");

        // Máy gửi lại đúng → nhận bình thường.
        driver.Receive(Frame(ResultText), T0).Readings.Count.ShouldBe(1);
    }

    [Fact]
    public void ValuesAndFlags_ZeroPaddingNegativeOverflowAndNotMeasured()
    {
        var driver = new BeckmanAuDriver(BeckmanAuOptions.Au480);
        var text = "D 001203 01232611030001    E"
            + "010123.4  "   // không zero suppress
            + "02-01234L "   // âm
            + "03999999F "   // vượt số chữ số → bỏ
            + "04      / "   // chưa đo → bỏ
            + "05  0.12Fp";  // 2 cờ ghép
        var output = driver.Receive(Frame(text, bcc: false), T0);

        var reading = output.Readings.ShouldHaveSingleItem();
        reading.Items.Select(i => (i.ChannelCode, i.RawValue, i.InstrumentFlag)).ShouldBe(
            [("01", "123.4", null), ("02", "-1234", "L"), ("05", "0.12", "Fp")]);
        output.Notes.Count.ShouldBe(2);
        output.Notes.ShouldContain(n => n.Contains("kênh 03") && n.Contains("digit over"));
        output.Notes.ShouldContain(n => n.Contains("kênh 04") && n.Contains("chưa đo"));
    }

    [Fact]
    public void ThreeDigitTestNo_NineDigitResult_EightCharFlags_KeepLeadingZerosInChannel()
    {
        var options = BeckmanAuOptions.Au480 with { TestNoDigits = 3, ResultDigits = 9, FlagLength = 8, SampleIdLength = 12 };
        var driver = new BeckmanAuDriver(options);
        var text = "D 001203 0123  2611030001    E" + "001" + "   12.345" + "H ph    " + "120" + "-  0.1234" + "        ";

        var reading = driver.Receive(Frame(text, bcc: false), T0).Readings.ShouldHaveSingleItem();

        reading.Items.Select(i => (i.ChannelCode, i.RawValue, i.InstrumentFlag)).ShouldBe(
            [("001", "12.345", "H ph"), ("120", "-0.1234", null)]);
    }

    [Fact]
    public void PatientInfoAndSystemNo_AreSkipped_OnFirstBlockOnly()
    {
        var options = BeckmanAuOptions.Au480 with { SystemNo = true, Sex = true, YearAge = true, MonthAge = true, PatientInfoLengths = [5] };
        var driver = new BeckmanAuDriver(options);
        var head = "D 07001203 01232611030001    ";
        var block0 = head + "0" + "M04503" + "Thu01" + "01  5.42  ";
        var blockE = head + "E" + "03  86.0H ";

        var first = driver.Receive(Frame(block0, bcc: false), T0);
        var last = driver.Receive(Frame(blockE, bcc: false), T0);

        first.Readings.ShouldBeEmpty();
        var reading = last.Readings.ShouldHaveSingleItem();
        reading.Items.Select(i => i.ChannelCode).ShouldBe(["01", "03"]);
        reading.RawText.Split('\n').Length.ShouldBe(2);
    }

    [Fact]
    public void QcAndRepeat_AreRecognized_CalibrationIsSkipped()
    {
        var driver = new BeckmanAuDriver(BeckmanAuOptions.Au480);

        var qc = driver.Receive(Frame("DQ" + "0000" + "01" + " " + "Q001" + " QC-LOT-01" + "    " + "E" + "01" + "01  5.10  ", bcc: false), T0);
        var repeat = driver.Receive(Frame("DH" + "0012" + "03" + " " + "0201" + "2611030001" + "0123" + "E" + "01  5.40  ", bcc: false), T0);
        var cal = driver.Receive(Frame("DA000001 A001      CAL01    E01  1.00  ", bcc: false), T0);

        var qcReading = qc.Readings.ShouldHaveSingleItem();
        qcReading.IsQc.ShouldBeTrue();
        qcReading.Barcode.ShouldBe("QC-LOT-01");
        qcReading.Items.ShouldHaveSingleItem().RawValue.ShouldBe("5.10");

        var rep = repeat.Readings.ShouldHaveSingleItem();
        rep.IsQc.ShouldBeFalse();
        rep.Barcode.ShouldBe("2611030001");

        cal.Readings.ShouldBeEmpty();
        cal.Notes.ShouldBe(["Bỏ qua bản tin chuẩn (DA)"]);
    }

    [Fact]
    public void LengthMismatch_DropsWholeSample_InsteadOfGuessing()
    {
        var driver = new BeckmanAuDriver(BeckmanAuOptions.Au480);

        // Máy đặt test No. 3 chữ số nhưng hồ sơ để 2 → độ dài lệch.
        var output = driver.Receive(Frame("D 001203 01232611030001    E001  5.42  ", bcc: false), T0);

        output.Readings.ShouldBeEmpty();
        output.Notes.ShouldHaveSingleItem().ShouldContain("cấu hình không khớp máy");
    }

    [Fact]
    public void MalformedBlocks_AreReportedNotCrashing()
    {
        var driver = new BeckmanAuDriver(BeckmanAuOptions.Au480);

        driver.Receive(Frame("D 0012", bcc: false), T0).Notes.ShouldHaveSingleItem().ShouldContain("ngắn hơn phần cố định");
        driver.Receive(Frame("ZZ", bcc: false), T0).Notes.ShouldHaveSingleItem().ShouldContain("Mã phân biệt không rõ");
        driver.Receive(Frame("D 001203 01232611030001    X01  5.42  ", bcc: false), T0).Notes.ShouldHaveSingleItem().ShouldContain("Data classification");
        driver.Receive(Frame("D 001203 01232611030001    101  5.42  ", bcc: false), T0).Notes.ShouldHaveSingleItem().ShouldContain("chưa có khối đầu");
    }

    [Fact]
    public void EmptySampleId_StillEmitsReadingWithEmptyBarcode()
    {
        var driver = new BeckmanAuDriver(BeckmanAuOptions.Au480);

        var output = driver.Receive(Frame("D 001203 0123" + new string(' ', 10) + "    " + "E01  5.42  ", bcc: false), T0);

        output.Readings.ShouldHaveSingleItem().Barcode.ShouldBe("");
        output.Notes.ShouldHaveSingleItem().ShouldContain("không có Sample ID");
    }

    [Fact]
    public void Tick_DropsStaleHalfFrame_AndIncompleteMultiBlock()
    {
        var driver = new BeckmanAuDriver(BeckmanAuOptions.Au480);
        driver.Receive(Frame("D 001203 01232611030001    001  5.42  ", bcc: false), T0);
        driver.Receive(Encoding.ASCII.GetBytes("\u0002D 0012"), T0);

        driver.Tick(T0.AddSeconds(1)).IsEmpty.ShouldBeTrue();
        var stale = driver.Tick(T0.AddSeconds(5));
        stale.Notes.ShouldHaveSingleItem().ShouldContain("Bỏ khung nhận dở");

        var multi = driver.Tick(T0.AddSeconds(40));
        multi.Notes.ShouldHaveSingleItem().ShouldContain("khối cuối");

        // Bản tin một khối (E) sau đó vẫn nhận bình thường.
        driver.Receive(Frame("D 001203 01232611030001    E03  86.0H ", bcc: false), T0.AddSeconds(41)).Readings.ShouldHaveSingleItem();
    }

    [Fact]
    public void Query_ClassB_AcksThenAnswersWithTests_MatchingDocumentFrame()
    {
        var driver = new BeckmanAuDriver(DocReference);

        driver.Receive(Frame("RB"), T0).Send.ShouldBe([new byte[] { 0x06 }]);
        var asked = driver.Receive(Frame("R 001203 01232611030001"), T0);

        asked.Send.ShouldBe([new byte[] { 0x06 }]);
        var query = asked.Queries.ShouldHaveSingleItem();
        query.Barcode.ShouldBe("2611030001");

        var answer = driver.AnswerQuery(query, ["01", "3", "08"], T0.AddMilliseconds(300));

        answer.Send.ShouldBe([Frame("S 001203 01232611030001    E010308")]);
        answer.Notes.ShouldBeEmpty();

        // Máy ACK → không còn gì chờ.
        driver.Receive([0x06], T0.AddSeconds(1)).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Query_NoOrders_RepliesSE_ByDefault_OrEmptyRequisition()
    {
        var stop = new BeckmanAuDriver(DocReference);
        var q1 = stop.Receive(Frame("R 001203 01232611030001"), T0).Queries.Single();
        stop.AnswerQuery(q1, [], T0).Send.ShouldBe([new byte[] { 0x02, (byte)'S', (byte)'E', 0x03, 0x15 }]);

        var empty = new BeckmanAuDriver(DocReference with { NoOrderReply = AuNoOrderReply.EmptyRequisition });
        var q2 = empty.Receive(Frame("R 001203 01232611030001"), T0).Queries.Single();
        empty.AnswerQuery(q2, [], T0).Send.ShouldBe([Frame("S 001203 01232611030001    E")]);
    }

    [Fact]
    public void Query_NakFromMachine_ResendsOnce_ThenGivesUp()
    {
        var driver = new BeckmanAuDriver(DocReference);
        var query = driver.Receive(Frame("R 001203 01232611030001"), T0).Queries.Single();
        var sent = driver.AnswerQuery(query, ["01"], T0).Send.Single();

        var retry = driver.Receive([0x15], T0.AddSeconds(1));
        retry.Send.ShouldBe([sent]);

        var giveUp = driver.Receive([0x15], T0.AddSeconds(2));
        giveUp.Send.ShouldBeEmpty();
        giveUp.Notes.ShouldHaveSingleItem().ShouldContain("hết số lần gửi lại");
    }

    [Fact]
    public void Query_LateAnswer_IsFlagged_AndNonNumericCodesSkipped()
    {
        var driver = new BeckmanAuDriver(BeckmanAuOptions.Au480 with { DilutionInfo = true });
        var query = driver.Receive(Frame("R 001203 01232611030001", bcc: false), T0).Queries.Single();

        var answer = driver.AnswerQuery(query, ["01", "GLU", "123"], T0.AddSeconds(3));

        answer.Send.Select(Text).ShouldBe(["S 001203 01232611030001    E010"]);
        answer.Notes.Count.ShouldBe(3);
        answer.Notes[0].ShouldContain("muộn");
    }

    [Fact]
    public void RepeatQuery_RepliesSH_WithDiluentType()
    {
        var driver = new BeckmanAuDriver(BeckmanAuOptions.Au480);
        var query = driver.Receive(Frame("RH001203 02012611030001", bcc: false), T0).Queries.Single();

        driver.AnswerQuery(query, ["01"], T0).Send.Select(Text).ShouldBe(["SH001203 02012611030001" + "0201" + "E010"]);
    }

    [Fact]
    public void LongRequisition_IsSplitIntoBlocks_ClassAPacedByTick()
    {
        var driver = new BeckmanAuDriver(BeckmanAuOptions.Au480 with { TextLength = 256, TestNoDigits = 3 });
        var query = driver.Receive(Frame("R 001203 01232611030001", bcc: false), T0).Queries.Single();
        var tests = Enumerable.Range(1, 100).Select(i => i.ToString("000")).ToList();

        var first = driver.AnswerQuery(query, tests, T0);

        var block0 = Text(first.Send.ShouldHaveSingleItem());
        block0[27].ShouldBe('0');
        (block0.Length + 2).ShouldBeLessThanOrEqualTo(256);
        driver.Tick(T0.AddSeconds(1)).Send.ShouldBeEmpty();
        var second = Text(driver.Tick(T0.AddSeconds(2)).Send.ShouldHaveSingleItem());
        second[27].ShouldBe('E');
        (block0[28..] + second[28..]).ShouldBe(string.Concat(tests));
    }

    [Fact]
    public void NormalizeValue_Cases()
    {
        BeckmanAuDriver.NormalizeValue("  5.42").ShouldBe("5.42");
        BeckmanAuDriver.NormalizeValue("0123.4").ShouldBe("123.4");
        BeckmanAuDriver.NormalizeValue("-0123.456").ShouldBe("-123.456");
        BeckmanAuDriver.NormalizeValue("- 1234").ShouldBe("-1234");
        BeckmanAuDriver.NormalizeValue("0.1234").ShouldBe("0.1234");
        BeckmanAuDriver.NormalizeValue("000000").ShouldBe("0");
        BeckmanAuDriver.NormalizeValue("  .5").ShouldBe("0.5");
    }

    [Fact]
    public void InvalidOptions_Throw()
    {
        Should.Throw<ArgumentException>(() => new BeckmanAuDriver(BeckmanAuOptions.Au480 with { TestNoDigits = 4 }));
        Should.Throw<ArgumentException>(() => new BeckmanAuDriver(BeckmanAuOptions.Au480 with { SampleIdLength = 30 }));
    }
}
