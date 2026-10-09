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
        BuildCalendarRows();
        BuildBannerRows();
        UpdateClaudeShareText();
        UpdateUpdateRow();
        UpdateCheck.PendingChanged += () => Dispatcher.BeginInvoke(UpdateUpdateRow);
        vm.Calendar.StatusChanged += () => Dispatcher.BeginInvoke(BuildCalendarRows);
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
        (Widgets.Claude, "Icon.Sparkles", "Claude", "Claude", "Claude-Code-Sitzungen und Agenten-Status", "Claude Code sessions and agent status"),
        (Widgets.Messenger, "Icon.Mail", "Nachrichten", "Messages", "WhatsApp, Telegram, Signal, Discord und Co.", "WhatsApp, Telegram, Signal, Discord and more"),
    };

    private static (string id, string icon, string de, string en)[] CompactInfo => new[]
    {
        (Widgets.Media, "Icon.Music", "Musik (während der Wiedergabe)", "Music (while playing)"),
        (Widgets.Timer, "Icon.Timer", "Laufender Timer", "Running timer"),
        (Widgets.Calendar, "Icon.Calendar", "Termin beginnt bald", "Event starting soon"),
        (Widgets.Battery, "Icon.BatteryLow", "Akku schwach", "Low battery"),
        (Widgets.Clock, "Icon.Clock", "Uhrzeit (immer)", "Time (always)"),
        (Widgets.Claude, "Icon.Sparkles", "Claude arbeitet oder wartet", "Claude working or waiting"),
        (Widgets.Mic, "Icon.Mic", "Mikrofon oder Kamera aktiv", "Microphone or camera in use"),
        (Widgets.Muted, "Icon.VolumeOff", "Ton aus", "Sound off"),
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
            sw.IsEnabled = true; // no limit: the Nook page scrolls when widgets need more room
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

    // ---- update ----

    private void UpdateUpdateRow()
    {
        var p = UpdateCheck.Pending;
        UpdateStatus.Text = p is null
            ? (Loc.German ? $"Version {App.Version} ist aktuell" : $"Version {App.Version} is up to date")
            : (Loc.German ? $"Version {p.Version.ToString(3)} verfügbar" : $"Version {p.Version.ToString(3)} available");
        UpdateButton.Content = Loc.T("Update.Install");
        UpdateButton.Visibility = p is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await UpdateCheck.InstallAsync();

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatus.Text = Loc.German ? "Suche nach Updates…" : "Checking for updates…";
        try { await UpdateCheck.CheckNowAsync(); UpdateUpdateRow(); }
        catch { UpdateStatus.Text = Loc.German ? "GitHub nicht erreichbar" : "GitHub not reachable"; }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    // ---- claude ----

    private void UpdateClaudeShareText() =>
        ClaudeShareText.Text = string.IsNullOrEmpty(_ctx.S.ClaudeShareFolder) ? (Loc.German ? "Aus" : "Off") : _ctx.S.ClaudeShareFolder;

    private void PickClaudeShare_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var off = new MenuItem { Header = Loc.German ? "Aus" : "Off" };
        off.Click += (_, _) => { _ctx.S.ClaudeShareFolder = ""; UpdateClaudeShareText(); };
        menu.Items.Add(off);
        foreach (var t in CloudTargets.Detect())
        {
            var item = new MenuItem { Header = t.Name };
            string root = t.Root;
            item.Click += (_, _) => { _ctx.S.ClaudeShareFolder = root; UpdateClaudeShareText(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---- banner suppression ----

    private void BuildBannerRows()
    {
        BannerRows.Children.Clear();
        foreach (var id in _ctx.S.SuppressBannerApps)
        {
            var app = id;
            var remove = new Button { Style = (Style)FindResource("S.Button"), Padding = new Thickness(8, 4, 8, 4),
                Content = new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource("Icon.Close"), Width = 12, Height = 12 } };
            remove.Click += (_, _) => { _ctx.S.SuppressBannerApps.Remove(app); BuildBannerRows(); };
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(remove);
            var tile = new ContentPresenter
            {
                Content = ShortcutsService.Create(@"shell:AppsFolder\" + app, AppIconStyle.Mono),
                ContentTemplate = (DataTemplate)FindResource("AppTile"),
                Margin = new Thickness(0, 0, 12, 0),
                LayoutTransform = new System.Windows.Media.ScaleTransform(0.75, 0.75),
            };
            DockPanel.SetDock(tile, Dock.Left);
            dock.Children.Add(tile);
            dock.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Base"), VerticalAlignment = VerticalAlignment.Center, Text = BannerSuppressor.NameOf(app) });
            BannerRows.Children.Add(dock);
        }
    }

    private void AddBannerApp_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        // Apps that recently sent notifications, plus common candidates that are installed.
        var ids = _ctx.I.Notifications.Items.Select(n => n.AppId).Where(id => id.Length > 0).ToList();
        ids.Add(BannerSuppressor.SnippingTool);
        foreach (var known in new[] { "WhatsApp", "Discord", "Microsoft Teams", "Outlook", "Telegram", "Signal", "Claude", "Spotify" })
        {
            var app = InstalledApps.All().FirstOrDefault(a => a.Name.StartsWith(known, StringComparison.OrdinalIgnoreCase));
            if (app is not null) ids.Add(app.LaunchPath.Replace(@"shell:AppsFolder\", ""));
        }
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase).Where(id => !_ctx.S.SuppressBannerApps.Contains(id)))
        {
            var item = new MenuItem { Header = BannerSuppressor.NameOf(id) };
            string appId = id;
            item.Click += (_, _) => { _ctx.S.SuppressBannerApps.Add(appId); BuildBannerRows(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---- calendar accounts ----

    private void BuildCalendarRows()
    {
        CalendarRows.Children.Clear();
        int i = 0;
        foreach (var a in _ctx.S.CalendarAccounts)
        {
            var account = a;
            string kind = a.Kind switch { CalendarKind.ICloud => "iCloud", CalendarKind.Google => "Google", _ => "ICS" };
            string icon = a.Kind switch { CalendarKind.ICloud => "Icon.Cloud", CalendarKind.Google => "Icon.User", _ => "Icon.Link" };
            string status = _ctx.I.Calendar.StatusOf(a);
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            var sw = new CheckBox { Style = (Style)FindResource("S.Switch"), IsChecked = a.Enabled, Margin = new Thickness(0, 0, 10, 0) };
            sw.Click += (_, _) => { account.Enabled = sw.IsChecked == true; SaveAccounts(); };
            controls.Children.Add(sw);
            var remove = new Button { Style = (Style)FindResource("S.Button"), Padding = new Thickness(8, 4, 8, 4),
                Content = new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource("Icon.Trash"), Width = 13, Height = 13 } };
            remove.Click += (_, _) =>
            {
                foreach (var k in new[] { "password", "secret", "refresh" }) SecretStore.Set($"cal:{account.Id}:{k}", null);
                _ctx.S.CalendarAccounts.Remove(account);
                BuildCalendarRows();
            };
            controls.Children.Add(remove);
            string label = $"{a.Name}  ·  {kind}" + (a.Kind == CalendarKind.ICloud ? $"  ·  {a.User}" : "");
            var row = Row(icon, label, status.Length > 0 ? status : null, controls, i++ > 0);
            ((Border)row).Padding = new Thickness(0, 8, 0, 8);
            CalendarRows.Children.Add(row);
        }
        if (_ctx.S.CalendarAccounts.Count == 0)
            CalendarRows.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Desc"), Margin = new Thickness(0, 0, 0, 8),
                Text = Loc.German ? "Noch kein Kalender verbunden." : "No calendar connected yet." });
    }

    /// <summary>Accounts are plain objects; re-adding forces change notification + save + refresh.</summary>
    private void SaveAccounts()
    {
        var list = _ctx.S.CalendarAccounts.ToList();
        _ctx.S.CalendarAccounts.Clear();
        foreach (var a in list) _ctx.S.CalendarAccounts.Add(a);
        _ctx.S.SaveSoon();
    }

    private void ShowCalForm(FrameworkElement form)
    {
        ICloudForm.Visibility = GoogleForm.Visibility = IcsForm.Visibility = Visibility.Collapsed;
        form.Visibility = Visibility.Visible;
        form.BringIntoView();
    }

    private void AddICloud_Click(object sender, RoutedEventArgs e) => ShowCalForm(ICloudForm);
    private void AddGoogle_Click(object sender, RoutedEventArgs e) => ShowCalForm(GoogleForm);
    private void AddIcs_Click(object sender, RoutedEventArgs e) => ShowCalForm(IcsForm);
    private void CloseCalForms_Click(object sender, RoutedEventArgs e) =>
        ICloudForm.Visibility = GoogleForm.Visibility = IcsForm.Visibility = Visibility.Collapsed;
    private void OpenAppleId_Click(object sender, RoutedEventArgs e) => Open("https://appleid.apple.com/account/manage");
    private void OpenGoogleConsole_Click(object sender, RoutedEventArgs e) => Open("https://console.cloud.google.com/apis/library/calendar-json.googleapis.com");

    /// <summary>Tests the account before saving it, so users get immediate feedback.</summary>
    private async Task<bool> TestAndAddAsync(CalendarAccount account, TextBlock status)
    {
        status.Text = Loc.German ? "Verbinde…" : "Connecting…";
        try
        {
            var events = await _ctx.I.Calendar.FetchAsync(account, DateTime.Today, DateTime.Today.AddDays(7));
            _ctx.S.CalendarAccounts.Add(account);
            status.Text = Loc.German ? $"Verbunden. {events.Count} Termine in den nächsten 7 Tagen gefunden." : $"Connected. Found {events.Count} events in the next 7 days.";
            BuildCalendarRows();
            return true;
        }
        catch (Exception ex)
        {
            status.Text = (Loc.German ? "Fehlgeschlagen: " : "Failed: ") + ex.Message;
            return false;
        }
    }

    private async void SaveICloud_Click(object sender, RoutedEventArgs e)
    {
        var account = new CalendarAccount { Kind = CalendarKind.ICloud, Name = "iCloud", User = ICloudUser.Text.Trim(), Url = "https://caldav.icloud.com/" };
        SecretStore.Set($"cal:{account.Id}:password", ICloudPassword.Password.Trim());
        if (await TestAndAddAsync(account, ICloudStatus)) { ICloudPassword.Clear(); }
        else SecretStore.Set($"cal:{account.Id}:password", null);
    }

    private async void SaveGoogle_Click(object sender, RoutedEventArgs e)
    {
        var account = new CalendarAccount { Kind = CalendarKind.Google, Name = "Google", User = GoogleClientId.Text.Trim() };
        try
        {
            GoogleStatus.Text = Loc.German ? "Bitte im Browser anmelden…" : "Please sign in in the browser…";
            var client = new Services.Calendars.GoogleCalendarClient(account.User, GoogleSecret.Password, null);
            await client.SignInAsync();
            SecretStore.Set($"cal:{account.Id}:secret", GoogleSecret.Password.Trim());
            SecretStore.Set($"cal:{account.Id}:refresh", client.RefreshToken);
            if (!await TestAndAddAsync(account, GoogleStatus))
                foreach (var k in new[] { "secret", "refresh" }) SecretStore.Set($"cal:{account.Id}:{k}", null);
        }
        catch (Exception ex) { GoogleStatus.Text = (Loc.German ? "Fehlgeschlagen: " : "Failed: ") + ex.Message; }
    }

    private async void SaveIcs_Click(object sender, RoutedEventArgs e)
    {
        var account = new CalendarAccount
        {
            Kind = CalendarKind.Ics, Url = IcsUrl.Text.Trim(),
            Name = string.IsNullOrWhiteSpace(IcsName.Text) ? (Loc.German ? "Kalender" : "Calendar") : IcsName.Text.Trim(),
        };
        if (await TestAndAddAsync(account, IcsStatus)) { IcsUrl.Clear(); IcsName.Clear(); }
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
        if (_ctx.S.Shortcuts.Count > 0 && !_ctx.S.HomeWidgets.Contains(Widgets.Shortcuts))
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

    private void MicHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (Keyboard.Modifiers == ModifierKeys.None) return;
        _ctx.S.MicMuteHotkey = Hotkey.Format(Keyboard.Modifiers, key);
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
