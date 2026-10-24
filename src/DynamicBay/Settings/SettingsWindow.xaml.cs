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
        _theme = SettingsTheme.Create(SettingsTheme.IsLight());
        Resources.MergedDictionaries.Add(_theme);
        _ctx = new SettingsContext(settings, vm);
        DataContext = _ctx;
        SourceInitialized += (_, _) =>
        {
            ApplyBackdrop();
            Core.TaskbarIdentity.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle, Core.TaskbarIdentity.SettingsAppId, "DynamicBay");
        };
        System.ComponentModel.PropertyChangedEventHandler placementChanged = (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.Edge) or nameof(AppSettings.Align) or nameof(AppSettings.Along)) UpdatePreview();
            if (e.PropertyName is nameof(AppSettings.WindowTheme)) Dispatcher.BeginInvoke(ApplyTheme);
        };
        settings.PropertyChanged += placementChanged;
        UpdatePreview();
        ShowPage(SettingsPage.General);
        BuildWidgetBoard();
        BuildScriptRows();
        Action<string> scriptsUpdated = _ => Dispatcher.BeginInvoke(UpdateScriptStatus);
        vm.Scripts.Updated += scriptsUpdated;
        Closed += (_, _) => vm.Scripts.Updated -= scriptsUpdated;
        BuildCompactRows();
        BuildCalendarRows();
        BuildBannerRows();
        BuildPhoneFocusRows();
        BuildFocusProfileRows();
        UpdateClaudeShareText();
        UpdateTaildropFolderText();
        BuildScreenshotFolders();
        SetPath(ScriptFolderButton, ScriptFolderText, Services.Scripting.ScriptWidgetsService.Folder);
        SetPath(DataFolderButton, DataFolderText, AppSettings.Folder);
        BuildProjectRows();
        UpdateTimeFileText();
        BuildTodoListChoices();
        if (vm.Todo is { } todo)
        {
            System.Collections.Specialized.NotifyCollectionChangedEventHandler listsChanged = (_, _) => Dispatcher.BeginInvoke(BuildTodoListChoices);
            todo.Lists.CollectionChanged += listsChanged;
            Closed += (_, _) => todo.Lists.CollectionChanged -= listsChanged;
            if (todo.Lists.Count == 0) _ = todo.RefreshAsync();
        }
        _ctx.S.PropertyChanged += OnTimeProjectsChanged;
        Closed += (_, _) => _ctx.S.PropertyChanged -= OnTimeProjectsChanged;
        UpdateUpdateRow();
        DeviceNamePlaceholder.Text = Environment.MachineName;
        Action updateChanged = () => Dispatcher.BeginInvoke(UpdateUpdateRow);
        Action calendarChanged = () => Dispatcher.BeginInvoke(RefreshCalendarRows);
        UpdateCheck.PendingChanged += updateChanged;
        vm.Calendar.StatusChanged += calendarChanged;
        // Long-lived objects must not keep a closed window (and its whole page tree) alive.
        Closed += (_, _) =>
        {
            settings.PropertyChanged -= placementChanged;
            UpdateCheck.PendingChanged -= updateChanged;
            vm.Calendar.StatusChanged -= calendarChanged;
        };
    }

    private ResourceDictionary _theme;

    /// <summary>The theme was switched in the settings: new palette and title bar right away.</summary>
    private void ApplyTheme()
    {
        var next = SettingsTheme.Create(SettingsTheme.IsLight());
        Resources.MergedDictionaries.Remove(_theme);
        Resources.MergedDictionaries.Add(next);
        _theme = next;
        ApplyBackdrop();
        // Rows built in code took their brushes once: build them again with the new ones.
        BuildWidgetBoard();
        BuildScriptRows();
        BuildCompactRows();
        BuildCalendarRows();
        BuildBannerRows();
        BuildPhoneFocusRows();
        BuildFocusProfileRows();
        BuildProjectRows();
        BuildTodoListChoices();
        BuildScreenshotFolders();
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

    /// <summary>The sidebar entries that are pages (in SettingsPage order); section headings are skipped.</summary>
    private List<object> NavPages => Nav.Items.Cast<object>().Where(i => i is not ListBoxItem { Tag: "header" }).ToList();

    public void ShowPage(SettingsPage page) => Nav.SelectedItem = NavPages[(int)page];

    /// <summary>Opens a page and scrolls its group heading (German or English text start) to the top.</summary>
    public void ShowSection(SettingsPage page, string? group)
    {
        ShowPage(page);
        if (string.IsNullOrEmpty(group)) { PageScroll.ScrollToTop(); return; }
        Dispatcher.BeginInvoke(() =>
        {
            var heading = Find(PageScroll, group);
            if (heading is null) return;
            var y = heading.TransformToAncestor(PageScroll).Transform(new Point(0, 0)).Y;
            PageScroll.ScrollToVerticalOffset(Math.Max(0, PageScroll.VerticalOffset + y - 8));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static TextBlock? Find(DependencyObject root, string start)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is TextBlock t && t.IsVisible && t.Text.StartsWith(start, StringComparison.OrdinalIgnoreCase) && t.FontWeight == FontWeights.SemiBold) return t;
            if (c is UIElement { IsVisible: false }) continue;
            if (Find(c, start) is { } found) return found;
        }
        return null;
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ctx is null || NavPages.IndexOf(Nav.SelectedItem) is not (>= 0 and var index)) return;
        var sw = Stopwatch.StartNew();
        _ctx.Page = index;
        PageScroll.ScrollToTop();
        // Slow pages show up in the log (time until the page is laid out and rendered).
        int page = _ctx.Page;
        Dispatcher.BeginInvoke(() => { if (sw.ElapsedMilliseconds > 150) Log.Info($"Settings page {(SettingsPage)page} took {sw.ElapsedMilliseconds} ms"); }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void GoToAccount_Click(object sender, RoutedEventArgs e) =>
        ShowSection(SettingsPage.Accounts, ((FrameworkElement)sender).Tag is "Calendars" ? (Loc.German ? "Kalender" : "Calendars") : (string)((FrameworkElement)sender).Tag);

    private void GoToCompact_Click(object sender, RoutedEventArgs e) => ShowSection(SettingsPage.Content, Loc.German ? "Kompakte" : "Compact");

    // ---- widgets & modules ----

    private static (string id, string icon, string de, string en, string descDe, string descEn)[] HomeWidgetInfo => new[]
    {
        (Widgets.Media, "Icon.Music", "Medien", "Media", "Cover, Titel und Steuerung", "Artwork, title and controls"),
        (Widgets.Clock, "Icon.Clock", "Uhr", "Clock", "Große Uhrzeit mit Datum", "Large time with date"),
        (Widgets.Calendar, "Icon.Calendar", "Kalender", "Calendar", "Woche und Termine des Tages", "Week and the day’s events"),
        (Widgets.Timer, "Icon.Timer", "Timer", "Timer", "Timer und Fokuszeiten", "Timers and focus sessions"),
        (Widgets.System, "Icon.Sliders", "System", "System", "CPU- und Speicherauslastung", "CPU and memory load"),
        (Widgets.Shortcuts, "Icon.AppWindow", "Schnellstart", "Launcher", "Angepinnte Apps und Ordner", "Pinned apps and folders"),
        (Widgets.Claude, "Glyph.Claude", "Claude", "Claude", "Deine Sitzungen und ob Claude gerade arbeitet", "Your sessions and whether Claude is working"),
        (Widgets.Notes, "Icon.Pencil", "Notizen", "Notes", "Kurze Mitschriften, schnell geschrieben und wieder gelöscht", "Short notes, quickly written and deleted again"),
        (Widgets.Audio, "Icon.Headphones", "Audio", "Audio", "Ausgabe und Mikrofon wechseln, AirPods und andere Bluetooth-Geräte verbinden", "Switch output and microphone, connect AirPods and other Bluetooth devices"),
        (Widgets.Devices, "Icon.Bluetooth", "Geräte", "Devices", "Akkustand von PC, Kopfhörern, Tastatur und Maus", "Battery of this PC, headphones, keyboard and mouse"),
        (Widgets.AirPods, "Icon.Headphones", "AirPods", "AirPods", "Akku jedes Ohrhörers und des Case, verbinden", "Battery of each earbud and the case, connect"),
        (Widgets.CursorHop, "Icon.Mouse", "CursorHop", "CursorHop", "Verbundene Geräte und wo die Maus gerade ist", "Connected devices and where the mouse is"),
        (Widgets.Paired, "Icon.Devices", "Gekoppelte Geräte", "Paired devices", "Verbunden, Akku, Dateien hinüberziehen", "Connected, battery, drag files across"),
        (Widgets.Messenger, "Icon.Mail", "Nachrichten", "Messages", "WhatsApp, Telegram, Signal, Discord und Co.", "WhatsApp, Telegram, Signal, Discord and more"),
        (Widgets.Todo, "Icon.Check", "Aufgaben", "Tasks", "Microsoft To Do: offene Aufgaben abhaken", "Microsoft To Do: tick off open tasks"),
        (Widgets.Teams, "Icon.User", "Teams-Status", "Teams status", "Eigenen Teams-Status sehen und umschalten", "See and change your Teams status"),
        (Widgets.Contacts, "Icon.User", "Kontakte", "Contacts", "Häufige Kontakte: Teams-Chat, Teams-Anruf, Telefon über Smartphone-Link", "Frequent contacts: Teams chat, Teams call, phone via Phone Link"),
        (Widgets.TimeTrack, "Icon.Clock", "Zeiterfassung", "Time tracking", "Zeit pro Projekt starten und stoppen, CSV-Export", "Start and stop time per project, CSV export"),
    };

    private static (string id, string icon, string de, string en)[] CompactInfo => new[]
    {
        (Widgets.Media, "Icon.Music", "Musik (während der Wiedergabe)", "Music (while playing)"),
        (Widgets.Timer, "Icon.Timer", "Laufender Timer", "Running timer"),
        (Widgets.Calendar, "Icon.Calendar", "Termin beginnt bald", "Event starting soon"),
        (Widgets.Battery, "Icon.BatteryLow", "Akku schwach", "Low battery"),
        (Widgets.Clock, "Icon.Clock", "Uhrzeit (immer)", "Time (always)"),
        (Widgets.Claude, "Glyph.Claude", "Claude arbeitet oder wartet", "Claude working or waiting"),
        (Widgets.Mic, "Icon.Mic", "Mikrofon oder Kamera aktiv", "Microphone or camera in use"),
        (Widgets.Muted, "Icon.VolumeOff", "Ton aus", "Sound off"),
        (Widgets.TimeTrack, "Icon.Clock", "Zeiterfassung läuft", "Time tracking running"),
        (Widgets.Downloads, "Icon.Download", "Downloads (Fortschritt und fertige Datei)", "Downloads (progress and finished file)"),
    };

    /// <summary>Built-in widgets plus the user's script widgets.</summary>
    private (string id, string icon, string de, string en, string descDe, string descEn)[] AllWidgetInfo() =>
        HomeWidgetInfo
            // CursorHop only where it is installed (or already placed, so it can be removed again)
            .Where(w => w.id != Widgets.CursorHop || Services.CursorHopService.IsInstalledOnDisk || PlaceOf(w.id).Length > 0)
            // Paired devices only once there is one (or it is placed already)
            .Where(w => w.id != Widgets.Paired || _ctx.S.PairedDevices.Count > 0 || _ctx.I.Link?.HasDevices == true || PlaceOf(w.id).Length > 0)
            .Concat(_ctx.S.ScriptWidgets.Select(sc => (sc.HomeId, "Icon.Code", sc.Name, sc.Name, "Eigenes Skript-Widget", "Own script widget"))).ToArray();

    /// <summary>Where a widget is: "home" (Nook), "system" or "" (not shown).</summary>
    private string PlaceOf(string id) => _ctx.S.HomeWidgets.Contains(id) ? "home" : _ctx.S.SystemWidgets.Contains(id) ? "system" : "";

    /// <summary>Puts a widget on a page (or nowhere) at a position (-1 = at the end).</summary>
    private void Place(string id, string place, int index = -1)
    {
        var s = _ctx.S;
        var target = place == "home" ? s.HomeWidgets : place == "system" ? s.SystemWidgets : null;
        int old = target?.IndexOf(id) ?? -1;
        if (target is not null && old >= 0 && index >= 0)
        {
            // within the same page: just move it
            if (index > old) index--;
            index = Math.Clamp(index, 0, target.Count - 1);
            if (index != old) target.Move(old, index);
        }
        else
        {
            s.HomeWidgets.Remove(id);
            s.SystemWidgets.Remove(id);
            if (target is not null)
            {
                if (index < 0 || index > target.Count) target.Add(id); else target.Insert(index, id);
            }
        }
        BuildWidgetBoard();
    }

    /// <summary>Without dragging: move a widget to Nook, System or Off, or up and down in its column.</summary>
    private ContextMenu ChipMenu(string id)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            string place = PlaceOf(id);
            var list = place == "home" ? _ctx.S.HomeWidgets : place == "system" ? _ctx.S.SystemWidgets : null;
            int pos = list?.IndexOf(id) ?? -1;
            void Add(string header, bool enabled, Action click)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled };
                item.Click += (_, _) => Dispatcher.BeginInvoke(click);
                menu.Items.Add(item);
            }
            Add("Nook", place != "home", () => Place(id, "home"));
            Add("System", place != "system", () => Place(id, "system"));
            Add(Loc.German ? "Aus" : "Off", place != "", () => Place(id, ""));
            if (list is null) return;
            menu.Items.Add(new Separator());
            Add(Loc.German ? "Nach oben" : "Move up", pos > 0, () => Place(id, place, pos - 1));
            Add(Loc.German ? "Nach unten" : "Move down", pos < list.Count - 1, () => Place(id, place, pos + 2));
        };
        return menu;
    }

    // ---- script widgets ----

    private readonly Dictionary<string, TextBlock> _scriptStatus = new();

    /// <summary>A script ran: only its status line changes.</summary>
    private void UpdateScriptStatus()
    {
        foreach (var sc in _ctx.S.ScriptWidgets)
            if (_scriptStatus.TryGetValue(sc.Id, out var t)) SetDesc(t, _ctx.I.Scripts.StatusOf(sc));
    }

    private void BuildScriptRows()
    {
        ScriptRows.Children.Clear();
        _scriptStatus.Clear();
        var scripts = _ctx.S.ScriptWidgets.ToList();
        if (scripts.Count == 0)
        {
            ScriptRows.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("ST.Desc"), Margin = new Thickness(16, 14, 16, 2), TextWrapping = TextWrapping.Wrap,
                Text = Loc.German ? "Noch keine Skripte. „Neues Skript“ legt eine Vorlage an und öffnet sie im Editor."
                                  : "No scripts yet. \u201cNew script\u201d creates a template and opens it in the editor.",
            });
            return;
        }
        for (int i = 0; i < scripts.Count; i++)
        {
            var sc = scripts[i];
            var controls = new StackPanel { Orientation = Orientation.Horizontal };

            // Size of the card: Klein / Groß
            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (size, de, en) in new[] { (ScriptSize.Small, "Klein", "Small"), (ScriptSize.Large, "Groß", "Large") })
            {
                var rb = new RadioButton { Style = (Style)FindResource("S.Segment"), Content = Loc.German ? de : en, IsChecked = sc.Size == size, GroupName = "size-" + sc.Id };
                var chosen = size;
                rb.Checked += (_, _) => { if (sc.Size == chosen) return; sc.Size = chosen; _ctx.S.ScriptWidgetsChanged(); _ctx.I.Scripts.RunNow(sc); };
                seg.Children.Add(rb);
            }
            controls.Children.Add(new Border { Style = (Style)FindResource("S.Segments"), Child = seg, Margin = new Thickness(0, 0, 12, 0) });

            // Mini line in the compact island
            var mini = new CheckBox { Style = (Style)FindResource("S.Switch"), IsChecked = sc.ShowInCompact, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc.German ? "Mini: eine Zeile in der kleinen Insel, immer sichtbar" : "Mini: one line in the small island, always visible" };
            mini.Click += (_, _) => { sc.ShowInCompact = mini.IsChecked == true; _ctx.S.ScriptWidgetsChanged(); _ctx.I.Scripts.RunNow(sc); };
            controls.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Desc"), Text = "Mini", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            controls.Children.Add(mini);
            controls.Children.Add(new Border { Width = 8 });

            controls.Children.Add(IconButton("Icon.Pencil", Loc.German ? "Im Editor öffnen" : "Open in editor", () => Services.Scripting.ScriptWidgetsService.Edit(sc)));
            controls.Children.Add(IconButton("Icon.Refresh", Loc.German ? "Jetzt ausführen" : "Run now", () => _ctx.I.Scripts.RunNow(sc)));
            controls.Children.Add(IconButton("Icon.Trash", Loc.German ? "Entfernen" : "Remove", () => { _ctx.I.Scripts.Remove(sc); BuildScriptRows(); BuildWidgetBoard(); }));

            var row = Row("Icon.Code", sc.Name, _ctx.I.Scripts.StatusOf(sc), controls, i > 0, out var status);
            _scriptStatus[sc.Id] = status;
            ScriptRows.Children.Add(row);
        }
    }

    private Button IconButton(string icon, string tip, Action click)
    {
        var b = new Button
        {
            Style = (Style)FindResource("S.Button"), Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip,
            Content = new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource(icon), Width = 13, Height = 13 },
        };
        b.Click += (_, _) => click();
        return b;
    }

    private void NewScript_Click(object sender, RoutedEventArgs e)
    {
        var sc = _ctx.I.Scripts.CreateNew();
        BuildScriptRows();
        BuildWidgetBoard();
        Services.Scripting.ScriptWidgetsService.Edit(sc);
    }

    private void ImportScript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JavaScript (*.js)|*.js", Title = Loc.German ? "Skript hinzufügen" : "Add script" };
        if (dlg.ShowDialog(this) != true) return;
        try { _ctx.I.Scripts.Import(dlg.FileName); }
        catch (Exception ex) { Log.Error("ScriptImport", ex); }
        BuildScriptRows();
        BuildWidgetBoard();
    }

    private void OpenScriptFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Services.Scripting.ScriptWidgetsService.Folder);

    private void ScriptHelp_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/SuperSparrow-sys/dynamic-island-win/blob/main/docs/SCRIPTS.md") { UseShellExecute = true }); } catch { }
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

    private void NvidiaOn_Click(object sender, RoutedEventArgs e) => SetNvidia(true);
    private void NvidiaOff_Click(object sender, RoutedEventArgs e) => SetNvidia(false);

    /// <summary>The NVIDIA profile of the last fullscreen game (FullscreenGames remembers it); Windows asks for admin rights if needed.</summary>
    private async void SetNvidia(bool on)
    {
        string game = _ctx.S.LastFullscreenGame;
        if (string.IsNullOrEmpty(game))
        {
            NvidiaStatus.Text = Loc.German ? "Erst ein Spiel im Vollbild starten." : "Start a game in fullscreen first.";
            return;
        }
        string exe = game.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? game : game + ".exe";
        NvidiaStatus.Text = "…";
        int st = await Services.NvidiaProfile.ApplyAsync(exe, on);
        NvidiaStatus.Text = st switch
        {
            Services.NvidiaProfile.Ok when on => Loc.German ? $"{exe}: umgestellt, Spiel neu starten." : $"{exe}: applied, restart the game.",
            Services.NvidiaProfile.Ok => Loc.German ? $"{exe}: zurückgesetzt." : $"{exe}: reset.",
            Services.NvidiaProfile.NoNvidia => Loc.German ? "Keine NVIDIA-Grafikkarte." : "No NVIDIA graphics card.",
            -1 => Loc.German ? "Abgebrochen." : "Cancelled.",
            _ => Loc.German ? "Hat nicht geklappt." : "That did not work.",
        };
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatus.Text = Loc.German ? "Suche nach Updates…" : "Checking for updates…";
        try { await UpdateCheck.CheckNowAsync(); UpdateUpdateRow(); }
        catch (Exception ex)
        {
            Log.Error("Update check", ex);
            // GitHub answers 403 when its hourly limit for anonymous requests is used up: not a sign-in problem.
            UpdateStatus.Text = ex is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden }
                ? ErrorText.For(System.Net.HttpStatusCode.TooManyRequests) : ErrorText.For(ex);
        }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    // ---- claude ----

    private void UpdateClaudeShareText()
    {
        string folder = _ctx.S.ClaudeShareFolder;
        if (string.IsNullOrEmpty(folder))
        {
            ClaudeShareText.Text = Loc.German ? "Aus" : "Off";
            ClaudeShareText.ClearValue(TextBlock.ForegroundProperty);
            ClaudeShareButton.ToolTip = Loc.German ? "Cloud-Ordner wählen" : "Choose a cloud folder";
        }
        else SetPath(ClaudeShareButton, ClaudeShareText, folder, exists: System.IO.Directory.Exists(folder));
    }

    // ---- places on the PC: [icon] name, full path as tooltip ----

    private static string ShortName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;
    }

    /// <summary>Shows a place by its name; the full path goes into the tooltip, a missing place turns orange.</summary>
    private void SetPath(Button button, TextBlock text, string path, string? display = null, bool exists = true)
    {
        text.Text = display ?? ShortName(path);
        button.ToolTip = exists ? path : path + "\n" + (Loc.German ? "Ordner nicht gefunden" : "Folder not found");
        if (exists) text.ClearValue(TextBlock.ForegroundProperty);
        else text.SetResourceReference(TextBlock.ForegroundProperty, "S.Orange");
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
        }
        catch { }
    }

    /// <summary>The folders the clipboard watches for new screenshots, one button each (opens it).</summary>
    private void BuildScreenshotFolders()
    {
        ScreenshotFolderButtons.Children.Clear();
        foreach (var folder in ClipboardService.ScreenshotFolders())
        {
            var text = new TextBlock { Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var b = new Button
            {
                Style = (Style)FindResource("S.PathButton"), Margin = new Thickness(6, 3, 0, 3),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
                {
                    new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource("Icon.Folder"), Width = 14, Height = 14 },
                    text,
                } },
            };
            SetPath(b, text, folder);
            string f = folder;
            b.Click += (_, _) => OpenFolder(f);
            ScreenshotFolderButtons.Children.Add(b);
        }
        if (ScreenshotFolderButtons.Children.Count == 0)
            ScreenshotFolderButtons.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Desc"), Text = Loc.German ? "Keiner gefunden" : "None found" });
    }

    // ---- Taildrop folder ----

    private void UpdateTaildropFolderText()
    {
        string folder = _ctx.S.TaildropFolder;
        bool custom = !string.IsNullOrWhiteSpace(folder);
        if (custom) SetPath(TaildropFolderButton, TaildropFolderText, folder, exists: System.IO.Directory.Exists(folder));
        else SetPath(TaildropFolderButton, TaildropFolderText, Services.TaildropService.DownloadsFolder, "Downloads");
        TaildropFolderReset.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PickTaildropFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.German ? "Ordner für Dateien per Taildrop" : "Folder for files received via Taildrop" };
        if (!string.IsNullOrWhiteSpace(_ctx.S.TaildropFolder)) dlg.InitialDirectory = _ctx.S.TaildropFolder;
        if (dlg.ShowDialog(this) != true) return;
        _ctx.S.TaildropFolder = dlg.FolderName;
        UpdateTaildropFolderText();
    }

    private void ResetTaildropFolder_Click(object sender, RoutedEventArgs e)
    {
        _ctx.S.TaildropFolder = "";
        UpdateTaildropFolderText();
    }

    // ---- To Do list ----

    /// <summary>"Alle Listen" plus one chip per To Do list; the chosen one is highlighted.</summary>
    private void BuildTodoListChoices()
    {
        TodoListChoices.Children.Clear();
        var lists = _ctx.I.Todo?.Lists.ToList() ?? new();
        if (lists.Count == 0)
        {
            TodoListChoices.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Desc"),
                Text = _ctx.I.Microsoft?.IsConnected == true ? (Loc.German ? "Listen werden geladen …" : "Loading lists …") : (Loc.German ? "Erst unter Konten mit Microsoft verbinden." : "Connect Microsoft under Accounts first.") });
            return;
        }
        void Add(string id, string name)
        {
            bool on = _ctx.S.TodoListId == id;
            var b = new Button { Style = (Style)FindResource(on ? "S.AccentButton" : "S.Button"), Content = name, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 5, 12, 5) };
            b.Click += (_, _) => { _ctx.S.TodoListId = id; _ctx.S.TodoListName = id.Length == 0 ? "" : name; BuildTodoListChoices(); };
            TodoListChoices.Children.Add(b);
        }
        Add("", Loc.German ? "Alle Listen" : "All lists");
        foreach (var l in lists) Add(l.Id, l.Name);
    }

    // ---- time tracking projects ----

    private void BuildProjectRows()
    {
        ProjectRows.Children.Clear();
        foreach (var name in _ctx.S.TimeProjects)
        {
            string project = name;
            var remove = new Button { Style = (Style)FindResource("S.Button"), Padding = new Thickness(8, 4, 8, 4),
                Content = new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource("Icon.Close"), Width = 12, Height = 12 },
                ToolTip = Loc.German ? "Entfernen (die erfasste Zeit bleibt)" : "Remove (recorded time stays)" };
            remove.Click += (_, _) => { _ctx.S.TimeProjects.Remove(project); BuildProjectRows(); };
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(remove);
            dock.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Base"), VerticalAlignment = VerticalAlignment.Center, Text = project });
            ProjectRows.Children.Add(dock);
        }
    }

    /// <summary>Projects added with the + in the island show up here too.</summary>
    private void OnTimeProjectsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.TimeProjects)) Dispatcher.BeginInvoke(BuildProjectRows);
    }

    private void UpdateTimeFileText()
    {
        // The file itself appears with the first entry; only a missing folder is a problem.
        string path = _ctx.I.Time.FilePath;
        SetPath(TimeFileButton, TimeFileText, path, exists: System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(path)));
    }

    private void PickTimeFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.German ? "Datei für die Zeiterfassung" : "File for time tracking",
            Filter = "CSV (Excel)|*.csv", OverwritePrompt = false, FileName = System.IO.Path.GetFileName(_ctx.I.Time.FilePath),
            InitialDirectory = System.IO.Path.GetDirectoryName(_ctx.I.Time.FilePath),
        };
        if (dlg.ShowDialog(this) != true) return;
        _ctx.S.TimeTrackingFile = dlg.FileName;
        UpdateTimeFileText();
    }

    private void AddProject_Click(object sender, RoutedEventArgs e) => AddProject();

    private void NewProject_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { AddProject(); e.Handled = true; }
    }

    private void AddProject()
    {
        string name = NewProject.Text.Trim();
        if (name.Length == 0 || _ctx.S.TimeProjects.Contains(name)) return;
        _ctx.S.TimeProjects.Add(name);
        NewProject.Text = "";
        BuildProjectRows();
    }

    // ---- Microsoft 365 ----

    private async void MicrosoftSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_ctx.I.Microsoft is { IsBusy: false } ms) await ms.SignInAsync(withPresence: _ctx.S.MicrosoftPresence);
    }

    /// <summary>With an app ID it signs in at once; without one the setup opens first.</summary>
    private void MicrosoftConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_ctx.S.MicrosoftClientId.Trim().Length == 0) { MsSetup.Visibility = Visibility.Visible; return; }
        MicrosoftSignIn_Click(sender, e);
    }

    /// <summary>Shows or hides the setup steps of an account (also to fix a wrong client ID).</summary>
    private void ToggleSetup_Click(object sender, RoutedEventArgs e)
    {
        if (FindName((string)((FrameworkElement)sender).Tag) is FrameworkElement setup)
            setup.Visibility = setup.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SpotifyConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_ctx.S.SpotifyClientId.Trim().Length == 0) { SpotifySetup.Visibility = Visibility.Visible; return; }
        _ctx.I.Spotify.ConnectCommand.Execute(null);
    }

    private async void MicrosoftPresence_Click(object sender, RoutedEventArgs e)
    {
        if (_ctx.I.Microsoft is { IsBusy: false } ms) await ms.SignInAsync(withPresence: true);
    }

    private void MicrosoftSignOut_Click(object sender, RoutedEventArgs e)
    {
        _ctx.I.Microsoft?.SignOut();
        foreach (var a in _ctx.S.CalendarAccounts.Where(a => a.Kind == CalendarKind.Microsoft).ToList()) _ctx.S.CalendarAccounts.Remove(a);
    }

    private void OpenEntra_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade/quickStartType~/null/sourceType/Microsoft_AAD_IAM");

    private void OpenMicrosoftGuide_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/SuperSparrow-sys/dynamic-island-win/blob/main/docs/INTEGRATIONS.md#microsoft-365-outlook-teams-to-do");

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private async void PickClaudeShare_Click(object sender, RoutedEventArgs e)
    {
        var targets = await CloudTargets.GetAsync();
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var off = new MenuItem { Header = Loc.German ? "Aus" : "Off" };
        off.Click += (_, _) => { _ctx.S.ClaudeShareFolder = ""; UpdateClaudeShareText(); };
        menu.Items.Add(off);
        foreach (var t in targets)
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
        foreach (var id in _ctx.S.BannerExceptions)
        {
            var app = id;
            var remove = new Button { Style = (Style)FindResource("S.Button"), Padding = new Thickness(8, 4, 8, 4),
                Content = new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource("Icon.Close"), Width = 12, Height = 12 } };
            remove.Click += (_, _) => { _ctx.S.BannerExceptions.Remove(app); BuildBannerRows(); };
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
        foreach (var known in new[] { "WhatsApp", "Discord", "Microsoft Teams", "Outlook", "Telegram", "Signal", "Claude", "Spotify" })
        {
            var app = InstalledApps.All().FirstOrDefault(a => a.Name.StartsWith(known, StringComparison.OrdinalIgnoreCase));
            if (app is not null) ids.Add(app.LaunchPath.Replace(@"shell:AppsFolder\", ""));
        }
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase).Where(id => !_ctx.S.BannerExceptions.Contains(id)))
        {
            var item = new MenuItem { Header = BannerSuppressor.NameOf(id) };
            string appId = id;
            item.Click += (_, _) => { _ctx.S.BannerExceptions.Add(appId); BuildBannerRows(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---- calendar accounts ----

    private string _calRowsKey = "";
    private readonly Dictionary<string, TextBlock> _calStatus = new();
    private readonly Dictionary<string, List<FrameworkElement>> _calLines = new();

    /// <summary>Which accounts and calendars the list shows; only a change here needs a rebuild.</summary>
    private string CalendarRowsKey() => string.Join(";", _ctx.S.CalendarAccounts.Select(a =>
        a.Id + ":" + string.Join(",", _ctx.I.Calendar.CalendarsOf(a).Select(c => c.Id))));

    /// <summary>After a sync: new status texts in place; a new or missing account/calendar rebuilds the list.</summary>
    private void RefreshCalendarRows()
    {
        if (CalendarRowsKey() != _calRowsKey) { BuildCalendarRows(); return; }
        foreach (var a in _ctx.S.CalendarAccounts)
            if (_calStatus.TryGetValue(a.Id, out var t)) SetDesc(t, _ctx.I.Calendar.StatusOf(a));
    }

    private void BuildCalendarRows()
    {
        var names = _ctx.S.CalendarAccounts.Select(a => a.Name).Distinct().ToList();
        CalendarSummary.Text = names.Count == 0 ? (Loc.German ? "Noch keiner" : "None yet") : string.Join(", ", names);
        CalendarRows.Children.Clear();
        _calStatus.Clear();
        _calLines.Clear();
        _calRowsKey = CalendarRowsKey();
        int i = 0;
        foreach (var a in _ctx.S.CalendarAccounts)
        {
            var account = a;
            string kind = a.Kind switch { CalendarKind.ICloud => IsICloud(a.Url) ? "iCloud" : "CalDAV", CalendarKind.Google => "Google", CalendarKind.Microsoft => "Microsoft", _ => "ICS" };
            string icon = a.Kind switch { CalendarKind.ICloud => "Icon.Cloud", CalendarKind.Google => "Icon.User", CalendarKind.Microsoft => "Icon.Mail", _ => "Icon.Link" };
            string status = _ctx.I.Calendar.StatusOf(a);
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            var sw = new CheckBox { Style = (Style)FindResource("S.Switch"), IsChecked = a.Enabled, Margin = new Thickness(0, 0, 10, 0) };
            sw.Click += (_, _) =>
            {
                account.Enabled = sw.IsChecked == true;
                if (_calLines.TryGetValue(account.Id, out var lines)) foreach (var l in lines) l.Opacity = account.Enabled ? 1 : 0.5;
                SaveAccounts();
            };
            controls.Children.Add(sw);
            // Sign in again (after an import or an expired login): same account, its calendars and switches stay
            if (NeedsSignIn(a, status))
            {
                var signIn = new Button { Style = (Style)FindResource("S.Button"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = Loc.German ? "Neu anmelden, das Konto bleibt erhalten" : "Sign in again, the account stays",
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
                    {
                        new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource("Icon.LogIn"), Width = 13, Height = 13 },
                        new TextBlock { Margin = new Thickness(6, 0, 0, 0), Text = Loc.German ? "Anmelden" : "Sign in" },
                    } } };
                signIn.Click += (_, _) => SignInAgain(account);
                controls.Children.Add(signIn);
            }
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
            var row = Row(icon, label, status.Length > 0 ? status : null, controls, i++ > 0, out var statusText);
            ((Border)row).Padding = new Thickness(0, 8, 0, 8);
            _calStatus[a.Id] = statusText;
            _calLines[a.Id] = new();
            CalendarRows.Children.Add(row);

            // The account's calendars, each with its own switch (e.g. to hide some of many Google calendars)
            foreach (var cal in _ctx.I.Calendar.CalendarsOf(a).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var c = cal;
                var calSwitch = new CheckBox { Style = (Style)FindResource("S.Switch"), IsChecked = !account.Hidden.Contains(c.Id), VerticalAlignment = VerticalAlignment.Center };
                calSwitch.Click += (_, _) =>
                {
                    if (calSwitch.IsChecked == true) account.Hidden.Remove(c.Id);
                    else if (!account.Hidden.Contains(c.Id)) account.Hidden.Add(c.Id);
                    SaveAccounts();
                };
                var line = new DockPanel { Margin = new Thickness(32, 2, 0, 2), Opacity = account.Enabled ? 1 : 0.5 };
                DockPanel.SetDock(calSwitch, Dock.Right);
                line.Children.Add(calSwitch);
                var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                    Background = new CalendarEvent { Color = c.Color }.Brush };
                DockPanel.SetDock(dot, Dock.Left);
                line.Children.Add(dot);
                line.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Base"), Text = c.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                _calLines[a.Id].Add(line);
                CalendarRows.Children.Add(line);
            }
        }
        if (_ctx.S.CalendarAccounts.Count == 0)
            CalendarRows.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Desc"), Margin = new Thickness(0, 0, 0, 8),
                Text = Loc.German ? "Noch kein Kalender verbunden." : "No calendar connected yet." });
    }

    /// <summary>
    /// Google or CalDAV without its sign-in (imported settings carry no passwords or tokens) or with a sign-in the
    /// server refused. ICS links and Microsoft (signed in on the Accounts card) have no such button.
    /// </summary>
    internal static bool NeedsSignIn(CalendarKind kind, bool hasSecret, string status) => kind switch
    {
        CalendarKind.Google or CalendarKind.ICloud => !hasSecret || (status.Length > 0 && !status.StartsWith("OK", StringComparison.Ordinal)),
        _ => false,
    };

    private static bool NeedsSignIn(CalendarAccount a, string status) =>
        NeedsSignIn(a.Kind, SecretStore.Get($"cal:{a.Id}:{(a.Kind == CalendarKind.Google ? "refresh" : "password")}") is { Length: > 0 }, status);

    /// <summary>The account being signed in again by the open form (null: the form adds a new account).</summary>
    private CalendarAccount? _reauth;

    private async void SignInAgain(CalendarAccount account)
    {
        _reauth = account;
        if (account.Kind == CalendarKind.Google)
        {
            // Client id and secret travel with the export: then only the browser sign-in is left
            GoogleClientId.Text = account.User;
            if (SecretStore.Get($"cal:{account.Id}:secret") is { Length: > 0 }) { await SignInGoogleAsync(account, null); return; }
            ShowCalForm(GoogleForm);
            GoogleStatus.Text = Loc.German ? "Clientschlüssel eintragen, dann anmelden." : "Enter the client secret, then sign in.";
            GoogleSecret.Focus();
        }
        else
        {
            ICloudServer.Text = string.IsNullOrEmpty(account.Url) ? "https://caldav.icloud.com/" : account.Url;
            ICloudUser.Text = account.User;
            ShowCalForm(ICloudForm);
            ICloudStatus.Text = Loc.German ? "Passwort eintragen, dann verbinden." : "Enter the password, then connect.";
            ICloudPassword.Focus();
        }
    }

    /// <summary>Signs the existing account in again and checks it; its id, name and hidden calendars stay.</summary>
    private async Task SignInGoogleAsync(CalendarAccount account, string? newSecret)
    {
        try
        {
            GoogleStatus.Text = Loc.German ? "Bitte im Browser anmelden…" : "Please sign in in the browser…";
            string secret = newSecret ?? SecretStore.Get($"cal:{account.Id}:secret") ?? "";
            var client = new Services.Calendars.GoogleCalendarClient(account.User, secret, null);
            await client.SignInAsync();
            SecretStore.Set($"cal:{account.Id}:secret", secret);
            SecretStore.Set($"cal:{account.Id}:refresh", client.RefreshToken);
            await RecheckAsync(account, GoogleStatus);
        }
        catch (Exception ex)
        {
            Log.Error("Google sign-in again", ex);
            ShowCalForm(GoogleForm);
            GoogleStatus.Text = ErrorText.For(ex, Loc.T("Err.SignInFailed"));
        }
    }

    private async Task RecheckAsync(CalendarAccount account, TextBlock status)
    {
        status.Text = Loc.German ? "Verbinde…" : "Connecting…";
        try
        {
            var events = await _ctx.I.Calendar.FetchAsync(account, DateTime.Today, DateTime.Today.AddDays(7));
            _reauth = null;
            ICloudForm.Visibility = GoogleForm.Visibility = IcsForm.Visibility = Visibility.Collapsed;
            SaveAccounts(); // saved and synced: the account's status line turns to OK
            _ = _ctx.I.Calendar.RefreshAsync();
            Log.Info($"Calendar {account.Kind} signed in again ({events.Count} events in 7 days)");
            BuildCalendarRows();
        }
        catch (Exception ex)
        {
            Log.Error("Calendar sign-in again " + account.Kind, ex);
            status.Text = ErrorText.For(ex);
        }
    }

    /// <summary>An account changed in place: save and sync, the list itself stays as it is.</summary>
    private void SaveAccounts() => _ctx.S.CalendarAccountsChanged();

    private void ShowCalForm(FrameworkElement form)
    {
        ICloudForm.Visibility = GoogleForm.Visibility = IcsForm.Visibility = Visibility.Collapsed;
        form.Visibility = Visibility.Visible;
        form.BringIntoView();
    }

    private void AddICloud_Click(object sender, RoutedEventArgs e) { _reauth = null; ShowCalForm(ICloudForm); }
    private void AddGoogle_Click(object sender, RoutedEventArgs e) { _reauth = null; ShowCalForm(GoogleForm); }
    private void AddIcs_Click(object sender, RoutedEventArgs e) => ShowCalForm(IcsForm);
    private void CloseCalForms_Click(object sender, RoutedEventArgs e)
    {
        _reauth = null;
        ICloudForm.Visibility = GoogleForm.Visibility = IcsForm.Visibility = Visibility.Collapsed;
    }
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
            Log.Error("Calendar test " + account.Kind, ex);
            status.Text = ErrorText.For(ex);
            return false;
        }
    }

    /// <summary>iCloud, GMX and web.de fill in their server; only "Other" shows the address field.</summary>
    private void CalDavProvider_Checked(object sender, RoutedEventArgs e)
    {
        if (ICloudServer is null || CalDavServerPanel is null || CalDavAppleHint is null) return;
        string server = (string)((FrameworkElement)sender).Tag;
        bool other = server.Length == 0;
        ICloudServer.Text = server;
        CalDavServerPanel.Visibility = other ? Visibility.Visible : Visibility.Collapsed;
        CalDavAppleHint.Visibility = !other && IsICloud(server) ? Visibility.Visible : Visibility.Collapsed;
        if (other) ICloudServer.Focus();
    }

    private async void SaveICloud_Click(object sender, RoutedEventArgs e)
    {
        var server = ICloudServer.Text.Trim();
        if (server.Length == 0) { ICloudStatus.Text = Loc.German ? "Bitte die Server-Adresse eintragen." : "Please enter the server address."; return; }
        if (!server.Contains("://")) server = "https://" + server;
        if (!server.EndsWith("/")) server += "/";
        if (_reauth is { Kind: CalendarKind.ICloud } again)
        {
            // Same account: new password (and maybe a corrected user or server), its calendars and switches stay
            string? old = SecretStore.Get($"cal:{again.Id}:password");
            again.User = ICloudUser.Text.Trim();
            again.Url = server;
            SecretStore.Set($"cal:{again.Id}:password", ICloudPassword.Password.Trim());
            await RecheckAsync(again, ICloudStatus);
            if (_reauth is null) ICloudPassword.Clear();
            else SecretStore.Set($"cal:{again.Id}:password", old); // refused: the old one stays
            return;
        }
        var account = new CalendarAccount { Kind = CalendarKind.ICloud, Name = ServerName(server), User = ICloudUser.Text.Trim(), Url = server };
        SecretStore.Set($"cal:{account.Id}:password", ICloudPassword.Password.Trim());
        if (await TestAndAddAsync(account, ICloudStatus)) { ICloudPassword.Clear(); }
        else SecretStore.Set($"cal:{account.Id}:password", null);
    }

    private static bool IsICloud(string? url) => string.IsNullOrWhiteSpace(url) || url.Contains("icloud.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>"iCloud", "GMX", "Web.de" ... from the CalDAV server address.</summary>
    private static string ServerName(string url)
    {
        if (IsICloud(url)) return "iCloud";
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        var parts = host.Split((char)46);
        var name = parts.Length >= 2 ? parts[^2] : host;
        return name.Length <= 4 ? name.ToUpperInvariant() : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private async void SaveGoogle_Click(object sender, RoutedEventArgs e)
    {
        if (_reauth is { Kind: CalendarKind.Google } again)
        {
            again.User = GoogleClientId.Text.Trim();
            await SignInGoogleAsync(again, GoogleSecret.Password.Trim());
            if (_reauth is null) GoogleSecret.Clear();
            return;
        }
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
        catch (Exception ex)
        {
            Log.Error("Google sign-in", ex);
            GoogleStatus.Text = ErrorText.For(ex, Loc.T("Err.SignInFailed"));
        }
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
            BuildWidgetBoard();
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

    private FrameworkElement Row(string icon, string label, string? desc, FrameworkElement control, bool divider) =>
        Row(icon, label, desc, control, divider, out _);

    private static void SetDesc(TextBlock t, string? text)
    {
        t.Text = text ?? "";
        t.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement Row(string icon, string label, string? desc, FrameworkElement control, bool divider, out TextBlock descText)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(control, Dock.Right);
        control.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(control);
        var ic = new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource(icon), Filled = Controls.Icon.IsGlyph(icon), Width = 18, Height = 18, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(ic, Dock.Left);
        dock.Children.Add(ic);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Style = (Style)FindResource("ST.Label"), Text = label });
        descText = new TextBlock { Style = (Style)FindResource("ST.Desc") };
        SetDesc(descText, desc);
        text.Children.Add(descText);
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

    private void PickExcludedProgram_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.German ? "Programm wählen" : "Choose a program",
            Filter = (Loc.German ? "Programme" : "Programs") + " (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) == true) AddExcluded(System.IO.Path.GetFileName(dlg.FileName));
    }

    private void AddExcluded(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        if (!_ctx.S.ExcludedApps.Contains(name, StringComparer.OrdinalIgnoreCase)) _ctx.S.ExcludedApps.Add(name);
        _ctx.S.SaveSoon();
    }

    /// <summary>Same checklist as for the launcher: checked apps hide the island, unchecked ones are removed.</summary>
    private void PickExcludedApps_Click(object sender, RoutedEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        List<InstalledApp> apps;
        try { apps = InstalledApps.All(refresh: true); }
        finally { Mouse.OverrideCursor = null; }
        var current = new HashSet<string>(_ctx.S.ExcludedApps, StringComparer.OrdinalIgnoreCase);
        var pinned = apps.Where(a => current.Contains(ExcludedApps.TokenFor(a))).Select(a => a.LaunchPath).ToList();
        var picker = new AppPickerWindow(apps, pinned, Loc.German ? "Ausblenden bei diesen Apps" : "Hide while these apps are active",
            Loc.German ? "Ist eine dieser Apps im Vordergrund, blendet sich die Insel aus." : "While one of these apps is in front, the island hides.")
        { Owner = this };
        if (picker.ShowDialog() != true) return;
        var chosen = new HashSet<string>(picker.Selected, StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            var token = ExcludedApps.TokenFor(app);
            var existing = _ctx.S.ExcludedApps.FirstOrDefault(x => string.Equals(x, token, StringComparison.OrdinalIgnoreCase));
            if (chosen.Contains(app.LaunchPath) && existing is null) _ctx.S.ExcludedApps.Add(token);
            else if (!chosen.Contains(app.LaunchPath) && existing is not null) _ctx.S.ExcludedApps.Remove(existing);
        }
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

    private void OpenLicenses_Click(object sender, RoutedEventArgs e) => Open(System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(AppSettings.Folder);

    // ---- devices (link) ----

    private PairingWindow? _pairingWindow;

    private void PairAddress_Click(object sender, RoutedEventArgs e) => PairTyped();

    private void PairAddress_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        PairTyped();
    }

    private void PairTyped()
    {
        if (_ctx.I.Link is { } link && PairAddress.Text.Trim().Length > 0) _ = link.PairWithAsync(PairAddress.Text);
    }

    /// <summary>A pairing reached the code: the code big in a small window over the settings, until it is answered.</summary>
    public void ShowPairingCode(Services.Link.PairingSession session)
    {
        _pairingWindow?.Close();
        var w = new PairingWindow(session, session.PeerName, session.Code) { Owner = this };
        _pairingWindow = w;
        w.Closed += (_, _) => { if (_pairingWindow == w) _pairingWindow = null; };
        void Done(Services.Link.PairingSession s, Services.Link.PairingResult r)
        {
            if (s != session) return;
            _ctx.I.Link!.PairingFinished -= Done;
            if (w.IsLoaded) w.ShowResult(r, s.PeerName);
        }
        _ctx.I.Link!.PairingFinished += Done;
        w.Show();
    }

    private void OpenCursorHopWebsite_Click(object sender, RoutedEventArgs e) => Open("https://cursorhop.com/");

    private void ShowTransferStatus(string text, bool error = false)
    {
        TransferStatus.Text = text;
        TransferStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, error ? "S.Red" : "S.Text2");
        TransferStatus.Visibility = Visibility.Visible;
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        bool de = Loc.German;
        var pin = new PinWindow(de ? "Einstellungen exportieren" : "Export settings",
            de ? "Passwörter, Anmeldungen und Kopplungen bleiben auf diesem PC." : "Passwords, sign-ins and pairings stay on this PC.",
            de ? "Weiter" : "Continue", confirm: true) { Owner = this };
        if (pin.ShowDialog() != true) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = de ? "Einstellungen exportieren" : "Export settings",
            Filter = $"DynamicBay ({SettingsTransfer.Extension})|*{SettingsTransfer.Extension}",
            FileName = $"DynamicBay-{(de ? "Einstellungen" : "Settings")}-{DateTime.Now:yyyy-MM-dd}{SettingsTransfer.Extension}",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var bundle = SettingsTransfer.Collect(_ctx.S, App.Version, Services.Scripting.ScriptWidgetsService.Folder, SecretStore.Get);
            System.IO.File.WriteAllBytes(dlg.FileName, SettingsTransfer.Encrypt(bundle, pin.Code));
            Log.Info($"Settings exported ({bundle.Scripts.Count} script files)");
            ShowTransferStatus(de ? $"Gespeichert: {System.IO.Path.GetFileName(dlg.FileName)}" : $"Saved: {System.IO.Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            Log.Error("Settings export", ex);
            ShowTransferStatus(de ? "Export fehlgeschlagen. Details im Log." : "Export failed. Details in the log.", error: true);
        }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        bool de = Loc.German;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = de ? "Einstellungen importieren" : "Import settings",
            Filter = $"DynamicBay ({SettingsTransfer.Extension})|*{SettingsTransfer.Extension}",
        };
        if (dlg.ShowDialog(this) != true) return;
        byte[] file;
        try { file = System.IO.File.ReadAllBytes(dlg.FileName); }
        catch (Exception ex)
        {
            Log.Error("Settings import read", ex);
            ShowTransferStatus(de ? "Die Datei lässt sich nicht lesen." : "The file can't be read.", error: true);
            return;
        }
        SettingsTransfer.Bundle? bundle = null;
        var pin = new PinWindow(de ? "Einstellungen importieren" : "Import settings",
            de ? "Ersetzt alle Einstellungen, danach startet DynamicBay neu." : "Replaces all settings, then DynamicBay restarts.",
            de ? "Importieren" : "Import", confirm: false, check: code =>
            {
                try { bundle = SettingsTransfer.Decrypt(file, code); return null; }
                catch (SettingsTransfer.WrongPinException) { return de ? "Falscher Code." : "Wrong code."; }
                catch (System.IO.InvalidDataException) { return de ? "Das ist keine DynamicBay-Sicherung." : "This is not a DynamicBay export."; }
            }) { Owner = this };
        if (pin.ShowDialog() != true || bundle is null) return;
        try
        {
            SettingsTransfer.Apply(bundle, AppSettings.Folder, SecretStore.Set);
            Log.Info($"Settings imported from {bundle.AppVersion} ({bundle.Created:yyyy-MM-dd}), restarting");
        }
        catch (Exception ex)
        {
            Log.Error("Settings import", ex);
            ShowTransferStatus(de ? "Import fehlgeschlagen, nichts wurde geändert." : "Import failed, nothing was changed.", error: true);
            return;
        }
        ((App)Application.Current).RestartAfterImport();
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
