using System.Text;

namespace Lis.Core.Drivers.Hl7;

/// <summary>
/// Bản tin HL7 v2 đã tách segment/trường — chỉ đủ dùng cho driver (đọc trường theo số thứ tự
/// HL7, bỏ thoát ký tự). Ký tự phân cách lấy từ chính MSH của bản tin, không giả định "|^~\&amp;".
/// </summary>
internal sealed class Hl7Message
{
    private Hl7Message(char field, string encoding, List<string[]> segments, string text)
    {
        FieldSeparator = field;
        EncodingCharacters = encoding;
        Segments = segments;
        Text = text;
    }

    public char FieldSeparator { get; }

    /// <summary>MSH-2: thành phần, lặp, thoát, thành phần con (mặc định "^~\&amp;").</summary>
    public string EncodingCharacters { get; }

    public char ComponentSeparator => EncodingCharacters.Length > 0 ? EncodingCharacters[0] : '^';

    public char Repetition => EncodingCharacters.Length > 1 ? EncodingCharacters[1] : '~';

    public char Escape => EncodingCharacters.Length > 2 ? EncodingCharacters[2] : '\\';

    public char SubComponent => EncodingCharacters.Length > 3 ? EncodingCharacters[3] : '&';

    /// <summary>Mỗi segment là mảng trường thô; phần tử 0 là tên segment.</summary>
    public List<string[]> Segments { get; }

    /// <summary>Văn bản bản tin, các segment ngăn bằng "\r".</summary>
    public string Text { get; }

    /// <summary>Tách bản tin; null nếu không bắt đầu bằng MSH hợp lệ.</summary>
    public static Hl7Message? Parse(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim('\0'))
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("MSH", StringComparison.Ordinal) || lines[0].Length < 8)
        {
            return null;
        }

        var field = lines[0][3];
        var encodingEnd = lines[0].IndexOf(field, 4);
        var encoding = encodingEnd < 0 ? lines[0][4..] : lines[0][4..encodingEnd];
        if (encoding.Length == 0)
        {
            return null;
        }

        var segments = new List<string[]>();
        foreach (var line in lines)
        {
            var parts = line.Split(field);
            if (parts[0] == "MSH")
            {
                // Trong MSH, MSH-1 chính là ký tự phân cách: chèn lại để số thứ tự trường khớp chuẩn HL7.
                var shifted = new string[parts.Length + 1];
                shifted[0] = "MSH";
                shifted[1] = field.ToString();
                Array.Copy(parts, 1, shifted, 2, parts.Length - 1);
                parts = shifted;
            }

            segments.Add(parts);
        }

        return new Hl7Message(field, encoding, segments, string.Join("\r", lines));
    }

    public string[] Msh => Segments[0];

    /// <summary>Trường thô (chưa bỏ thoát) theo số HL7 của segment; "" nếu không có.</summary>
    public static string Field(string[] segment, int index) => index < segment.Length ? segment[index] : "";

    /// <summary>Thành phần (đếm từ 1) của một trường, đã bỏ thoát.</summary>
    public string Comp(string[] segment, int field, int component)
    {
        var parts = Field(segment, field).Split(ComponentSeparator);
        return component - 1 < parts.Length ? Unescape(parts[component - 1]) : "";
    }

    /// <summary>Cả trường, đã bỏ thoát (giữ nguyên ký tự phân cách thành phần).</summary>
    public string Value(string[] segment, int field) => Unescape(Field(segment, field));

    /// <summary>Bỏ thoát \F\ \S\ \T\ \R\ \E\ \.br\ — chuỗi thoát lạ thì giữ nguyên.</summary>
    public string Unescape(string value)
    {
        var escape = Escape;
        if (value.IndexOf(escape) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            var end = value[i] == escape ? value.IndexOf(escape, i + 1) : -1;
            if (end < 0)
            {
                builder.Append(value[i]);
                i++;
                continue;
            }

            var code = value[(i + 1)..end];
            builder.Append(code switch
            {
                "F" => FieldSeparator.ToString(),
                "S" => ComponentSeparator.ToString(),
                "T" => SubComponent.ToString(),
                "R" => Repetition.ToString(),
                "E" => escape.ToString(),
                ".br" => "\n",
                _ => value[i..(end + 1)],
            });
            i = end + 1;
        }

        return builder.ToString();
    }

    /// <summary>Thoát ký tự đặc biệt khi LIS ghi chuỗi vào bản tin gửi máy.</summary>
    public static string EscapeText(string value, char field = '|', string encoding = "^~\\&")
    {
        var escape = encoding[2];
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == escape) builder.Append(escape).Append('E').Append(escape);
            else if (c == field) builder.Append(escape).Append('F').Append(escape);
            else if (c == encoding[0]) builder.Append(escape).Append('S').Append(escape);
            else if (c == encoding[1]) builder.Append(escape).Append('R').Append(escape);
            else if (encoding.Length > 3 && c == encoding[3]) builder.Append(escape).Append('T').Append(escape);
            else if (c == '\r' || c == '\n') builder.Append(escape).Append(".br").Append(escape);
            else builder.Append(c);
        }

        return builder.ToString();
    }
}
