namespace Lis.App.ViewModels.Catalog;

/// <summary>Một loại mẫu bệnh phẩm, dùng cho ComboBox chọn loại mẫu của dịch vụ.</summary>
public sealed record SpecimenTypeItem(Guid Id, string Code, string Name)
{
    public override string ToString() => $"{Code} — {Name}";
}

/// <summary>Một lựa chọn kiểu giá trị của dịch vụ (cột <c>value_type</c>).</summary>
public sealed record ValueTypeOption(string Code, string Label)
{
    public override string ToString() => Label;

    public static readonly ValueTypeOption So = new("so", "Số");
    public static readonly ValueTypeOption DinhTinh = new("dinh-tinh", "Định tính");
    public static readonly ValueTypeOption Chu = new("chu", "Chữ");

    public static readonly IReadOnlyList<ValueTypeOption> All = [So, DinhTinh, Chu];
}
