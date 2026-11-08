using Lis.Core.Ingest;

namespace Lis.Core.Drivers.Astm;

/// <summary>
/// Driver họ <c>astm-e1394</c>: tầng thấp E1381 (<see cref="AstmLink"/>) + đọc record E1394 thành
/// bản tin kết quả (mỗi record O là một <see cref="DriverReading"/>) và câu hỏi chỉ định (record Q).
/// Biến thể từng máy nằm trong <see cref="AstmDriverOptions"/> (vd <see cref="AstmDriverOptions.Access2"/>).
/// </summary>
public sealed class AstmDriver : IInstrumentDriver
{
    private readonly AstmDriverOptions _options;
    private readonly AstmLink _link;

    public AstmDriver(AstmDriverOptions? options = null)
    {
        _options = options ?? AstmDriverOptions.Generic;
        _link = new AstmLink(_options.Timeouts);
    }

    public string Family => "astm-e1394";

    public DriverOutput Receive(ReadOnlySpan<byte> data, DateTimeOffset now) => Convert(_link.Receive(data, now));

    public DriverOutput Tick(DateTimeOffset now) => Convert(_link.Tick(now));

    public void Reset() => _link.Reset();

    public DriverOutput AnswerQuery(DriverQuery query, IReadOnlyList<string> channelCodes, DateTimeOffset now)
    {
        var context = query.Context as QueryContext;
        var d = context?.Delimiters ?? AstmDelimiters.Default;
        var receiver = context?.Sender ?? "";
        var stamp = now.ToString("yyyyMMddHHmmss");

        // H|\^&|||LIS|||||<máy>||P|1|<giờ>
        var records = new List<string> { $"H{d.Field}{d.Repeat}{d.Component}{d.Escape}{d.Field}{d.Field}{d.Field}{_options.HostName}{d.Field}{d.Field}{d.Field}{d.Field}{d.Field}{receiver}{d.Field}{d.Field}P{d.Field}1{d.Field}{stamp}" };
        var codes = channelCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string note;
        if (codes.Count == 0)
        {
            records.Add($"L{d.Field}1{d.Field}{_options.NoOrderTerminationCode}");
            note = $"Trả lời câu hỏi chỉ định mẫu {query.Barcode}: không có chỉ định";
        }
        else
        {
            var tests = string.Join(d.Repeat, codes.Select(c => $"{d.Component}{d.Component}{d.Component}{d.EscapeValue(c)}"));
            // O|1|<SID>||^^^A\^^^B|R||||||A||||<O.16>||||||||||<O.26>
            var order = new List<string> { "O", "1", d.EscapeValue(query.Barcode), "", tests, "R", "", "", "", "", "", _options.AnswerActionCode };
            if (_options.AnswerReportType.Length > 0)
            {
                while (order.Count < 25)
                    order.Add("");
                order.Add(_options.AnswerReportType);
            }

            records.Add($"P{d.Field}1");
            records.Add(string.Join(d.Field, order));
            records.Add($"L{d.Field}1{d.Field}{_options.AnswerTerminationCode}");
            note = $"Trả lời câu hỏi chỉ định mẫu {query.Barcode}: {string.Join(", ", codes)}";
        }

        var output = Convert(_link.Enqueue(records, now));
        output.Notes.Insert(0, note);
        return output;
    }

    /// <summary>Dữ liệu riêng gắn vào <see cref="DriverQuery.Context"/>: phân cách và tên máy ở H.5 của tin hỏi.</summary>
    private sealed record QueryContext(AstmDelimiters Delimiters, string Sender);

    private DriverOutput Convert(AstmLink.Result result)
    {
        var output = new DriverOutput();
        output.Send.AddRange(result.Send);
        output.Notes.AddRange(result.Notes);
        foreach (var message in result.Messages)
        {
            Interpret(message, output);
        }

        return output;
    }

