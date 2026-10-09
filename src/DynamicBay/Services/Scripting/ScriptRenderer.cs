using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicBay.Services.Scripting;

/// <summary>
/// Turns the widget tree of a script into WPF, following Scriptable's (SwiftUI's) layout rules: stacks lay out their
/// items in a row or column, a spacer without length takes the free space, a stack with such a spacer stretches,
/// "align" places items across the stack. Cards are laid out at the iOS design size (small 158×158, medium 338×158)
/// and scaled to fit, so a script looks the same as on the iPhone.
/// </summary>
public static class ScriptRenderer
{
    private static readonly Color DefaultText = Color.FromRgb(0xED, 0xED, 0xED);

    public static Size DesignSize(string family) => family switch
    {
        ScriptWidgetsService.Small => new Size(158, 158),
        "large" => new Size(338, 354),
        _ => new Size(338, 158),
    };

    /// <summary>Card content (scaled to the card) or, for the Mini, one line for the compact island.</summary>
    public static FrameworkElement Build(JsonNode widget, string family, bool vertical = false)
    {
        if (family == ScriptWidgetsService.Mini)
        {
            // Side edges: the parts of the line stack up and shrink to the narrow island.
            var line = Stack(widget, root: true, inline: true, column: vertical);
            return vertical
                ? new Viewbox { Child = line, StretchDirection = StretchDirection.DownOnly, MaxWidth = 22, HorizontalAlignment = HorizontalAlignment.Center }
                : new Viewbox { Child = line, StretchDirection = StretchDirection.DownOnly, MaxHeight = 18, VerticalAlignment = VerticalAlignment.Center };
        }
        var size = DesignSize(family);
        var content = Stack(widget, root: true, inline: false);
        content.Width = size.Width;
        content.Height = size.Height;
        return new Border
        {
            Background = Brush(widget["bg"]) ?? Brushes.Transparent,
            Child = new Viewbox { Child = content, Stretch = Stretch.Uniform },
        };
    }

