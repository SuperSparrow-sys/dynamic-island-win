using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using DynamicBay.Core;
using DynamicBay.Motion;

namespace DynamicBay.Settings;

/// <summary>Pure rules of the widget board (testable without a window).</summary>
internal static class BoardMath
{
    /// <summary>
    /// Where a dragged chip goes among <paramref name="count"/> others of the same height: before the first whose middle
    /// is below the pointer. Counted on the resting positions, so the gap opening under the pointer does not make it flicker.
    /// </summary>
    public static int InsertIndex(double y, double slotHeight, int count) =>
        slotHeight <= 0 ? count : Math.Clamp((int)Math.Ceiling(y / slotHeight - 0.5), 0, count);

    /// <summary>
    /// The index for <c>Place</c>, which counts the chip itself when it stays in its column: from <paramref name="old"/>
    /// on everything is one further down.
    /// </summary>
    public static int PlaceIndex(int insertIndex, int old) => old >= 0 && insertIndex >= old ? insertIndex + 1 : insertIndex;
}

public partial class SettingsWindow
{
    // ---- board: drag widgets between Nook, System and Off ----
    // A chip is dragged by its grip (three lines on the right); a click anywhere else opens its settings. While dragging,
    // a lifted copy follows the pointer, a gap opens where it would land and the others slide aside (springs).

    private Canvas? _boardOverlay;
    private readonly List<(string Place, StackPanel List, Border Card)> _boardColumns = new();
    private BoardDrag? _drag;

    private sealed class BoardDrag
    {
        public required string Id;
        public required Border Slot;
        public required FrameworkElement Ghost;
        public required Point Grab;
        public required double SlotHeight;
        public int SourceColumn;
        public int SourceIndex;
        public int Column;
        public int Index;
        /// <summary>The open gap: the source slot (chip hidden) or a new empty slot.</summary>
        public Border? Gap;
        public bool Landing;
    }

    private const double ChipGap = 6;

