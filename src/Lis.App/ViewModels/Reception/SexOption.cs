namespace Lis.App.ViewModels.Reception;

/// <summary>Một lựa chọn giới tính người bệnh cho ComboBox (<c>patient.sex</c>: 'M'/'F'/'O').</summary>
public sealed record SexOption(char Code, string Label)
{
    public override string ToString() => Label;

    public static readonly SexOption Male = new('M', "Nam");
    public static readonly SexOption Female = new('F', "Nữ");
    public static readonly SexOption Other = new('O', "Khác");

    public static readonly IReadOnlyList<SexOption> All = [Male, Female, Other];

    public static SexOption FromCode(char code) => All.First(o => o.Code == code);
}
