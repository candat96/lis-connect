using System.Text;
using Lis.Core.Drivers;
using Lis.Core.Drivers.Astm;
using Lis.Core.Drivers.BeckmanAu;
using Lis.Core.Drivers.Hl7;
using Lis.Core.Drivers.ListenOnly;
using Lis.Simulator.Messages;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Drivers;

/// <summary>
/// Ranh giới lượt truyền (<see cref="DriverOutput.Boundaries"/>): mỗi lần phía nhận chuyển từ "đang có dữ liệu dở"
/// sang "rảnh" thì phát đúng một ranh giới; byte đến lúc rảnh mà không mở lượt mới thì không phát gì.
/// </summary>
public class BoundaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.FromHours(7));

    private const byte Enq = 0x05, Ack = 0x06, Nak = 0x15, Eot = 0x04;

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static List<DriverBoundary> Feed(IInstrumentDriver driver, byte[] data, DateTimeOffset? at = null) =>
        driver.Receive(data, at ?? T0).Boundaries;

    // ------------------------------------------------------------------ HL7

    private const string OruText = "MSH|^~\\&|A|M|||1||ORU^R01|1|P|2.3.1\rOBR|1||BC0001\rOBX|1|NM|6690-2^WBC^LN||5\r";

    private static byte[] Hl7Frame(string text = OruText) => Hl7ResultBuilder.Frame(text);

    [Fact]
    public void Hl7_ranh_gioi_ngay_sau_FS()
    {
        var frame = Hl7Frame();

        var boundaries = Feed(new Hl7MllpDriver(), frame);

        // Khung = VT + văn bản + FS + CR: CR đuôi thuộc khung (cùng khúc), ranh giới nằm sau CR.
        boundaries.ShouldBe([new DriverBoundary(frame.Length, false)]);
    }

    [Fact]
    public void Hl7_CR_duoi_den_o_khuc_sau_thi_ranh_gioi_sau_FS_va_CR_la_byte_roi()
    {
        var frame = Hl7Frame();
        var driver = new Hl7MllpDriver();

        var first = Feed(driver, frame[..^1]);
        var second = Feed(driver, frame[^1..]);

        first.ShouldBe([new DriverBoundary(frame.Length - 1, false)]);
        second.ShouldBeEmpty();
        driver.IsReceiving.ShouldBeFalse();
    }

    [Fact]
    public void Hl7_hai_khung_trong_mot_lan_Receive_cho_hai_ranh_gioi_theo_thu_tu()
    {
        var first = Hl7Frame();
        var second = Hl7Frame(OruText.Replace("BC0001", "BC0002"));

        var boundaries = Feed(new Hl7MllpDriver(), Cat([0x41], first, second));

        boundaries.ShouldBe([new DriverBoundary(1 + first.Length, false), new DriverBoundary(1 + first.Length + second.Length, false)]);
    }

    [Fact]
    public void Hl7_VT_giua_khung_la_bo_do_va_ranh_gioi_truoc_VT_moi()
    {
        var half = Cat([0x0B], Ascii("MSH|^~\\&|A"));
        var frame = Hl7Frame();

        var boundaries = Feed(new Hl7MllpDriver(), Cat(half, frame));

        boundaries.ShouldBe([new DriverBoundary(half.Length, true), new DriverBoundary(half.Length + frame.Length, false)]);
    }

    [Fact]
    public void Hl7_het_30s_tick_phat_ranh_gioi_bo_do_mot_lan()
    {
        var driver = new Hl7MllpDriver();
        driver.Receive(Cat([0x0B], Ascii("MSH|^~\\&|A")), T0);

        driver.Tick(T0.AddSeconds(10)).Boundaries.ShouldBeEmpty();
        driver.Tick(T0.AddSeconds(31)).Boundaries.ShouldBe([new DriverBoundary(0, true)]);
        driver.Tick(T0.AddSeconds(62)).Boundaries.ShouldBeEmpty();
    }

    [Fact]
    public void Hl7_khung_qua_2MB_la_bo_do()
    {
        var driver = new Hl7MllpDriver();
        var big = Cat([0x0B], new byte[2 * 1024 * 1024 + 10]);

        var boundaries = Feed(driver, big);

        var boundary = boundaries.ShouldHaveSingleItem();
        boundary.Abandoned.ShouldBeTrue();
        boundary.Offset.ShouldBeInRange(1, big.Length);
        // Sau đó đã rảnh: phần đuôi không đủ để mở lượt mới.
        Feed(driver, Cat([0x1C, 0x0D])).ShouldBeEmpty();
    }

    [Fact]
    public void Hl7_byte_roi_khi_ranh_khong_phat_ranh_gioi()
    {
        var driver = new Hl7MllpDriver();

        Feed(driver, [0x41, 0x00, 0xFF, 0x0D, 0x0A, 0x1C]).ShouldBeEmpty();
        // CR sau FS ở lần Receive sau cũng chỉ là byte rời.
        var frame = Hl7Frame();
        Feed(driver, frame[..^1]).ShouldBe([new DriverBoundary(frame.Length - 1, false)]);
        Feed(driver, frame[^1..]).ShouldBeEmpty();
    }

    [Fact]
    public void Hl7_chia_tung_byte_chi_phat_dung_mot_ranh_gioi_o_byte_FS()
    {
        var driver = new Hl7MllpDriver();
        var frame = Hl7Frame();
        var emitted = new List<(int At, DriverBoundary Boundary)>();

        for (var i = 0; i < frame.Length; i++)
        {
            foreach (var b in Feed(driver, frame[i..(i + 1)]))
            {
                emitted.Add((i, b));
            }
        }

        emitted.ShouldBe([(frame.Length - 2, new DriverBoundary(1, false))]);
    }

    // ------------------------------------------------------------------ ASTM

    private static readonly string[] Records =
    [
        @"H|\^&|||SIM|||||LIS||P|1|20261108093000",
        "P|1",
        "O|1|2611080001||^^^WBC|R|20261108093000||||||||||||||||||N",
        "R|1|^^^WBC|7.5|||||F",
        "L|1|N",
    ];

    private static List<byte[]> AstmFrames(IEnumerable<string> records)
    {
        var frames = new List<byte[]>();
        var fn = 1;
        foreach (var record in records)
        {
            frames.AddRange(AstmFraming.BuildFrames(record, fn, out fn));
        }

        return frames;
    }

    private static byte[] AstmSession(IEnumerable<string> records) => Cat([Enq], Cat([.. AstmFrames(records)]), [Eot]);

    private static AstmDriver NewAstm() => new(AstmDriverOptions.Generic);

    [Fact]
    public void Astm_EOT_ket_thuc_phien_nhan_phat_ranh_gioi_sau_EOT()
    {
        var session = AstmSession(Records);

        var boundaries = Feed(NewAstm(), session);

        boundaries.ShouldBe([new DriverBoundary(session.Length, false)]);
    }

    [Fact]
    public void Astm_ENQ_giua_phien_dat_ranh_gioi_truoc_ENQ()
    {
        var frames = AstmFrames(Records);
        var head = Cat([Enq], frames[0], frames[1]);
        var second = AstmSession(Records);

        var boundaries = Feed(NewAstm(), Cat(head, second));

        // Phần dở (ENQ + 2 frame) là một lượt bỏ dở; phiên mới nằm trọn sau đó và kết thúc ở EOT.
        boundaries.ShouldBe([new DriverBoundary(head.Length, true), new DriverBoundary(head.Length + second.Length, false)]);
    }

    [Fact]
    public void Astm_het_gio_tick_phat_ranh_gioi_bo_do()
    {
        var driver = NewAstm();
        var frames = AstmFrames(Records);
        driver.Receive(Cat([Enq], frames[0]), T0);

        driver.Tick(T0.AddSeconds(29)).Boundaries.ShouldBeEmpty();
        driver.Tick(T0.AddSeconds(31)).Boundaries.ShouldBe([new DriverBoundary(0, true)]);
        driver.Tick(T0.AddSeconds(100)).Boundaries.ShouldBeEmpty();
    }

    [Fact]
    public void Astm_EOT_khi_record_con_do_la_bo_do()
    {
        var driver = NewAstm();
        var frames = AstmFrames(Records);
        // Frame ETB (chưa phải frame cuối của record) rồi EOT.
        var etb = AstmFraming.BuildFrame(2, Ascii("R|1|^^^WBC|7."), last: false);

        var session = Cat([Enq], frames[0], etb, [Eot]);
        Feed(driver, session).ShouldBe([new DriverBoundary(session.Length, true)]);
    }

    [Fact]
    public void Astm_sai_checksum_khong_tu_no_phat_ranh_gioi_phien_van_mo()
    {
        var driver = NewAstm();
        var frames = AstmFrames(Records);
        var corrupt = (byte[])frames[1].Clone();
        corrupt[^4] = corrupt[^4] == (byte)'0' ? (byte)'1' : (byte)'0';

        Feed(driver, Cat([Enq], frames[0], corrupt)).ShouldBeEmpty();
        Feed(driver, Cat(frames[1], frames[2], frames[3], frames[4], [Eot])).Count.ShouldBe(1);
    }

    [Fact]
    public void Astm_byte_roi_khi_ranh_khong_phat_ranh_gioi()
    {
        var driver = NewAstm();
        // Rác, CR/LF, XON/XOFF khi rảnh.
        Feed(driver, [0x11, 0x13, 0x0D, 0x0A, 0xFF, Ack, Nak, Eot]).ShouldBeEmpty();

        // Máy hỏi chỉ định; LIS trả lời (ENQ + 2 frame); ACK máy trả là byte rời, EOT của máy sau phiên LIS cũng vậy.
        var query = AstmSession(["H|\\^&|||SIM|||||LIS||P|1|20261108093000", "Q|1|^2611080001||||||||||O", "L|1|N"]);
        var output = driver.Receive(query, T0);
        output.Boundaries.ShouldBe([new DriverBoundary(query.Length, false)]);
        output.Queries.ShouldHaveSingleItem();
        var answer = driver.AnswerQuery(output.Queries[0], [], T0);
        answer.Send[0].ShouldBe([Enq]);
        answer.Boundaries.ShouldBeEmpty();

        // Máy ACK ENQ rồi ACK từng frame: toàn byte rời.
        Feed(driver, [Ack, Ack, Ack, Ack]).ShouldBeEmpty();
    }

    [Fact]
    public void Astm_phien_LIS_den_may_ket_thuc_bang_EOT_cua_LIS_va_khong_phat_ranh_gioi_nao()
    {
        var driver = NewAstm();
        var output = driver.Receive(AstmSession(["H|\\^&|||SIM|||||LIS||P|1|20261108093000", "Q|1|^2611080001||||||||||O", "L|1|N"]), T0);
        var sent = new List<byte[]>(driver.AnswerQuery(output.Queries[0], [], T0).Send);

        // Hỏi chỉ định không có chỉ định: H + L = 2 frame; máy ACK ENQ + 2 ACK.
        foreach (var b in new byte[] { Ack, Ack, Ack })
        {
            var o = driver.Receive([b], T0);
            o.Boundaries.ShouldBeEmpty();
            sent.AddRange(o.Send);
        }

        sent.Select(s => s.Length == 1 ? s[0] : (byte)0).ShouldContain(Eot);
    }

    // ------------------------------------------------------------------ Beckman AU

    private static readonly BeckmanAuOptions Small = BeckmanAuOptions.Au480 with { ClassB = true, Bcc = true, TextLength = 256 };

    private static IReadOnlyList<byte[]> AuFrames(string barcode, int items) =>
        AuMessageBuilder.BuildResultFrames(Small, barcode, [.. Enumerable.Range(1, items).Select(i => new SimResult(i.ToString("00"), $"{i}.5"))], false);

    private static BeckmanAuDriver NewAu() => new(Small);

    [Fact]
    public void Au_nhieu_khoi_chi_mot_ranh_gioi_sau_khoi_E()
    {
        var frames = AuFrames("2611030001", 55); // DB, D0, D1, E, DE
        frames.Count.ShouldBeGreaterThanOrEqualTo(5);
        var blocks = frames.Skip(1).Take(frames.Count - 2).ToList();
        blocks.Count.ShouldBeGreaterThanOrEqualTo(3);
        var driver = NewAu();
        var emitted = new List<(int Frame, DriverBoundary Boundary)>();
        var offset = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            foreach (var b in driver.Receive(blocks[i], T0).Boundaries)
            {
                emitted.Add((i, b));
            }

            offset += blocks[i].Length;
        }

        emitted.ShouldBe([(blocks.Count - 1, new DriverBoundary(blocks[^1].Length, false))]);
    }

    [Fact]
    public void Au_nhieu_khoi_trong_mot_lan_Receive_cung_chi_mot_ranh_gioi()
    {
        var frames = AuFrames("2611030002", 55);
        var blocks = frames.Skip(1).Take(frames.Count - 2).ToList();
        var data = Cat([.. blocks]);

        Feed(NewAu(), data).ShouldBe([new DriverBoundary(data.Length, false)]);
    }

    [Fact]
    public void Au_DB_va_DE_moi_cai_la_mot_luot_ranh_vi_khong_co_khung_do()
    {
        var frames = AuFrames("2611030003", 3); // DB, D(E), DE
        var driver = NewAu();

        driver.Receive(frames[0], T0).Boundaries.ShouldBe([new DriverBoundary(frames[0].Length, false)]);
        driver.Receive(frames[1], T0).Boundaries.ShouldBe([new DriverBoundary(frames[1].Length, false)]);
        driver.Receive(frames[2], T0).Boundaries.ShouldBe([new DriverBoundary(frames[2].Length, false)]);
    }

    [Fact]
    public void Au_chia_tung_byte_ranh_gioi_o_byte_BCC_cua_khoi_E()
    {
        var frames = AuFrames("2611030004", 55);
        var blocks = frames.Skip(1).Take(frames.Count - 2).ToList();
        var driver = NewAu();
        var count = 0;
        for (var f = 0; f < blocks.Count; f++)
        {
            for (var i = 0; i < blocks[f].Length; i++)
            {
                var boundaries = driver.Receive(blocks[f][i..(i + 1)], T0).Boundaries;
                if (boundaries.Count > 0)
                {
                    f.ShouldBe(blocks.Count - 1);
                    i.ShouldBe(blocks[f].Length - 1);
                    boundaries.ShouldBe([new DriverBoundary(1, false)]);
                    count++;
                }
            }
        }

        count.ShouldBe(1);
    }

    [Fact]
    public void Au_het_3s_khung_do_va_het_30s_pending_la_bo_do_chi_mot_ranh_gioi()
    {
        var blocks = AuFrames("2611030005", 55);

        // Khung dở không có ETX: 3 s.
        var d1 = NewAu();
        d1.Receive(blocks[1][..10], T0).Boundaries.ShouldBeEmpty();
        d1.Tick(T0.AddSeconds(2)).Boundaries.ShouldBeEmpty();
        d1.Tick(T0.AddSeconds(4)).Boundaries.ShouldBe([new DriverBoundary(0, true)]);

        // Khối 0 đã đủ, đang chờ khối E: 30 s.
        var d2 = NewAu();
        d2.Receive(blocks[1], T0).Boundaries.ShouldBeEmpty();
        d2.Tick(T0.AddSeconds(10)).Boundaries.ShouldBeEmpty();
        d2.Tick(T0.AddSeconds(31)).Boundaries.ShouldBe([new DriverBoundary(0, true)]);

        // Cả hai cùng hết giờ trong một Tick (khung dở của khối kế): vẫn một ranh giới.
        var d3 = NewAu();
        d3.Receive(Cat(blocks[1], blocks[2][..10]), T0).Boundaries.ShouldBeEmpty();
        d3.Tick(T0.AddSeconds(31)).Boundaries.ShouldBe([new DriverBoundary(0, true)]);
    }

    [Fact]
    public void Au_byte_roi_khi_ranh_khong_phat_ranh_gioi()
    {
        var driver = NewAu();

        // Rác, ACK/NAK máy trả khi LIS gửi chỉ định.
        Feed(driver, [0x00, 0xFF, 0x41, 0x0D, 0x0A, Ack, Nak, Ack]).ShouldBeEmpty();
        // Giữa chừng một mẫu nhiều khối, byte rời không tách mẫu làm đôi.
        var frames = AuFrames("2611030006", 55);
        var blocks = frames.Skip(1).Take(frames.Count - 2).ToList();
        Feed(driver, blocks[0]).ShouldBeEmpty();
        Feed(driver, [Ack]).ShouldBeEmpty();
        Feed(driver, blocks[1]).ShouldBeEmpty();
        Feed(driver, blocks[2]).ShouldBe([new DriverBoundary(blocks[2].Length, false)]);
    }

    [Fact]
    public void Au_sai_BCC_ket_thuc_luot_khi_khong_con_gi_do()
    {
        var frame = (byte[])AuFrames("2611030007", 3)[1].Clone();
        frame[^1] ^= 0x01;

        Feed(NewAu(), frame).ShouldBe([new DriverBoundary(frame.Length, false)]);
    }

    [Fact]
    public void Au_khung_do_gap_STX_moi_van_la_mot_luot_khong_phat_giua_chung()
    {
        var frames = AuFrames("2611030008", 3);
        var half = frames[1][..12];

        Feed(NewAu(), Cat(half, frames[1])).ShouldBe([new DriverBoundary(half.Length + frames[1].Length, false)]);
    }

    // ------------------------------------------------------------------ ListenOnly

    [Fact]
    public void ListenOnly_khong_bao_gio_phat()
    {
        var driver = new ListenOnlyDriver();

        driver.Receive(Hl7Frame(), T0).Boundaries.ShouldBeEmpty();
        driver.Receive(AstmSession(Records), T0).Boundaries.ShouldBeEmpty();
        driver.Receive([Enq, 0x02, 0x03, Eot], T0).Boundaries.ShouldBeEmpty();
        driver.Tick(T0.AddMinutes(10)).Boundaries.ShouldBeEmpty();
        driver.Tick(T0.AddMinutes(10)).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void IsEmpty_tinh_ca_Boundaries()
    {
        var output = new DriverOutput();
        output.IsEmpty.ShouldBeTrue();
        output.Boundaries.Add(new DriverBoundary(0, true));
        output.IsEmpty.ShouldBeFalse();
    }
}

