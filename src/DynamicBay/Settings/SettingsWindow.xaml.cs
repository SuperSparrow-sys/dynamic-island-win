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

    public void ShowPage(int index) => Nav.SelectedIndex = index;

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ctx is null) return;
        _ctx.Page = Nav.SelectedIndex;
        PageScroll.ScrollToTop();
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
