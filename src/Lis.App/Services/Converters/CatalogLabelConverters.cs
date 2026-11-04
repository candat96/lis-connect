using System.Globalization;
using Avalonia.Data.Converters;
using Lis.App.ViewModels.Catalog;
using Lis.Core.Results;

namespace Lis.App.Services.Converters;

/// <summary>
/// Các converter chỉ đổi CÁCH HIỂN THỊ (không đổi giá trị lưu/bind) sang tiếng Việt có dấu cho
/// những ComboBox trên màn Danh mục mà giá trị gốc là mã kỹ thuật (enum, ký tự "M"/"F") —
/// đúng nguyên tắc "toàn bộ chữ trên giao diện là tiếng Việt có dấu".
/// </summary>
public sealed class AgeUnitLabelConverter : IValueConverter
{
    public static readonly AgeUnitLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AgeUnit unit ? unit.Label() : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ResultFlagLabelConverter : IValueConverter
{
    public static readonly ResultFlagLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ResultFlag flag ? flag.Label() : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"M"/"F"/null (lưu CSDL theo <c>reference_range.sex</c>) → "Nam"/"Nữ"/"Chung" (chung cho cả hai giới — ngắn gọn để vừa cột hẹp, xem CatalogView.axaml).</summary>
public sealed class SexLabelConverter : IValueConverter
{
    public static readonly SexLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "M" => "Nam",
        "F" => "Nữ",
        _ => "Chung",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary><c>service.is_panel</c> (bool) → "Gói (panel)"/"Chỉ số" — dùng ở gợi ý dịch vụ màn Tiếp nhận.</summary>
public sealed class PanelLabelConverter : IValueConverter
{
    public static readonly PanelLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Gói (panel)" : "Chỉ số";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Ghép danh sách tên dịch vụ của một mẫu (<c>CreatedSample.ServiceNames</c>) thành một chuỗi, cách nhau bởi dấu phẩy.</summary>
public sealed class JoinNamesConverter : IValueConverter
{
    public static readonly JoinNamesConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> names ? string.Join(", ", names) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
