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

    [Theory]
    [MemberData(nameof(Streams))]
    public void Duong_cu_bang_duong_moi_voi_moi_cach_chia_khuc(string name)
    {
        var stream = Get(name);
        var index = DriverCorpus.All().ToList().FindIndex(s => s.Name == name);

        foreach (var (planName, seed, chunks) in Plans(stream.InBytes, index, stream.SyncPoints))
        {
            var context = $"dòng '{name}', cách chia '{planName}', seed {seed}, khúc [{string.Join(",", chunks.Select(c => c.Length))}]";

            var oldPath = Replay(stream, chunks);
            var newPath = ReplayNew(stream, chunks, context);

            // 3. Readings của hai đường bằng nhau theo thứ tự, so đủ trường.
            Describe(newPath.Readings).ShouldBe(Describe(oldPath.Readings), context);
            oldPath.Readings.Count.ShouldBe(stream.ExpectedReadings, $"{context}: đường cũ");
            newPath.Readings.Count.ShouldBe(stream.ExpectedReadings, $"{context}: đường mới");

            // 4. Nối các bản tin lại = đúng dòng byte vào.
            newPath.Messages.SelectMany(m => m).ToArray().ShouldBe(stream.InBytes, $"{context}: nối các bản tin phải bằng dòng byte vào");
        }
    }

    // ------------------------------------------------------------------ Hai đường

    private sealed record OldResult(List<DriverReading> Readings, List<byte[]> Sends);

    private sealed record NewResult(List<DriverReading> Readings, List<byte[]> Messages);

    private static IReadOnlyList<byte[]> WholeStream(CorpusStream s) => [s.InBytes];

    /// <summary>Đường cũ: đúng cách InstrumentRunner gọi driver — Receive từng khúc, Tick mỗi khúc, câu hỏi trả bằng kênh rỗng, Tick cuối +5 phút.</summary>
    private static OldResult Replay(CorpusStream stream, IReadOnlyList<byte[]>? chunks = null)
    {
        var driver = DriverRegistry.Create(stream.Family, stream.Model, stream.DriverConfigJson)!;
        var readings = new List<DriverReading>();
        var sends = new List<byte[]>();
        var now = T0;

        void Absorb(DriverOutput first)
        {
            var queue = new Queue<DriverOutput>();
            queue.Enqueue(first);
            while (queue.TryDequeue(out var output))
            {
                readings.AddRange(output.Readings);
                sends.AddRange(output.Send);
                foreach (var query in output.Queries)
                {
                    queue.Enqueue(driver.AnswerQuery(query, [], now));
                }
            }
        }

        foreach (var chunk in chunks ?? WholeStream(stream))
        {
            now = now.AddMilliseconds(10);
            Absorb(driver.Receive(chunk, now));
            Absorb(driver.Tick(now));
        }

        Absorb(driver.Tick(now.AddMinutes(5)));
        return new OldResult(readings, sends);
    }

    /// <summary>
    /// Đường mới: một driver sống nhận cùng các khúc để lấy ranh giới; cắt dòng byte theo đúng quy tắc của
    /// RawMessageCollector (ranh giới từ Receive: vị trí tuyệt đối = đầu khúc + Offset; từ Tick: mọi byte đã nhận;
    /// phần dư sau ranh giới cuối là bản tin partial cuối); mỗi bản tin giải mã bằng driver mới tinh.
    /// </summary>
    private static NewResult ReplayNew(CorpusStream stream, IReadOnlyList<byte[]> chunks, string context)
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
                    cut.ShouldBeGreaterThanOrEqualTo(cuts[^1], $"{context}: ranh giới đi lùi");
                }

                cuts.Add(cut);
            }
        }

        foreach (var chunk in chunks)
        {
            now = now.AddMilliseconds(10);
            var start = received;
            var output = live.Receive(chunk, now);
            received += chunk.Length;
            Collect(output, start, chunk.Length, fromReceive: true);

            // Câu hỏi chỉ định được trả (đường cũ làm thế); kết quả/ranh giới của AnswerQuery không có nhưng vẫn gom cho chắc.
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
        foreach (var cut in cuts.Append(stream.InBytes.Length))
        {
            if (cut > from)
            {
                messages.Add(stream.InBytes[from..cut]);
                from = cut;
            }
        }

        var readings = new List<DriverReading>();
        foreach (var message in messages)
        {
            var driver = DriverRegistry.Create(stream.Family, stream.Model, stream.DriverConfigJson)!;
            readings.AddRange(RawMessageDecoder.Decode(driver, message, T0).Readings);
        }

        return new NewResult(readings, messages);
    }

    private static List<string> Describe(IEnumerable<DriverReading> readings) =>
        readings.Select(r => $"{r.Barcode}|{r.IsQc}|{r.RawText}|" +
            string.Join(";", r.Items.Select(i => $"{i.ChannelCode}={i.RawValue}~{i.Unit}~{i.InstrumentFlag}"))).ToList();

    // ------------------------------------------------------------------ Cách chia khúc

    private static IEnumerable<(string Name, int Seed, IReadOnlyList<byte[]> Chunks)> Plans(byte[] data, int streamIndex, IReadOnlyList<int>? sync)
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

        yield return ("nguyên khối", 0, [.. segments.Select(g => data[g.From..g.To])]);
        yield return ("từng byte", 0, [.. data.Select(b => new[] { b })]);

        for (var plan = 0; plan < RandomPlans; plan++)
        {
            var seed = BaseSeed + (1000 * streamIndex) + plan;
            var random = new Random(seed);
            var maxChunk = plan % 3 == 0 ? 3 : plan % 3 == 1 ? 12 : 60;
            var chunks = new List<byte[]>();
            foreach (var (from, to) in segments)
            {
                var pos = from;
                while (pos < to)
                {
                    var length = Math.Min(random.Next(1, maxChunk + 1), to - pos);
                    chunks.Add(data[pos..(pos + length)]);
                    pos += length;
                }
            }

            yield return ($"ngẫu nhiên tối đa {maxChunk} byte", seed, chunks);
        }
    }
}
