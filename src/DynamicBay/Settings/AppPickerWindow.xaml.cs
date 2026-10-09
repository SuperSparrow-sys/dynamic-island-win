using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;
using DynamicBay.Services;

namespace DynamicBay.Settings;

public sealed partial class PickItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>Searchable checklist of apps (installed apps or the contents of a shortcut folder).</summary>
public partial class AppPickerWindow : Window
{
    private readonly List<PickItem> _all;
    public IReadOnlyList<string> Selected { get; private set; } = Array.Empty<string>();

    public AppPickerWindow(IEnumerable<InstalledApp> apps, IEnumerable<string> alreadyPinned, string heading, string hint)
    {
        InitializeComponent();
        Resources.MergedDictionaries.Add(SettingsTheme.Create(SettingsTheme.IsLight()));
        Heading.Text = heading;
        Hint.Text = hint;
        OkButton.Content = Loc.German ? "Hinzufügen" : "Add";
        var pinned = new HashSet<string>(alreadyPinned, StringComparer.OrdinalIgnoreCase);
        _all = apps.Select(a => new PickItem { Name = a.Name, Path = a.LaunchPath, IsChecked = pinned.Contains(a.LaunchPath) }).ToList();
        List.ItemsSource = _all;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = SettingsTheme.IsLight() ? 0 : 1, mica = 2;
            Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            if (Environment.OSVersion.Version.Build >= 22000) Native.DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int));
            else Background = (Brush)FindResource("S.Window");
        };
        Loaded += (_, _) => { Search.Focus(); LoadIconsLazily(); };
    }

    /// <summary>Icons are fetched in the background priority queue so the list opens instantly.</summary>
    private void LoadIconsLazily()
    {
        foreach (var item in _all)
        {
            var i = item;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => i.Icon = ShellThumbnail.Get(i.Path, 48, iconOnly: true));
        }
    }

    private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string q = Search.Text.Trim();
        List.ItemsSource = q.Length == 0 ? _all : _all.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.CheckBox>(d) is not null) return;
        if (((FrameworkElement)sender).DataContext is PickItem p) p.IsChecked = !p.IsChecked;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Selected = _all.Where(a => a.IsChecked).Select(a => a.Path).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
