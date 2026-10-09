using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynamicBay.Services;

namespace DynamicBay.Island.Views;

public partial class ExpandedView : UserControl
{
    private Point _dragStart;
    private bool _dragging;

    /// <summary>Raised while an item is dragged out, so the island stays open/doesn't collapse mid-drag.</summary>
    public event Action<bool>? DragOutActive;

    public ExpandedView()
    {
        InitializeComponent();
        SetVertical(false);
    }

    private IslandViewModel? Vm => DataContext as IslandViewModel;

    /// <summary>Re-flows the cards: a wide row at top/bottom edges, a tall stack at the side edges.</summary>
    public void SetVertical(bool vertical)
    {
        Layout(HomeGrid, vertical,
            horizontal: new[] { (MediaCard, 0, 0, 1), (CalendarCard, 0, 1, 1), (TimerCard, 0, 2, 1) },
            hCols: new[] { new GridLength(2.15, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star) },
            hRows: new[] { new GridLength(1, GridUnitType.Star) },
            vertical: new[] { (MediaCard, 0, 0, 2), (CalendarCard, 1, 0, 1), (TimerCard, 1, 1, 1) },
            vCols: new[] { new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star) },
            vRows: new[] { new GridLength(1.25, GridUnitType.Star), new GridLength(1, GridUnitType.Star) });

        Layout(TrayGrid, vertical,
            horizontal: new[] { (ShelfCard, 0, 0, 1), (ClipCard, 0, 1, 1) },
            hCols: new[] { new GridLength(1, GridUnitType.Star), new GridLength(1.9, GridUnitType.Star) },
            hRows: new[] { new GridLength(1, GridUnitType.Star) },
            vertical: new[] { (ShelfCard, 0, 0, 1), (ClipCard, 1, 0, 1) },
            vCols: new[] { new GridLength(1, GridUnitType.Star) },
            vRows: new[] { new GridLength(1, GridUnitType.Star), new GridLength(1.7, GridUnitType.Star) });

        ClipList.ItemsPanel = (ItemsPanelTemplate)FindResource(vertical ? "WrapPanel" : "RowPanel");
        ClipScroll.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ClipScroll.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    private static void Layout(Grid grid, bool isVertical,
        (Border el, int row, int col, int span)[] horizontal, GridLength[] hCols, GridLength[] hRows,
        (Border el, int row, int col, int span)[] vertical, GridLength[] vCols, GridLength[] vRows)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        foreach (var c in isVertical ? vCols : hCols) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
        foreach (var r in isVertical ? vRows : hRows) grid.RowDefinitions.Add(new RowDefinition { Height = r });
        foreach (var (el, row, col, span) in isVertical ? vertical : horizontal)
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
            Grid.SetColumnSpan(el, isVertical ? span : 1);
            bool lastCol = col + (isVertical ? span : 1) >= grid.ColumnDefinitions.Count;
            bool lastRow = row + 1 >= grid.RowDefinitions.Count;
            el.Margin = new Thickness(0, 0, lastCol ? 0 : 8, lastRow ? 0 : 8);
        }
    }

    // ---- media ----

    private async void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || Vm.Media.DurationSeconds <= 0) return;
        double ratio = Math.Clamp(e.GetPosition(Timeline).X / Timeline.ActualWidth, 0, 1);
        await Vm.Media.SeekAsync(ratio * Vm.Media.DurationSeconds);
        e.Handled = true;
    }

    private async void Devices_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        DevicesOverlay.Visibility = Visibility.Visible;
        await Vm.Spotify.LoadDevices();
    }

    private void DevicesClose_Click(object sender, RoutedEventArgs e) => DevicesOverlay.Visibility = Visibility.Collapsed;

    // ---- shelf ----

    private void Shelf_Drop(object sender, DragEventArgs e)
    {
        if (Vm is null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Vm.Shelf.Add(files);
        e.Handled = true;
    }

    private void ShelfItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragging = false;
        if (e.ClickCount == 2 && ((FrameworkElement)sender).DataContext is ShelfItem item)
        {
            Vm?.Shelf.Open(item);
            e.Handled = true;
        }
    }

    private void ShelfItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragging || Vm is null) return;
        if ((e.GetPosition(this) - _dragStart).Length < 6) return;
        if (((FrameworkElement)sender).DataContext is not ShelfItem item) return;
        _dragging = true;
        var items = new[] { item };
        DragOutActive?.Invoke(true);
        var result = DragDrop.DoDragDrop((DependencyObject)sender, Vm.Shelf.CreateDragData(items), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        DragOutActive?.Invoke(false);
        Vm.Shelf.AfterDragOut(items, result);
    }

    // ---- clipboard ----

    private void ClipItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragging = false;
    }

    private void ClipItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragging) return;
        if ((e.GetPosition(this) - _dragStart).Length < 6) return;
        if (((FrameworkElement)sender).DataContext is not ClipItem item) return;
        _dragging = true;
        DragOutActive?.Invoke(true);
        DragDrop.DoDragDrop((DependencyObject)sender, ClipboardService.CreateDragData(item), DragDropEffects.Copy);
        DragOutActive?.Invoke(false);
    }

    private void ClipItem_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || Vm is null) return;
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;
        if (((FrameworkElement)sender).DataContext is ClipItem item)
        {
            Vm.Clipboard.Copy(item);
            CopiedFeedback?.Invoke(item);
        }
    }

    public event Action<ClipItem>? CopiedFeedback;

    private void ClipScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (ClipScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled) return;
        ClipScroll.ScrollToHorizontalOffset(ClipScroll.HorizontalOffset - e.Delta * 0.6);
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
