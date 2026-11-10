using Lis.Core.Drivers.Astm;

namespace Lis.Simulator.Messages;

/// <summary>
/// Phía nhận của ASTM E1381 (khi LIS gửi chỉ định xuống máy): ENQ → ACK, mỗi khung hợp lệ → ACK
/// (sai checksum/số khung → NAK), EOT = hết phiên. Thuần logic, không IO.
/// </summary>
public sealed class AstmReceiver
{
    private readonly List<byte> _frame = [];
    private readonly List<byte> _text = [];
    private bool _inFrame;
    private int _trailer;
    private int _expectedFn = 1;

    /// <summary>Các record đã nhận trọn (không có CR).</summary>
    public List<string> Records { get; } = [];

    /// <summary>Đã gặp EOT.</summary>
    public bool Finished { get; private set; }

    /// <summary>Đưa byte nhận được vào; trả về các byte cần gửi lại LIS (ACK/NAK), mỗi phần tử một lần trả lời.</summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        var replies = new List<byte[]>();
        foreach (var b in data)
        {
            if (!_inFrame)
            {
                switch (b)
                {
                    case AstmFraming.Enq:
                        Reset();
                        replies.Add([AstmFraming.Ack]);
                        break;
                    case AstmFraming.Stx:
                        _inFrame = true;
                        _frame.Clear();
                        break;
                    case AstmFraming.Eot:
                        Finished = true;
                        break;
                }

                continue;
            }

            _frame.Add(b);
            if (_trailer > 0)
            {
                if (--_trailer == 0)
                {
                    _inFrame = false;
                    replies.Add([Complete() ? AstmFraming.Ack : AstmFraming.Nak]);
                }
            }
            else if (b is AstmFraming.Etx or AstmFraming.Etb)
            {
                _trailer = 2;
            }
        }

        return replies;
    }

    private void Reset()
    {
        _frame.Clear();
        _text.Clear();
        _inFrame = false;
        _trailer = 0;
        _expectedFn = 1;
        Records.Clear();
        Finished = false;
    }

    private bool Complete()
    {
        var bytes = _frame.ToArray();
        _frame.Clear();
        var bodyLength = bytes.Length - 2;
        var checksum = AstmFraming.TextEncoding.GetString(bytes, bodyLength, 2);
        if (!string.Equals(checksum, AstmFraming.Checksum(bytes.AsSpan(0, bodyLength)), StringComparison.OrdinalIgnoreCase)
            || bytes[0] - '0' != _expectedFn)
        {
            return false;
        }

        _expectedFn = (_expectedFn + 1) % 8;
        _text.AddRange(bytes.AsSpan(1, bodyLength - 2).ToArray());
        if (bytes[bodyLength - 1] == AstmFraming.Etx)
        {
            foreach (var record in AstmFraming.TextEncoding.GetString(_text.ToArray()).Split('\r'))
            {
                var trimmed = record.Trim('\n');
                if (trimmed.Length > 0)
                {
                    Records.Add(trimmed);
                }
            }

            _text.Clear();
        }

        return true;
    }
}
