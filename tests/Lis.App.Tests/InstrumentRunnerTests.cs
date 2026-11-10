using System.Text;
using System.Threading.Channels;
using Lis.Connections;
using Lis.Core.Drivers.Hl7;
using Lis.Core.Ingest;
using Lis.Core.Instruments;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>Vòng chạy kết nối một máy: byte vào driver, ACK ra máy, kết quả vào CSDL, mất CSDL thì lưu tạm rồi ghi lại.</summary>
public class InstrumentRunnerTests : IDisposable
{
    private const string Oru =
        "MSH|^~\\&|Analyzer|Mindray|||20261108090000||ORU^R01|1|P|2.3.1||||||UNICODE\r" +
        "PID|1||BN0001^^^^MR||Mẫu^Thử\r" +
        "OBR|1||2611080001|00001^Automated Count^99MRC\r" +
        "OBX|1|NM|6690-2^WBC^LN||9.81|10*9/L|4.00-10.00|N|||F\r" +
        "OBX|2|NM|718-7^HGB^LN||135|g/L|110-150|N|||F\r";

    private static readonly InstrumentInfo Bc5380 = new(Guid.NewGuid(), "Mindray BC-5380", "BC-5380", "hl7-mllp", null, null, "MT01", true);

    private readonly string _pendingRoot = Path.Combine(Path.GetTempPath(), "lis-runner-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_pendingRoot))
        {
            Directory.Delete(_pendingRoot, recursive: true);
        }
    }

    private static byte[] Frame(string message) => [0x0B, .. Encoding.UTF8.GetBytes(message), 0x1C, 0x0D];

    private InstrumentRunner Runner(FakeLink link, FakeSink sink, FakeRawLogStore rawLogs) =>
        new(Bc5380, new ConnectionSettings { Kind = ConnectionKind.TcpServer, TcpPort = 5100 }, new Hl7MllpDriver(), _ => link, rawLogs,
            sink, new FakeQueries(), _pendingRoot, TimeProvider.System, _ => { });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Hết giờ chờ");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ResultMessage_IsAcked_IngestedAndLogged()
    {
        var link = new FakeLink();
        var sink = new FakeSink();
        var rawLogs = new FakeRawLogStore();
        await using var runner = Runner(link, sink, rawLogs);
        runner.Start();

        var frame = Frame(Oru);
        link.Incoming.Writer.TryWrite(frame[..10]);
        link.Incoming.Writer.TryWrite(frame[10..]);

        await WaitUntilAsync(() => sink.Readings.Count == 1 && link.Written.Count == 1);

        var reading = sink.Readings.Single();
        reading.InstrumentId.ShouldBe(Bc5380.Id);
        reading.Barcode.ShouldBe("2611080001");
        reading.Items.Select(i => (i.ChannelCode, i.RawValue)).ShouldBe([("6690-2", "9.81"), ("718-7", "135")]);
        Encoding.UTF8.GetString(link.Written.Single()).ShouldContain("MSA|AA|1");

        var log = rawLogs.Entries[Bc5380.Id];
        log.ShouldContain(e => e.Direction == RawLogDirection.In);
        log.ShouldContain(e => e.Direction == RawLogDirection.Out);
        log.ShouldContain(e => e.Direction == RawLogDirection.Event && e.Note!.StartsWith("Nhận kết quả mẫu 2611080001"));
        runner.Status.State.ShouldBe(LinkState.Connected);
        runner.Status.SamplesReceived.ShouldBe(1);
    }

    [Fact]
    public async Task DatabaseDown_ResultSavedToDisk_ThenWrittenOnRetry()
    {
        var link = new FakeLink();
        var sink = new FakeSink { Fail = true };
        var rawLogs = new FakeRawLogStore();
        await using var runner = Runner(link, sink, rawLogs);
        runner.Start();

        link.Incoming.Writer.TryWrite(Frame(Oru));
        await WaitUntilAsync(() => Directory.Exists(runner.PendingDirectory) && Directory.GetFiles(runner.PendingDirectory, "*.json").Length == 1 && runner.Status.PendingToRetry == 1);

        // Máy vẫn được ACK — kết quả không mất dù CSDL đang hỏng.
        Encoding.UTF8.GetString(link.Written.Single()).ShouldContain("MSA|AA|1");
        runner.Status.PendingToRetry.ShouldBe(1);

        sink.Fail = false;
        await runner.RetryPendingAsync();

        sink.Readings.Single().Barcode.ShouldBe("2611080001");
        sink.Readings.Single().Items.Count.ShouldBe(2);
        Directory.GetFiles(runner.PendingDirectory, "*.json").ShouldBeEmpty();
        runner.Status.PendingToRetry.ShouldBe(0);
    }

    private sealed class FakeLink : IByteLink
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
            lock (Written)
            {
                Written.Add(data.ToArray());
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSink : IReadingSink
    {
        public bool Fail { get; set; }

        public List<InstrumentReading> Readings { get; } = [];

        public Task<IngestOutcome> IngestAsync(InstrumentReading reading)
        {
            if (Fail)
            {
                throw new InvalidOperationException("mất CSDL");
            }

            lock (Readings)
            {
                Readings.Add(reading);
            }

            return Task.FromResult(new IngestOutcome(reading.Items.Count, 0, 0, 0));
        }
    }

    private sealed class FakeQueries : IOrderQueryService
    {
        public Task<IReadOnlyList<string>> ChannelsToRunAsync(Guid instrumentId, string barcode, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
