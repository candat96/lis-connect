using System.Text;
using Lis.Core.Drivers;
using Lis.Core.Drivers.Hl7;
using Lis.Core.Instruments;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Drivers;

public class RawLogDecoderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.FromHours(7));

    private static string Oru(string barcode, string wbc) =>
        "MSH|^~\\&|BC-5380|Mindray|||20261110090000||ORU^R01|1|P|2.3.1||||||UNICODE\r" +
        "PID|1||BN0001^^^^MR||Thử^Nguyễn Văn Mẫu|||Nam\r" +
        $"OBR|1||{barcode}|00001^Automated Count^99MRC\r" +
        $"OBX|6|NM|6690-2^WBC^LN||{wbc}|10*9/L|4.00-10.00|N|||F\r";

    private static byte[] Frame(string text) => [0x0B, .. Encoding.UTF8.GetBytes(text), 0x1C, 0x0D];

    [Fact]
    public void Decode_SplitAcrossEntries_IgnoresOutAndEvents()
    {
        var frame = Frame(Oru("2611100001", "7.5"));
        var entries = new List<RawLogEntry>
        {
            new(T0, RawLogDirection.Event, "p1", [], "Máy nối vào"),
            new(T0, RawLogDirection.In, "p1", frame[..20]),
            new(T0.AddMilliseconds(5), RawLogDirection.In, "p1", frame[20..]),
            new(T0.AddMilliseconds(9), RawLogDirection.Out, "p1", Frame("MSH|^~\\&|LIS\rMSA|AA|1\r")),
        };

        var result = RawLogDecoder.Decode(new Hl7MllpDriver(), entries);

        var decoded = result.Readings.ShouldHaveSingleItem();
        decoded.Session.ShouldBe("p1");
        decoded.Reading.Barcode.ShouldBe("2611100001");
        decoded.Reading.Items.ShouldHaveSingleItem().RawValue.ShouldBe("7.5");
    }

    [Fact]
    public void Decode_SessionChange_ResetsHalfFrame()
    {
        var first = Frame(Oru("2611100001", "7.5"));
        var second = Frame(Oru("2611100002", "8.1"));
        var entries = new List<RawLogEntry>
        {
            // Phiên 1 đứt giữa khung — nửa khung này không được dính vào phiên 2.
            new(T0, RawLogDirection.In, "p1", first[..30]),
            new(T0.AddSeconds(1), RawLogDirection.In, "p2", second),
        };

        var result = RawLogDecoder.Decode(new Hl7MllpDriver(), entries);

        var decoded = result.Readings.ShouldHaveSingleItem();
        decoded.Session.ShouldBe("p2");
        decoded.Reading.Barcode.ShouldBe("2611100002");
    }
}
