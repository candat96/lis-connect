using Xunit;
using Lis.Core.Drivers;
using Lis.Core.Drivers.BeckmanAu;
using Lis.Core.Instruments;
using Lis.Simulator.Messages;
using Lis.Simulator.Sessions;
using Shouldly;

namespace Lis.Simulator.Tests;

/// <summary>
/// Vòng tròn quan trọng nhất: bản tin do simulator dựng, đưa qua driver THẬT của Lis.Core bằng
/// hội thoại byte trong bộ nhớ, phải ra đúng barcode + kênh/giá trị.
/// </summary>
public class LoopbackTests
{
    private static readonly SessionTimings Fast = new() { ClassAGap = TimeSpan.Zero, Reply = TimeSpan.FromMilliseconds(50), QueryAnswer = TimeSpan.FromMilliseconds(50) };

    private static readonly SimResult[] Cbc = [new("WBC", "7.5"), new("HGB", "135"), new("PLT", "250")];

    private static MachineSession NewSession(DriverLoopbackLink link, List<string>? log = null) =>
        new(link, line => log?.Add(line), Fast);

    private static void ShouldHave(DriverReading reading, string barcode, bool isQc, params (string Code, string Value)[] expected)
    {
        reading.Barcode.ShouldBe(barcode);
        reading.IsQc.ShouldBe(isQc);
        reading.Items.Select(i => (i.ChannelCode, i.RawValue)).ToArray().ShouldBe(expected);
    }

    // ---------------- HL7 / MLLP ----------------

