using System.Text;
using System.Threading.Channels;
using Lis.Connections;
using Lis.Connections.RawMessages;
using Lis.Core.Drivers.Hl7;
using Lis.Core.Ingest;
using Lis.Core.Instruments;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>Runner ở chế độ bản tin thô: lưu bản tin xuống đĩa trước khi trả lời máy, không đi đường kết quả cũ.</summary>
public class RawModeRunnerTests : IDisposable
{
    private const string Oru =
        "MSH|^~\\&|Analyzer|Mindray|||20261108090000||ORU^R01|1|P|2.3.1||||||UNICODE\r" +
        "PID|1||BN0001^^^^MR||Mẫu^Thử\r" +
        "OBR|1||2611080001|00001^Automated Count^99MRC\r" +
        "OBX|1|NM|6690-2^WBC^LN||9.81|10*9/L|4.00-10.00|N|||F\r";

    private static readonly InstrumentInfo Bc = new(Guid.NewGuid(), "Mindray BC-5380", "BC-5380", "hl7-mllp", null, null, "MT01", true);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-rawrun-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static byte[] Frame(string m) => [0x0B, .. Encoding.UTF8.GetBytes(m), 0x1C, 0x0D];

    private InstrumentRunner Runner(Link link, IReadingSink sink, IRawMessageSink? raw) =>
        new(Bc, new ConnectionSettings { Kind = ConnectionKind.TcpServer, TcpPort = 5100 }, new Hl7MllpDriver(), _ => link, new FakeRawLogStore(),
            sink, new NoQueries(), _root, TimeProvider.System, _ => { }, raw);

    private static async Task WaitUntilAsync(Func<bool> cond)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Hết giờ chờ");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Runner_raw_luu_ban_tin_truoc_khi_gui_ACK()
    {
        var events = new List<string>();
        var link = new Link(events);
        var raw = new Raw(events) { Delay = TimeSpan.FromMilliseconds(150) };
        await using var runner = Runner(link, new Sink(), raw);
        runner.Start();

        var frame = Frame(Oru);
        link.Incoming.Writer.TryWrite(frame);
        await WaitUntilAsync(() => { lock (events) return events.Contains("gui"); });

        lock (events) events.ShouldBe(["luu", "gui"]);
        raw.Saved.Single().Bytes.ShouldBe(frame[..^1]); // CR sau FS là byte rời
        raw.Saved.Single().Partial.ShouldBeFalse();
        raw.Saved.Single().InstrumentId.ShouldBe(Bc.Id);
    }

    [Fact]
    public async Task Runner_raw_khong_goi_reading_sink()
    {
        var events = new List<string>();
        var link = new Link(events);
        var raw = new Raw(events);
        var sink = new Sink();
        await using var runner = Runner(link, sink, raw);
        runner.Start();
        link.Incoming.Writer.TryWrite(Frame(Oru));
        await WaitUntilAsync(() => raw.Saved.Count == 1 && link.Written.Count == 1);
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        sink.Calls.ShouldBe(0);
        Directory.Exists(runner.PendingDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task Runner_raw_chia_khuc_van_mot_ban_tin_va_byte_roi_vao_ban_tin_sau()
    {
        var events = new List<string>();
        var link = new Link(events);
        var raw = new Raw(events);
        await using var runner = Runner(link, new Sink(), raw);
        runner.Start();
        var frame = Frame(Oru);
        // CR sau FS ở khung đầu là byte rời, dồn vào bản tin sau.
        link.Incoming.Writer.TryWrite(frame[..20]);
        link.Incoming.Writer.TryWrite(frame[20..]);
        link.Incoming.Writer.TryWrite(frame);
        await WaitUntilAsync(() => raw.Saved.Count == 2);
        raw.Saved[0].Bytes.ShouldBe(frame[..^1]);
        raw.Saved[1].Bytes.ShouldBe(frame.Skip(frame.Length - 1).Concat(frame[..^1]).ToArray());
    }

    [Fact]
    public async Task Runner_raw_ngat_ket_noi_xa_phan_du_thanh_partial()
    {
        var events = new List<string>();
        var link = new Link(events);
        var raw = new Raw(events);
        await using var runner = Runner(link, new Sink(), raw);
        runner.Start();
        link.Incoming.Writer.TryWrite(Frame(Oru)[..30]);
        await WaitUntilAsync(() => runner.Status.State == LinkState.Connected);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        link.Incoming.Writer.TryWrite([]); // ReadAsync trả 0: máy đóng kết nối
        await WaitUntilAsync(() => raw.Saved.Count == 1);
        raw.Saved[0].Partial.ShouldBeTrue();
        raw.Saved[0].Bytes.Length.ShouldBe(30);
    }

    [Fact]
    public async Task Runner_raw_loi_ghi_dia_khong_dung_may()
    {
        var events = new List<string>();
        var link = new Link(events);
        var raw = new Raw(events) { FailTimes = 2 };
        await using var runner = Runner(link, new Sink(), raw);
        runner.Start();
        var frame = Frame(Oru);
        link.Incoming.Writer.TryWrite(frame);

        // Máy không bị treo: ACK vẫn ra; bản tin giữ trong bộ nhớ và được ghi lại ở Tick sau.
        await WaitUntilAsync(() => link.Written.Count == 1 && raw.Saved.Count == 1);
        raw.Saved.Single().Bytes.ShouldBe(frame[..^1]); // CR sau FS là byte rời
        raw.Attempts.ShouldBeGreaterThanOrEqualTo(3);
        runner.Status.State.ShouldBe(LinkState.Connected);

        // Runner vẫn nhận tiếp.
        link.Incoming.Writer.TryWrite(frame);
        await WaitUntilAsync(() => raw.Saved.Count == 2 && link.Written.Count == 2);
        raw.Saved[0].Seq.ShouldBeLessThan(raw.Saved[1].Seq);
    }

    [Fact]
    public async Task Runner_khong_raw_giu_nguyen_hanh_vi_cu()
    {
        var events = new List<string>();
        var link = new Link(events);
        var sink = new Sink();
        await using var runner = Runner(link, sink, null);
        runner.Start();
        link.Incoming.Writer.TryWrite(Frame(Oru));
        await WaitUntilAsync(() => sink.Calls == 1 && link.Written.Count == 1);
        sink.Calls.ShouldBe(1);
    }

    private sealed class Link(List<string> events) : IByteLink
    {
        public Channel<byte[]> Incoming { get; } = Channel.CreateUnbounded<byte[]>();
        public List<byte[]> Written { get; } = [];
        public string Description => "giả";
        public Task OpenAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var chunk = await Incoming.Reader.ReadAsync(ct);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            lock (events) events.Add("gui");
            lock (Written) Written.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Raw(List<string> events) : IRawMessageSink
    {
        public TimeSpan Delay { get; init; }
        public int FailTimes { get; init; }
        public int Attempts;
        public List<RawMessage> Saved { get; } = [];

        public async Task SaveAsync(RawMessage message, CancellationToken ct)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Interlocked.Increment(ref Attempts) <= FailTimes) throw new IOException("đĩa đầy");
            lock (events) events.Add("luu");
            lock (Saved) Saved.Add(message);
        }
    }

    private sealed class Sink : IReadingSink
    {
        public int Calls;

        public Task<IngestOutcome> IngestAsync(InstrumentReading reading)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new IngestOutcome(reading.Items.Count, 0, 0, 0));
        }
    }

    private sealed class NoQueries : IOrderQueryService
    {
        public Task<IReadOnlyList<string>> ChannelsToRunAsync(Guid instrumentId, string barcode, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
