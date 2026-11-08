namespace Lis.Core.Drivers.Astm;

/// <summary>Thời gian chờ của tầng thấp E1381 (mặc định theo chuẩn / đặc tả Access 2 §2 Time-outs).</summary>
public sealed record AstmTimeouts
{
    /// <summary>Bên nhận chờ frame kế tiếp / EOT; quá thì bỏ tin dở.</summary>
    public TimeSpan ReceiverFrame { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Bên gửi chờ trả lời cho ENQ hoặc cho một frame; quá thì gửi EOT, giữ tin gửi lại sau.</summary>
    public TimeSpan SenderReply { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Bị NAK khi xin gửi (ENQ) → chờ rồi mới ENQ lại.</summary>
    public TimeSpan AfterEnqNak { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Tranh chấp (cả hai cùng ENQ): LIS nhường, chờ tối đa khoảng này (hoặc tới khi máy gửi xong) rồi thử lại.</summary>
    public TimeSpan AfterContention { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gửi lại một frame tối đa bấy nhiêu lần (lần đầu không tính) trước khi bỏ phiên.</summary>
    public int MaxFrameRetries { get; init; } = 6;

    /// <summary>Một tin gửi xuống máy thử tối đa bấy nhiêu phiên (ENQ bị NAK/hết giờ, frame hỏng) rồi bỏ.</summary>
    public int MaxMessageAttempts { get; init; } = 6;
}

/// <summary>
/// Tầng thấp ASTM E1381 cho một kết nối, thuần logic: nhận dòng byte (frame có thể bị cắt hoặc
/// gộp tùy cổng), kiểm checksum/số frame, ACK/NAK, ghép frame ETB, tách record theo CR; đồng thời
/// gửi các tin LIS xếp hàng (ENQ → frame → EOT) với xử lý NAK, hết giờ và tranh chấp — máy luôn
/// được ưu tiên. Không an toàn đa luồng.
/// </summary>
public sealed class AstmLink(AstmTimeouts? timeouts = null)
{
    private enum State
    {
        Idle,
        Receiving,
        WaitEnqReply,
        WaitFrameReply,
    }

    private sealed class Outgoing(IReadOnlyList<string> records, object? tag)
    {
        public IReadOnlyList<string> Records { get; } = records;

        public object? Tag { get; } = tag;

        public int Attempts { get; set; }
    }

    private readonly AstmTimeouts _timeouts = timeouts ?? new AstmTimeouts();
    private readonly Queue<Outgoing> _queue = new();

    private State _state = State.Idle;
    private DateTimeOffset _deadline;

    // Nhận
    private readonly List<byte> _frame = [];
    private bool _inFrame;
    private int _trailerNeeded; // số byte checksum còn thiếu sau ETB/ETX
    private int _expectedFn = 1;
    private int? _lastAcceptedFn;
    private readonly List<byte> _recordText = [];
    private readonly List<string> _sessionRecords = [];

    // Gửi
    private Outgoing? _current;
    private List<byte[]> _currentFrames = [];
    private int _frameIndex;
    private int _frameRetries;
    private DateTimeOffset? _notBefore;

    /// <summary>Đang có tin chờ gửi hoặc đang gửi.</summary>
    public bool HasPendingSend => _current is not null || _queue.Count > 0;

    /// <summary>Đầu ra của một lần gọi tầng thấp.</summary>
    public sealed class Result
    {
        public List<byte[]> Send { get; } = [];

        /// <summary>Mỗi phần tử là toàn bộ record của một phiên nhận trọn vẹn (kết thúc bằng EOT).</summary>
        public List<IReadOnlyList<string>> Messages { get; } = [];

        public List<string> Notes { get; } = [];

        /// <summary>Tin đã gửi xong (máy ACK frame cuối) — trả lại Tag lúc xếp hàng.</summary>
        public List<object?> Delivered { get; } = [];
    }

    /// <summary>Xếp hàng một tin (danh sách record, không có CR) để gửi xuống máy; mở phiên ngay nếu đường rảnh.</summary>
    public Result Enqueue(IReadOnlyList<string> records, DateTimeOffset now, object? tag = null)
    {
        var result = new Result();
        _queue.Enqueue(new Outgoing(records, tag));
        TryStartSend(now, result);
        return result;
    }

    /// <summary>Bỏ mọi trạng thái nửa chừng (kết nối mở lại/đứt); tin đang gửi dở được giữ lại để gửi lại.</summary>
    public void Reset()
    {
        if (_current is not null)
        {
            RequeueFront(_current);
            _current = null;
        }

        _state = State.Idle;
        _notBefore = null;
        ResetReceive();
    }

    public Result Receive(ReadOnlySpan<byte> data, DateTimeOffset now)
    {
        var result = new Result();
        foreach (var b in data)
        {
            switch (_state)
            {
                case State.Idle:
                    if (b == AstmFraming.Enq)
                        BeginReceive(now, result);
                    // Rảnh: bỏ qua mọi ký tự khác ENQ (rác, CR/LF thừa, XON/XOFF).
                    break;

                case State.Receiving:
                    ReceiveByte(b, now, result);
                    break;

                case State.WaitEnqReply:
                    if (b == AstmFraming.Ack)
                    {
                        _frameIndex = 0;
                        _frameRetries = 0;
                        SendCurrentFrame(now, result);
                    }
                    else if (b == AstmFraming.Nak)
                    {
                        result.Notes.Add($"Máy chưa sẵn sàng (NAK cho ENQ) — thử lại sau {_timeouts.AfterEnqNak.TotalSeconds:0} giây");
                        AbortCurrent(now, _timeouts.AfterEnqNak, result, sendEot: false);
                    }
                    else if (b == AstmFraming.Enq)
                    {
                        // Tranh chấp: máy được ưu tiên — nhường, nhận tin của máy trước.
                        result.Notes.Add("Tranh chấp đường truyền (cả hai cùng ENQ) — LIS nhường máy");
                        RequeueFront(_current!);
                        _current = null;
                        _notBefore = now + _timeouts.AfterContention;
                        BeginReceive(now, result);
                    }

                    break;

                case State.WaitFrameReply:
                    if (b == AstmFraming.Ack || b == AstmFraming.Eot)
                    {
                        // EOT thay ACK = máy muốn dừng; vẫn coi frame đã nhận, gửi nốt tin hiện tại.
                        _frameIndex++;
                        _frameRetries = 0;
                        if (_frameIndex < _currentFrames.Count)
                        {
                            SendCurrentFrame(now, result);
                        }
                        else
                        {
                            result.Send.Add([AstmFraming.Eot]);
                            result.Delivered.Add(_current!.Tag);
                            _current = null;
                            _state = State.Idle;
                            TryStartSend(now, result);
                        }
                    }
                    else if (b == AstmFraming.Nak)
                    {
                        _frameRetries++;
                        if (_frameRetries > _timeouts.MaxFrameRetries)
                        {
                            result.Notes.Add($"Máy NAK frame quá {_timeouts.MaxFrameRetries} lần — bỏ phiên, gửi lại tin sau");
                            AbortCurrent(now, _timeouts.AfterEnqNak, result, sendEot: true);
                        }
                        else
                        {
                            result.Notes.Add($"Máy NAK frame — gửi lại lần {_frameRetries}");
                            SendCurrentFrame(now, result);
                        }
                    }

                    // Ký tự khác (CR/LF/rác) bỏ qua: gửi lại vì nhiễu dễ sinh frame trùng hơn là có ích.
                    break;
            }
        }

        return result;
    }

    public Result Tick(DateTimeOffset now)
    {
        var result = new Result();
        switch (_state)
        {
            case State.Receiving when now >= _deadline:
                result.Notes.Add($"Máy im quá {_timeouts.ReceiverFrame.TotalSeconds:0} giây giữa phiên — bỏ tin dở ({_sessionRecords.Count} record)");
                ResetReceive();
                _state = State.Idle;
                TryStartSend(now, result);
                break;

            case State.WaitEnqReply or State.WaitFrameReply when now >= _deadline:
                result.Notes.Add($"Máy không trả lời quá {_timeouts.SenderReply.TotalSeconds:0} giây — gửi EOT, giữ tin gửi lại sau");
                AbortCurrent(now, _timeouts.AfterEnqNak, result, sendEot: true);
                break;

            case State.Idle:
                TryStartSend(now, result);
                break;
        }

        return result;
    }

    private void BeginReceive(DateTimeOffset now, Result result)
    {
        ResetReceive();
        _state = State.Receiving;
        _deadline = now + _timeouts.ReceiverFrame;
        result.Send.Add([AstmFraming.Ack]);
    }

    private void ResetReceive()
    {
        _frame.Clear();
        _inFrame = false;
        _trailerNeeded = 0;
        _expectedFn = 1;
        _lastAcceptedFn = null;
        _recordText.Clear();
        _sessionRecords.Clear();
    }

    private void ReceiveByte(byte b, DateTimeOffset now, Result result)
    {
        if (!_inFrame)
        {
            switch (b)
            {
                case AstmFraming.Stx:
                    _inFrame = true;
                    _frame.Clear();
                    _deadline = now + _timeouts.ReceiverFrame;
                    break;

                case AstmFraming.Eot:
                    if (_recordText.Count > 0)
                    {
                        result.Notes.Add("EOT khi record còn dở (frame ETB chưa có frame cuối) — bỏ phần dở");
                    }

                    if (_sessionRecords.Count > 0)
                    {
                        result.Messages.Add(_sessionRecords.ToList());
                    }

                    ResetReceive();
                    _state = State.Idle;
                    // Máy vừa gửi xong: nếu LIS có tin chờ (vd trả lời câu hỏi) thì xin gửi ngay.
                    _notBefore = null;
                    TryStartSend(now, result);
                    break;

                case AstmFraming.Enq:
                    result.Notes.Add("ENQ giữa phiên — máy mở lại phiên, bỏ phần đã nhận");
                    BeginReceive(now, result);
                    break;

                    // CR/LF đuôi frame, rác: bỏ qua.
            }

            return;
        }

        if (b == AstmFraming.Stx)
        {
            result.Notes.Add("STX giữa frame — bỏ frame dở, bắt đầu frame mới");
            _frame.Clear();
            _trailerNeeded = 0;
            return;
        }

        _frame.Add(b);
        if (_trailerNeeded > 0)
        {
            _trailerNeeded--;
            if (_trailerNeeded == 0)
            {
                CompleteFrame(now, result);
            }

            return;
        }

        if (b == AstmFraming.Etx || b == AstmFraming.Etb)
        {
            _trailerNeeded = 2;
        }
        else if (_frame.Count > AstmFraming.MaxFrameText + 1 + 8)
        {
            // Quá dài mà chưa thấy ETX/ETB: frame hỏng (mất ký tự kết thúc).
            result.Notes.Add("Frame quá dài, không thấy ETX/ETB — NAK");
            _inFrame = false;
            _frame.Clear();
            result.Send.Add([AstmFraming.Nak]);
        }
    }

    private void CompleteFrame(DateTimeOffset now, Result result)
    {
        _inFrame = false;
        _deadline = now + _timeouts.ReceiverFrame;

        // _frame = FN + text + ETB/ETX + C1 + C2
        var bytes = _frame.ToArray();
        _frame.Clear();
        var bodyLength = bytes.Length - 2;
        var fnChar = bytes[0];
        var expected = AstmFraming.Checksum(bytes.AsSpan(0, bodyLength));
        var received = AstmFraming.TextEncoding.GetString(bytes, bodyLength, 2);
        if (!string.Equals(expected, received, StringComparison.OrdinalIgnoreCase))
        {
            result.Notes.Add($"Checksum sai frame {(char)fnChar} (nhận {received}, tính {expected}) — NAK");
            result.Send.Add([AstmFraming.Nak]);
            return;
        }

        if (fnChar < '0' || fnChar > '7')
        {
            result.Notes.Add($"Số frame không hợp lệ '{(char)fnChar}' — NAK");
            result.Send.Add([AstmFraming.Nak]);
            return;
        }

        var fn = fnChar - '0';
        if (fn == _lastAcceptedFn)
        {
            // Máy gửi lại frame vừa ACK (ACK bị mất): ACK lại, bỏ nội dung trùng.
            result.Notes.Add($"Frame {fn} trùng — ACK lại, bỏ nội dung");
            result.Send.Add([AstmFraming.Ack]);
            return;
        }

        if (fn != _expectedFn)
        {
            result.Notes.Add($"Sai số frame (nhận {fn}, chờ {_expectedFn}) — NAK");
            result.Send.Add([AstmFraming.Nak]);
            return;
        }

        _lastAcceptedFn = fn;
        _expectedFn = (fn + 1) % 8;
        var last = bytes[bodyLength - 1] == AstmFraming.Etx;
        _recordText.AddRange(bytes.AsSpan(1, bodyLength - 2).ToArray());
        if (last)
        {
            var text = AstmFraming.TextEncoding.GetString(_recordText.ToArray());
            _recordText.Clear();
            foreach (var record in text.Split('\r'))
            {
                var trimmed = record.Trim('\n');
                if (trimmed.Length > 0)
                {
                    _sessionRecords.Add(trimmed);
                }
            }
        }

        result.Send.Add([AstmFraming.Ack]);
    }

    private void TryStartSend(DateTimeOffset now, Result result)
    {
        if (_state != State.Idle || _current is not null || _queue.Count == 0)
            return;
        if (_notBefore is { } notBefore && now < notBefore)
            return;

        _notBefore = null;
        _current = _queue.Dequeue();
        _current.Attempts++;

        var frames = new List<byte[]>();
        var fn = 1;
        foreach (var record in _current.Records)
        {
            frames.AddRange(AstmFraming.BuildFrames(record, fn, out fn));
        }

        _currentFrames = frames;
        _state = State.WaitEnqReply;
        _deadline = now + _timeouts.SenderReply;
        result.Send.Add([AstmFraming.Enq]);
    }

    private void SendCurrentFrame(DateTimeOffset now, Result result)
    {
        _state = State.WaitFrameReply;
        _deadline = now + _timeouts.SenderReply;
        result.Send.Add(_currentFrames[_frameIndex]);
    }

    private void AbortCurrent(DateTimeOffset now, TimeSpan wait, Result result, bool sendEot)
    {
        if (sendEot)
        {
            result.Send.Add([AstmFraming.Eot]);
        }

        var current = _current!;
        _current = null;
        _state = State.Idle;
        if (current.Attempts >= _timeouts.MaxMessageAttempts)
        {
            result.Notes.Add($"Bỏ tin gửi xuống máy sau {current.Attempts} lần thử không thành");
        }
        else
        {
            RequeueFront(current);
        }

        _notBefore = now + wait;
    }

    private void RequeueFront(Outgoing outgoing)
    {
        var rest = _queue.ToArray();
        _queue.Clear();
        _queue.Enqueue(outgoing);
        foreach (var item in rest)
        {
            _queue.Enqueue(item);
        }
    }
}
