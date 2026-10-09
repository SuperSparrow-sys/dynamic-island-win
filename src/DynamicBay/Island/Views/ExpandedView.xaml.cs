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
        foreach (var card in new[] { ClockCard, SystemCard, ShortcutsCard, MessengerCard, ClaudeCard })
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
            [Core.Widgets.Messenger] = MessengerCard,
            [Core.Widgets.Claude] = ClaudeCard,
        };
        DataContextChanged += (_, _) =>
        {
            if (Vm is null) return;
            Vm.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(Core.AppSettings.HomeWidgets) or nameof(Core.AppSettings.ClaudeEnabled) or nameof(Core.AppSettings.ScriptWidgets)) LayoutHome();
                if (e.PropertyName is nameof(Core.AppSettings.ShowTrayTab) or nameof(Core.AppSettings.ShowNotificationsTab)) EnsureVisibleTab();
            };
            LayoutHome();
            EnsureVisibleTab();
        };
        IsVisibleChanged += (_, _) => Vm?.System.SetActive(IsVisible && _widgets[Core.Widgets.System].Visibility == Visibility.Visible);
        HomeScroll.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(FitHomeToViewport, System.Windows.Threading.DispatcherPriority.Loaded);
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
        SyncScriptCards();
        var enabled = (Vm?.Settings.HomeWidgets ?? new System.Collections.ObjectModel.ObservableCollection<string> { "media", "calendar", "timer" })
            .Where(_widgets.ContainsKey).Where(id => id != Core.Widgets.Claude || Vm?.Settings.ClaudeEnabled != false).Distinct().ToList();
        foreach (var (id, card) in _widgets) card.Visibility = enabled.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
        HomeGrid.ColumnDefinitions.Clear();
        HomeGrid.RowDefinitions.Clear();
        if (enabled.Count == 0) return;

        double Weight(string id) => ScriptOf(id) is { } sc ? (sc.Size == Core.ScriptSize.Large ? 2.15 : 1)
            : id switch { "media" => 2.15, "messenger" => 1.6, "claude" => 1.6, "shortcuts" => 1.3, _ => 1 };

        if (!_vertical)
        {
            HomeGrid.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < enabled.Count; i++)
            {
                // Stars share the width; minimum widths make the page scroll sideways when there are many widgets.
                HomeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Weight(enabled[i]), GridUnitType.Star), MinWidth = MinWidthOf(enabled[i]) });
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
            var minRows = new List<double>();
            int row = 0;
            for (int i = 0; i < enabled.Count; row++)
            {
                string id = enabled[i];
                bool wide = IsWide(id);
                bool pair = !wide && i + 1 < enabled.Count && !IsWide(enabled[i + 1]);
                rows.Add(id == "media" ? 1.25 : 1);
                minRows.Add(id == "media" ? 170 : IsWide(id) ? 150 : 135);
                Place(_widgets[id], row, 0, pair ? 1 : 2);
                if (pair) Place(_widgets[enabled[i + 1]], row, 1, 1);
                i += pair ? 2 : 1;
            }
            for (int r = 0; r < rows.Count; r++)
                HomeGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rows[r], GridUnitType.Star), MinHeight = minRows[r] });
            foreach (var id in enabled)
            {
                var card = _widgets[id];
                bool lastRow = Grid.GetRow(card) == rows.Count - 1;
                bool rightEdge = Grid.GetColumnSpan(card) == 2 || Grid.GetColumn(card) == 1;
                card.Margin = new Thickness(0, 0, rightEdge ? 0 : 8, lastRow ? 0 : 8);
            }
        }
        Vm?.System.SetActive(IsVisible && enabled.Contains(Core.Widgets.System));
        FitHomeToViewport();
    }

    private bool IsWide(string id) => id is "media" or "messenger" or "claude" || ScriptOf(id)?.Size == Core.ScriptSize.Large;

    private double MinWidthOf(string id) => ScriptOf(id) is { } sc ? (sc.Size == Core.ScriptSize.Large ? 330 : 170) : id switch
    {
        "media" => 300, "messenger" or "claude" => 230, "shortcuts" => 170, "calendar" => 160, _ => 150,
    };

    // ---- script widgets ----

    private Core.ScriptWidgetConfig? ScriptOf(string id) =>
        id.StartsWith(Core.Widgets.ScriptPrefix) ? Vm?.Settings.ScriptWidgets.FirstOrDefault(s => s.HomeId == id) : null;

    /// <summary>One card per script widget (created on demand, removed with the script); its size follows the setting.</summary>
    private void SyncScriptCards()
    {
        if (Vm is null) return;
        var scripts = Vm.Settings.ScriptWidgets.ToList();
        foreach (var id in _widgets.Keys.Where(k => k.StartsWith(Core.Widgets.ScriptPrefix) && scripts.All(s => s.HomeId != k)).ToList())
        {
            HomeGrid.Children.Remove(_widgets[id]);
            _widgets.Remove(id);
        }
        foreach (var sc in scripts)
        {
            string family = Services.Scripting.ScriptWidgetsService.FamilyOf(sc.Size);
            if (_widgets.TryGetValue(sc.HomeId, out var card))
            {
                if (card.Child is Services.Scripting.ScriptWidgetView v) v.Family = family;
                continue;
            }
            // Same card frame as the built-in widgets; the script draws edge to edge inside it.
            card = new Border
            {
                Style = (Style)FindResource("Card"),
                Padding = new Thickness(0),
                ClipToBounds = true,
                Child = new Services.Scripting.ScriptWidgetView { ScriptId = sc.Id, Family = family },
            };
            card.SizeChanged += (s, _) =>
            {
                var b = (Border)s;
                b.Clip = new System.Windows.Media.RectangleGeometry(new Rect(b.RenderSize), b.CornerRadius.TopLeft, b.CornerRadius.TopLeft);
            };
            _widgets[sc.HomeId] = card;
            HomeGrid.Children.Add(card);
        }
    }

    // ---- Nook scrolling ----

    private void HomeScroll_SizeChanged(object sender, SizeChangedEventArgs e) => FitHomeToViewport();

    /// <summary>The grid is at least as big as the visible area, so few widgets still fill it (stars) and many scroll.</summary>
    private void FitHomeToViewport()
    {
        // The page reaches under the panel edge in its scroll direction (the panel clips it along its rounded shape),
        // so a card that does not fit is cut off by the island itself. The grid keeps the normal 12 px inset.
        const double edge = 12;
        HomeScroll.Margin = _vertical ? new Thickness(0, 0, 0, -edge) : new Thickness(-edge, 0, -edge, 0);
        HomeGrid.Margin = _vertical ? new Thickness(0, 0, 0, edge) : new Thickness(edge, 0, edge, 0);
        // Not laid out yet (panel closed, other tab): measuring now would pin the grid to 0 px and leave the page
        // empty. SizeChanged / IsVisibleChanged run this again once the page is visible.
        if (HomeScroll.ActualWidth < 1 || HomeScroll.ActualHeight < 1) return;
        // No fixed size across the scroll direction: the scroll viewer already limits it to the visible area.
        HomeGrid.Width = double.NaN;
        HomeGrid.Height = double.NaN;
        HomeGrid.MinWidth = _vertical ? 0 : Math.Max(0, HomeScroll.ActualWidth - 2 * edge);
        HomeGrid.MinHeight = _vertical ? Math.Max(0, HomeScroll.ActualHeight - edge) : 0;
    }

    private void HomeScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        // Inner lists (Claude sessions, messages) scroll themselves; only take the wheel when they can not.
        if (e.OriginalSource is DependencyObject d && FindParent<ScrollViewer>(d) is { } inner && inner != HomeScroll
            && inner.ScrollableHeight > 0) return;
        if (_vertical) HomeScroll.ScrollToVerticalOffset(HomeScroll.VerticalOffset - e.Delta * 0.6);
        else HomeScroll.ScrollToHorizontalOffset(HomeScroll.HorizontalOffset - e.Delta * 0.6);
        e.Handled = true;
    }

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
        // No scroll bar: a card cut off at the panel edge shows that there is more (like iOS).
        HomeScroll.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        HomeScroll.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        FitHomeToViewport();
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

    // Timeline: click or drag; the time follows the cursor and the player seeks once on release.
    private bool _scrubbing;

    private double TimelineSeconds(MouseEventArgs e) =>
        Math.Clamp(e.GetPosition(Timeline).X / Math.Max(1, Timeline.ActualWidth), 0, 1) * (Vm?.Media.DurationSeconds ?? 0);

    private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // never start moving the island from here
        if (Vm is null || Vm.Media.DurationSeconds <= 0) return;
        _scrubbing = true;
        Timeline.CaptureMouse();
        Vm.Media.Scrub(TimelineSeconds(e));
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (_scrubbing && Vm is not null) Vm.Media.Scrub(TimelineSeconds(e));
    }

    private async void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbing || Vm is null) return;
        _scrubbing = false;
        Timeline.ReleaseMouseCapture();
        e.Handled = true;
        await Vm.Media.SeekAsync(TimelineSeconds(e));
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
        e.Handled = true; // the item may be dragged out - the island itself must not move
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
        e.Handled = true; // the item may be dragged out - the island itself must not move
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
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
