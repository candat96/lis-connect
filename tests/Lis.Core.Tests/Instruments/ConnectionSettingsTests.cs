using System.Text;
using Lis.Core.Instruments;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Instruments;

/// <summary>Đọc/ghi cấu hình kết nối máy (cột <c>instrument.connection</c>).</summary>
public class ConnectionSettingsTests
{
    [Fact]
    public void Parse_Serial_ReadsAllFields()
    {
        var settings = ConnectionSettings.Parse("""{"type":"serial","port":"COM6","baudRate":19200,"dataBits":7,"parity":"even","stopBits":2}""");

        settings.Kind.ShouldBe(ConnectionKind.Serial);
        settings.PortName.ShouldBe("COM6");
        settings.BaudRate.ShouldBe(19200);
        settings.DataBits.ShouldBe(7);
        settings.Parity.ShouldBe("Even");
        settings.StopBits.ShouldBe(2);
        settings.Describe().ShouldBe("COM6 · 19200 7E2");
    }

    [Fact]
    public void Parse_TcpServerAndClient()
    {
        var server = ConnectionSettings.Parse("""{"type":"tcp","role":"server","port":5100}""");
        server.Kind.ShouldBe(ConnectionKind.TcpServer);
        server.TcpPort.ShouldBe(5100);

        var client = ConnectionSettings.Parse("""{"type":"tcp","role":"client","host":"10.0.0.5","port":"50001"}""");
        client.Kind.ShouldBe(ConnectionKind.TcpClient);
        client.Host.ShouldBe("10.0.0.5");
        client.TcpPort.ShouldBe(50001);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Parse_Empty_IsNone(string? json)
    {
        var settings = ConnectionSettings.Parse(json);

        settings.Kind.ShouldBe(ConnectionKind.None);
        settings.UnreadableJson.ShouldBeNull();
        settings.ToJson().ShouldBeNull();
    }

    [Theory]
    [InlineData("{khong phai json")]
    [InlineData("""{"type":"usb"}""")]
    [InlineData("[1,2]")]
    public void Parse_Unreadable_KeepsOriginalText(string json)
    {
        var settings = ConnectionSettings.Parse(json);

        settings.Kind.ShouldBe(ConnectionKind.None);
        settings.UnreadableJson.ShouldBe(json);
    }

    [Fact]
    public void RoundTrip_KeepsUnknownKeys()
    {
        var original = ConnectionSettings.Parse("""{"type":"serial","port":"COM3","baudRate":9600,"ghiChu":"cáp xanh"}""");

        var changed = original with { PortName = "COM4" };
        var reparsed = ConnectionSettings.Parse(changed.ToJson());

        reparsed.PortName.ShouldBe("COM4");
        changed.ToJson()!.ShouldContain("\"ghiChu\"");
    }

    [Fact]
    public void Validate_ReportsMissingFields()
    {
        new ConnectionSettings { Kind = ConnectionKind.Serial }.Validate().ShouldBe("Chọn cổng COM");
        new ConnectionSettings { Kind = ConnectionKind.TcpServer }.Validate().ShouldBe("Cổng TCP phải từ 1 đến 65535");
        new ConnectionSettings { Kind = ConnectionKind.TcpClient, TcpPort = 50001 }.Validate().ShouldBe("Nhập địa chỉ IP của máy");
        new ConnectionSettings { Kind = ConnectionKind.Serial, PortName = "COM1" }.Validate().ShouldBeNull();
        ConnectionSettings.None.Validate().ShouldBeNull();
    }
}

/// <summary>Hiển thị byte thô và đoán giao thức ở màn dò cổng.</summary>
public class RawBytesAndSnifferTests
{
    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    [Fact]
    public void ToReadable_NamesControlCharsAndBreaksLines()
    {
        var text = RawBytes.ToReadable(B("\u0002" + "1H|\\^&\r\u0003A7\r\n\u0006ÿ"));

        text.ShouldBe("<STX>1H|\\^&<CR>\n<ETX>A7<CR><LF>\n<ACK><xFF>");
    }

    [Fact]
    public void ToHex_ShowsOffsetBytesAndAscii()
    {
        RawBytes.ToHex(B("AB\u0002")).ShouldStartWith("000000  41 42 02 ");
        RawBytes.ToHex(B("AB\u0002")).TrimEnd().ShouldEndWith("AB.");
    }

    [Theory]
    [InlineData("\u0005", "astm-e1394")]
    [InlineData("\u0002" + "1H|\\^&|||CS-T240\r\u0003" + "4F\r\n", "astm-e1394")]
    [InlineData("\u000BMSH|^~\\&|BC-5380|Mindray\r\u001C\r", "hl7-mllp")]
    [InlineData("\u0002\r\nDate:2026-11-08\r\nNo.0012\r\n*LEU  +1\r\n\u0003", "dirui-h")]
    [InlineData("\u0002D 0101 000123   \u0003", "beckman-au")]
    public void Guess_RecognisesFamilies(string raw, string expected)
    {
        ProtocolSniffer.Guess(B(raw)).Family.ShouldBe(expected);
    }

    [Fact]
    public void Guess_GarbageBytes_FlagsWrongSettings()
    {
        var guess = ProtocolSniffer.Guess([0xF0, 0x80, 0xFE, 0x9C, 0xA1, 0x41, 0xE3, 0x00]);

        guess.Family.ShouldBeNull();
        guess.LooksLikeWrongSettings.ShouldBeTrue();
    }

    [Fact]
    public void Guess_Empty_SaysNothingReceived()
    {
        ProtocolSniffer.Guess([]).Explanation.ShouldBe("Chưa nhận được byte nào.");
    }
}
