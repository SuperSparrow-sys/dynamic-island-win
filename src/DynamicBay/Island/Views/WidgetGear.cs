using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DynamicBay.Island.Views;

/// <summary>
/// The small gear in a widget's top-right corner while Alt is held (the "edit" mode): it leads straight to that
/// widget's own settings. Drawn as an adorner, so the cards themselves stay untouched.
/// </summary>
public sealed class WidgetGear : Adorner
{
    private readonly VisualCollection _visuals;
    private readonly Button _button;
    private readonly Border _plate;

    public WidgetGear(UIElement card, Action open) : base(card)
    {
        _button = new Button
        {
            Style = (Style)Application.Current.FindResource("Btn.Icon"),
            Width = 26, Height = 26,
            ToolTip = Core.Loc.German ? "Einstellungen dieses Widgets" : "Settings of this widget",
            Content = new Controls.Icon
            {
                Data = (Geometry)Application.Current.FindResource("Icon.Settings"),
                Width = 13, Height = 13, Foreground = Brushes.White,
            },
        };
        _button.Click += (_, e) => { e.Handled = true; open(); };
        // A solid plate under the button: its hover tint is see-through and it shrinks when pressed, which let the
        // card's own header button (e.g. a cross) show through.
        _plate = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            Child = _button,
        };
        _visuals = new VisualCollection(this) { _plate };
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = true;
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _plate.Measure(constraint);
        return AdornedElement.RenderSize;
    }

    /// <summary>Top-right, inset like the card's own header buttons.</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = AdornedElement.RenderSize;
        _plate.Arrange(new Rect(size.Width - 28 - 6, 6, 28, 28));
        return finalSize;
    }
}