/// <summary>Bộ giải mã một bản tin thô: chạy bằng driver mới, bỏ Send, trả câu hỏi "không có", Tick cuối.</summary>
public class RawMessageDecoderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void Version_khong_rong()
    {
        RawMessageDecoder.Version.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Mot_ban_tin_HL7_ra_mot_ket_qua_va_khong_bo_do()
    {
        var bytes = Hl7ResultBuilder.Frame(Hl7ResultBuilder.BuildOru("BC0001", [new SimResult("6690-2", "5.2")], T0, 1, false));

        var result = RawMessageDecoder.Decode(new Hl7MllpDriver(), bytes, T0);

        result.Readings.ShouldHaveSingleItem().Barcode.ShouldBe("BC0001");
        result.Abandoned.ShouldBeFalse();
    }

    [Fact]
    public void Ban_tin_dang_do_duoc_Tick_cuoi_xa_va_danh_dau_bo_do()
    {
        var bytes = Encoding.ASCII.GetBytes("\u000BMSH|^~\\&|A|M|||1||ORU^R01|1|P|2.3.1\r");

        var result = RawMessageDecoder.Decode(new Hl7MllpDriver(), bytes, T0);

        result.Readings.ShouldBeEmpty();
        result.Abandoned.ShouldBeTrue();
        result.Notes.ShouldNotBeEmpty();
    }

    [Fact]
    public void Cau_hoi_chi_dinh_ra_ma_vach_va_khong_ket_qua()
    {
        var query = Hl7ResultBuilder.Frame("MSH|^~\\&|Analyzer|Mindray|||20081120174836||ORM^O01|4|P|2.3.1\rORC|RF||2611030001||IP\r");

        var result = RawMessageDecoder.Decode(new Hl7MllpDriver(), query, T0);

        result.QueryBarcodes.ShouldBe(["2611030001"]);
        result.Readings.ShouldBeEmpty();
    }

    [Fact]
    public void Decode_dung_lai_cung_driver_van_Reset_sach()
    {
        var driver = new Hl7MllpDriver();
        var half = Encoding.ASCII.GetBytes("\u000BMSH|^~\\&|A");
        RawMessageDecoder.Decode(driver, half, T0);

        // Bản tin sau không bị dính phần dở của bản tin trước.
        var full = Hl7ResultBuilder.Frame(Hl7ResultBuilder.BuildOru("BC0002", [new SimResult("6690-2", "5.2")], T0, 2, false));
        RawMessageDecoder.Decode(driver, full, T0).Readings.ShouldHaveSingleItem();
    }
}

