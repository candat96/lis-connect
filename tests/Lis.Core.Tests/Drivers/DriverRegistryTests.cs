using Lis.Core.Drivers;
using Lis.Core.Drivers.ListenOnly;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Drivers;

public class DriverRegistryTests
{
    [Theory]
    [InlineData("hl7-mllp")]
    [InlineData("astm-e1394")]
    [InlineData("beckman-au")]
    [InlineData("chi-nghe")]
    public void Create_SupportedFamily_ReturnsDriverOfThatFamily(string family)
    {
        DriverRegistry.SupportedFamilies.ShouldContain(family);
        DriverRegistry.Create(family)!.Family.ShouldBe(family);
    }

    [Fact]
    public void Create_UnknownFamily_ReturnsNull() => DriverRegistry.Create("sysmex-ca").ShouldBeNull();

    [Fact]
    public void ListenOnly_NeverAnswersOrDecodes()
    {
        var driver = new ListenOnlyDriver();
        var now = DateTimeOffset.UnixEpoch;

        // ENQ ASTM và một khung HL7 — chế độ chỉ nghe không ACK, không tách kết quả.
        driver.Receive([0x05], now).IsEmpty.ShouldBeTrue();
        driver.Receive("\vMSH|^~\\&|\r\u001c\r"u8, now).IsEmpty.ShouldBeTrue();
        driver.Tick(now).IsEmpty.ShouldBeTrue();
        driver.AnswerQuery(new DriverQuery("9261110001", new object()), ["WBC"], now).IsEmpty.ShouldBeTrue();
    }
}
