using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lis.Data.Results;

namespace Lis.App.ViewModels.Results;

/// <summary>Một dòng trong danh sách mẫu bên trái màn Kết quả; <see cref="Update"/> thay dữ liệu tại chỗ sau mỗi thao tác.</summary>
public partial class SampleListItemViewModel(SampleListItem item, TimeZoneInfo timeZone) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Barcode))]
    [NotifyPropertyChangedFor(nameof(PatientName))]
    [NotifyPropertyChangedFor(nameof(SpecimenName))]
    [NotifyPropertyChangedFor(nameof(OrderedAtText))]
    [NotifyPropertyChangedFor(nameof(Progress))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsApproved))]
    [NotifyPropertyChangedFor(nameof(HasAbnormal))]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    private SampleListItem _item = item;

    public Guid SampleId => Item.SampleId;

    public string Barcode => Item.Barcode;

    public string PatientName => Item.PatientName;

    public string SpecimenName => Item.SpecimenName;

    public string OrderedAtText =>
        TimeZoneInfo.ConvertTime(Item.OrderedAt, timeZone).ToString("HH:mm dd/MM", CultureInfo.InvariantCulture);

    /// <summary>"đã có giá trị / tổng chỉ số", vd "3/5".</summary>
    public string Progress => $"{Item.WithValue}/{Item.Total}";

    public bool IsApproved => Item.Approved;

    public string StatusLabel => Item.Approved
        ? "Đã duyệt"
        : Item.Total > 0 && Item.WithValue == Item.Total ? "Chờ duyệt" : "Chờ kết quả";

    public bool HasAbnormal => Item.HasAbnormal;

    public bool HasPending => Item.HasPending;

    public void Update(SampleListItem item) => Item = item;
}
