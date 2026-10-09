using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicBay.Core;
using DynamicBay.Island;
using DynamicBay.Services;

namespace DynamicBay.Settings;

public sealed partial class SettingsContext : ObservableObject
{
    public AppSettings S { get; }
    public IslandViewModel I { get; }
    [ObservableProperty] private int _page;
    public string RedirectUri => SpotifyService.RedirectUri;
    public string VersionText => (Loc.German ? "Version " : "Version ") + App.Version;
    public string ScreenshotFolders => string.Join("\n", ClipboardService.ScreenshotFolders());
    public string NotificationStatus => I.Notifications.Access switch
    {
        NotificationAccess.Allowed => Loc.German ? "Zugriff erlaubt: Mitteilungen aller Apps werden gespiegelt." : "Access granted: notifications from all apps are mirrored.",
        NotificationAccess.Denied => Loc.German ? "Zugriff verweigert. Erlaube ihn unter Windows-Einstellungen, Datenschutz, Benachrichtigungen." : "Access denied. Allow it in Windows Settings, Privacy, Notifications.",
        _ => Loc.T("Notif.Unavailable") + ". " + Loc.T("Notif.UnavailableHint") + ".",
    };

    public SettingsContext(AppSettings s, IslandViewModel i)
    {
        S = s;
        I = i;
    }
}

public partial class SettingsWindow : Window
{
    private readonly SettingsContext _ctx;

