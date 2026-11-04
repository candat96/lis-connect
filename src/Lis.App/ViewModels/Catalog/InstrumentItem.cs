namespace Lis.App.ViewModels.Catalog;

/// <summary>
/// Một máy xét nghiệm, dùng cho ComboBox chọn máy trên khoảng tham chiếu/quy đổi định tính.
/// <see cref="Any"/> là mục đầu danh sách ("Mọi máy") — chọn mục này nghĩa là không giới hạn
/// theo máy (<c>instrument_id</c> lưu CSDL là <c>null</c>).
/// </summary>
public sealed record InstrumentItem(Guid Id, string Name)
{
    public static readonly InstrumentItem Any = new(Guid.Empty, "Mọi máy");

    public override string ToString() => Name;
}
