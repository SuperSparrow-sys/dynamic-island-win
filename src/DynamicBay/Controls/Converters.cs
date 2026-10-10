using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DynamicBay.Controls;

/// <summary>Device type -> its symbol.</summary>
public sealed class DeviceKindIcon : IValueConverter
{
    public object? Convert(object value, Type t, object p, CultureInfo c) => Application.Current.TryFindResource(value switch
    {
        DynamicBay.Services.DeviceKind.Headphones => "Icon.Headphones",
        DynamicBay.Services.DeviceKind.Speaker => "Icon.Speaker",
        DynamicBay.Services.DeviceKind.Keyboard => "Icon.Keyboard",
        DynamicBay.Services.DeviceKind.Mouse => "Icon.Mouse",
        DynamicBay.Services.DeviceKind.Phone => "Icon.Phone",
        _ => "Icon.Bluetooth",
    });
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Battery percent (int or null) -> ring value 0..1, colour (green, orange under 20 %, red under 10 %) or label.</summary>
public sealed class BatteryRing : IValueConverter
{
    public string Mode { get; set; } = "value";
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        int? pct = value as int?;
        return Mode switch
        {
            "brush" => Application.Current.TryFindResource(pct is null ? "B.Text3" : pct < 10 ? "B.Red" : pct < 20 ? "B.Orange" : "B.Green")!,
            "text" => pct is null ? "–" : $"{pct} %",
            _ => pct is null ? 0.0 : pct.Value / 100.0,
        };
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Readable app name for an entry of "hide while these apps are active".</summary>
public sealed class ExcludedLabel : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is string s ? Core.ExcludedApps.Label(s) : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class BoolToVis : IValueConverter
{
    public bool Invert { get; set; }
    /// <summary>Hidden instead of Collapsed: keeps the space (e.g. event dots under the week days).</summary>
    public bool KeepSpace { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value switch
        {
            bool x => x,
            int i => i != 0,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        return b ^ Invert ? Visibility.Visible : KeepSpace ? Visibility.Hidden : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class Percent01 : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is double d ? d / 100.0 : 0.0;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

public sealed class EqualsToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EqualsToBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
    {
        if (v is not true) return Binding.DoNothing;
        if (t.IsEnum) return Enum.Parse(t, p.ToString()!);
        if (t == typeof(int)) return int.Parse(p.ToString()!, CultureInfo.InvariantCulture);
        return p;
    }
}

/// <summary>Picks a value for Spotify's repeat state ("off", "context", "track").</summary>
public sealed class RepeatPick : IValueConverter
{
    public object? Off { get; set; }
    public object? Context { get; set; }
    public object? Track { get; set; }
    public object? Convert(object value, Type t, object p, CultureInfo c) => value as string switch { "track" => Track, "context" => Context, _ => Off };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Picks between two values: Converter={c:BoolPick True=..., False=...}</summary>
public sealed class BoolPick : IValueConverter
{
    public object? True { get; set; }
    public object? False { get; set; }
    public object? Convert(object value, Type t, object p, CultureInfo c) => value is true ? True : False;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class RatioConverter : IMultiValueConverter
{
    /// <summary>values: current, max, width</summary>
    public object Convert(object[] values, Type t, object p, CultureInfo c)
    {
        if (values.Length < 3 || values[0] is not double cur || values[1] is not double max || values[2] is not double w) return 0.0;
        return max <= 0 ? 0.0 : Math.Clamp(cur / max, 0, 1) * w;
    }
    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class PercentToWidth : IMultiValueConverter
{
    /// <summary>values: percent (0..100 int), width</summary>
    public object Convert(object[] values, Type t, object p, CultureInfo c)
    {
        if (values.Length < 2 || values[1] is not double w) return 0.0;
        double pct = values[0] switch { int i => i, double d => d, _ => 0 };
        return Math.Clamp(pct / 100.0, 0, 1) * w;
    }
    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ColorToBrush : IValueConverter
{
    public double Opacity { get; set; } = 1;
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not Color col) return Brushes.Transparent;
        var b = new SolidColorBrush(col) { Opacity = Opacity };
        b.Freeze();
        return b;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
