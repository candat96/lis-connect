using Lis.Simulator.Messages;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests.EndToEnd;

/// <summary>
/// Đối chiếu trọn vòng (an toàn người bệnh): cùng một dòng bản tin máy gửi, đường cũ (runner + <c>IngestReadingSink</c> ghi thẳng CSDL,
/// phòng khám A) và đường mới (runner chế độ bản tin thô + hàng chờ đĩa + uploader + server giải mã, phòng khám B) phải để lại
/// cùng dữ liệu. So theo mã vạch + mã chỉ số, bỏ qua id / thời điểm / raw_ref.
/// </summary>
public sealed class RawPipelineEquivalenceTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string B1 = "9261120001";
    private const string B2 = "9261120002";
    private const string B3 = "9261120003";
    private const string BQuery = "9261120004";
    private const string BApproved = "9261120005";
    private const string BUnmapped = "9261120006";
    private const string BBad = "9261120008";
    private const string BUnknown = "9261120099";

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ------------------------------------------------------------------ đối chiếu trọn bộ kịch bản

    [Theory]
    [MemberData(nameof(Family.Names), MemberType = typeof(Family))]
    public async Task Hai_duong_cho_cung_du_lieu_trong_bo_kich_ban_thuong(string name)
    {
        var family = Family.Named(name);
        await using var rig = await PipelineRig.CreateAsync(fixture, family, Tag());

        // 3 mẫu thường (mẫu 1 được gửi lại đúng giá trị: mẫu chưa duyệt vẫn thêm một dòng lịch sử "nhan", như đường cũ).
        await rig.BothAsync(m => m.SendAsync(B1));
        await rig.BothAsync(m => m.SendAsync(B2, m.Cbc("5.2", "120", "310")));
        await rig.BothAsync(m => m.SendAsync(B3, m.Cbc("11.0", "98", "150")));
        await rig.BothAsync(m => m.SendAsync(B1));

        // 1 QC (hai lần, hai mã vạch QC)
        await rig.BothAsync(m => m.SendAsync("QC0001", m.Cbc("7.1", "130", "240"), qc: true));
        await rig.BothAsync(m => m.SendAsync("QC0002", m.Cbc("7.3", "131", "238"), qc: true));

        // 1 câu hỏi chỉ định rồi kết quả
        await rig.QueryBothAsync(BQuery);
        await rig.BothAsync(m => m.SendAsync(BQuery, m.Cbc("6.4", "140", "200")));

        // 1 mẫu đã duyệt nhận kết quả mới: WBC và HGB đổi, PLT giữ nguyên.
        await rig.BothAsync(m => m.SendAsync(BApproved, m.Cbc("8.0", "125", "260")));
        await PipelineRig.WaitAsync(() => rig.A.HasValueAsync(BApproved), "mẫu đã duyệt ở đường cũ");
        await rig.DrainAsync(BApproved);
        await rig.A.Tenant.Results.ApproveAsync(rig.A.Samples[BApproved], rig.A.ActorId);
        await rig.B.Tenant.Results.ApproveAsync(rig.B.Samples[BApproved], rig.B.ActorId);
        await rig.BothAsync(m => m.SendAsync(BApproved, m.Cbc("9.9", "150", "260")));

        // 1 kênh chưa map (mẫu có chỉ định) và một mã vạch không có chỉ định
        await rig.BothAsync(m => m.SendAsync(BUnmapped, m.Cbc(extra: new SimResult(family.Unmapped, "5.5"))));
        await rig.BothAsync(m => m.SendAsync(BUnknown, m.Cbc("4.4", "100", "100")));

        await rig.FinishAsync();

        await rig.AssertEquivalentAsync();

        // Chống đối chiếu rỗng: dữ liệu thật có mặt và đúng chỗ.
        var results = await rig.B.ResultsAsync();
        results.ShouldContain(r => r.StartsWith(B1 + "|WBC|7.5") && r.EndsWith("|2"), "mẫu 1: giá trị + 2 dòng lịch sử (gửi hai lần)");
        results.ShouldContain(r => r.StartsWith(B2 + "|PLT|310"));
        results.ShouldContain(r => r.StartsWith(BQuery + "|HGB|140"));
        var approvedWbc = results.Single(r => r.StartsWith(BApproved + "|WBC|"));
        approvedWbc.ShouldContain("|da-duyet|");
        approvedWbc.ShouldNotContain("|da-duyet|~|", Case.Sensitive, "WBC đã duyệt, máy gửi giá trị mới: phải có giá trị chờ thay");
        results.Single(r => r.StartsWith(BApproved + "|PLT|")).ShouldContain("|da-duyet|~|", Case.Sensitive, "PLT cùng giá trị: không chờ thay");
        (await rig.B.QcAsync()).Count.ShouldBeGreaterThanOrEqualTo(6);
        var unmatched = await rig.B.UnmatchedAsync();
        unmatched.ShouldContain(u => u.StartsWith(BUnmapped + "|" + family.Unmapped) && u.EndsWith("|kenh-chua-map"));
        unmatched.ShouldContain(u => u.StartsWith(BUnknown + "|") && u.EndsWith("|khong-co-barcode"), "mã vạch không có mẫu nào");

        // Không mất, không trùng: hàng chờ agent rỗng, mỗi bản tin ghi đĩa có đúng một dòng ở server.
        rig.Queue.Count().ShouldBe(0);
        (await rig.ServerMessagesAsync()).ShouldBe(rig.Saved);
    }

    // ------------------------------------------------------------------ kịch bản lỗi

    [Theory]
    [MemberData(nameof(Family.Names), MemberType = typeof(Family))]
    public async Task Loi_a_server_dung_giua_luc_agent_gui_roi_bat_lai_khong_mat_khong_trung(string name)
    {
        await using var rig = await PipelineRig.CreateAsync(fixture, Family.Named(name), Tag());

        await rig.BothAsync(m => m.SendAsync(B1));
        await rig.BothAsync(m => m.SendAsync(B2, m.Cbc("5.2", "120", "310")));
        await PipelineRig.WaitAsync(() => rig.A.HasValueAsync(B2), "mẫu 2 ở đường cũ");
        await rig.DrainAsync(B2); // hai mẫu đầu đã tới server

        await rig.StopServerAsync();
        await rig.BothAsync(m => m.SendAsync(B3, m.Cbc("11.0", "98", "150")));
        await rig.BothAsync(m => m.SendAsync("QC0001", m.Cbc("7.1", "130", "240"), qc: true));
        await PipelineRig.WaitAsync(() => rig.A.HasValueAsync(B3), "mẫu 3 ở đường cũ");
        await rig.SettleAsync();

        var flush = await rig.Uploader.FlushNowAsync(TestContext.Current.CancellationToken);
        flush.Remaining.ShouldBeGreaterThanOrEqualTo(2, "server tắt: bản tin ở lại trên đĩa agent");
        rig.Switch.Failures.ShouldBeGreaterThan(0);
        (await rig.B.HasValueAsync(B3)).ShouldBeFalse();

        await rig.StartServerAsync();
        await rig.FinishAsync();

        await rig.AssertEquivalentAsync();
        rig.Queue.Count().ShouldBe(0);
        (await rig.ServerMessagesAsync()).ShouldBe(rig.Saved);
        (await rig.B.QcAsync()).ShouldNotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Family.Names), MemberType = typeof(Family))]
    public async Task Loi_b_agent_gui_lai_ca_lo_da_gui_cung_id_server_tra_trung(string name)
    {
        await using var rig = await PipelineRig.CreateAsync(fixture, Family.Named(name), Tag());

        await rig.BothAsync(m => m.SendAsync(B1));
        await rig.BothAsync(m => m.SendAsync(B2, m.Cbc("5.2", "120", "310")));
        await rig.BothAsync(m => m.SendAsync("QC0001", m.Cbc("7.1", "130", "240"), qc: true));
        await rig.BothAsync(m => m.SendAsync(B3, m.Cbc("11.0", "98", "150")));
        await PipelineRig.WaitAsync(() => rig.A.HasValueAsync(B3), "mẫu 3 ở đường cũ");
        await rig.SettleAsync();
        var batch = rig.Saved;
        batch.ShouldBeGreaterThanOrEqualTo(4);

        // Server nhận và giải mã cả lô, nhưng phản hồi không về tới agent: agent giữ nguyên tệp.
        rig.Switch.LoseNextResponses(1);
        var first = await rig.Uploader.FlushNowAsync(TestContext.Current.CancellationToken);
        first.Remaining.ShouldBe(batch);
        (await rig.B.HasValueAsync(B3)).ShouldBeTrue("server đã lưu và giải mã dù agent không nhận được xác nhận");
        (await rig.ServerMessagesAsync()).ShouldBe(batch);
        var afterFirst = await rig.B.ResultsAsync();

        // Gửi lại cả lô cùng Id: server trả "trung", agent xoá tệp, dữ liệu không đổi.
        var second = await rig.Uploader.FlushNowAsync(TestContext.Current.CancellationToken);
        second.Remaining.ShouldBe(0);
        (await rig.B.ResultsAsync()).ShouldBe(afterFirst, "gửi lại cùng Id không được ingest lần hai");
        (await rig.ServerMessagesAsync()).ShouldBe(batch);

        await rig.FinishAsync();
        await rig.AssertEquivalentAsync();
        rig.Queue.Count().ShouldBe(0);
        (await rig.ServerMessagesAsync()).ShouldBe(rig.Saved);
    }

    [Theory]
    [MemberData(nameof(Family.NamesWithChecksum), MemberType = typeof(Family))]
    public async Task Loi_c_ban_tin_sai_mot_byte_checksum_khong_sinh_ket_qua_sai(string name)
    {
        var family = Family.Named(name);
        await using var rig = await PipelineRig.CreateAsync(fixture, family, Tag());

        await rig.BothAsync(m => m.SendAsync(B1));
        // Sai một byte checksum / BCC ở khung có chứa mã vạch (khung O của ASTM, khối kết quả đầu của AU Class B): LIS NAK, máy bỏ phiên.
        await rig.BothAsync(async m =>
        {
            m.TamperNextFrame(BadChecksum(family));
            var result = await m.TrySendAsync(BBad, m.Cbc("6.6", "111", "222"));
            result.Ok.ShouldBeFalse("LIS phải từ chối khung sai checksum");
        });
        await rig.BothAsync(m => m.SendAsync(B2, m.Cbc("5.2", "120", "310")));

        await rig.FinishAsync();

        await rig.AssertEquivalentAsync();
        foreach (var side in new[] { rig.A, rig.B })
        {
            (await side.ResultsAsync()).Where(r => r.StartsWith(BBad)).ShouldAllBe(r => r.Contains("|~|~|"), "mẫu bản tin hỏng: không có giá trị");
            (await side.QcAsync()).ShouldNotContain(r => r.StartsWith(BBad));
            (await side.UnmatchedAsync()).ShouldNotContain(r => r.StartsWith(BBad));
        }

        (await rig.B.ResultsAsync()).ShouldContain(r => r.StartsWith(B2 + "|WBC|5.2"), "bản tin tốt sau bản tin hỏng vẫn vào");
        var statuses = await rig.ServerStatusesAsync(BBad, Side.Sentinel);
        statuses.ShouldNotBeEmpty("bản tin hỏng vẫn được lưu để xem tay");
        statuses.ShouldAllBe(s => s == "khong-co-ket-qua" || s == "loi", $"trạng thái: {string.Join(",", statuses)}");
        rig.Queue.Count().ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(Family.Names), MemberType = typeof(Family))]
    public async Task Loi_d_mat_mang_5_phut_trong_luc_may_van_gui_khong_mat_khong_trung(string name)
    {
        await using var rig = await PipelineRig.CreateAsync(fixture, Family.Named(name), Tag());

        rig.Switch.Down = true;
        for (var step = 0; step <= 60; step++) // 60 bước × 5 giây = 5 phút theo đồng hồ giả của agent
        {
            switch (step)
            {
                case 0:
                    await rig.BothAsync(m => m.SendAsync(B1));
                    break;
                case 10:
                    await rig.BothAsync(m => m.SendAsync(B2, m.Cbc("5.2", "120", "310")));
                    break;
                case 25:
                    await rig.BothAsync(m => m.SendAsync("QC0001", m.Cbc("7.1", "130", "240"), qc: true));
                    break;
                case 40:
                    await rig.BothAsync(m => m.SendAsync(B3, m.Cbc("11.0", "98", "150")));
                    break;
            }

            rig.UploaderClock.Advance(TimeSpan.FromSeconds(5));
            await rig.Uploader.TickAsync(TestContext.Current.CancellationToken);
        }

        await rig.SettleAsync();
        rig.Queue.Count().ShouldBe(rig.Saved, "mất mạng: không bản tin nào bị xoá khỏi đĩa");
        rig.Saved.ShouldBeGreaterThanOrEqualTo(4);
        rig.Switch.Failures.ShouldBeInRange(1, 15, "lùi nhịp 1, 2, 4 … 60 giây: không gửi dồn dập trong 5 phút");
        (await rig.ServerMessagesAsync()).ShouldBe(0);

        // Có mạng lại: gửi theo nhịp lùi, đúng thứ tự, đủ.
        rig.Switch.Down = false;
        for (var step = 0; step < 40 && rig.Queue.Count() > 0; step++)
        {
            rig.UploaderClock.Advance(TimeSpan.FromSeconds(5));
            await rig.Uploader.TickAsync(TestContext.Current.CancellationToken);
        }

        rig.Queue.Count().ShouldBe(0, "hết lùi nhịp thì hàng chờ phải cạn");
        await rig.FinishAsync();

        await rig.AssertEquivalentAsync();
        (await rig.ServerMessagesAsync()).ShouldBe(rig.Saved);
        (await rig.B.QcAsync()).ShouldNotBeEmpty();
    }

    /// <summary>Sửa một byte checksum của khung dữ liệu chứa mã vạch (khung thứ 3 của ASTM; khối kết quả đầu "D " của AU).</summary>
    private static Func<byte[], byte[]> BadChecksum(Family family)
    {
        var frames = 0;
        return data =>
        {
            if (family.IsAstm)
            {
                if (data.Length >= 6 && data[0] == 0x02 && ++frames == 3)
                {
                    var changed = (byte[])data.Clone();
                    changed[^4] = changed[^4] == (byte)'0' ? (byte)'1' : (byte)'0'; // C1 của <CS1><CS2><CR><LF>
                    return changed;
                }
            }
            else if (data.Length > 4 && data[0] == 0x02 && data[1] == (byte)'D' && data[2] == (byte)' ')
            {
                var changed = (byte[])data.Clone();
                changed[^1] ^= 0x01; // BCC
                return changed;
            }

            return data;
        };
    }
}
