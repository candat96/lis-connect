using Lis.Data.Results;

namespace Lis.App.ViewModels.Results;

/// <summary>Một lựa chọn trạng thái trong ComboBox lọc của màn Kết quả; <see cref="Code"/> null là "Tất cả".</summary>
public sealed record StatusOption(string? Code, string Label)
{
    public static readonly StatusOption All = new(null, "Tất cả");

    public static readonly IReadOnlyList<StatusOption> Options =
    [
        All,
        new(SampleStatus.AwaitingResults, "Chờ kết quả"),
        new(SampleStatus.AwaitingApproval, "Chờ duyệt"),
        new(SampleStatus.Approved, "Đã duyệt"),
    ];

    public override string ToString() => Label;
}
