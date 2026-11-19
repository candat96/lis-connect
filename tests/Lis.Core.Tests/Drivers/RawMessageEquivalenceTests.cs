using Lis.Core.Drivers;
using Lis.Core.Tests.Drivers.Corpus;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Drivers;

/// <summary>
/// Phép thử "giống hệt" (thiết kế mục 9): với mỗi dòng byte trong <see cref="DriverCorpus"/> và nhiều cách chia khúc,
/// danh sách <c>Readings</c> của ĐƯỜNG CŨ (một driver sống nhận cả dòng byte) bằng đúng ĐƯỜNG MỚI (cắt dòng byte theo
/// <see cref="DriverOutput.Boundaries"/>, mỗi bản tin giải mã bằng một driver mới tinh qua <see cref="RawMessageDecoder"/>),
/// và nối các bản tin lại bằng đúng dòng byte vào — không rơi, không lặp byte.
/// </summary>
public class RawMessageEquivalenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.FromHours(7));

    /// <summary>Seed gốc cố định; seed của mỗi ca = gốc + 1000 × số thứ tự dòng + số thứ tự cách chia. In ra khi đỏ.</summary>
    private const int BaseSeed = 20261119;

    private const int RandomPlans = 50;

    private const int GapPlans = 30;

    public static IEnumerable<object[]> Streams() => DriverCorpus.All().Select(s => new object[] { s.Name });

    private static CorpusStream Get(string name) => DriverCorpus.All().Single(s => s.Name == name);

    [Fact]
    public void Corpus_co_du_cac_ca_bat_buoc_cho_tung_ho()
    {
        var names = DriverCorpus.All().Select(s => s.Name).ToList();
        names.Count.ShouldBe(names.Distinct().Count());
        string[] hl7 = ["mot-ket-qua", "nhieu-mau", "qc", "cau-hoi", "bo-do", "khung-hong", "rac-truoc"];
        string[] astm = ["mot-ket-qua", "nhieu-mau", "qc", "cau-hoi", "bo-do", "sai-checksum", "rac-truoc", "ENQ-giua-phien", "access2"];
        string[] au = ["mot-ket-qua", "nhieu-mau", "qc", "cau-hoi", "bo-do", "sai-BCC", "rac-truoc", "nhieu-khoi"];
        foreach (var key in hl7) names.ShouldContain(n => n.StartsWith("hl7-") && n.Contains(key), $"hl7: {key}");
        foreach (var key in astm) names.ShouldContain(n => n.StartsWith("astm-") && n.Contains(key), $"astm: {key}");
        foreach (var key in au) names.ShouldContain(n => n.StartsWith("au-") && n.Contains(key), $"au: {key}");
        names.ShouldContain(n => n.Contains("ack-may-tra") && n.StartsWith("hl7-"));
        names.ShouldContain(n => n.Contains("ack-may-tra") && n.StartsWith("astm-"));
        names.ShouldContain(n => n.Contains("ack-may-tra") && n.StartsWith("au-"));
    }

    [Fact]
    public void Corpus_au_nhieu_khoi_that_su_co_tu_3_khoi()
    {
        var stream = Get("au-nhieu-khoi-3-khoi-tro-len");
        var driver = DriverRegistry.Create(stream.Family, stream.Model, stream.DriverConfigJson)!;
        var output = driver.Receive(stream.InBytes, T0);

        output.Readings.ShouldHaveSingleItem().Items.Count.ShouldBe(55);
        // Mỗi khối D␣ bắt đầu bằng STX "D " — đếm theo mã phân biệt.
        output.Readings[0].RawText.Split('\n').Length.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Corpus_cau_hoi_chi_dinh_that_su_tra_loi_LIS_va_byte_may_tra_duoc_tieu_thu()
    {
        // Đường cũ phải thật sự đi vào phiên LIS→máy: sau ACK của máy, driver gửi EOT (ASTM) hoặc khung chỉ định (AU/HL7).
        var astm = Replay(Get("astm-cau-hoi-chi-dinh-va-ack-may-tra"));
        astm.Sends.ShouldContain(s => s.Length == 1 && s[0] == 0x04, "ASTM: LIS phải gửi EOT sau khi máy ACK đủ frame");

        var au = Replay(Get("au-cau-hoi-chi-dinh-va-ack-may-tra"));
        au.Sends.Count(s => s.Length > 1).ShouldBeGreaterThanOrEqualTo(1, "AU: LIS phải gửi khung S/SE");

        var hl7 = Replay(Get("hl7-cau-hoi-chi-dinh-va-ack-may-tra"));
        hl7.Sends.Count(s => s.Length > 1).ShouldBeGreaterThanOrEqualTo(2, "HL7: ORR + ACK cho kết quả");
    }

    [Fact]
    public void Au_classA_nhieu_khoi_hong_ghi_lai_hanh_vi_hien_co_ca_hai_duong_nhu_nhau()
    {
        // Rủi ro AN TOÀN NGƯỜI BỆNH đã biết, có từ trước, KHÔNG sửa ở task này (chỉ ghi lại để task sau không bất ngờ).
        // 1) Class A tắt BCC, mất hẳn khối giữa: driver không kiểm thứ tự khối nên vẫn phát reading THIẾU kênh, không phát hiện được.
        var lost = Replay(Get("au-classA-nhieu-khoi-mat-khoi-giua"));
        lost.Readings.ShouldHaveSingleItem().Items.Count.ShouldBeLessThan(55);
        lost.Discarded.ShouldBeEmpty("không phát hiện được: đây là rủi ro đã biết");

        // 2) Khối giữa bị cắt ngắn: mẫu bị bỏ (Discarded không rỗng) nhưng khối E đến sau mở lại mẫu và phát reading chỉ có kênh khối E.
        var broken = Replay(Get("au-classA-nhieu-khoi-khoi-giua-hong"));
        broken.Discarded.ShouldNotBeEmpty();
        broken.Readings.ShouldHaveSingleItem().Items.Count.ShouldBeLessThan(55);

        foreach (var name in new[] { "au-classA-nhieu-khoi-mat-khoi-giua", "au-classA-nhieu-khoi-khoi-giua-hong" })
        {
            var stream = Get(name);
            var steps = WholeStream(stream);
            Describe(ReplayNew(stream, steps, name).Readings).ShouldBe(Describe(Replay(stream, steps).Readings), name);
        }
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void Duong_cu_bang_duong_moi_voi_moi_cach_chia_khuc(string name)
    {
        var stream = Get(name);
        var index = DriverCorpus.All().ToList().FindIndex(s => s.Name == name);

        foreach (var (planName, seed, steps) in Plans(stream.InBytes, index, stream.SyncPoints))
        {
            var context = $"dòng '{name}', cách chia '{planName}', seed {seed}, khúc [{string.Join(",", steps.Select(c => (c.Gap > TimeSpan.Zero ? $"+{c.Gap.TotalSeconds:0}s:" : "") + c.Bytes.Length))}]";
            var hasGaps = steps.Any(c => c.Gap > TimeSpan.Zero);

            var oldPath = Replay(stream, steps);
            var newPath = ReplayNew(stream, steps, context);

            // 4. Nối các bản tin lại = đúng dòng byte vào, không bản tin rỗng.
            newPath.Messages.SelectMany(m => m).ToArray().ShouldBe(stream.InBytes, $"{context}: nối các bản tin phải bằng dòng byte vào");

            if (hasGaps && stream.Family == "beckman-au")
            {
                // Beckman AU hết giờ giữa lượt: đường mới ĐỦ HƠN đường cũ. Lỗi có từ trước của driver (hết giờ tính từ khối
                // đầu của mẫu; Tick bỏ khung dở khi đang gom) làm đường cũ mất kết quả mà byte vẫn có đủ trên dây. Không sửa
                // driver ở task này, nên chỉ đòi: mọi reading đường cũ đều có trong đường mới (cùng barcode, item cũ là tập
                // con item mới, giá trị bằng nhau) và đường mới không có barcode lạ.
                AssertAuSubset(oldPath.Readings, newPath.Readings, stream.InBytes, context);
                continue;
            }

            // 3. Readings của hai đường bằng nhau theo thứ tự, so đủ trường.
            Describe(newPath.Readings).ShouldBe(Describe(oldPath.Readings), context);
            newPath.QueryBarcodes.ShouldBe(oldPath.QueryBarcodes, $"{context}: câu hỏi chỉ định hai đường");
            if (!hasGaps)
            {
                oldPath.Readings.Count.ShouldBe(stream.ExpectedReadings, $"{context}: đường cũ");
                newPath.Readings.Count.ShouldBe(stream.ExpectedReadings, $"{context}: đường mới");
            }
        }
    }

    private static void AssertAuSubset(List<DriverReading> oldReadings, List<DriverReading> newReadings, byte[] inBytes, string context)
    {
        var text = System.Text.Encoding.Latin1.GetString(inBytes);
        foreach (var reading in newReadings)
        {
            text.ShouldContain(reading.Barcode, Case.Sensitive, $"{context}: đường mới có barcode lạ '{reading.Barcode}'");
        }

        foreach (var old in oldReadings)
        {
            var found = newReadings.Any(n => n.Barcode == old.Barcode && n.IsQc == old.IsQc
                && old.Items.All(oi => n.Items.Contains(oi)));
            found.ShouldBeTrue($"{context}: reading đường cũ '{old.Barcode}' không có (đủ item) trong đường mới");
        }
    }

    // ------------------------------------------------------------------ Hai đường

    /// <summary>Một khúc byte; <see cref="Gap"/> là khoảng lặng TRƯỚC khúc (có Tick ở cuối khoảng lặng).</summary>
    private sealed record Step(byte[] Bytes, TimeSpan Gap);

    private sealed record OldResult(List<DriverReading> Readings, List<byte[]> Sends, List<string> QueryBarcodes, List<string> Discarded);

    private sealed record NewResult(List<DriverReading> Readings, List<byte[]> Messages, List<string> QueryBarcodes);

    private static IReadOnlyList<Step> WholeStream(CorpusStream s) => [new Step(s.InBytes, TimeSpan.Zero)];

    /// <summary>Đường cũ: đúng cách InstrumentRunner gọi driver — Receive từng khúc, Tick mỗi khúc, câu hỏi trả bằng kênh rỗng, Tick cuối +5 phút.</summary>
    private static OldResult Replay(CorpusStream stream, IReadOnlyList<Step>? steps = null)
    {
        var driver = DriverRegistry.Create(stream.Family, stream.Model, stream.DriverConfigJson)!;
        var readings = new List<DriverReading>();
        var sends = new List<byte[]>();
        var queries = new List<string>();
        var discarded = new List<string>();
        var now = T0;

        void Absorb(DriverOutput first)
        {
            var queue = new Queue<DriverOutput>();
            queue.Enqueue(first);
            while (queue.TryDequeue(out var output))
            {
                readings.AddRange(output.Readings);
                sends.AddRange(output.Send);
                discarded.AddRange(output.Discarded);
                foreach (var query in output.Queries)
                {
                    queries.Add(query.Barcode);
                    queue.Enqueue(driver.AnswerQuery(query, [], now));
                }
            }
        }

        foreach (var step in steps ?? WholeStream(stream))
        {
            if (step.Gap > TimeSpan.Zero)
            {
                now += step.Gap;
                Absorb(driver.Tick(now));
            }

            now = now.AddMilliseconds(10);
            Absorb(driver.Receive(step.Bytes, now));
            Absorb(driver.Tick(now));
        }

        Absorb(driver.Tick(now.AddMinutes(5)));
        return new OldResult(readings, sends, queries, discarded);
    }

    /// <summary>
    /// Đường mới: một driver sống nhận cùng các khúc (cùng khoảng lặng) để lấy ranh giới; cắt dòng byte theo đúng quy tắc của
    /// RawMessageCollector (ranh giới từ Receive: vị trí tuyệt đối = đầu khúc + Offset; từ Tick: mọi byte đã nhận;
    /// phần dư sau ranh giới cuối là bản tin partial cuối); mỗi bản tin giải mã bằng driver mới tinh.
    /// </summary>
    private static NewResult ReplayNew(CorpusStream stream, IReadOnlyList<Step> steps, string context)
    {
        var live = DriverRegistry.Create(stream.Family, stream.Model, stream.DriverConfigJson)!;
        var cuts = new List<int>();
        var received = 0;
        var now = T0;

        void Collect(DriverOutput output, int position, int chunkLength, bool fromReceive)
        {
            foreach (var boundary in output.Boundaries)
            {
                boundary.Offset.ShouldBeInRange(0, fromReceive ? chunkLength : 0, $"{context}: Offset nằm ngoài khoảng");
                var cut = fromReceive ? position + boundary.Offset : position;
                if (cuts.Count > 0)
                {
                    cut.ShouldBeGreaterThan(cuts[^1], $"{context}: ranh giới phải tăng nghiêm ngặt (không đi lùi, không trùng)");
                }

                cuts.Add(cut);
            }
        }

        foreach (var step in steps)
        {
            if (step.Gap > TimeSpan.Zero)
            {
                now += step.Gap;
                Collect(live.Tick(now), received, 0, fromReceive: false);
            }

            now = now.AddMilliseconds(10);
            var start = received;
            var output = live.Receive(step.Bytes, now);
            received += step.Bytes.Length;
            Collect(output, start, step.Bytes.Length, fromReceive: true);

            foreach (var query in output.Queries)
            {
                var answer = live.AnswerQuery(query, [], now);
                answer.Boundaries.ShouldBeEmpty($"{context}: AnswerQuery không được phát ranh giới");
            }

            Collect(live.Tick(now), received, 0, fromReceive: false);
        }

        Collect(live.Tick(now.AddMinutes(5)), received, 0, fromReceive: false);

        var messages = new List<byte[]>();
        var from = 0;
        foreach (var cut in cuts)
        {
            cut.ShouldBeLessThanOrEqualTo(stream.InBytes.Length, $"{context}: ranh giới vượt dòng byte");
            messages.Add(stream.InBytes[from..cut]);
            from = cut;
        }

        if (from < stream.InBytes.Length)
        {
            messages.Add(stream.InBytes[from..]); // phần dư sau ranh giới cuối: bản tin partial cuối
        }

        if (stream.InBytes.Length > 0)
        {
            messages.ShouldAllBe(m => m.Length > 0, $"{context}: có bản tin rỗng");
        }

        var readings = new List<DriverReading>();
        var queries = new List<string>();
        foreach (var message in messages)
        {
            var driver = DriverRegistry.Create(stream.Family, stream.Model, stream.DriverConfigJson)!;
            var decoded = RawMessageDecoder.Decode(driver, message, T0);
            readings.AddRange(decoded.Readings);
            queries.AddRange(decoded.QueryBarcodes);
        }

        return new NewResult(readings, messages, queries);
    }

    private static List<string> Describe(IEnumerable<DriverReading> readings) =>
        readings.Select(r => $"{r.Barcode}|{r.IsQc}|{r.RawText}|" +
            string.Join(";", r.Items.Select(i => $"{i.ChannelCode}={i.RawValue}~{i.Unit}~{i.InstrumentFlag}"))).ToList();

    // ------------------------------------------------------------------ Cách chia khúc

    private static IEnumerable<(string Name, int Seed, IReadOnlyList<Step> Steps)> Plans(byte[] data, int streamIndex, IReadOnlyList<int>? sync)
    {
        // Khúc không vắt qua điểm đồng bộ (máy chờ LIS trả lời rồi mới gửi tiếp).
        var syncs = (sync ?? []).Where(p => p > 0 && p < data.Length).Distinct().Order().ToList();
        var segments = new List<(int From, int To)>();
        var last = 0;
        foreach (var point in syncs.Append(data.Length))
        {
            segments.Add((last, point));
            last = point;
        }

        IReadOnlyList<Step> Whole() => [.. segments.Where(g => g.To > g.From).Select(g => new Step(data[g.From..g.To], TimeSpan.Zero))];

        yield return ("nguyên khối", 0, Whole());
        yield return ("từng byte", 0, [.. data.Select(b => new Step([b], TimeSpan.Zero))]);

        for (var plan = 0; plan < RandomPlans; plan++)
        {
            var seed = BaseSeed + (1000 * streamIndex) + plan;
            var maxChunk = plan % 3 == 0 ? 3 : plan % 3 == 1 ? 12 : 60;
            yield return ($"ngẫu nhiên tối đa {maxChunk} byte", seed, RandomSteps(seed, maxChunk, withGaps: false));
        }

        // Có khoảng lặng: giữa các khúc ngẫu nhiên chèn Tick +4 s hoặc +31 s (ranh giới hết giờ cũng xuất hiện).
        for (var plan = 0; plan < GapPlans; plan++)
        {
            var seed = BaseSeed + (1000 * streamIndex) + 500 + plan;
            var maxChunk = plan % 3 == 0 ? 5 : plan % 3 == 1 ? 20 : 80;
            yield return ($"khoảng lặng, khúc tối đa {maxChunk} byte", seed, RandomSteps(seed, maxChunk, withGaps: true));
        }

        IReadOnlyList<Step> RandomSteps(int seed, int maxChunk, bool withGaps)
        {
            var random = new Random(seed);
            var steps = new List<Step>();
            foreach (var (from, to) in segments)
            {
                var pos = from;
                while (pos < to)
                {
                    var length = Math.Min(random.Next(1, maxChunk + 1), to - pos);
                    var gap = TimeSpan.Zero;
                    if (withGaps && random.Next(6) == 0)
                    {
                        gap = TimeSpan.FromSeconds(random.Next(2) == 0 ? 4 : 31);
                    }

                    steps.Add(new Step(data[pos..(pos + length)], gap));
                    pos += length;
                }
            }

            return steps;
        }
    }
}