/// <summary>
/// <see cref="IInstrumentDriver.IsReceiving"/> (false lúc mới Reset, true giữa lượt, false sau ranh giới) và
/// <see cref="DriverOutput.Discarded"/> (mỗi lần driver bỏ dữ liệu máy đã gửi thì có một lý do tiếng Việt).
/// </summary>
public class DriverStateSignalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.FromHours(7));

    private const byte Enq = 0x05, Eot = 0x04;

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static readonly string[] Records =
    [
        @"H|\^&|||SIM|||||LIS||P|1|20261108093000",
        "P|1",
        "O|1|2611080001||^^^WBC|R|20261108093000||||||||||||||||||N",
        "R|1|^^^WBC|7.5|||||F",
        "L|1|N",
    ];

    private static List<byte[]> AstmFrames()
    {
        var frames = new List<byte[]>();
        var fn = 1;
        foreach (var record in Records)
        {
            frames.AddRange(AstmFraming.BuildFrames(record, fn, out fn));
        }

        return frames;
    }

    private static readonly BeckmanAuOptions AuOptions = BeckmanAuOptions.Au480 with { ClassB = true, Bcc = true, TextLength = 256 };

    private static IReadOnlyList<byte[]> AuFrames(int items) =>
        AuMessageBuilder.BuildResultFrames(AuOptions, "2611030001", [.. Enumerable.Range(1, items).Select(i => new SimResult(i.ToString("00"), $"{i}.5"))], false);

    // ---------------------------------------------------------------- IsReceiving

    [Fact]
    public void Hl7_IsReceiving_theo_khung()
    {
        var driver = new Hl7MllpDriver();
        driver.IsReceiving.ShouldBeFalse();

        driver.Receive([0x41, 0x0D], T0);
        driver.IsReceiving.ShouldBeFalse();

        driver.Receive(Cat([0x0B], Encoding.ASCII.GetBytes("MSH|^~\\&|A")), T0);
        driver.IsReceiving.ShouldBeTrue();

        driver.Receive(Encoding.ASCII.GetBytes("|M\r\u001c"), T0);
        driver.IsReceiving.ShouldBeFalse();

        driver.Receive([0x0B, 0x41], T0);
        driver.IsReceiving.ShouldBeTrue();
        driver.Reset();
        driver.IsReceiving.ShouldBeFalse();

        driver.Receive([0x0B, 0x41], T0);
        driver.Tick(T0.AddSeconds(31));
        driver.IsReceiving.ShouldBeFalse();
    }

    [Fact]
    public void Astm_IsReceiving_theo_phien_nhan()
    {
        var driver = new AstmDriver(AstmDriverOptions.Generic);
        var frames = AstmFrames();
        driver.IsReceiving.ShouldBeFalse();

        driver.Receive([0x11, 0x0D], T0);
        driver.IsReceiving.ShouldBeFalse();

        driver.Receive([Enq], T0);
        driver.IsReceiving.ShouldBeTrue();
        driver.Receive(frames[0], T0);
        driver.IsReceiving.ShouldBeTrue();
        driver.Receive([Eot], T0);
        driver.IsReceiving.ShouldBeFalse();

        driver.Receive([Enq], T0);
        driver.Reset();
        driver.IsReceiving.ShouldBeFalse();

        driver.Receive([Enq], T0);
        driver.Tick(T0.AddSeconds(31));
        driver.IsReceiving.ShouldBeFalse();
    }

    [Fact]
    public void Astm_IsReceiving_false_khi_LIS_dang_gui_cho_may()
    {
        var driver = new AstmDriver(AstmDriverOptions.Generic);
        var queryFrames = new List<byte[]>();
        var fn = 1;
        foreach (var record in new[] { "H|\\^&|||SIM|||||LIS||P|1|20261108093000", "Q|1|^2611080001||||||||||O", "L|1|N" })
        {
            queryFrames.AddRange(AstmFraming.BuildFrames(record, fn, out fn));
        }

        var query = Cat([Enq], Cat([.. queryFrames]), [Eot]);
        var output = driver.Receive(query, T0);
        driver.AnswerQuery(output.Queries[0], [], T0);

        // LIS đã gửi ENQ, chờ máy ACK: phía NHẬN vẫn rảnh.
        driver.IsReceiving.ShouldBeFalse();
        driver.Receive([0x06], T0);
        driver.IsReceiving.ShouldBeFalse();
    }

    [Fact]
    public void Au_IsReceiving_theo_khung_va_khoi()
    {
        var driver = new BeckmanAuDriver(AuOptions);
        var frames = AuFrames(55); // DB, D0, D1, E, DE
        driver.IsReceiving.ShouldBeFalse();

        driver.Receive(frames[0], T0);
        driver.IsReceiving.ShouldBeFalse();

        driver.Receive(frames[1][..10], T0);
        driver.IsReceiving.ShouldBeTrue();
        driver.Receive(frames[1][10..], T0);
        driver.IsReceiving.ShouldBeTrue(); // khối 0 đủ, đang gom
        driver.Receive(frames[2], T0);
        driver.IsReceiving.ShouldBeTrue();
        driver.Receive(frames[3], T0);
        driver.IsReceiving.ShouldBeFalse();

        driver.Receive(frames[1], T0);
        driver.Reset();
        driver.IsReceiving.ShouldBeFalse();
    }

    [Fact]
    public void ListenOnly_IsReceiving_luon_false()
    {
        var driver = new ListenOnlyDriver();
        driver.Receive([0x0B, Enq, 0x02], T0);
        driver.IsReceiving.ShouldBeFalse();
    }

    // ---------------------------------------------------------------- Discarded

    [Fact]
    public void Hl7_Discarded_VT_giua_khung_va_het_gio_nhung_khung_tron_thi_khong()
    {
        var driver = new Hl7MllpDriver();
        var ok = driver.Receive(Hl7ResultBuilder.Frame("MSH|^~\\&|A|M|||1||ORU^R01|1|P|2.3.1\rOBR|1||BC1\r"), T0);
        ok.Discarded.ShouldBeEmpty();

        var restart = driver.Receive([0x0B, 0x41, 0x0B], T0);
        restart.Discarded.ShouldHaveSingleItem().ShouldContain("VT");

        driver.Tick(T0.AddSeconds(31)).Discarded.ShouldHaveSingleItem().ShouldContain("30");
    }

    [Fact]
    public void Hl7_Discarded_khung_qua_2MB()
    {
        var output = new Hl7MllpDriver().Receive(Cat([0x0B], new byte[2 * 1024 * 1024 + 10]), T0);

        output.Discarded.ShouldHaveSingleItem().ShouldContain("MB");
    }

    [Fact]
    public void Astm_Discarded_checksum_ENQ_giua_phien_het_gio_EOT_do()
    {
        var frames = AstmFrames();
        var driver = new AstmDriver(AstmDriverOptions.Generic);

        // Phiên bình thường: không bỏ gì.
        driver.Receive(Cat([Enq], Cat([.. frames]), [Eot]), T0).Discarded.ShouldBeEmpty();

        // Sai checksum.
        var corrupt = (byte[])frames[1].Clone();
        corrupt[^4] = corrupt[^4] == (byte)'0' ? (byte)'1' : (byte)'0';
        var bad = driver.Receive(Cat([Enq], frames[0], corrupt), T0);
        bad.Discarded.ShouldHaveSingleItem().ShouldContain("checksum");

        // ENQ giữa phiên.
        driver.Receive([Enq], T0).Discarded.ShouldHaveSingleItem().ShouldContain("ENQ");

        // Hết giờ.
        driver.Tick(T0.AddSeconds(31)).Discarded.ShouldHaveSingleItem().ShouldContain("30");

        // EOT khi record còn dở.
        driver.Receive(Cat([Enq], frames[0], AstmFraming.BuildFrame(2, Encoding.ASCII.GetBytes("R|1|^^^WBC|7."), last: false), [Eot]), T0)
            .Discarded.ShouldHaveSingleItem().ShouldContain("EOT");
    }

    [Fact]
    public void Au_Discarded_BCC_sai_STX_moi_khoi_khong_co_khoi_dau_va_het_gio()
    {
        var frames = AuFrames(55);

        // Phiên bình thường: không bỏ gì.
        var ok = new BeckmanAuDriver(AuOptions);
        foreach (var f in frames)
        {
            ok.Receive(f, T0).Discarded.ShouldBeEmpty();
        }

        // BCC sai.
        var bad = (byte[])frames[1].Clone();
        bad[^1] ^= 0x01;
        new BeckmanAuDriver(AuOptions).Receive(bad, T0).Discarded.ShouldHaveSingleItem().ShouldContain("BCC");

        // STX mới giữa khung.
        new BeckmanAuDriver(AuOptions).Receive(Cat(frames[1][..10], frames[1]), T0).Discarded.ShouldHaveSingleItem().ShouldContain("khung dở");

        // Khối giữa đến khi chưa có khối đầu.
        new BeckmanAuDriver(AuOptions).Receive(frames[2], T0).Discarded.ShouldHaveSingleItem().ShouldContain("chưa có khối đầu");

        // Hết giờ: khung dở (3 s) và phần gom dở (30 s).
        var d1 = new BeckmanAuDriver(AuOptions);
        d1.Receive(frames[1][..10], T0);
        d1.Tick(T0.AddSeconds(4)).Discarded.ShouldHaveSingleItem().ShouldContain("khung");
        var d2 = new BeckmanAuDriver(AuOptions);
        d2.Receive(frames[1], T0);
        d2.Tick(T0.AddSeconds(31)).Discarded.ShouldHaveSingleItem().ShouldContain("khối cuối");
    }

    [Fact]
    public void IsEmpty_tinh_ca_Discarded()
    {
        var output = new DriverOutput();
        output.Discarded.Add("x");
        output.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void ListenOnly_khong_bao_gio_co_Discarded()
    {
        new ListenOnlyDriver().Receive([0x0B, Enq, 0x02, 0x03], T0).Discarded.ShouldBeEmpty();
    }

    [Fact]
    public void Decoder_gom_Discarded_tu_moi_buoc()
    {
        // Bản tin HL7 dở: Tick cuối của decoder bỏ phần dở, lý do vào Discarded.
        var half = Encoding.ASCII.GetBytes("\u000BMSH|^~\\&|A|M|||1||ORU^R01|1|P|2.3.1\r");
        RawMessageDecoder.Decode(new Hl7MllpDriver(), half, T0).Discarded.ShouldHaveSingleItem();

        var full = Hl7ResultBuilder.Frame("MSH|^~\\&|A|M|||1||ORU^R01|1|P|2.3.1\rOBR|1||BC1\r");
        RawMessageDecoder.Decode(new Hl7MllpDriver(), full, T0).Discarded.ShouldBeEmpty();
    }
}
