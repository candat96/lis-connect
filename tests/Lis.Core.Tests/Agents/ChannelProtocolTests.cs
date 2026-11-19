using System.Text;
using System.Text.Json;
using Lis.Core.Agents;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Agents;

public sealed class ChannelProtocolTests
{
    [Fact]
    public void Lenh_co_tham_so_di_ve_nguyen_ven()
    {
        var id = Guid.NewGuid();
        var args = JsonDocument.Parse("""{"port":"COM3","seconds":5}""").RootElement;
        var json = ChannelProtocol.Serialize(new ChannelEnvelope(ChannelProtocol.Command, id, "test-port", args));

        var back = ChannelProtocol.Parse(Encoding.UTF8.GetBytes(json));

        back.ShouldNotBeNull();
        back.Type.ShouldBe("command");
        back.Id.ShouldBe(id);
        back.Name.ShouldBe("test-port");
        back.Args!.Value.GetProperty("port").GetString().ShouldBe("COM3");
        back.Args!.Value.GetProperty("seconds").GetInt32().ShouldBe(5);
        json.ShouldContain("\"type\":\"command\"");
        json.ShouldNotContain("error");
    }

    [Fact]
    public void Ket_qua_va_bao_cao_cap_nhat_di_ve_nguyen_ven()
    {
        var data = JsonDocument.Parse("""{"ports":[]}""").RootElement;
        var commandId = Guid.NewGuid();
        var json = ChannelProtocol.Serialize(new ChannelEnvelope(
            ChannelProtocol.Hello, Version: "1.2.3", MachineName: "MAY-THU", Update: new UpdateReport(commandId, false, "1.2.3", "Lỗi thử")));
        var hello = ChannelProtocol.Parse(Encoding.UTF8.GetBytes(json))!;
        hello.Update.ShouldBe(new UpdateReport(commandId, false, "1.2.3", "Lỗi thử"));
        hello.MachineName.ShouldBe("MAY-THU");

        var result = ChannelProtocol.Parse(Encoding.UTF8.GetBytes(
            ChannelProtocol.Serialize(new ChannelEnvelope(ChannelProtocol.Result, commandId, Ok: true, Data: data))))!;
        result.Ok.ShouldBe(true);
        result.Data!.Value.GetProperty("ports").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"type":""}""")]
    [InlineData("không phải json")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("")]
    public void Parse_tra_null_khi_hong_hoac_thieu_type(string text) =>
        ChannelProtocol.Parse(Encoding.UTF8.GetBytes(text)).ShouldBeNull();

    [Fact]
    public void Parse_tra_null_khi_qua_mot_megabyte()
    {
        var padding = new string('a', ChannelProtocol.MaxMessageBytes);
        var bytes = Encoding.UTF8.GetBytes($$"""{"type":"hello","error":"{{padding}}"}""");
        bytes.Length.ShouldBeGreaterThan(ChannelProtocol.MaxMessageBytes);
        ChannelProtocol.Parse(bytes).ShouldBeNull();

        var exact = new byte[ChannelProtocol.MaxMessageBytes + 1];
        ChannelProtocol.Parse(exact).ShouldBeNull();
    }
}
