using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Lis.App.Services;

/// <summary>
/// Kéo giãn độ rộng cột cho mọi bảng kiểu <c>Border.table</c> của app (bảng = một Grid tiêu đề
/// + ListBox/ItemsControl mà mỗi dòng là một Grid cùng <c>ColumnDefinitions</c>): rê chuột tới
/// mép phải ô tiêu đề thì con trỏ đổi thành ↔, kéo để đổi độ rộng; các dòng (kể cả dòng cuộn
/// tới sau) đổi theo. Cột <c>*</c> tự co giãn phần còn lại. Bật cho cả app bằng một Style trong
/// Theme.axaml (<c>TableColumnResize.IsEnabled</c>).
/// </summary>
public sealed class TableColumnResize
{
    private const double GripWidth = 5;
    private const double MinColumnWidth = 36;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TableColumnResize, Border, bool>("IsEnabled");

    private static readonly AttachedProperty<TableColumnResize?> StateProperty =
        AvaloniaProperty.RegisterAttached<TableColumnResize, Border, TableColumnResize?>("State");

    private readonly Grid _header;
    private readonly ItemsControl? _items;
    private int _dragColumn = -1;
    private double _dragStartX;
    private double _dragStartWidth;

    static TableColumnResize()
    {
        IsEnabledProperty.Changed.AddClassHandler<Border>((border, e) =>
        {
            if (e.NewValue is true)
            {
                border.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Attach(border), DispatcherPriority.Loaded);
                if (border.IsAttachedToVisualTree())
                {
                    Dispatcher.UIThread.Post(() => Attach(border), DispatcherPriority.Loaded);
                }
            }
        });
    }

    private TableColumnResize(Grid header, ItemsControl? items)
    {
        _header = header;
        _items = items;

        header.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        header.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        header.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        header.PointerExited += (_, _) =>
        {
            if (_dragColumn < 0)
            {
                header.Cursor = null;
            }
        };

        if (items is not null)
        {
            // Dòng mới tạo (cuộn tới, nạp lại dữ liệu) nhận độ rộng hiện tại của tiêu đề.
            items.ContainerPrepared += (_, e) => Dispatcher.UIThread.Post(() => ApplyTo(e.Container), DispatcherPriority.Loaded);
        }
    }

    public static bool GetIsEnabled(Border border) => border.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Border border, bool value) => border.SetValue(IsEnabledProperty, value);

    private static void Attach(Border border)
    {
        if (border.GetValue(StateProperty) is not null || border.Child is not Grid root)
        {
            return;
        }

        // Tiêu đề: Grid con đầu tiên có nhiều cột; thân bảng: ItemsControl đầu tiên bên trong.
        var header = root.Children.OfType<Grid>().FirstOrDefault(g => g.ColumnDefinitions.Count > 1);
        if (header is null)
        {
            return;
        }

        var items = root.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault();
        border.SetValue(StateProperty, new TableColumnResize(header, items));
    }

    /// <summary>Cột có mép phải nằm sát vị trí chuột (và kéo được); không có thì -1.</summary>
    private int ColumnAt(double x)
    {
        var edge = 0.0;
        var columns = _header.ColumnDefinitions;
        for (var i = 0; i < columns.Count - 1; i++)
        {
            edge += columns[i].ActualWidth;
            if (Math.Abs(x - edge) <= GripWidth)
            {
                // Mép sau cột *: kéo mép này là đổi cột cố định kế bên (cột * tự bù).
                return columns[i].Width.IsAbsolute ? i : columns[i + 1].Width.IsAbsolute ? i + 1 : -1;
            }
        }

        return -1;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var x = e.GetPosition(_header).X;
        if (_dragColumn < 0)
        {
            _header.Cursor = ColumnAt(x) >= 0 ? new Cursor(StandardCursorType.SizeWestEast) : null;
            return;
        }

        var delta = x - _dragStartX;
        // Kéo mép TRÁI của cột (trường hợp cột trước là *): kéo sang phải làm cột hẹp lại.
        var width = Math.Max(MinColumnWidth, _dragStartWidth + (_draggingLeftEdge ? -delta : delta));
        _header.ColumnDefinitions[_dragColumn].Width = new GridLength(width);
        ApplyToAll();
        e.Handled = true;
    }

    private bool _draggingLeftEdge;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_header).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var x = e.GetPosition(_header).X;
        var column = ColumnAt(x);
        if (column < 0)
        {
            return;
        }

        // Xác định đang nắm mép phải của chính cột này hay mép trái (mép phải của cột * đứng trước).
        var rightEdge = 0.0;
        for (var i = 0; i <= column; i++)
        {
            rightEdge += _header.ColumnDefinitions[i].ActualWidth;
        }

        _draggingLeftEdge = Math.Abs(x - rightEdge) > GripWidth;
        _dragColumn = column;
        _dragStartX = x;
        _dragStartWidth = _header.ColumnDefinitions[column].ActualWidth;
        e.Pointer.Capture(_header);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragColumn < 0)
        {
            return;
        }

        _dragColumn = -1;
        e.Pointer.Capture(null);
        _header.Cursor = null;
        e.Handled = true;
    }

    private void ApplyToAll()
    {
        if (_items is null)
        {
            return;
        }

        foreach (var container in _items.GetRealizedContainers())
        {
            ApplyTo(container);
        }
    }

    /// <summary>Chép độ rộng các cột cố định của tiêu đề sang Grid dòng (Grid đầu tiên trong dòng có cùng số cột).</summary>
    private void ApplyTo(Control container)
    {
        var count = _header.ColumnDefinitions.Count;
        var rowGrid = container.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.ColumnDefinitions.Count == count);
        if (rowGrid is null)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var width = _header.ColumnDefinitions[i].Width;
            if (width.IsAbsolute && rowGrid.ColumnDefinitions[i].Width != width)
            {
                rowGrid.ColumnDefinitions[i].Width = width;
            }
        }
    }
}