    /// <summary>Đọc các record của một phiên: H đặt phân cách; mỗi O gom các R (và C ngay sau R) thành một bản tin; Q thành câu hỏi.</summary>
    private void Interpret(IReadOnlyList<string> recordTexts, DriverOutput output)
    {
        var delimiters = AstmDelimiters.Default;
        AstmRecord? header = null;
        AstmRecord? patient = null;
        OrderBuilder? order = null;

        void Flush()
        {
            if (order is not null)
            {
                order.Complete(output);
                order = null;
            }
        }

        foreach (var text in recordTexts)
        {
            var record = AstmRecord.Parse(text, delimiters);
            switch (record.Type)
            {
                case 'H':
                    Flush();
                    delimiters = record.Delimiters;
                    header = record;
                    patient = null;
                    break;

                case 'P':
                    Flush();
                    patient = record;
                    break;

                case 'O':
                    Flush();
                    order = new OrderBuilder(_options, header, patient, record);
                    break;

                case 'R':
                    if (order is null)
                        output.Notes.Add($"Record R không thuộc O nào — bỏ: {text}");
                    else
                        order.AddResult(record);
                    break;

                case 'C':
                    order?.AddComment(record);
                    break;

                case 'Q':
                    Flush();
                    var barcode = record.Component(3, _options.QuerySpecimenComponent);
                    if (barcode.Length == 0)
                        barcode = record.Component(3, 1);
                    if (barcode.Length == 0)
                    {
                        output.Notes.Add($"Máy hỏi chỉ định nhưng không có mã mẫu — bỏ qua: {text}");
                        break;
                    }

                    output.Queries.Add(new DriverQuery(barcode, new object(), new QueryContext(delimiters, header?.Field(5) ?? "")));
                    break;

                case 'L':
                    Flush();
                    break;

                    // M, S và record lạ: bỏ qua.
            }
        }

        Flush();
    }

    /// <summary>Gom một record O và các kết quả dưới nó.</summary>
    private sealed class OrderBuilder(AstmDriverOptions options, AstmRecord? header, AstmRecord? patient, AstmRecord order)
    {
        private readonly List<string> _raw = [.. new[] { header?.Text, patient?.Text, order.Text }.OfType<string>()];
        private readonly List<(string Code, string Value, string? Unit, List<string> Flags)> _items = [];
        private readonly List<string> _messages = [];
        private bool _lastWasResult;

        public void AddResult(AstmRecord r)
        {
            _raw.Add(r.Text);
            _lastWasResult = false;

            var status = r.Value(9);
            if (options.SkipStatuses.Contains(status, StringComparer.OrdinalIgnoreCase)
                || (options.AcceptStatuses.Count > 0 && !options.AcceptStatuses.Contains(status, StringComparer.OrdinalIgnoreCase)))
                return;

            var code = r.Component(3, options.TestIdComponent);
            var value = r.Component(4, options.ValueComponent);
            if (code.Length == 0 || value.Length == 0)
                return;

            var flags = new List<string>();
            var abnormal = r.Value(7);
            if (abnormal.Length > 0 && !abnormal.Equals("N", StringComparison.OrdinalIgnoreCase))
                flags.Add(abnormal);
            if (options.InterpretationComponent is { } ic && r.Component(4, ic) is { Length: > 0 } interpretation)
                flags.Add(interpretation);

            var unit = r.Value(5);
            _items.Add((code, value, unit.Length == 0 ? null : unit, flags));
            _lastWasResult = true;
        }

        public void AddComment(AstmRecord c)
        {
            _raw.Add(c.Text);
            var text = c.Value(4);
            var type = c.Value(5);
            if (options.FlagsFromComment && _lastWasResult && type.Equals("I", StringComparison.OrdinalIgnoreCase) && text.Length > 0)
            {
                _items[^1].Flags.AddRange(text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else if (text.Length > 0)
            {
                _messages.Add(text);
            }
        }

        public void Complete(DriverOutput output)
        {
            var barcode = order.Component(options.SpecimenIdField, options.SpecimenIdComponent);
            var isQc = order.Value(12).Equals(options.QcActionCode, StringComparison.OrdinalIgnoreCase)
                || (header?.Value(12).Equals(options.QcProcessingId, StringComparison.OrdinalIgnoreCase) ?? false);

            foreach (var message in _messages)
                output.Notes.Add($"Máy báo cho mẫu {barcode}: {message}");

            if (_items.Count == 0)
            {
                if (order.Value(26).Equals("X", StringComparison.OrdinalIgnoreCase))
                    output.Notes.Add($"Máy báo không làm được / từ chối mẫu {barcode}");
                return;
            }

            var items = _items
                .Select(i => new ReadingItem(i.Code, i.Value, i.Unit, i.Flags.Count == 0 ? null : string.Join(";", i.Flags.Distinct())))
                .ToList();
            output.Readings.Add(new DriverReading(barcode, isQc, items, string.Join("\r", _raw)));
        }
    }
}
