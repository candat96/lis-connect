using System.Text;
using Lis.Core.Drivers;
using Lis.Core.Drivers.Hl7;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Drivers;

/// <summary>
/// Driver HL7-MLLP (Mindray BC-5380): cắt khung theo byte, tách kết quả/QC, ACK ngay, hỏi và trả
/// chỉ định. Dữ liệu theo bản tin mẫu Phụ lục C, họ tên/mã giả.
/// </summary>
public class Hl7MllpDriverTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 9, 30, 0, TimeSpan.FromHours(7));

    private const string Result =
        "MSH|^~\\&|Analyzer|Mindray|||20080617143943||ORU^R01|1|P|2.3.1||||||UNICODE\r" +
        "PID|1||BN0001^^^^MR||Mẫu^Thử||19900804000000|Female\r" +
        "PV1|1||nk^^001\r" +
        "OBR|1||2611080001|00001^Automated Count^99MRC||20080508140600|20080508150616|||Kỹ thuật viên||||20080508150000||||||||||HM||||||||Mindray\r" +
        "OBX|1|IS|08001^Take Mode^99MRC||O||||||F\r" +
        "OBX|3|IS|08003^Test Mode^99MRC||CBC||||||F\r" +
        "OBX|5|NM|30525-0^Age^LN||18|yr|||||F\r" +
        "OBX|6|NM|6690-2^WBC^LN||9.81|10*9/L|4.00-10.00|N|||F||E\r" +
        "OBX|21|NM|789-8^RBC^LN||4.53|10*12/L|3.50-5.00|N|||F\r" +
        "OBX|22|NM|718-7^HGB^LN||65|g/L|110-150|L|||F\r" +
        "OBX|23|NM|4544-3^HCT^LN||***.*|%||||F\r" +
        "OBX|24|NM|10002^PCT^99MRC||0.212|%|0.108-0.282|N|||F\r" +
        "OBX|25|NM|15001^WBC Left Line^99MRC||12||||||F\r" +
        "OBX|29|NM|777-3^PLT^LN||212|10*9/L|100-300|H~A|||F\r" +
        "OBX|33|IS|12014^Anemia^99MRC||T||||||F\r" +
        "OBX|38|ED|15008^WBC Histogram. BMP^99MRC||^Image^BMP^Base64^Qk0AAAA=||||||F\r" +
        "OBR|2||2611080001|00002^Manual Count^99MRC\r" +
        "OBX|1|NM|769-0^Neuts Seg%^LN||60|%|||||F\r" +
        "OBX|2|ST|6742-1^RBC Morphology^LN||Hồng cầu nhỏ\\F\\nhược sắc||||||F\r";

    private static byte[] Frame(string text) => [0x0B, .. Encoding.UTF8.GetBytes(text), 0x1C, 0x0D];

    private static string Unframe(byte[] frame)
    {
        frame[0].ShouldBe((byte)0x0B);
        frame[^2].ShouldBe((byte)0x1C);
        frame[^1].ShouldBe((byte)0x0D);
        return Encoding.UTF8.GetString(frame, 1, frame.Length - 3);
    }

    [Fact]
    public void Result_ParsedIntoOneReading_SkipsNonResultsAndAcksAA()
    {
        var driver = new Hl7MllpDriver();

        var output = driver.Receive(Frame(Result), Now);

        var reading = output.Readings.ShouldHaveSingleItem();
        reading.Barcode.ShouldBe("2611080001");
        reading.IsQc.ShouldBeFalse();
        reading.Items.Select(i => i.ChannelCode).ShouldBe(["6690-2", "789-8", "718-7", "10002", "777-3", "769-0", "6742-1"]);
        reading.Items[0].RawValue.ShouldBe("9.81");
        reading.Items[0].Unit.ShouldBe("10*9/L");
        reading.Items[0].InstrumentFlag.ShouldBe("N");
        reading.Items[4].InstrumentFlag.ShouldBe("H~A");
        reading.Items[^1].RawValue.ShouldBe("Hồng cầu nhỏ|nhược sắc");
        reading.Items[^1].Unit.ShouldBeNull();
        reading.RawText.ShouldStartWith("MSH|");
        reading.RawText.ShouldContain("\rPID|1||BN0001");

        var ack = Unframe(output.Send.ShouldHaveSingleItem()).Split('\r', StringSplitOptions.RemoveEmptyEntries);
        ack.Length.ShouldBe(2);
        ack[0].ShouldStartWith("MSH|^~\\&|LIS||||20261108093000||ACK^R01|");
        ack[0].Split('|')[10].ShouldBe("P");
        ack[1].ShouldBe("MSA|AA|1");
    }

    [Fact]
    public void Result_SplitByteByByte_StillOneReadingAndOneAck()
    {
        var driver = new Hl7MllpDriver();
        var readings = new List<DriverReading>();
        var sends = new List<byte[]>();

        foreach (var b in Frame(Result))
        {
            var output = driver.Receive([b], Now);
            readings.AddRange(output.Readings);
            sends.AddRange(output.Send);
        }

        readings.ShouldHaveSingleItem().Items.Count.ShouldBe(7);
        sends.ShouldHaveSingleItem();
    }

    [Fact]
    public void TwoFramesInOneChunk_WithGarbageBefore_BothParsed()
    {
        var driver = new Hl7MllpDriver();
        var second = Result.Replace("ORU^R01|1|", "ORU^R01|2|").Replace("2611080001", "2611080002");

        var output = driver.Receive([0x41, 0x00, 0xFF, .. Frame(Result), .. Frame(second)], Now);

        output.Readings.Select(r => r.Barcode).ShouldBe(["2611080001", "2611080002"]);
        output.Send.Select(f => Unframe(f).Split('\r')[1]).ShouldBe(["MSA|AA|1", "MSA|AA|2"]);
        output.Notes.ShouldContain(n => n.Contains("3 byte"));
    }

    [Fact]
    public void QcMessage_EachObrIsSeparateQcReading()
    {
        var qc =
            "MSH|^~\\&|Analyzer|Mindray|||20261108090000||ORU^R01|7|Q|2.3.1||||||UNICODE\r" +
            "PID|1||LOT123^^^^MR\r" +
            "OBR|1||QC01|00006^XR QCR^99MRC\r" +
            "OBX|1|NM|6690-2^WBC^LN||7.10|10*9/L|||||F\r" +
            "PID|2||LOT123^^^^MR\r" +
            "OBR|2||QC01|00006^XR QCR^99MRC\r" +
            "OBX|1|NM|6690-2^WBC^LN||7.30|10*9/L|||||F\r";

        var output = new Hl7MllpDriver().Receive(Frame(qc), Now);

        output.Readings.Count.ShouldBe(2);
        output.Readings.ShouldAllBe(r => r.IsQc && r.Barcode == "QC01");
        output.Readings.Select(r => r.Items[0].RawValue).ShouldBe(["7.10", "7.30"]);
        var ack = Unframe(output.Send.ShouldHaveSingleItem()).Split('\r');
        ack[0].Split('|')[10].ShouldBe("Q");
        ack[1].ShouldBe("MSA|AA|7");
    }

    [Fact]
    public void Query_EmitsQuery_AnswerWithDiffTests_BuildsOrr()
    {
        var driver = new Hl7MllpDriver();
        var queryText =
            "MSH|^~\\&|Analyzer|Mindray|||20081120174836||ORM^O01|4|P|2.3.1||||||UNICODE\r" +
            "ORC|RF||2611030001||IP\r";

        var output = driver.Receive(Frame(queryText), Now);

        output.Send.ShouldBeEmpty();
        var query = output.Queries.ShouldHaveSingleItem();
        query.Barcode.ShouldBe("2611030001");

        var answer = driver.AnswerQuery(query, ["6690-2", "770-8"], Now);
        var lines = Unframe(answer.Send.ShouldHaveSingleItem()).Split('\r', StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldContain("||ORR^O02|");
        lines[1].ShouldBe("MSA|AA|4");
        lines.ShouldContain("ORC|AF|2611030001");
        lines.ShouldContain("OBR|1|2611030001");
        lines.ShouldContain("OBX|1|IS|08003^Test Mode^99MRC||CBC+5DIFF||||||F");
    }

    [Fact]
    public void Query_AnswerCbcOnly_And_NoOrderGivesAR()
    {
        var driver = new Hl7MllpDriver();
        var query = driver.Receive(Frame("MSH|^~\\&|Analyzer|Mindray|||20081120174836||ORM^O01|9|P|2.3.1\rORC|RF||2611030002||IP\r"), Now)
            .Queries.ShouldHaveSingleItem();

        Unframe(driver.AnswerQuery(query, ["789-8"], Now).Send[0]).ShouldContain("||CBC||||||F");

        var none = Unframe(driver.AnswerQuery(query, [], Now).Send.ShouldHaveSingleItem()).Split('\r', StringSplitOptions.RemoveEmptyEntries);
        none.Length.ShouldBe(2);
        none[1].ShouldBe("MSA|AR|9");
    }

    [Fact]
    public void Query_InvalidBarcode_RepliesARWithoutQuery()
    {
        var output = new Hl7MllpDriver().Receive(Frame("MSH|^~\\&|Analyzer|Mindray|||20081120174836||ORM^O01|5|P|2.3.1\rORC|RF||Invalid||IP\r"), Now);

        output.Queries.ShouldBeEmpty();
        Unframe(output.Send.ShouldHaveSingleItem()).ShouldContain("MSA|AR|5");
    }

    [Fact]
    public void Malformed_NoMsh_RepliesARAndNotes()
    {
        var output = new Hl7MllpDriver().Receive(Frame("PID|1||X\r"), Now);

        output.Readings.ShouldBeEmpty();
        Unframe(output.Send.ShouldHaveSingleItem()).ShouldContain("MSA|AR|");
        output.Notes.ShouldNotBeEmpty();
    }

    [Fact]
    public void MissingControlId_RepliesAE101()
    {
        var output = new Hl7MllpDriver().Receive(Frame("MSH|^~\\&|Analyzer|Mindray|||20081120174836||ORU^R01||P|2.3.1\rOBR|1||1\r"), Now);

        Unframe(output.Send.ShouldHaveSingleItem()).ShouldContain("MSA|AE|||||101");
    }

    [Fact]
    public void UnsupportedMessageType_RepliesAR200()
    {
        var output = new Hl7MllpDriver().Receive(Frame("MSH|^~\\&|Analyzer|Mindray|||20081120174836||ADT^A01|3|P|2.3.1\r"), Now);

        Unframe(output.Send.ShouldHaveSingleItem()).ShouldContain("MSA|AR|3||||200");
    }

    [Fact]
    public void Latin1Bytes_DoNotCrash()
    {
        byte[] body = [.. Encoding.ASCII.GetBytes("MSH|^~\\&|A|M|||1||ORU^R01|1|P|2.3.1\rOBR|1||B1\rOBX|1|ST|6742-1^M^LN||"), 0xE9, 0xFF, .. "\r"u8];

        var output = new Hl7MllpDriver().Receive([0x0B, .. body, 0x1C, 0x0D], Now);

        output.Readings.ShouldHaveSingleItem().Items.ShouldHaveSingleItem().RawValue.ShouldBe("éÿ");
    }

    [Fact]
    public void Tick_DropsStaleHalfFrame()
    {
        var driver = new Hl7MllpDriver();
        driver.Receive([0x0B, .. Encoding.ASCII.GetBytes("MSH|^~\\&|A")], Now);

        driver.Tick(Now.AddSeconds(10)).IsEmpty.ShouldBeTrue();
        driver.Tick(Now.AddSeconds(31)).Notes.ShouldHaveSingleItem();

        // Phần đuôi còn lại của bản tin cũ không được ghép thành bản tin mới.
        driver.Receive(Encoding.ASCII.GetBytes("|M\r\u001c\r"), Now.AddSeconds(32)).Readings.ShouldBeEmpty();
    }
}
