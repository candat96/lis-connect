using Lis.Connections.RawMessages;
using Lis.Core.Drivers;
using Lis.Core.Drivers.Hl7;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>Bộ gom bản tin thô: cắt dòng byte theo ranh giới driver, xả byte rời theo thời gian.</summary>
public class RawMessageCollectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(7));
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly IReadOnlyList<DriverBoundary> None = [];

    private static RawMessageCollector Collector(FakeTimeProvider? clock = null, TimeSpan? idle = null) =>
        new(Id, clock ?? new FakeTimeProvider(T0), idle ?? TimeSpan.FromSeconds(60));

    private static byte[] B(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    [Fact]
    public void Collector_cat_dung_ranh_gioi_va_byte_du_sang_ban_tin_sau()
    {
        var c = Collector();
        c.NewSession();
        c.Append(B("AAAxxBB"), T0);
        // ranh giới sau "AAA" và sau "AAAxxBB"? chỉ cắt sau AAA; "xxBB" còn lại
        var first = c.ApplyBoundaries(7, [new DriverBoundary(3, false)], T0, isReceiving: true);
        first.Count.ShouldBe(1);
        first[0].Bytes.ShouldBe(B("AAA"));
        first[0].Partial.ShouldBeFalse();
        first[0].InstrumentId.ShouldBe(Id);

        // khúc sau: ranh giới ở offset 2 của khúc mới => cắt "xxBB" + "CC"
        c.Append(B("CCDD"), T0.AddSeconds(1));
        var second = c.ApplyBoundaries(4, [new DriverBoundary(2, true)], T0.AddSeconds(1), isReceiving: true);
        second.Count.ShouldBe(1);
        second[0].Bytes.ShouldBe(B("xxBBCC"));
        second[0].Partial.ShouldBeTrue();
        second[0].StartedAt.ShouldBe(T0);
        second[0].CompletedAt.ShouldBe(T0.AddSeconds(1));
        second[0].Seq.ShouldBeGreaterThan(first[0].Seq);
        second[0].Id.ShouldNotBe(first[0].Id);
        second[0].Session.ShouldBe(first[0].Session);

        // "DD" là byte rời dồn vào lượt sau; ngắt kết nối xả thành Partial
        var rest = c.OnDisconnect(T0.AddSeconds(2));
        rest.Single().Bytes.ShouldBe(B("DD"));
        rest.Single().Partial.ShouldBeTrue();
    }

    [Fact]
    public void Collector_nhieu_ranh_gioi_cung_khuc_va_offset_0_cua_ENQ_giua_phien()
    {
        var c = Collector();
        c.Append(B("ab"), T0);
        c.ApplyBoundaries(2, None, T0, true);
        c.Append(B("cdef"), T0);
        var r = c.ApplyBoundaries(4, [new DriverBoundary(0, true), new DriverBoundary(2, false), new DriverBoundary(4, false)], T0, true);
        r.Select(m => System.Text.Encoding.ASCII.GetString(m.Bytes)).ShouldBe(["ab", "cd", "ef"]);
        r.Select(m => m.Partial).ShouldBe([true, false, false]);
        r.Select(m => m.Seq).ShouldBeInOrder();
    }

    [Fact]
    public void Collector_im_lang_60s_xa_ban_tin_partial()
    {
        var c = Collector();
        c.Append(B("rac"), T0);
        c.ApplyBoundaries(3, None, T0, false);
        c.OnTick(None, T0.AddSeconds(59), false).ShouldBeEmpty();
        var r = c.OnTick(None, T0.AddSeconds(60), false);
        r.Single().Bytes.ShouldBe(B("rac"));
        r.Single().Partial.ShouldBeTrue();
        c.OnTick(None, T0.AddSeconds(120), false).ShouldBeEmpty();
    }

    [Fact]
    public void Collector_chi_nghe_khoang_lang_2s()
    {
        var c = Collector(idle: TimeSpan.FromSeconds(2));
        c.Append(B("dulieu"), T0);
        c.ApplyBoundaries(6, None, T0, false);
        c.OnTick(None, T0.AddSeconds(1), false).ShouldBeEmpty();
        var r = c.OnTick(None, T0.AddSeconds(2), false);
        r.Single().Bytes.ShouldBe(B("dulieu"));
        r.Single().Partial.ShouldBeTrue();
    }

    [Fact]
    public void Collector_Tick_ranh_gioi_cat_moi_byte_da_nhan()
    {
        var c = Collector();
        c.Append(B("abc"), T0);
        c.ApplyBoundaries(3, None, T0, true);
        var r = c.OnTick([new DriverBoundary(0, true)], T0.AddSeconds(31), false);
        r.Single().Bytes.ShouldBe(B("abc"));
        r.Single().Partial.ShouldBeTrue();
        c.OnDisconnect(T0.AddSeconds(32)).ShouldBeEmpty();
    }

    [Fact]
    public void Collector_ngat_ket_noi_xa_phan_du()
    {
        var c = Collector();
        c.Append(B("dodang"), T0);
        c.ApplyBoundaries(6, None, T0, true);
        var r = c.OnDisconnect(T0.AddSeconds(3));
        r.Single().Bytes.ShouldBe(B("dodang"));
        r.Single().Partial.ShouldBeTrue();
        c.OnDisconnect(T0.AddSeconds(4)).ShouldBeEmpty();
    }

    [Fact]
    public void Collector_doi_phien_cho_session_moi()
    {
        var c = Collector();
        c.NewSession();
        c.Append(B("a"), T0);
        var s1 = c.OnDisconnect(T0).Single().Session;
        c.NewSession();
        c.Append(B("b"), T0);
        var s2 = c.OnDisconnect(T0).Single().Session;
        s2.ShouldNotBe(s1);
        Guid.TryParse(s1, out _).ShouldBeTrue();
    }

    [Fact]
    public void Collector_byte_rac_deu_moi_giay_cho_driver_ranh_khong_vuot_60s()
    {
        // Máy gửi rác đều mỗi giây cho driver đang rảnh suốt 5 phút: bộ đệm không được phình quá ~60 giây byte.
        var driver = new Hl7MllpDriver();
        var c = Collector();
        var all = new List<byte>();
        var messages = new List<RawMessage>();
        for (var s = 0; s < 300; s++)
        {
            var now = T0.AddSeconds(s);
            byte[] data = [(byte)('a' + s % 26)];
            all.AddRange(data);
            c.Append(data, now);
            var o = driver.Receive(data, now);
            messages.AddRange(c.ApplyBoundaries(data.Length, o.Boundaries, now, driver.IsReceiving));
            messages.AddRange(c.OnTick(driver.Tick(now).Boundaries, now, driver.IsReceiving));
        }

        messages.AddRange(c.OnDisconnect(T0.AddSeconds(300)));
        messages.Count.ShouldBeGreaterThanOrEqualTo(4);
        messages.Max(m => m.Bytes.Length).ShouldBeLessThanOrEqualTo(62);
        messages.SelectMany(m => m.Bytes).ToArray().ShouldBe(all.ToArray());
        messages.Select(m => m.Seq).ShouldBeInOrder();
        messages.Take(messages.Count - 1).ShouldAllBe(m => m.Partial);
    }

    [Fact]
    public void Collector_bo_dem_cham_4MB_cat_ngay_thanh_ban_tin_Partial_phan_con_lai_sang_ban_sau()
    {
        var c = Collector();
        c.Append(new byte[RawMessageCollector.MaxMessageBytes - 1], T0);
        c.ApplyBoundaries(RawMessageCollector.MaxMessageBytes - 1, None, T0, isReceiving: true).ShouldBeEmpty();

        c.Append(new byte[11], T0.AddSeconds(1));
        var cut = c.ApplyBoundaries(11, None, T0.AddSeconds(1), isReceiving: true);

        cut.Count.ShouldBe(1);
        cut[0].Bytes.Length.ShouldBe(RawMessageCollector.MaxMessageBytes);
        cut[0].Partial.ShouldBeTrue();
        c.Pending.ShouldBe(10);
    }

    [Fact]
    public void Collector_ranh_gioi_sau_bo_dem_gan_day_khong_sinh_ban_tin_qua_4MB()
    {
        var c = Collector();
        c.Append(new byte[RawMessageCollector.MaxMessageBytes - 10], T0);
        c.ApplyBoundaries(RawMessageCollector.MaxMessageBytes - 10, None, T0, isReceiving: true).ShouldBeEmpty();

        c.Append(new byte[100], T0.AddSeconds(1));
        var cut = c.ApplyBoundaries(100, [new DriverBoundary(50, false)], T0.AddSeconds(1), isReceiving: true);

        cut.ShouldAllBe(m => m.Bytes.Length <= RawMessageCollector.MaxMessageBytes);
        cut.Sum(m => m.Bytes.Length).ShouldBe(RawMessageCollector.MaxMessageBytes - 10 + 50);
        cut.Select(m => m.Seq).ShouldBeInOrder();
        c.Pending.ShouldBe(50);
    }

    [Fact]
    public void Collector_Tick_va_ngat_ket_noi_cung_khong_vuot_4MB()
    {
        var c = Collector();
        c.Append(new byte[RawMessageCollector.MaxMessageBytes - 1], T0);
        // Receive ném lỗi nên ApplyBoundaries không được gọi; bộ đệm chỉ được cắt ở lượt Tick sau đó.
        c.Append(new byte[5], T0);
        var tick = c.OnTick(None, T0, isReceiving: true);
        tick.Single().Bytes.Length.ShouldBe(RawMessageCollector.MaxMessageBytes);
        c.Pending.ShouldBe(4);
        c.OnDisconnect(T0).Single().Bytes.Length.ShouldBe(4);
    }

    [Fact]
    public void Collector_khong_xa_theo_tuoi_khi_driver_dang_nhan()
    {
        var c = Collector();
        c.Append(B("x"), T0);
        c.ApplyBoundaries(1, None, T0, true);
        for (var s = 1; s <= 120; s++)
        {
            c.Append(B("y"), T0.AddSeconds(s));
            c.ApplyBoundaries(1, None, T0.AddSeconds(s), true).ShouldBeEmpty();
        }
    }

    [Fact]
    public void Collector_Seq_tang_dan_va_theo_ms_nhan_1000()
    {
        var clock = new FakeTimeProvider(T0);
        var c = new RawMessageCollector(Id, clock, TimeSpan.FromSeconds(60));
        c.Append(B("a"), T0);
        var a = c.ApplyBoundaries(1, [new DriverBoundary(1, false)], T0, true).Single();
        c.Append(B("b"), T0);
        var b = c.ApplyBoundaries(1, [new DriverBoundary(1, false)], T0, true).Single();
        a.Seq.ShouldBe(T0.ToUnixTimeMilliseconds() * 1000);
        b.Seq.ShouldBe(a.Seq + 1);
        a.Id.Version.ShouldBe(7);
    }

    [Fact]
    public void Collector_Seq_khoi_dong_lai_tiep_tuc_du_dong_ho_lui()
    {
        var clock = new FakeTimeProvider(T0);
        var last = T0.ToUnixTimeMilliseconds() * 1000 + 5000; // lần chạy trước ghi tới đây, giờ máy sau đó bị chỉnh lùi
        var c = new RawMessageCollector(Id, clock, TimeSpan.FromSeconds(60), initialSeq: last);
        c.Append(B("A"), T0);
        c.OnDisconnect(T0).Single().Seq.ShouldBe(last + 1);
        c.Append(B("B"), T0);
        c.OnDisconnect(T0).Single().Seq.ShouldBe(last + 2);
    }

    [Fact]
    public void Collector_Boundaries_offset_lon_hon_chieu_dai_khuc_chi_cat_den_het_bo_dem()
    {
        var c = Collector();
        c.Append(B("xx"), T0);
        c.Append(B("AB"), T0.AddSeconds(1));
        var r = c.ApplyBoundaries(2, [new DriverBoundary(50, false)], T0.AddSeconds(1), isReceiving: false);
        r.Single().Bytes.ShouldBe(B("xxAB"));
        c.Pending.ShouldBe(0);

        c.Append(B("C"), T0.AddSeconds(2));
        var again = c.ApplyBoundaries(1, [new DriverBoundary(9, false), new DriverBoundary(9, false)], T0.AddSeconds(2), isReceiving: false);
        again.Single().Bytes.ShouldBe(B("C")); // ranh giới thứ hai không còn byte: bỏ, không sinh bản tin rỗng
    }
}
