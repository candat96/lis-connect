using CommunityToolkit.Mvvm.Input;

namespace Lis.App.ViewModels.Catalog;

/// <summary>Vai trò của một dòng trong cây dịch vụ ở màn Danh mục.</summary>
public enum ServiceRowKind
{
    /// <summary>Dịch vụ có chỉ số con (panel), vd "Tổng phân tích tế bào máu".</summary>
    Panel,

    /// <summary>Chỉ số con, hiện thụt vào ngay dưới dịch vụ cha.</summary>
    Child,

    /// <summary>Dịch vụ lẻ — không có chỉ số con và không thuộc dịch vụ nào.</summary>
    Single,
}

/// <summary>
/// Một dòng của bảng dịch vụ dạng cây: dịch vụ cha, rồi các chỉ số con của nó thụt vào bên dưới,
/// cuối cùng là dịch vụ lẻ. Một chỉ số con thuộc nhiều dịch vụ thì hiện dưới mỗi dịch vụ cha
/// (mỗi lần là một dòng riêng, cùng trỏ tới một <see cref="Item"/>).
/// </summary>
public sealed class ServiceRowViewModel(
    ServiceListItemViewModel item,
    ServiceRowKind kind,
    ServiceListItemViewModel? parent = null,
    int childCount = 0,
    bool isLastChild = false,
    bool isExpanded = true,
    Action<ServiceRowViewModel>? toggle = null)
{
    public ServiceListItemViewModel Item { get; } = item;

    public ServiceRowKind Kind { get; } = kind;

    /// <summary>Dịch vụ cha của dòng chỉ số con này (null với dịch vụ cha / dịch vụ lẻ).</summary>
    public ServiceListItemViewModel? Parent { get; } = parent;

    public bool IsPanel => Kind == ServiceRowKind.Panel;

    public bool IsChild => Kind == ServiceRowKind.Child;

    /// <summary>Dịch vụ cha đang mở (hiện chỉ số con bên dưới). Luôn false với dòng không phải dịch vụ cha.</summary>
    public bool IsExpanded { get; } = kind == ServiceRowKind.Panel && isExpanded;

    /// <summary>Đóng/mở chỉ số con của dịch vụ cha này (nút ▾/▸ trên dòng).</summary>
    public IRelayCommand ToggleCommand { get; private set; } = new RelayCommand(() => { }, () => false);

    /// <summary>Ký hiệu cây trước tên: "▾" (đang mở) / "▸" (thu gọn) cho dịch vụ cha, "├─"/"└─" cho chỉ số con.</summary>
    public string TreeMark { get; } = kind switch
    {
        ServiceRowKind.Panel => isExpanded ? "▾" : "▸",
        ServiceRowKind.Child => isLastChild ? "└─" : "├─",
        _ => "",
    };

    public string Code => Item.Code;

    public string Name => Item.Name;

    public string Loai { get; } = kind switch
    {
        ServiceRowKind.Panel => $"Dịch vụ · {childCount} chỉ số con",
        ServiceRowKind.Child => "Chỉ số con",
        _ => "Dịch vụ lẻ",
    };

    public bool Active => Item.Active;

    /// <summary>Gắn lệnh đóng/mở thật sau khi dựng (tham số primary constructor không dùng được trong lambda khởi tạo thuộc tính có <c>this</c>).</summary>
    internal ServiceRowViewModel WithToggle()
    {
        if (toggle is not null && Kind == ServiceRowKind.Panel)
        {
            ToggleCommand = new RelayCommand(() => toggle(this));
        }

        return this;
    }
}
