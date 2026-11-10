using Lis.Core.Drivers;
using Lis.Simulator.Links;

namespace Lis.Simulator.Tests;

/// <summary>
/// Đường nối trong bộ nhớ: byte simulator ghi đi vào <c>driver.Receive</c>, byte driver muốn trả
/// (<c>DriverOutput.Send</c>) xếp hàng cho simulator đọc. Câu hỏi chỉ định được trả lời ngay bằng
/// <c>answerQuery</c> (giống lớp chạy kết nối thật tra CSDL rồi gọi <c>AnswerQuery</c>).
/// </summary>
internal sealed class DriverLoopbackLink(IInstrumentDriver driver, Func<DriverQuery, IReadOnlyList<string>>? answerQuery = null) : IMachineLink
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.FromHours(7));

    private readonly List<byte> _pending = [];

    public List<DriverReading> Readings { get; } = [];

    public List<DriverQuery> Queries { get; } = [];

    public List<string> Notes { get; } = [];

    public Task WriteAsync(byte[] data, CancellationToken ct)
    {
        Absorb(driver.Receive(data, Now));
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        var data = _pending.ToArray();
        _pending.Clear();
        return Task.FromResult(data);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void Absorb(DriverOutput output)
    {
        foreach (var chunk in output.Send)
        {
            _pending.AddRange(chunk);
        }

        Readings.AddRange(output.Readings);
        Notes.AddRange(output.Notes);
        foreach (var query in output.Queries)
        {
            Queries.Add(query);
            if (answerQuery is not null)
            {
                Absorb(driver.AnswerQuery(query, answerQuery(query), Now));
            }
        }
    }
}
