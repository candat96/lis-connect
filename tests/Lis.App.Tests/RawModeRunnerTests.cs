using System.Text;
using System.Threading.Channels;
using Lis.Connections;
using Lis.Connections.RawMessages;
using Lis.Core.Drivers;
using Lis.Core.Drivers.Astm;
using Lis.Core.Drivers.Hl7;
using Microsoft.Extensions.Time.Testing;
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

    private readonly FakeRawLogStore _logs = new();
    private readonly List<InstrumentConnectionStatus> _statuses = [];

    private InstrumentRunner Runner(Link link, IReadingSink sink, IRawMessageSink? raw, IInstrumentDriver? driver = null, TimeProvider? clock = null) =>
        new(Bc, new ConnectionSettings { Kind = ConnectionKind.TcpServer, TcpPort = 5100 }, driver ?? new Hl7MllpDriver(), _ => link, _logs,
            sink, new NoQueries(), _root, clock ?? TimeProvider.System, s => { lock (_statuses) _statuses.Add(s); }, raw);

    /// <summary>Đồng hồ giả chạy nhanh: mỗi 5 ms thật trôi 1 giây giả, để RetryDelay 5 giây và nhịp Tick không làm test chậm.</summary>
    private static (FakeTimeProvider Clock, CancellationTokenSource Stop) FastClock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(7)));
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                try { await Task.Delay(5, stop.Token); } catch (OperationCanceledException) { }
            }
        });
        return (clock, stop);
    }

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
        raw.Saved.Single().Bytes.ShouldBe(frame); // CR đuôi cùng khúc thuộc khung
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
        // Khung đầu chia hai khúc, CR đuôi đến riêng ở khúc thứ ba → là byte rời, dồn vào bản tin sau.
        link.Incoming.Writer.TryWrite(frame[..20]);
        link.Incoming.Writer.TryWrite(frame[20..^1]);
        link.Incoming.Writer.TryWrite(frame[^1..]);
        link.Incoming.Writer.TryWrite(frame);
        await WaitUntilAsync(() => raw.Saved.Count == 2);
        raw.Saved[0].Bytes.ShouldBe(frame[..^1]);
        raw.Saved[1].Bytes.ShouldBe(frame.Skip(frame.Length - 1).Concat(frame).ToArray());
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
        var (clock, stop) = FastClock();
        await using var runner = Runner(link, new Sink(), raw, clock: clock);
        runner.Start();
        var frame = Frame(Oru);
        link.Incoming.Writer.TryWrite(frame);

        // Không ghi được đĩa: không ACK, ngắt kết nối; hết lỗi thì lưu lại rồi nối lại, bản tin nào cũng có trên đĩa.
        await WaitUntilAsync(() => raw.Saved.Count >= 1);
        raw.Saved[0].Bytes.ShouldBe(frame);
        raw.Attempts.ShouldBeGreaterThanOrEqualTo(3);

        // Runner vẫn sống: máy gửi lại thì nhận và ACK.
        await WaitUntilAsync(() => link.Opens >= 2 && runner.Status.State == LinkState.Connected);
        link.Incoming.Writer.TryWrite(frame);
        await WaitUntilAsync(() => link.Written.Count == 1);
        raw.Saved.Select(m => m.Seq).ShouldBeInOrder();
        stop.Cancel();
    }

    [Fact]
    public async Task Runner_raw_khong_ghi_duoc_dia_khong_ack_ngat_ket_noi_roi_luu_lai_moi_noi_lai()
    {
        var events = new List<string>();
        var link = new Link(events) { OnOpen = () => { lock (events) events.Add("mo"); } };
        var raw = new Raw(events) { FailTimes = 3 };
        var (clock, stop) = FastClock();
        await using var runner = Runner(link, new Sink(), raw, clock: clock);
        runner.Start();
        await WaitUntilAsync(() => runner.Status.State == LinkState.Connected);
        var frame = Frame(Oru);
        link.Incoming.Writer.TryWrite(frame);

        // Lượt đầu: ghi đĩa lỗi -> không có byte Send nào, trạng thái Error, ngắt kết nối.
        await WaitUntilAsync(() => link.Opens >= 2);
        lock (events)
        {
            events.ShouldNotContain("gui");
            // Bản tin được lưu TRƯỚC lần mở kết nối kế tiếp (đĩa phục hồi sau 3 lần lỗi).
            events.IndexOf("luu").ShouldBeGreaterThan(-1);
            events.IndexOf("luu").ShouldBeLessThan(events.LastIndexOf("mo"));
        }

        link.Written.ShouldBeEmpty();
        lock (_statuses) _statuses.ShouldContain(s => s.State == LinkState.Error && s.Text.Contains("đĩa"));
        raw.Saved[0].Bytes.ShouldBe(frame);

        // Máy gửi lại bản tin -> được lưu rồi ACK.
        await WaitUntilAsync(() => runner.Status.State == LinkState.Connected);
        link.Incoming.Writer.TryWrite(frame);
        await WaitUntilAsync(() => link.Written.Count == 1);
        raw.Saved.Count(m => m.Bytes.SequenceEqual(frame)).ShouldBe(2);
        lock (events) events.Last().ShouldBe("gui");
        stop.Cancel();
    }

    [Fact]
    public async Task Runner_raw_Tick_sinh_Send_van_luu_truoc()
    {
        var events = new List<string>();
        var link = new Link(events);
        var raw = new Raw(events);
        var driver = new FakeDriver();
        driver.TickOutput = (d, armed) => { if (armed) { d.Send.Add([0x06]); d.Boundaries.Add(new DriverBoundary(0, false)); } };
        var (clock, stop) = FastClock();
        await using var runner = Runner(link, new Sink(), raw, driver, clock);
        runner.Start();
        await WaitUntilAsync(() => runner.Status.State == LinkState.Connected);
        link.Incoming.Writer.TryWrite([1, 2, 3]);
        await WaitUntilAsync(() => link.Written.Count == 1);
        lock (events) events.ShouldBe(["luu", "gui"]);
        raw.Saved.Single().Bytes.ShouldBe(new byte[] { 1, 2, 3 });
        stop.Cancel();
    }

    [Fact]
    public async Task Runner_raw_Tick_nem_loi_van_xa_phan_du_khi_ngat_ket_noi()
    {
        var events = new List<string>();
        var link = new Link(events);
        var raw = new Raw(events);
        var driver = new FakeDriver { TickOutput = (_, _) => throw new InvalidOperationException("Tick hỏng") };
        var (clock, stop) = FastClock();
        await using var runner = Runner(link, new Sink(), raw, driver, clock);
        runner.Start();
        await WaitUntilAsync(() => runner.Status.State == LinkState.Connected);
        link.Incoming.Writer.TryWrite([7, 8, 9]);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        link.Incoming.Writer.TryWrite([]); // máy đóng kết nối
        await WaitUntilAsync(() => raw.Saved.Count == 1);
        raw.Saved[0].Partial.ShouldBeTrue();
        raw.Saved[0].Bytes.ShouldBe(new byte[] { 7, 8, 9 });
        stop.Cancel();
    }

    [Fact]
    public async Task Runner_raw_ghi_link_loi_trong_Tick_van_xa_phan_du_khi_ngat_ket_noi()
    {
        var events = new List<string>();
        var link = new Link(events) { FailWrites = true };
        var raw = new Raw(events);
        var driver = new FakeDriver { TickOutput = (d, armed) => { if (armed) d.Send.Add([0x06]); } };
        var (clock, stop) = FastClock();
        await using var runner = Runner(link, new Sink(), raw, driver, clock);
        runner.Start();
        await WaitUntilAsync(() => runner.Status.State == LinkState.Connected);
        link.Incoming.Writer.TryWrite([7, 8, 9]);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        link.Incoming.Writer.TryWrite([]);
        await WaitUntilAsync(() => raw.Saved.Count == 1);
        raw.Saved[0].Partial.ShouldBeTrue();
        raw.Saved[0].Bytes.ShouldBe(new byte[] { 7, 8, 9 });
        stop.Cancel();
    }

    [Fact]
    public async Task Runner_raw_driver_Receive_nem_loi_khong_mat_byte_da_gom()
    {
        var events = new List<string>();
        var link = new Link(events);
        var raw = new Raw(events);
        var driver = new FakeDriver { ReceiveOutput = _ => throw new InvalidOperationException("Receive hỏng") };
        await using var runner = Runner(link, new Sink(), raw, driver);
        runner.Start();
        link.Incoming.Writer.TryWrite([4, 5, 6]);
        await WaitUntilAsync(() => raw.Saved.Count == 1);
        raw.Saved[0].Partial.ShouldBeTrue();
        raw.Saved[0].Bytes.ShouldBe(new byte[] { 4, 5, 6 });
    }

    [Fact]
    public async Task Runner_raw_mot_ca_ASTM_ENQ_den_EOT_thanh_dung_mot_ban_tin_luu_truoc_moi_byte_sau_ranh_gioi()
    {
        var events = new List<string>();
        var link = new Link(events);
        var raw = new Raw(events);
        await using var runner = Runner(link, new Sink(), raw, new AstmDriver(AstmDriverOptions.Generic));
        runner.Start();
        await WaitUntilAsync(() => runner.Status.State == LinkState.Connected);

        string[] records =
        [
            @"H|\^&|||SIM|||||LIS||P|1|20261108093000", "P|1", "O|1|2611080001||^^^WBC|R|20261108093000||||||||||||||||||N", "R|1|^^^WBC|7.5|||||F", "L|1|N",
        ];
        var fn = 1;
        var frames = new List<byte[]>();
        foreach (var r in records) frames.AddRange(AstmFraming.BuildFrames(r, fn, out fn));
        var all = new List<byte>();
        List<byte[]> parts = [[AstmFraming.Enq], .. frames, [AstmFraming.Eot]];
        foreach (var part in parts)
        {
            all.AddRange(part);
            link.Incoming.Writer.TryWrite(part);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await WaitUntilAsync(() => raw.Saved.Count == 1);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        raw.Saved.Count.ShouldBe(1);
        raw.Saved[0].Bytes.ShouldBe(all.ToArray());
        raw.Saved[0].Partial.ShouldBeFalse();
        // ACK ENQ + ACK từng khung đi trước (không thể lưu trước ACK khung cuối: máy chỉ gửi EOT sau ACK đó); EOT không có byte trả, nên không còn Send nào sau lần lưu.
        lock (events)
        {
            events.Count(e => e == "luu").ShouldBe(1);
            events.Last().ShouldBe("luu");
            events.Count(e => e == "gui").ShouldBe(1 + frames.Count);
        }
    }

    [Fact]
    public async Task Runner_raw_dung_ma_van_ghi_duoc_thi_xa_het_va_khong_log_critical()
    {
        var events = new List<string>();
        var link = new Link(events);
        var raw = new Raw(events);
        await using (var runner = Runner(link, new Sink(), raw))
        {
            runner.Start();
            await WaitUntilAsync(() => runner.Status.State == LinkState.Connected);
            link.Incoming.Writer.TryWrite(Frame(Oru)[..30]);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        raw.Saved.Single().Partial.ShouldBeTrue();
        _logs.Entries[Bc.Id].ShouldNotContain(e => e.Note != null && e.Note.Contains("Critical"));
    }

    [Fact]
    public async Task Runner_raw_dung_ma_dia_van_loi_thu_5_lan_roi_log_Critical_so_luong_khong_noi_dung()
    {
        var events = new List<string>();
        var link = new Link(events);
        var raw = new Raw(events) { FailTimes = int.MaxValue };
        var (clock, stop) = FastClock();
        var frame = Frame(Oru);
        var runner = Runner(link, new Sink(), raw, clock: clock);
        runner.Start();
        await WaitUntilAsync(() => runner.Status.State == LinkState.Connected);
        link.Incoming.Writer.TryWrite(frame);
        await WaitUntilAsync(() => raw.Attempts >= 2);
        var before = raw.Attempts;
        await runner.DisposeAsync();
        stop.Cancel();

        raw.Attempts.ShouldBeGreaterThanOrEqualTo(before + 5);
        var critical = _logs.Entries[Bc.Id].Select(e => e.Note ?? "").Where(n => n.Contains("Critical")).ToList();
        critical.Count.ShouldBe(1);
        critical[0].ShouldContain($"{frame.Length} byte");
        critical[0].ShouldNotContain("MSH");
        critical[0].ShouldNotContain("Mẫu");
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
        public Action? OnOpen { get; init; }
        public bool FailWrites { get; init; }
        public int Opens;

        public Task OpenAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Opens);
            OnOpen?.Invoke();
            return Task.CompletedTask;
        }

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var chunk = await Incoming.Reader.ReadAsync(ct);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (FailWrites) throw new IOException("cổng đóng");
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

    private sealed class FakeDriver : IInstrumentDriver
    {
        private volatile bool _armed;
        public Action<DriverOutput, bool>? TickOutput { get; set; }
        public Func<byte[], DriverOutput>? ReceiveOutput { get; init; }

        public string Family => "fake";
        public bool IsReceiving => false;

        public DriverOutput Receive(ReadOnlySpan<byte> data, DateTimeOffset now)
        {
            _armed = true;
            return ReceiveOutput is { } f ? f(data.ToArray()) : new DriverOutput();
        }

        public DriverOutput Tick(DateTimeOffset now)
        {
            var o = new DriverOutput();
            TickOutput?.Invoke(o, _armed);
            if (TickOutput is not null && o.Boundaries.Count > 0) _armed = false;
            return o;
        }

        public DriverOutput AnswerQuery(DriverQuery query, IReadOnlyList<string> channelCodes, DateTimeOffset now) => new();
        public void Reset() { }
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