    public SettingsWindow(AppSettings settings, IslandViewModel vm)
    {
        InitializeComponent();
        Resources.MergedDictionaries.Add(SettingsTheme.Create(SettingsTheme.IsLight()));
        _ctx = new SettingsContext(settings, vm);
        DataContext = _ctx;
        SourceInitialized += (_, _) => ApplyBackdrop();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.Edge) or nameof(AppSettings.Align) or nameof(AppSettings.Along)) UpdatePreview();
        };
        UpdatePreview();
        BuildWidgetRows();
        BuildCompactRows();
    }

    private void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = SettingsTheme.IsLight() ? 0 : 1;
        Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // immersive dark title bar
        bool win11 = Environment.OSVersion.Version.Build >= 22000;
        if (win11)
        {
            int mica = 2; // DWMSBT_MAINWINDOW
            Native.DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int));
        }
        else
        {
            Background = (System.Windows.Media.Brush)FindResource("S.Window");
        }
    }

    // Sidebar position -> page id ("Widgets und Module" is page 11 but sits second in the sidebar).
    private static readonly int[] NavToPage = { 0, 1, 11, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

    public void ShowPage(int page) => Nav.SelectedIndex = Array.IndexOf(NavToPage, page);

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ctx is null || Nav.SelectedIndex < 0) return;
        _ctx.Page = NavToPage[Nav.SelectedIndex];
        PageScroll.ScrollToTop();
    }

    // ---- widgets & modules ----

    private static (string id, string icon, string de, string en, string descDe, string descEn)[] HomeWidgetInfo => new[]
    {
        (Widgets.Media, "Icon.Music", "Medien", "Media", "Cover, Titel und Steuerung", "Artwork, title and controls"),
        (Widgets.Clock, "Icon.Clock", "Uhr", "Clock", "Große Uhrzeit mit Datum", "Large time with date"),
        (Widgets.Calendar, "Icon.Calendar", "Kalender", "Calendar", "Woche und nächster Termin", "Week and next event"),
        (Widgets.Timer, "Icon.Timer", "Timer", "Timer", "Timer und Fokus-Sitzungen", "Timers and focus sessions"),
        (Widgets.System, "Icon.Sliders", "System", "System", "CPU- und Speicherauslastung", "CPU and memory load"),
        (Widgets.Shortcuts, "Icon.AppWindow", "Schnellstart", "Launcher", "Angepinnte Apps und Ordner", "Pinned apps and folders"),
    };

    private static (string id, string icon, string de, string en)[] CompactInfo => new[]
    {
        (Widgets.Media, "Icon.Music", "Musik (während der Wiedergabe)", "Music (while playing)"),
        (Widgets.Timer, "Icon.Timer", "Laufender Timer", "Running timer"),
        (Widgets.Calendar, "Icon.Calendar", "Termin beginnt bald", "Event starting soon"),
        (Widgets.Battery, "Icon.BatteryLow", "Akku schwach", "Low battery"),
        (Widgets.Clock, "Icon.Clock", "Uhrzeit (immer)", "Time (always)"),
    };

    private void BuildWidgetRows()
    {
        var s = _ctx.S;
        WidgetRows.Children.Clear();
        // Enabled widgets first (in their order), then the rest.
        var ordered = s.HomeWidgets.Where(id => HomeWidgetInfo.Any(w => w.id == id))
            .Concat(HomeWidgetInfo.Select(w => w.id).Where(id => !s.HomeWidgets.Contains(id))).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var info = HomeWidgetInfo.First(w => w.id == ordered[i]);
            bool on = s.HomeWidgets.Contains(info.id);
            int pos = s.HomeWidgets.IndexOf(info.id);

            var sw = new CheckBox { Style = (Style)FindResource("S.Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
            sw.IsEnabled = on || s.HomeWidgets.Count < 4;
            string id = info.id;
            sw.Click += (_, _) =>
            {
                if (sw.IsChecked == true && !s.HomeWidgets.Contains(id)) s.HomeWidgets.Add(id);
                else if (sw.IsChecked != true) s.HomeWidgets.Remove(id);
                BuildWidgetRows();
            };

            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(ArrowButton("Icon.ChevronUp", on && pos > 0, () => Move(id, -1)));
            controls.Children.Add(ArrowButton("Icon.ChevronDown", on && pos >= 0 && pos < s.HomeWidgets.Count - 1, () => Move(id, +1)));
            controls.Children.Add(new Border { Width = 12 });
            controls.Children.Add(sw);

            WidgetRows.Children.Add(Row(info.icon, Loc.German ? info.de : info.en, Loc.German ? info.descDe : info.descEn, controls, i > 0));
        }
    }

    private void Move(string id, int delta)
    {
        var list = _ctx.S.HomeWidgets;
        int i = list.IndexOf(id), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        list.Move(i, j);
        BuildWidgetRows();
    }

    private Button ArrowButton(string icon, bool enabled, Action click)
    {
        var b = new Button
        {
            Style = (Style)FindResource("S.Button"),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(4, 0, 0, 0),
            IsEnabled = enabled,
            Content = new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource(icon), Width = 13, Height = 13 },
        };
        b.Click += (_, _) => click();
        return b;
    }

    // ---- launcher apps ----

    private void PickInstalledApps_Click(object sender, RoutedEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        List<InstalledApp> apps;
        try { apps = InstalledApps.All(refresh: true); }
        finally { Mouse.OverrideCursor = null; }
        ApplyPick(apps, Loc.German ? "Apps auswählen" : "Choose apps",
            Loc.German ? "Alle installierten Apps, wie im Startmenü." : "All installed apps, like in the Start menu.");
    }

    private void PickFromFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.German ? "Ordner mit Verknüpfungen wählen" : "Choose a folder with shortcuts" };
        if (dlg.ShowDialog(this) != true) return;
        var apps = InstalledApps.FromFolder(dlg.FolderName);
        ApplyPick(apps, System.IO.Path.GetFileName(dlg.FolderName), dlg.FolderName);
    }

    /// <summary>Checked items are added, unchecked ones (from this list) removed; everything else stays.</summary>
    private void ApplyPick(List<InstalledApp> source, string heading, string hint)
    {
        var picker = new AppPickerWindow(source, _ctx.S.Shortcuts, heading, hint) { Owner = this };
        if (picker.ShowDialog() != true) return;
        var chosen = new HashSet<string>(picker.Selected, StringComparer.OrdinalIgnoreCase);
        foreach (var app in source)
        {
            var existing = _ctx.S.Shortcuts.FirstOrDefault(s => string.Equals(s, app.LaunchPath, StringComparison.OrdinalIgnoreCase));
            if (chosen.Contains(app.LaunchPath) && existing is null && _ctx.S.Shortcuts.Count < ShortcutsService.MaxItems)
                _ctx.S.Shortcuts.Add(app.LaunchPath);
            else if (!chosen.Contains(app.LaunchPath) && existing is not null)
                _ctx.S.Shortcuts.Remove(existing);
        }
        // Picking apps implies wanting to see them.
        if (_ctx.S.Shortcuts.Count > 0 && !_ctx.S.HomeWidgets.Contains(Widgets.Shortcuts) && _ctx.S.HomeWidgets.Count < 4)
        {
            _ctx.S.HomeWidgets.Add(Widgets.Shortcuts);
            BuildWidgetRows();
        }
    }

    private void RemoveShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ShortcutItem item) _ctx.I.Shortcuts.Remove(item);
    }

    private void BuildCompactRows()
    {
        var s = _ctx.S;
        CompactRows.Children.Clear();
        int i = 0;
        foreach (var info in CompactInfo)
        {
            var sw = new CheckBox { Style = (Style)FindResource("S.Switch"), IsChecked = s.CompactItems.Contains(info.id) };
            string id = info.id;
            sw.Click += (_, _) =>
            {
                if (sw.IsChecked == true && !s.CompactItems.Contains(id)) s.CompactItems.Add(id);
                else if (sw.IsChecked != true) s.CompactItems.Remove(id);
            };
            CompactRows.Children.Add(Row(info.icon, Loc.German ? info.de : info.en, null, sw, i++ > 0));
        }
    }

    private FrameworkElement Row(string icon, string label, string? desc, FrameworkElement control, bool divider)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(control, Dock.Right);
        control.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(control);
        var ic = new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource(icon), Width = 18, Height = 18, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(ic, Dock.Left);
        dock.Children.Add(ic);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Label"), Text = label });
        if (desc is not null) text.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Desc"), Text = desc });
        dock.Children.Add(text);
        return new Border
        {
            Padding = new Thickness(16, 11, 16, 11),
            MinHeight = 56,
            BorderBrush = (System.Windows.Media.Brush)FindResource("S.Divider"),
            BorderThickness = new Thickness(0, divider ? 1 : 0, 0, 0),
            Child = dock,
        };
    }

    // ---- general ----

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None) return; // require at least one modifier
        _ctx.S.ToggleHotkey = Hotkey.Format(mods, key);
        Keyboard.ClearFocus();
    }

    // ---- position ----

    private void Anchor_Click(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((FrameworkElement)sender).Tag).Split(',');
        var edge = Enum.Parse<IslandEdge>(parts[0]);
        var align = Enum.Parse<IslandAlign>(parts[1]);
        _ctx.S.Edge = edge;
        _ctx.S.Align = align;
        bool horizontalEdge = edge is IslandEdge.Top or IslandEdge.Bottom;
        double span = horizontalEdge ? SystemParameters.WorkArea.Width : SystemParameters.WorkArea.Height;
        _ctx.S.Along = align switch
        {
            IslandAlign.Start => 8 / span,
            IslandAlign.End => 1 - 8 / span,
            _ => 0.5,
        };
        _ctx.S.Inset = 8;
    }

    private void UpdatePreview()
    {
        var s = _ctx.S;
        bool vertical = s.Edge is IslandEdge.Left or IslandEdge.Right;
        PreviewIsland.Width = vertical ? 6 : 44;
        PreviewIsland.Height = vertical ? 44 : 6;
        // Map the real placement into the 240x150 preview (inner area inset by the bezel).
        double innerW = 240 - 12 - 24, innerH = 150 - 12 - 24;
        double along = Math.Clamp(s.Along, 0, 1);
        PreviewIsland.HorizontalAlignment = HorizontalAlignment.Left;
        PreviewIsland.VerticalAlignment = VerticalAlignment.Top;
        double x, y;
        if (!vertical)
        {
            double ax = 18 + along * innerW;
            x = s.Align switch { IslandAlign.Start => ax, IslandAlign.End => ax - 44, _ => ax - 22 };
            y = s.Edge == IslandEdge.Top ? 12 : 150 - 12 - 6;
        }
        else
        {
            double ay = 18 + along * innerH;
            y = s.Align switch { IslandAlign.Start => ay, IslandAlign.End => ay - 44, _ => ay - 22 };
            x = s.Edge == IslandEdge.Left ? 12 : 240 - 12 - 6;
        }
        PreviewIsland.Margin = new Thickness(Math.Clamp(x, 8, 240 - 8 - PreviewIsland.Width), Math.Clamp(y, 8, 150 - 8 - PreviewIsland.Height), 0, 0);
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _ctx.S.Edge = IslandEdge.Top;
        _ctx.S.Align = IslandAlign.Center;
        _ctx.S.Along = 0.5;
        _ctx.S.Inset = 8;
    }

    // ---- excluded apps ----

    private void AddExcluded_Click(object sender, RoutedEventArgs e) => AddExcluded(ExcludeBox.Text);

    private void AddExcluded(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        if (!_ctx.S.ExcludedApps.Contains(name, StringComparer.OrdinalIgnoreCase)) _ctx.S.ExcludedApps.Add(name);
        ExcludeBox.Text = "";
        _ctx.S.SaveSoon();
    }

    private void RemoveExcluded_Click(object sender, RoutedEventArgs e)
    {
        _ctx.S.ExcludedApps.Remove((string)((FrameworkElement)sender).Tag);
        _ctx.S.SaveSoon();
    }

    private void PickRunningApp_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var names = Process.GetProcesses()
            .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.ProcessName != "DynamicBay"; } catch { return false; } })
            .Select(p => p.ProcessName + ".exe")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            var item = new MenuItem { Header = n };
            item.Click += (_, _) => AddExcluded(n);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---- notifications ----

    private void OpenWindowsNotifications_Click(object sender, RoutedEventArgs e) => NotificationService.OpenWindowsSettings();

    private void RemoveMuted_Click(object sender, RoutedEventArgs e)
    {
        _ctx.S.MutedApps.Remove((string)((FrameworkElement)sender).Tag);
        _ctx.S.SaveSoon();
    }

    private void PickMuted_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        foreach (var app in _ctx.I.Notifications.Items.Select(n => n.App).Distinct().OrderBy(a => a))
        {
            var item = new MenuItem { Header = app };
            item.Click += (_, _) =>
            {
                if (!_ctx.S.MutedApps.Contains(app)) _ctx.S.MutedApps.Add(app);
                _ctx.S.SaveSoon();
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = Loc.T("Notif.Empty"), IsEnabled = false });
        menu.IsOpen = true;
    }

    // ---- spotify / about ----

    private void OpenSpotifyDashboard_Click(object sender, RoutedEventArgs e) => Open("https://developer.spotify.com/dashboard");

    private async void CopyRedirect_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(SpotifyService.RedirectUri); } catch { }
        CopyRedirectText.Text = Loc.T("Clip.Copied");
        await Task.Delay(1500);
        CopyRedirectText.Text = Loc.T("Clip.Copy");
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => Open($"https://github.com/{UpdateCheck.Repo}");

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppSettings.Folder);
        Open(AppSettings.Folder);
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
