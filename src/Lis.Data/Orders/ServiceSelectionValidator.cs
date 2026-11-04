using Lis.Data.Catalog;

namespace Lis.Data.Orders;

/// <summary>
/// Kiểm tra danh sách dịch vụ đã chọn cho một chỉ định — dùng chung cho cả
/// <see cref="OrderService.CreateAsync"/> (trước khi ghi CSDL) lẫn màn Tiếp nhận (ngay khi
/// thêm dịch vụ), để không bao giờ tạo ra hai dòng <c>order_item</c> cho cùng một chỉ số trên
/// cùng một mẫu. Thuần túy, không chạm CSDL.
/// </summary>
public static class ServiceSelectionValidator
{
    /// <summary>
    /// Ném <see cref="InvalidOperationException"/> (thông điệp tiếng Việt, nêu tên dịch vụ) khi:
    /// một dịch vụ không có trong danh mục; một dịch vụ/gói được chọn hai lần; một gói chưa có
    /// chỉ số con; hoặc sau khi tách gói ra chỉ số con, một chỉ số xuất hiện nhiều lần (chọn
    /// lẻ một chỉ số đã nằm trong gói, hai gói dùng chung chỉ số).
    /// </summary>
    public static void Validate(IEnumerable<Guid> selectedIds, IReadOnlyDictionary<Guid, ServiceInfo> servicesById)
    {
        var topLevelSeen = new HashSet<Guid>();

        // Chỉ số lá -> gói chứa nó trong lựa chọn này (null nếu chọn lẻ, không qua gói).
        var ownerByLeaf = new Dictionary<Guid, ServiceInfo?>();

        foreach (var id in selectedIds)
        {
            if (!servicesById.TryGetValue(id, out var service))
            {
                throw new InvalidOperationException("Dịch vụ không tồn tại trong danh mục");
            }

            if (!topLevelSeen.Add(id))
            {
                throw new InvalidOperationException($"Dịch vụ '{service.Name}' đã được chọn hai lần");
            }

            if (!service.IsPanel)
            {
                Claim(service.Id, panel: null, ownerByLeaf, servicesById);
                continue;
            }

            if (service.ChildIds.Count == 0)
            {
                throw new InvalidOperationException($"Gói '{service.Code}' chưa có chỉ số con");
            }

            foreach (var childId in service.ChildIds)
            {
                Claim(childId, service, ownerByLeaf, servicesById);
            }
        }
    }

    private static void Claim(
        Guid leafId,
        ServiceInfo? panel,
        Dictionary<Guid, ServiceInfo?> ownerByLeaf,
        IReadOnlyDictionary<Guid, ServiceInfo> servicesById)
    {
        if (ownerByLeaf.TryAdd(leafId, panel))
        {
            return;
        }

        var leafName = servicesById.TryGetValue(leafId, out var leaf) ? leaf.Name : leafId.ToString();
        var previous = ownerByLeaf[leafId];

        throw new InvalidOperationException((previous, panel) switch
        {
            (null, null) => $"Dịch vụ '{leafName}' đã được chọn hai lần",
            (null, { } p) => $"Chỉ số '{leafName}' đã nằm trong gói '{p.Name}'",
            ({ } p, null) => $"Chỉ số '{leafName}' đã nằm trong gói '{p.Name}'",
            ({ } p1, { } p2) when p1.Id == p2.Id => $"Gói '{p1.Name}' chứa chỉ số '{leafName}' hai lần",
            ({ } p1, { } p2) => $"Chỉ số '{leafName}' nằm trong cả gói '{p1.Name}' và gói '{p2.Name}'",
        });
    }
}
