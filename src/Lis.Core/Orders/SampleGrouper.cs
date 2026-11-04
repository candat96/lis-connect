namespace Lis.Core.Orders;

/// <summary>Một dịch vụ/chỉ số đã chọn trong chỉ định, cần gom mẫu theo loại mẫu bệnh phẩm của nó.</summary>
public sealed record OrderedService(Guid ServiceId, Guid SpecimenTypeId);

/// <summary>Một nhóm dịch vụ dùng chung một mẫu (một barcode), cùng loại mẫu bệnh phẩm.</summary>
public sealed record SampleGroup(Guid SpecimenTypeId, IReadOnlyList<Guid> ServiceIds);

/// <summary>
/// Gom các dịch vụ đã chọn trong một chỉ định thành các nhóm mẫu, theo chế độ cấp barcode của
/// cơ sở (<c>facility.barcode_mode</c>): mỗi loại mẫu một barcode (<c>per-specimen</c>, mặc
/// định) hoặc cả phiếu một barcode (<c>single</c>).
/// </summary>
public static class SampleGrouper
{
    public const string PerSpecimen = "per-specimen";
    public const string Single = "single";

    /// <summary>
    /// Gom <paramref name="items"/> thành các <see cref="SampleGroup"/>. Chế độ
    /// <see cref="PerSpecimen"/> gom theo loại mẫu, giữ đúng thứ tự xuất hiện của loại mẫu
    /// trong <paramref name="items"/>. Chế độ <see cref="Single"/> luôn trả về đúng một nhóm,
    /// lấy loại mẫu của dịch vụ đầu tiên (các dịch vụ còn lại theo cùng một mẫu dù loại mẫu
    /// khai báo khác nhau).
    /// </summary>
    public static IReadOnlyList<SampleGroup> Group(IEnumerable<OrderedService> items, string mode)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return [];
        }

        if (mode == Single)
        {
            return [new SampleGroup(list[0].SpecimenTypeId, list.Select(i => i.ServiceId).ToList())];
        }

        // per-specimen: gom theo loại mẫu, giữ thứ tự loại mẫu xuất hiện lần đầu.
        var specimenOrder = new List<Guid>();
        var serviceIdsBySpecimen = new Dictionary<Guid, List<Guid>>();

        foreach (var item in list)
        {
            if (!serviceIdsBySpecimen.TryGetValue(item.SpecimenTypeId, out var serviceIds))
            {
                serviceIds = [];
                serviceIdsBySpecimen[item.SpecimenTypeId] = serviceIds;
                specimenOrder.Add(item.SpecimenTypeId);
            }

            serviceIds.Add(item.ServiceId);
        }

        return specimenOrder.Select(specimenTypeId => new SampleGroup(specimenTypeId, serviceIdsBySpecimen[specimenTypeId])).ToList();
    }
}
