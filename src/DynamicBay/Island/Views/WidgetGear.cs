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

    public WidgetGear(UIElement card, Action open) : base(card)
    {
        _button = new Button
        {
            Style = (Style)Application.Current.FindResource("Btn.Icon"),
            Width = 26, Height = 26,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x3A, 0x3A, 0x3C)),
            ToolTip = Core.Loc.German ? "Einstellungen dieses Widgets" : "Settings of this widget",
            Content = new Controls.Icon
            {
                Data = (Geometry)Application.Current.FindResource("Icon.Settings"),
                Width = 13, Height = 13, Foreground = Brushes.White,
            },
        };
        _button.Click += (_, e) => { e.Handled = true; open(); };
        _visuals = new VisualCollection(this) { _button };
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = true;
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _button.Measure(constraint);
        return AdornedElement.RenderSize;
    }

    /// <summary>Top-right, inset like the card's own header buttons.</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = AdornedElement.RenderSize;
        _button.Arrange(new Rect(size.Width - 26 - 7, 7, 26, 26));
        return finalSize;
    }
}
