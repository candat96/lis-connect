using System.Collections.ObjectModel;

namespace Lis.App.ViewModels;

/// <summary>
/// Đồng bộ một danh sách đang hiển thị với dữ liệu mới mà không Clear() — giữ nguyên thể hiện
/// ViewModel của các dòng còn lại để dòng đang chọn không bị bỏ chọn (tự tải lại ngầm định kỳ
/// không được làm người dùng mất chỗ đang xem / đang sửa).
/// </summary>
public static class CollectionSync
{
    public static void Sync<TViewModel, TItem>(
        ObservableCollection<TViewModel> target,
        IReadOnlyList<TItem> items,
        Func<TViewModel, Guid> viewModelKey,
        Func<TItem, Guid> itemKey,
        Func<TItem, TViewModel> create,
        Action<TViewModel, TItem>? update = null)
    {
        var wanted = items.Select(itemKey).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(viewModelKey(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            var key = itemKey(items[i]);
            if (i < target.Count && viewModelKey(target[i]) == key)
            {
                update?.Invoke(target[i], items[i]);
                continue;
            }

            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (viewModelKey(target[j]) == key)
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                target.Move(existing, i);
                update?.Invoke(target[i], items[i]);
            }
            else
            {
                target.Insert(i, create(items[i]));
            }
        }
    }
}