    private void BuildWidgetBoard()
    {
        if (_drag is not null) EndDragVisuals();
        var s = _ctx.S;
        var infos = AllWidgetInfo();
        WidgetBoard.Children.Clear();
        WidgetBoard.ColumnDefinitions.Clear();
        _boardColumns.Clear();
        var columns = new[]
        {
            ("home", "Nook", s.HomeWidgets.Where(id => infos.Any(w => w.id == id)).ToList()),
            ("system", "System", s.SystemWidgets.Where(id => infos.Any(w => w.id == id)).ToList()),
            ("", Loc.German ? "Aus" : "Off", infos.Select(w => w.id).Where(id => !s.HomeWidgets.Contains(id) && !s.SystemWidgets.Contains(id)).ToList()),
        };
        for (int c = 0; c < columns.Length; c++)
        {
            var (place, title, ids) = columns[c];
            WidgetBoard.ColumnDefinitions.Add(new ColumnDefinition());
            var list = new StackPanel { MinHeight = 60 };
            var card = new Border
            {
                // "S.Card" is both a style and a brush here (the theme brush wins in code): set the card look directly.
                CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("S.CardBorder"), Background = (Brush)FindResource("S.Card"),
                Padding = new Thickness(10, 8, 10, 4), Margin = new Thickness(c == 0 ? 0 : 6, 0, c == 2 ? 0 : 6, 0),
            };
            var dock = new DockPanel();
            var head = new TextBlock { Style = (Style)FindResource("ST.Label"), Text = title, Margin = new Thickness(2, 0, 0, 8) };
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);
            dock.Children.Add(list);
            card.Child = dock;
            foreach (var id in ids)
            {
                var info = infos.First(w => w.id == id);
                list.Children.Add(BoardSlot(id, info.icon, Loc.German ? info.de : info.en));
            }
            Grid.SetColumn(card, c);
            WidgetBoard.Children.Add(card);
            _boardColumns.Add((place, list, card));
        }
        _boardOverlay = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        Grid.SetColumnSpan(_boardOverlay, columns.Length);
        Panel.SetZIndex(_boardOverlay, 10);
        WidgetBoard.Children.Add(_boardOverlay);
    }

    /// <summary>The chip's look: icon and name, the grip on the right (also used for the lifted copy).</summary>
    private (Border Chip, FrameworkElement Grip) ChipVisual(string icon, string name)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 6, 7),
            Background = (Brush)FindResource("S.Field"), BorderBrush = (Brush)FindResource("S.FieldBorder"), BorderThickness = new Thickness(1),
        };
        var row = new DockPanel();
        var grip = new Border
        {
            Background = Brushes.Transparent, Padding = new Thickness(6, 0, 2, 0), Cursor = Cursors.SizeAll,
            Child = new Controls.Icon { Data = (Geometry)FindResource("Icon.Reorder"), Width = 14, Height = 14, Foreground = (Brush)FindResource("S.Text3") },
            ToolTip = Loc.German ? "Ziehen zum Verschieben" : "Drag to move",
        };
        DockPanel.SetDock(grip, Dock.Right);
        row.Children.Add(grip);
        row.Children.Add(new Controls.Icon { Data = (Geometry)FindResource(icon), Filled = Controls.Icon.IsGlyph(icon), Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Base"), FontSize = 12.5, Text = name, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center });
        chip.Child = row;
        return (chip, grip);
    }

    /// <summary>A place in a column: the chip and the space under it. Its height animates when chips make room.</summary>
    private Border BoardSlot(string id, string icon, string name)
    {
        var (chip, grip) = ChipVisual(icon, name);
        chip.Cursor = Cursors.Hand;
        chip.ToolTip = Loc.German ? "Klicken: Einstellungen · Striche ziehen: verschieben · Rechtsklick: Menü" : "Click: settings · Drag the lines: move · Right-click: menu";
        chip.ContextMenu = ChipMenu(id);
        var slot = new Border { Child = chip, Padding = new Thickness(0, 0, 0, ChipGap), ClipToBounds = true, Tag = id };

        // A click on the chip (not on the grip) opens the widget's settings.
        bool pressed = false;
        chip.MouseLeftButtonDown += (_, e) => { if (!grip.IsMouseOver) pressed = true; };
        chip.MouseLeftButtonUp += (_, _) =>
        {
            if (!pressed || _drag is not null) return;
            pressed = false;
            var (page, group) = Widgets.SettingsOf(id);
            if (page == SettingsPage.Content && group is null) return; // nothing of its own beyond this page
            ShowSection(page, group);
        };
        chip.MouseLeave += (_, _) => pressed = false;

        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (_drag is not null) return;
            StartDrag(id, icon, name, slot, chip, grip, e);
            e.Handled = true;
        };
        grip.MouseMove += (_, e) => { if (_drag is { Landing: false }) MoveDrag(e.GetPosition(_boardOverlay)); };
        grip.MouseLeftButtonUp += (_, e) => { if (_drag is { Landing: false }) Drop(); e.Handled = true; };
        // Capture taken away (Alt+Tab, a dialog): it lands where it is now.
        grip.LostMouseCapture += (_, _) => { if (_drag is { Landing: false }) Drop(); };
        return slot;
    }

    private void StartDrag(string id, string icon, string name, Border slot, Border chip, UIElement grip, MouseButtonEventArgs e)
    {
        if (_boardOverlay is null) return;
        int col = _boardColumns.FindIndex(c => c.List.Children.Contains(slot));
        if (col < 0) return;
        var (ghostChip, _) = ChipVisual(icon, name);
        // An opaque copy: the field brush is see-through in the dark theme.
        var ghost = new Border
        {
            Child = ghostChip, Width = chip.ActualWidth, CornerRadius = new CornerRadius(8), Background = (Brush)FindResource("S.Window"),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.28, Color = Colors.Black },
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        var at = chip.TranslatePoint(new Point(0, 0), _boardOverlay);
        Canvas.SetLeft(ghost, at.X);
        Canvas.SetTop(ghost, at.Y);
        _boardOverlay.Children.Add(ghost);
        Animate(ghost.RenderTransform, ScaleTransform.ScaleXProperty, 1.035, 0.3);
        Animate(ghost.RenderTransform, ScaleTransform.ScaleYProperty, 1.035, 0.3);

        int index = _boardColumns[col].List.Children.IndexOf(slot);
        _drag = new BoardDrag
        {
            Id = id, Slot = slot, Ghost = ghost, Grab = e.GetPosition(chip), SlotHeight = slot.ActualHeight,
            SourceColumn = col, SourceIndex = index, Column = col, Index = index, Gap = slot,
        };
        chip.Opacity = 0; // its place stays open as the first gap
        Mouse.OverrideCursor = Cursors.SizeAll;
        grip.CaptureMouse();
    }

    private void MoveDrag(Point p)
    {
        if (_drag is not { } d || _boardOverlay is null) return;
        Canvas.SetLeft(d.Ghost, p.X - d.Grab.X);
        Canvas.SetTop(d.Ghost, p.Y - d.Grab.Y);

        // Column under the pointer (the nearest one beside the board).
        int col = 0;
        double best = double.MaxValue;
        for (int c = 0; c < _boardColumns.Count; c++)
        {
            var card = _boardColumns[c].Card;
            var left = card.TranslatePoint(new Point(0, 0), _boardOverlay).X;
            double dist = p.X < left ? left - p.X : p.X > left + card.ActualWidth ? p.X - left - card.ActualWidth : 0;
            if (dist < best) { best = dist; col = c; }
        }
        int index = TargetIndex(col, p);
        if (col == d.Column && index == d.Index) return;
        MoveGap(col, index);
    }

    /// <summary>Insert position among the other chips of the column (the "Aus" column keeps its own order).</summary>
    private int TargetIndex(int col, Point p)
    {
        var d = _drag!;
        var (place, list, _) = _boardColumns[col];
        var others = list.Children.OfType<Border>().Where(b => b != d.Slot && b.Tag is string).ToList();
        if (place.Length == 0)
        {
            // Off has no order of its own: the gap opens where the widget will be listed.
            var order = AllWidgetInfo().Select(w => w.id).ToList();
            int mine = order.IndexOf(d.Id);
            return others.Count(b => order.IndexOf((string)b.Tag) < mine);
        }
        double y = p.Y - list.TranslatePoint(new Point(0, 0), _boardOverlay!).Y;
        return BoardMath.InsertIndex(y, d.SlotHeight, others.Count);
    }

    /// <summary>Closes the current gap and opens one at <paramref name="index"/> of <paramref name="col"/> (both animated).</summary>
    private void MoveGap(int col, int index)
    {
        var d = _drag!;
        if (d.Gap is { } old)
        {
            if (old == d.Slot) Animate(old, HeightProperty, 0, 0.32, from: old.ActualHeight);
            else Animate(old, HeightProperty, 0, 0.32, done: () => (old.Parent as Panel)?.Children.Remove(old));
        }
        var list = _boardColumns[col].List;
        Border gap;
        if (col == d.SourceColumn && index == d.SourceIndex)
            gap = d.Slot; // back to where it came from: its own place opens again
        else
        {
            gap = new Border
            {
                Height = 0, Padding = new Thickness(0, 0, 0, ChipGap),
                Child = new Border { CornerRadius = new CornerRadius(8), Background = (Brush)FindResource("S.Hover"), BorderBrush = (Brush)FindResource("S.FieldBorder"), BorderThickness = new Thickness(1) },
            };
            // Position among the real chips (skipping the source slot and closing gaps).
            int k = 0, at = list.Children.Count;
            for (int i = 0; i < list.Children.Count; i++)
            {
                if (list.Children[i] is not Border b || b == d.Slot || b.Tag is not string) continue;
                if (k++ == index) { at = i; break; }
            }
            list.Children.Insert(at, gap);
        }
        Animate(gap, HeightProperty, d.SlotHeight, 0.32, from: gap == d.Slot ? gap.ActualHeight : 0);
        d.Gap = gap;
        d.Column = col;
        d.Index = index;
    }

    /// <summary>Lands the copy in the gap, then saves the new order (the board is rebuilt in that order).</summary>
    private void Drop()
    {
        if (_drag is not { } d || _boardOverlay is null) return;
        d.Landing = true;
        Mouse.OverrideCursor = null;
        if (Mouse.Captured is UIElement captured) captured.ReleaseMouseCapture();
        var (place, list, _) = _boardColumns[d.Column];
        // Where the gap will be once everything has settled: the column's top plus the chips before it.
        var top = list.TranslatePoint(new Point(0, 0), _boardOverlay);
        var dest = new Point(top.X, top.Y + d.Index * d.SlotHeight);
        Animate(d.Ghost.RenderTransform, ScaleTransform.ScaleXProperty, 1, 0.28);
        Animate(d.Ghost.RenderTransform, ScaleTransform.ScaleYProperty, 1, 0.28);
        Animate(d.Ghost, Canvas.TopProperty, dest.Y, 0.28);
        Animate(d.Ghost, Canvas.LeftProperty, dest.X, 0.28, done: () =>
        {
            if (_drag != d) return;
            bool moved = d.Column != d.SourceColumn || d.Index != d.SourceIndex;
            int old = d.Column == d.SourceColumn ? d.SourceIndex : -1;
            _drag = null;
            if (moved) Place(d.Id, place, place.Length == 0 ? -1 : BoardMath.PlaceIndex(d.Index, old));
            else BuildWidgetBoard();
        });
    }

    /// <summary>The board is rebuilt mid-drag (settings changed): nothing of the drag may stay behind.</summary>
    private void EndDragVisuals()
    {
        _drag = null;
        Mouse.OverrideCursor = null;
        if (Mouse.Captured is UIElement captured && WidgetBoard.IsAncestorOf(captured)) captured.ReleaseMouseCapture();
    }

    private static void Animate(DependencyObject target, DependencyProperty property, double to, double seconds, double? from = null, Action? done = null)
    {
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromSeconds(seconds), EasingFunction = new SpringEase { Bounce = 0.12 } };
        if (from is double f) a.From = f;
        if (done is not null) a.Completed += (_, _) => done();
        (target as IAnimatable)?.BeginAnimation(property, a);
    }
}