    [Fact]
    public async Task Hl7_ket_qua_ra_dung_barcode_va_kenh()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("hl7-mllp")!);
        var result = await NewSession(link).SendHl7ResultAsync("2611100001", Cbc, false, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        link.Readings.Count.ShouldBe(1);
        ShouldHave(link.Readings[0], "2611100001", false, ("WBC", "7.5"), ("HGB", "135"), ("PLT", "250"));
    }

    [Fact]
    public async Task Hl7_qc_va_gia_tri_chu()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("hl7-mllp")!);
        var result = await NewSession(link).SendHl7ResultAsync("QC0001", [new("BLD", "A+")], true, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        ShouldHave(link.Readings[0], "QC0001", true, ("BLD", "A+"));
    }

    // ---------------- ASTM ----------------

    [Fact]
    public async Task Astm_ket_qua_ra_dung_barcode_va_kenh()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("astm-e1394")!);
        var result = await NewSession(link).SendAstmResultAsync("2611100002", Cbc, false, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        link.Readings.Count.ShouldBe(1);
        ShouldHave(link.Readings[0], "2611100002", false, ("WBC", "7.5"), ("HGB", "135"), ("PLT", "250"));
    }

    [Fact]
    public async Task Astm_nhieu_khung_so_khung_quay_vong_mod_8()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("astm-e1394")!);
        var many = Enumerable.Range(1, 20).Select(i => new SimResult($"T{i}", (i * 1.5m).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        var result = await NewSession(link).SendAstmResultAsync("2611100003", many, false, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        link.Readings[0].Items.Count.ShouldBe(20);
        link.Readings[0].Items[19].ChannelCode.ShouldBe("T20");
        link.Readings[0].Items[19].RawValue.ShouldBe("30.0");
    }

    [Fact]
    public async Task Astm_qc_nhan_ra_qc()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("astm-e1394")!);
        var result = await NewSession(link).SendAstmResultAsync("QC9", [new("GLU", "5.6")], true, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        ShouldHave(link.Readings[0], "QC9", true, ("GLU", "5.6"));
    }

    [Fact]
    public async Task Astm_hoi_chi_dinh_va_nhan_cau_tra_loi()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("astm-e1394")!, _ => ["WBC", "HGB"]);
        var result = await NewSession(link).QueryAstmAsync("2611100004", TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        link.Queries.Select(q => q.Barcode).ShouldBe(["2611100004"]);
        var order = result.Lines.Single(l => l.StartsWith("O|", StringComparison.Ordinal));
        order.ShouldContain("2611100004");
        order.ShouldContain("^^^WBC");
        order.ShouldContain("^^^HGB");
    }

    [Fact]
    public async Task Astm_hoi_chi_dinh_khong_co_chi_dinh()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("astm-e1394")!, _ => []);
        var result = await NewSession(link).QueryAstmAsync("2611100005", TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        result.Lines.ShouldNotContain(l => l.StartsWith("O|", StringComparison.Ordinal));
        result.Lines.ShouldContain(l => l.StartsWith("L|", StringComparison.Ordinal));
    }

    // ---------------- Beckman AU ----------------

    [Fact]
    public async Task Au_mac_dinh_class_a_ket_qua_ra_dung_so_test()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("beckman-au")!);
        var result = await NewSession(link).SendAuResultAsync(
            BeckmanAuOptions.Au480, "2611100", [new("01", "5.42"), new("12", "135"), new("7", "0.35")], false, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        link.Readings.Count.ShouldBe(1);
        ShouldHave(link.Readings[0], "2611100", false, ("01", "5.42"), ("12", "135"), ("07", "0.35"));
    }

    [Fact]
    public async Task Au_class_b_bcc_3_chu_so_sample_id_13_device_no()
    {
        var options = new BeckmanAuOptions { ClassB = true, Bcc = true, TestNoDigits = 3, SampleIdLength = 13, SystemNo = true, ReagentInfoLength = 0 };
        var link = new DriverLoopbackLink(DriverRegistry.Create("beckman-au", driverConfigJson: """{"classB":true,"bcc":true,"testNoDigits":3,"sampleIdLength":13,"systemNo":true}""")!);
        var result = await NewSession(link).SendAuResultAsync(options, "2611100001234", [new("001", "5.42"), new("120", "98")], false, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        ShouldHave(link.Readings[0], "2611100001234", false, ("001", "5.42"), ("120", "98"));
    }

    [Fact]
    public async Task Au_nhieu_khoi_gom_thanh_mot_ket_qua()
    {
        var options = new BeckmanAuOptions { ClassB = true, TextLength = 256 };
        var link = new DriverLoopbackLink(DriverRegistry.Create("beckman-au", driverConfigJson: """{"classB":true,"textLength":256}""")!);
        var many = Enumerable.Range(1, 60).Select(i => new SimResult(i.ToString("00"), i.ToString())).ToList();
        var result = await NewSession(link).SendAuResultAsync(options, "2611100", many, false, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        link.Readings.Count.ShouldBe(1);
        link.Readings[0].Items.Count.ShouldBe(60);
        link.Readings[0].Items[59].ChannelCode.ShouldBe("60");
        link.Readings[0].Items[59].RawValue.ShouldBe("60");
    }

    [Fact]
    public async Task Au_qc_ra_dq()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("beckman-au")!);
        var result = await NewSession(link).SendAuResultAsync(BeckmanAuOptions.Au480, "QC0001", [new("01", "5.1")], true, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        ShouldHave(link.Readings[0], "QC0001", true, ("01", "5.1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Au_hoi_chi_dinh_nhan_s_voi_dung_test(bool classB)
    {
        var options = new BeckmanAuOptions { ClassB = classB };
        var link = new DriverLoopbackLink(
            DriverRegistry.Create("beckman-au", driverConfigJson: classB ? """{"classB":true}""" : null)!, _ => ["01", "12"]);
        var result = await NewSession(link).QueryAuAsync(options, "2611100", TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        link.Queries.Select(q => q.Barcode).ShouldBe(["2611100"]);
        var frame = result.Lines.Single();
        frame.ShouldStartWith("S ");
        frame.ShouldContain("2611100");
        frame.ShouldEndWith("E" + "01" + "12");
    }

    [Fact]
    public async Task Au_hoi_chi_dinh_khong_co_chi_dinh_tra_se()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("beckman-au")!, _ => []);
        var result = await NewSession(link).QueryAuAsync(BeckmanAuOptions.Au480, "2611100", TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        result.Lines.ShouldBe(["SE"]);
    }

    // ---------------- Hết thời hạn nhưng LIS đã trả lời ----------------
    // Máy tải nặng: luồng bị treo quá thời hạn trước khi kịp đọc. Trả lời đã nằm sẵn trong đường
    // nối thì phải đọc được — chỉ báo "không trả lời" khi đọc thật mà không có byte nào.

    private static readonly SessionTimings Expired = new() { ClassAGap = TimeSpan.Zero, Reply = TimeSpan.Zero, QueryAnswer = TimeSpan.Zero };

    [Fact]
    public async Task Hl7_het_thoi_han_van_doc_ack_da_co()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("hl7-mllp")!);
        var result = await new MachineSession(link, _ => { }, Expired).SendHl7ResultAsync("2611100011", Cbc, false, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
    }

    [Fact]
    public async Task Astm_hoi_chi_dinh_het_thoi_han_van_doc_tra_loi_da_co()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("astm-e1394")!, _ => ["WBC"]);
        var result = await new MachineSession(link, _ => { }, Expired).QueryAstmAsync("2611100012", TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
        result.Lines.ShouldContain(l => l.StartsWith("O|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Au_hoi_chi_dinh_het_thoi_han_van_doc_tra_loi_da_co()
    {
        var link = new DriverLoopbackLink(DriverRegistry.Create("beckman-au")!, _ => ["01"]);
        var result = await new MachineSession(link, _ => { }, Expired).QueryAuAsync(BeckmanAuOptions.Au480, "2611100", TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Message);
    }

    // ---------------- Phát lại .listrace ----------------

    [Fact]
    public async Task Replay_listrace_astm_qua_driver_that()
    {
        var driverProbe = new DriverLoopbackLink(DriverRegistry.Create("astm-e1394")!);
        List<byte[]> frames = [[0x05]];
        var fn = 1;
        foreach (var record in AstmMessageBuilder.BuildResultRecords("2611100009", [new("GLU", "5.6")], DateTimeOffset.Now, false))
        {
            frames.AddRange(Lis.Core.Drivers.Astm.AstmFraming.BuildFrames(record, fn, out fn));
        }

        frames.Add([0x04]);
        var t0 = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.FromHours(7));
        var path = Path.Combine(Path.GetTempPath(), $"sim-{Guid.NewGuid():N}.listrace");
        try
        {
            File.WriteAllLines(path, frames.Select((f, i) =>
                RawLogFormat.Serialize(new RawLogEntry(t0.AddMilliseconds(i * 50), RawLogDirection.In, "phien-1", f))));

            var entries = TraceReplayer.Load(path);
            entries.Count.ShouldBe(frames.Count);

            var replayer = new TraceReplayer(driverProbe, _ => { }) { QuietWait = TimeSpan.Zero };
            var stats = await replayer.RunAsync(entries, null, speed: 0, TestContext.Current.CancellationToken);

            stats.Session.ShouldBe("phien-1");
            stats.BlocksSent.ShouldBe(frames.Count);
            ShouldHave(driverProbe.Readings.Single(), "2611100009", false, ("GLU", "5.6"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------- Lớp dựng bản tin thuần ----------------

    [Fact]
    public void SimResult_doc_dang_ma_bang_gia_tri()
    {
        SimResult.Parse("WBC=7.5").ShouldBe(new SimResult("WBC", "7.5"));
        Should.Throw<ArgumentException>(() => SimResult.Parse("WBC"));
    }

    [Fact]
    public void Au_barcode_dai_hon_sample_id_bi_tu_choi()
    {
        Should.Throw<ArgumentException>(() =>
            AuMessageBuilder.BuildResultFrames(BeckmanAuOptions.Au480, "12345678901", [new("01", "1")], false));
    }
}
