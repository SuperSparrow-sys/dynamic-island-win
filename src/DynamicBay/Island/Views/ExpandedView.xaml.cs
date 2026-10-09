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

    private bool _vertical;
    private readonly Dictionary<string, Border> _widgets;

    public ExpandedView()
    {
        InitializeComponent();
        // The optional cards live in the home grid too; they're shown/positioned by LayoutHome.
        foreach (var card in new[] { ClockCard, SystemCard, ShortcutsCard })
        {
            ExtraCards.Children.Remove(card);
            HomeGrid.Children.Add(card);
        }
        _widgets = new()
        {
            [Core.Widgets.Media] = MediaCard,
            [Core.Widgets.Clock] = ClockCard,
            [Core.Widgets.Calendar] = CalendarCard,
            [Core.Widgets.Timer] = TimerCard,
            [Core.Widgets.System] = SystemCard,
            [Core.Widgets.Shortcuts] = ShortcutsCard,
        };
        DataContextChanged += (_, _) =>
        {
            if (Vm is null) return;
            Vm.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(Core.AppSettings.HomeWidgets)) LayoutHome();
                if (e.PropertyName is nameof(Core.AppSettings.ShowTrayTab) or nameof(Core.AppSettings.ShowNotificationsTab)) EnsureVisibleTab();
            };
            LayoutHome();
            EnsureVisibleTab();
        };
        IsVisibleChanged += (_, _) => Vm?.System.SetActive(IsVisible && _widgets[Core.Widgets.System].Visibility == Visibility.Visible);
        SetVertical(false);
    }

    private IslandViewModel? Vm => DataContext as IslandViewModel;

    private void EnsureVisibleTab()
    {
        if (Vm is null) return;
        if ((Vm.Tab == 1 && !Vm.Settings.ShowTrayTab) || (Vm.Tab == 2 && !Vm.Settings.ShowNotificationsTab)) Vm.Tab = 0;
    }

    /// <summary>
    /// Arranges the enabled widgets in the user's order. Wide layout: one row (media gets more room).
    /// Tall layout (side edges): media spans the full width, the rest flow two per row.
    /// </summary>
    private void LayoutHome()
    {
        var enabled = (Vm?.Settings.HomeWidgets ?? new System.Collections.ObjectModel.ObservableCollection<string> { "media", "calendar", "timer" })
            .Where(_widgets.ContainsKey).Distinct().ToList();
        foreach (var (id, card) in _widgets) card.Visibility = enabled.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
        HomeGrid.ColumnDefinitions.Clear();
        HomeGrid.RowDefinitions.Clear();
        if (enabled.Count == 0) return;

        static double Weight(string id) => id switch { "media" => 2.15, "shortcuts" => 1.3, _ => 1 };

        if (!_vertical)
        {
            HomeGrid.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < enabled.Count; i++)
            {
                HomeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Weight(enabled[i]), GridUnitType.Star) });
                var card = _widgets[enabled[i]];
                Grid.SetRow(card, 0);
                Grid.SetColumn(card, i);
                Grid.SetColumnSpan(card, 1);
                card.Margin = new Thickness(0, 0, i == enabled.Count - 1 ? 0 : 8, 0);
            }
        }
        else
        {
            HomeGrid.ColumnDefinitions.Add(new ColumnDefinition());
            HomeGrid.ColumnDefinitions.Add(new ColumnDefinition());
            // Rows: wide widgets take a full row; small ones pair up, and an unpaired one spans the row.
            var rows = new List<double>();
            int row = 0;
            for (int i = 0; i < enabled.Count; row++)
            {
                string id = enabled[i];
                bool wide = IsWide(id);
                bool pair = !wide && i + 1 < enabled.Count && !IsWide(enabled[i + 1]);
                rows.Add(id == "media" ? 1.25 : 1);
                Place(_widgets[id], row, 0, pair ? 1 : 2);
                if (pair) Place(_widgets[enabled[i + 1]], row, 1, 1);
                i += pair ? 2 : 1;
            }
            foreach (var h in rows) HomeGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h, GridUnitType.Star) });
            foreach (var id in enabled)
            {
                var card = _widgets[id];
                bool lastRow = Grid.GetRow(card) == rows.Count - 1;
                bool rightEdge = Grid.GetColumnSpan(card) == 2 || Grid.GetColumn(card) == 1;
                card.Margin = new Thickness(0, 0, rightEdge ? 0 : 8, lastRow ? 0 : 8);
            }
        }
        Vm?.System.SetActive(IsVisible && enabled.Contains(Core.Widgets.System));
    }

    private static bool IsWide(string id) => id is "media" or "messenger";

    private static void Place(Border card, int row, int col, int span)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, col);
        Grid.SetColumnSpan(card, span);
    }

    /// <summary>Re-flows the cards: a wide row at top/bottom edges, a tall stack at the side edges.</summary>
    public void SetVertical(bool vertical)
    {
        _vertical = vertical;
        LayoutHome();

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

    // ---- shortcuts ----

    private void Shortcuts_Drop(object sender, DragEventArgs e)
    {
        if (Vm is not null && e.Data.GetData(DataFormats.FileDrop) is string[] files) Vm.Shortcuts.Add(files);
        e.Handled = true;
    }

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

    // ---- save to cloud ----

    private void CloudSave_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var button = (FrameworkElement)sender;
        var item = button.DataContext;
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("Island.Menu"),
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var targets = CloudTargets.Detect();
        foreach (var t in targets)
        {
            var mi = new MenuItem
            {
                Header = t.Name,
                Style = (Style)FindResource("Island.MenuItem"),
                Icon = new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource("Icon.Cloud"), Width = 14, Height = 14 },
            };
            mi.Click += (_, _) => SaveTo(t, item);
            menu.Items.Add(mi);
        }
        if (targets.Count == 0)
            menu.Items.Add(new MenuItem { Header = Core.Loc.T("Cloud.None"), IsEnabled = false, Style = (Style)FindResource("Island.MenuItem") });
        DragOutActive?.Invoke(true); // keep the island open while the menu is up
        menu.Closed += (_, _) => DragOutActive?.Invoke(false);
        menu.IsOpen = true;
    }

    private void SaveTo(CloudTarget target, object? item)
    {
        try
        {
            switch (item)
            {
                case ShelfItem s when System.IO.File.Exists(s.Path):
                    CloudTargets.SaveFile(target, s.Path);
                    break;
                case ClipItem { IsImage: true, ImagePath: not null } c:
                    CloudTargets.SaveFile(target, c.ImagePath);
                    break;
                case ClipItem { IsText: true } c:
                    CloudTargets.SaveText(target, c.Text ?? "", $"Text {c.Created:yyyy-MM-dd HHmmss}.txt");
                    break;
                default:
                    return;
            }
            ShowToast(Core.Loc.F("Cloud.Saved", target.Name));
        }
        catch (Exception ex)
        {
            Core.Log.Error("CloudSave", ex);
            ShowToast(ex.Message);
        }
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        var show = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        show.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1, TimeSpan.FromMilliseconds(180)));
        show.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1, TimeSpan.FromMilliseconds(1800)));
        show.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0, TimeSpan.FromMilliseconds(2100)));
        Toast.BeginAnimation(OpacityProperty, show);
    }

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