    public static FrameworkElement Error(string message) => new Border
    {
        Padding = new Thickness(12),
        Child = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = DynamicBay.Core.Loc.German ? "Skript-Fehler" : "Script error", Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)), FontWeight = FontWeights.SemiBold, FontSize = 12 },
                new TextBlock { Text = message, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), MaxHeight = 120 },
            },
        },
    };

    // ---- stacks ----

    private static bool IsFlexSpacer(JsonNode? n) => (string?)n?["t"] == "spacer" && n["len"]?.GetValue<double?>() is null;
    private static bool IsStack(JsonNode? n) => (string?)n?["t"] is "stack" or "widget";
    private static bool Vertical(JsonNode n) => n["vertical"]?.GetValue<bool>() ?? false;
    private static double Num(JsonNode? n, string key) => n?[key]?.GetValue<double?>() ?? 0;

    /// <summary>Does this stack want all the room along <paramref name="vertical"/>? (it holds a flexible spacer in that direction)</summary>
    private static bool Expands(JsonNode n, bool vertical)
    {
        if (!IsStack(n)) return false;
        if (vertical ? Num(n, "h") > 0 : Num(n, "w") > 0) return false;
        var items = n["items"] as JsonArray;
        if (items is null) return false;
        if (Vertical(n) == vertical && items.Any(IsFlexSpacer)) return true;
        return items.Any(i => i is not null && IsStack(i) && Expands(i, vertical));
    }

    private static FrameworkElement Stack(JsonNode n, bool root, bool inline, bool column = false)
    {
        bool vertical = column || Vertical(n);
        string align = (string?)n["align"] ?? "top";
        var items = (n["items"] as JsonArray)?.Where(i => i is not null).Select(i => i!).ToList() ?? new();
        double spacing = Num(n, "spacing");
        var grid = new Grid();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool flex = IsFlexSpacer(item) || (IsStack(item) && Expands(item, vertical));
            double gap = i > 0 ? spacing : 0;
            var length = (string?)item["t"] == "spacer" && !flex ? new GridLength(Num(item, "len") + gap) : flex ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            if (vertical) grid.RowDefinitions.Add(new RowDefinition { Height = length });
            else grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
            if ((string?)item["t"] == "spacer") continue;

            var el = (string?)item["t"] == "text" ? Text(item) : Stack(item, root: false, inline: inline, column: column);
            if (gap > 0) el.Margin = vertical ? new Thickness(el.Margin.Left, gap, el.Margin.Right, 0) : new Thickness(gap, el.Margin.Top, 0, el.Margin.Bottom);
            if (vertical)
            {
                Grid.SetRow(el, i);
                el.HorizontalAlignment = IsStack(item) && Expands(item, false) ? HorizontalAlignment.Stretch
                    : align switch { "center" when !root => HorizontalAlignment.Center, "bottom" => HorizontalAlignment.Right, _ => HorizontalAlignment.Left };
                el.VerticalAlignment = flex ? VerticalAlignment.Stretch : VerticalAlignment.Center;
            }
            else
            {
                Grid.SetColumn(el, i);
                el.VerticalAlignment = IsStack(item) && Expands(item, true) ? VerticalAlignment.Stretch
                    : align switch { "center" => VerticalAlignment.Center, "bottom" => VerticalAlignment.Bottom, _ => VerticalAlignment.Top };
                el.HorizontalAlignment = flex ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            }
            grid.Children.Add(el);
        }
        // A widget without flexible items centres its content vertically (like Scriptable).
        if (root && vertical && !items.Any(i => IsFlexSpacer(i) || (IsStack(i) && Expands(i, true))))
            grid.VerticalAlignment = VerticalAlignment.Center;

        if (inline && root) return grid;
        var pad = n["pad"] as JsonArray;
        var thickness = pad is { Count: 4 } ? new Thickness(Num(pad, 1), Num(pad, 0), Num(pad, 3), Num(pad, 2)) : root ? new Thickness(16) : new Thickness(0);
        var border = new Border
        {
            Child = grid,
            Padding = thickness,
            Background = root ? null : Brush(n["bg"]),
            CornerRadius = new CornerRadius(Num(n, "radius")),
            BorderBrush = Brush(n["border"]),
            BorderThickness = new Thickness(Num(n, "borderWidth")),
        };
        if (Num(n, "w") > 0) border.Width = Num(n, "w");
        if (Num(n, "h") > 0) border.Height = Num(n, "h");
        Link(border, (string?)n["url"]);
        return border;
    }

    private static double Num(JsonArray a, int i) => a[i]?.GetValue<double?>() ?? 0;

    // ---- text ----

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    private static FrameworkElement Text(JsonNode n)
    {
        var tb = new TextBlock
        {
            Text = (string?)n["text"] ?? "",
            Foreground = Brush(n["color"]) ?? new SolidColorBrush(DefaultText),
            Opacity = n["opacity"]?.GetValue<double?>() ?? 1,
            TextAlignment = (string?)n["align"] switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left },
            FontSize = 17,
        };
        if (Application.Current?.TryFindResource("Font.UI") is FontFamily ui) tb.FontFamily = ui;
        if (n["font"] is JsonObject f)
        {
            tb.FontSize = Math.Clamp(f["size"]?.GetValue<double?>() ?? 17, 4, 120);
            string kind = (string?)f["kind"] ?? "system";
            if (kind == "mono") tb.FontFamily = Mono;
            else if (kind == "named" && !string.IsNullOrWhiteSpace((string?)f["name"])) tb.FontFamily = new FontFamily((string)f["name"]! + ", Segoe UI");
            string weight = (string?)f["weight"] ?? "regular";
            tb.FontWeight = weight switch
            {
                "ultraLight" => FontWeights.ExtraLight, "thin" => FontWeights.Thin, "light" => FontWeights.Light,
                "medium" => FontWeights.Medium, "semibold" => FontWeights.SemiBold, "bold" => FontWeights.Bold,
                "heavy" => FontWeights.ExtraBold, "black" => FontWeights.Black, _ => FontWeights.Normal,
            };
            if (weight == "italic") tb.FontStyle = FontStyles.Italic;
        }
        int lines = n["lines"]?.GetValue<int?>() ?? 0;
        if (lines == 1) { tb.TextWrapping = TextWrapping.NoWrap; tb.TextTrimming = TextTrimming.CharacterEllipsis; }
        else
        {
            tb.TextWrapping = TextWrapping.Wrap;
            if (lines > 1) { tb.TextTrimming = TextTrimming.CharacterEllipsis; tb.MaxHeight = lines * tb.FontSize * 1.3; }
        }
        Link(tb, (string?)n["url"]);
        return tb;
    }

    // ---- helpers ----

    private static Brush? Brush(JsonNode? c)
    {
        if (c is null) return null;
        try
        {
            var hex = ((string?)c["hex"] ?? "#000000").TrimStart('#');
            if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
            if (hex.Length == 8) hex = hex[..6]; // RRGGBBAA: alpha comes from the alpha value
            if (hex.Length != 6) return null;
            byte r = Convert.ToByte(hex[..2], 16), g = Convert.ToByte(hex[2..4], 16), b = Convert.ToByte(hex[4..6], 16);
            double a = Math.Clamp(c["a"]?.GetValue<double?>() ?? 1, 0, 1);
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(a * 255), r, g, b));
            brush.Freeze();
            return brush;
        }
        catch { return null; }
    }

    /// <summary>Tap opens an http(s) link in the browser (nothing else).</summary>
    private static void Link(FrameworkElement el, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
        el.Cursor = Cursors.Hand;
        el.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        };
    }
}
