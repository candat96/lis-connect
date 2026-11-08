using System.Text;

namespace Lis.Core.Drivers.Astm;

/// <summary>
/// Bốn ký tự phân cách của E1394 (trường, lặp, thành phần, thoát) — luôn đọc từ record H
/// (<c>H|\^&amp;</c>), không cố định cứng.
/// </summary>
public sealed record AstmDelimiters(char Field, char Repeat, char Component, char Escape)
{
    public static readonly AstmDelimiters Default = new('|', '\\', '^', '&');

    /// <summary>Đọc phân cách từ một record H; record không hợp lệ thì trả <see cref="Default"/>.</summary>
    public static AstmDelimiters FromHeader(string header) =>
        header.Length >= 5 && header[0] == 'H'
            ? new AstmDelimiters(header[1], header[2], header[3], header[4])
            : Default;

    /// <summary>Thoát các ký tự phân cách trong một giá trị trước khi gửi xuống máy (<c>&amp;F&amp;</c>, <c>&amp;S&amp;</c>…).</summary>
    public string EscapeValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == Escape)
                sb.Append(Escape).Append('E').Append(Escape);
            else if (c == Field)
                sb.Append(Escape).Append('F').Append(Escape);
            else if (c == Component)
                sb.Append(Escape).Append('S').Append(Escape);
            else if (c == Repeat)
                sb.Append(Escape).Append('R').Append(Escape);
            else
                sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Bỏ thoát <c>&amp;F&amp; &amp;S&amp; &amp;R&amp; &amp;E&amp;</c>; các chuỗi thoát khác giữ nguyên.</summary>
    public string UnescapeValue(string value)
    {
        if (value.IndexOf(Escape) < 0)
            return value;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == Escape && i + 2 < value.Length && value[i + 2] == Escape)
            {
                char? replacement = value[i + 1] switch
                {
                    'F' => Field,
                    'S' => Component,
                    'R' => Repeat,
                    'E' => Escape,
                    _ => null,
                };
                if (replacement is { } r)
                {
                    sb.Append(r);
                    i += 2;
                    continue;
                }
            }

            sb.Append(value[i]);
        }

        return sb.ToString();
    }
}

/// <summary>
/// Một record E1394 đã tách trường. Trường đánh số từ 1 như tài liệu (trường 1 = mã record,
/// vd <c>R</c>); trường không có trả chuỗi rỗng. Với record H, trường 2 là chuỗi phân cách
/// (không tách theo phân cách).
/// </summary>
public sealed class AstmRecord
{
    private readonly string[] _fields;

    private AstmRecord(string text, AstmDelimiters delimiters, string[] fields)
    {
        Text = text;
        Delimiters = delimiters;
        _fields = fields;
    }

    /// <summary>Chuỗi gốc của record (không có CR).</summary>
    public string Text { get; }

    public AstmDelimiters Delimiters { get; }

    /// <summary>Mã loại record viết hoa (<c>H P O R C Q L M S</c>); chuỗi rỗng nếu record trống.</summary>
    public char Type => Text.Length == 0 ? '\0' : char.ToUpperInvariant(Text[0]);

    public int FieldCount => _fields.Length;

    public static AstmRecord Parse(string text, AstmDelimiters delimiters)
    {
        if (text.Length > 0 && char.ToUpperInvariant(text[0]) == 'H' && text.Length >= 5)
        {
            // H|\^&|... — trường 2 là bộ phân cách, phải giữ nguyên chứ không tách.
            delimiters = AstmDelimiters.FromHeader(text);
            var rest = text.Length > 5 ? text[6..].Split(delimiters.Field) : [];
            string[] fields = [text[..1], text.Substring(2, 3), .. rest];
            return new AstmRecord(text, delimiters, fields);
        }

        return new AstmRecord(text, delimiters, text.Split(delimiters.Field));
    }

    /// <summary>Trường thứ <paramref name="index"/> (từ 1), chưa bỏ thoát, chưa tách thành phần.</summary>
    public string Field(int index) => index >= 1 && index <= _fields.Length ? _fields[index - 1] : "";

    /// <summary>
    /// Thành phần thứ <paramref name="component"/> (từ 1) của lần lặp đầu tiên trong trường
    /// <paramref name="index"/>, đã bỏ thoát và cắt khoảng trắng hai đầu.
    /// </summary>
    public string Component(int index, int component)
    {
        var firstRepeat = Field(index).Split(Delimiters.Repeat)[0];
        var parts = firstRepeat.Split(Delimiters.Component);
        return component >= 1 && component <= parts.Length ? Delimiters.UnescapeValue(parts[component - 1]).Trim() : "";
    }

    /// <summary>Số thành phần trong lần lặp đầu của trường.</summary>
    public int ComponentCount(int index) => Field(index).Split(Delimiters.Repeat)[0].Split(Delimiters.Component).Length;

    /// <summary>Giá trị cả trường (lần lặp đầu), đã bỏ thoát và cắt khoảng trắng.</summary>
    public string Value(int index) => Delimiters.UnescapeValue(Field(index).Split(Delimiters.Repeat)[0]).Trim();
}
