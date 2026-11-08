using Lis.Core.Instruments;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Instruments;

/// <summary>Định dạng tệp log thô <c>.listrace</c> (JSON lines, byte dạng hex).</summary>
public class RawLogFormatTests
{
    [Fact]
    public void RoundTrip_KeepsBytesDirectionSessionAndMilliseconds()
    {
        var entry = new RawLogEntry(new DateTimeOffset(2026, 11, 8, 9, 15, 2, 123, TimeSpan.FromHours(7)), RawLogDirection.In, "phien-1", [0x02, 0x31, 0x48, 0x7C, 0xFF]);

        var line = RawLogFormat.Serialize(entry);
        var parsed = RawLogFormat.TryParse(line)!;

        line.ShouldBe("""{"t":"2026-11-08T09:15:02.123+07:00","d":"in","s":"phien-1","hex":"0231487CFF"}""");
        parsed.Timestamp.ShouldBe(entry.Timestamp);
        parsed.Direction.ShouldBe(RawLogDirection.In);
        parsed.Session.ShouldBe("phien-1");
        parsed.Bytes.ShouldBe(entry.Bytes);
    }

    [Fact]
    public void Event_HasNoteAndNoBytes()
    {
        var entry = new RawLogEntry(DateTimeOffset.Now, RawLogDirection.Event, "p", [], "Mất kết nối");

        var parsed = RawLogFormat.TryParse(RawLogFormat.Serialize(entry))!;

        parsed.Direction.ShouldBe(RawLogDirection.Event);
        parsed.Bytes.ShouldBeEmpty();
        parsed.Note.ShouldBe("Mất kết nối");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"t\":\"2026-11-08T09:15")]
    [InlineData("{\"t\":\"khong-phai-ngay\",\"d\":\"in\"}")]
    [InlineData("{\"t\":\"2026-11-08T09:15:02.123+07:00\",\"d\":\"in\",\"hex\":\"ZZ\"}")]
    public void TryParse_BrokenLine_ReturnsNull(string line)
    {
        RawLogFormat.TryParse(line).ShouldBeNull();
    }

    [Fact]
    public void IsLoneAck_OnlyForSingleAckByte()
    {
        new RawLogEntry(DateTimeOffset.Now, RawLogDirection.Out, "p", [0x06]).IsLoneAck.ShouldBeTrue();
        new RawLogEntry(DateTimeOffset.Now, RawLogDirection.Out, "p", [0x06, 0x06]).IsLoneAck.ShouldBeFalse();
    }
}
