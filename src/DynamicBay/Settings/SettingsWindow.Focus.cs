using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynamicBay.Core;
using DynamicBay.Services;
using DynamicBay.Services.M365;
using DynamicBay.Services.Server;

namespace DynamicBay.Settings;

/// <summary>iPhone focus on the page "Ruhe und Fokus": address, key and state of the endpoint, one card per focus profile.</summary>
public sealed partial class SettingsWindow
{
    private TextBlock? _focusKeyText, _focusStateText;
    private Button? _focusEnd;
    private string _focusRowsKey = "";
    private bool _focusWired;

    private void WirePhoneFocus()
    {
        if (_focusWired) return;
        _focusWired = true;
        var pf = _ctx.I.PhoneFocus;
        PropertyChangedEventHandler serviceChanged = (_, _) => Dispatcher.BeginInvoke(UpdatePhoneFocusState);
        PropertyChangedEventHandler settingsChanged = (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.AnzeigetafelEnabled)) Dispatcher.BeginInvoke(BuildFocusProfileRows);
            if (e.PropertyName is nameof(AppSettings.FocusProfiles) or nameof(AppSettings.ActiveFocus) or nameof(AppSettings.ActiveRules))
                Dispatcher.BeginInvoke(() => { if (FocusRowsKey() != _focusRowsKey) BuildFocusProfileRows(); });
            if (e.PropertyName is nameof(AppSettings.PhoneFocusEnabled)) Dispatcher.BeginInvoke(BuildPhoneFocusRows);
        };
        PropertyChangedEventHandler serverChanged = (_, e) =>
        {
            if (e.PropertyName is nameof(ServerService.IsConnected) or nameof(ServerService.IphoneAddress) or nameof(ServerService.IphoneKey))
                Dispatcher.BeginInvoke(BuildPhoneFocusRows);
        };
        pf.PropertyChanged += serviceChanged;
        _ctx.I.Server.PropertyChanged += serverChanged;
        _ctx.S.PropertyChanged += settingsChanged;
        Closed += (_, _) =>
        {
            _ctx.I.Server.PropertyChanged -= serverChanged;
            pf.PropertyChanged -= serviceChanged;
            _ctx.S.PropertyChanged -= settingsChanged;
        };
    }

    private void BuildPhoneFocusRows()
    {
        WirePhoneFocus();
        var pf = _ctx.I.PhoneFocus;
        var server = _ctx.I.Server;
        PhoneFocusRows.Children.Clear();
        // Connected to the DynamicBay server: the iPhone reports there (HTTPS, also while this PC is off).
        bool viaServer = server.IsConnected && server.IphoneAddress.Length > 0;
        var addresses = PhoneFocusService.Addresses();
        string url = viaServer ? server.IphoneAddress : $"http://{(addresses.Count > 0 ? addresses[0] : Environment.MachineName)}:{pf.Port}/focus";
        string? also = viaServer ? (Loc.German ? "Über den DynamicBay-Server" : "Through the DynamicBay server")
            : addresses.Count > 1 ? (Loc.German ? "Auch: " : "Also: ") + string.Join(", ", addresses.Skip(1)) : null;
        var copyUrl = FocusButton(Loc.T("Clip.Copy"));
        copyUrl.Click += (_, _) => CopyText(url, copyUrl);
        PhoneFocusRows.Children.Add(Row("Icon.Link", Loc.German ? "Adresse" : "Address", url + (also is null ? "" : "\n" + also), copyUrl, true));

        var copyKey = FocusButton(Loc.T("Clip.Copy"));
        copyKey.Click += (_, _) => CopyText(viaServer ? server.IphoneKey : pf.Key, copyKey);
        var renew = FocusButton(Loc.German ? "Neu" : "New");
        renew.ToolTip = Loc.German ? "Der alte Schlüssel gilt dann nicht mehr." : "The old key stops working.";
        renew.Click += async (_, _) => { if (viaServer) await server.NewIphoneKeyAsync(); else pf.NewKey(); };
        var keyButtons = new StackPanel { Orientation = Orientation.Horizontal };
        keyButtons.Children.Add(copyKey);
        renew.Margin = new Thickness(8, 0, 0, 0);
        keyButtons.Children.Add(renew);
        PhoneFocusRows.Children.Add(Row("Icon.Key", Loc.German ? "Schlüssel" : "Key", viaServer ? server.IphoneKey : pf.Key, keyButtons, true, out var keyText));
        _focusKeyText = keyText;

        _focusEnd = FocusButton(Loc.German ? "Beenden" : "End");
        _focusEnd.Click += async (_, _) =>
        {
            if (_ctx.S.PhoneFocusHere.Length > 0) pf.End();
            else if (_ctx.S.PhoneFocusServer.Length > 0) await server.EndFocusAsync(_ctx.S.PhoneFocusServer);
        };
        PhoneFocusRows.Children.Add(Row("Icon.Phone", Loc.German ? "Jetzt" : "Now", null, _focusEnd, true, out var stateText));
        _focusStateText = stateText;
        UpdatePhoneFocusState();
    }

    private void UpdatePhoneFocusState()
    {
        var pf = _ctx.I.PhoneFocus;
        var server = _ctx.I.Server;
        if (_focusKeyText is not null) SetDesc(_focusKeyText, server.IsConnected && server.IphoneKey.Length > 0 ? server.IphoneKey : pf.Key);
        if (_focusStateText is not null) SetDesc(_focusStateText, pf.Status + (pf.LastEvent.Length > 0 ? "\n" + (Loc.German ? "Zuletzt: " : "Last: ") + pf.LastEvent : ""));
        if (_focusEnd is not null) _focusEnd.IsEnabled = _ctx.S.PhoneFocusHere.Length > 0 || _ctx.S.PhoneFocusServer.Length > 0;
    }

    private Button FocusButton(string text) => new() { Style = (Style)FindResource("S.Button"), Padding = new Thickness(12, 5, 12, 5), Content = text };

    private static async void CopyText(string text, Button button)
    {
        try { Clipboard.SetText(text); } catch { return; }
        var before = button.Content;
        button.Content = Loc.T("Clip.Copied");
        await Task.Delay(1500);
        button.Content = before;
    }

    // ---- profiles ----

    /// <summary>Names and the active one: only a change here rebuilds the cards (a switch flipped in a card does not).</summary>
    private string FocusRowsKey() => string.Join(";", _ctx.S.FocusProfiles.Select(p => p.Name + (_ctx.S.Holds(p) ? "*" : "")));

    /// <summary>The focus shown below the choice (by name); the active one, else the first, when none is chosen yet.</summary>
    private string _shownFocus = "";

    /// <summary>Snapshot mode: show this focus.</summary>
    internal void ShowFocus(string name)
    {
        _shownFocus = name;
        BuildFocusProfileRows();
    }

    private void BuildFocusProfileRows()
    {
        WirePhoneFocus();
        _focusRowsKey = FocusRowsKey();
        FocusChoice.Children.Clear();
        FocusProfileRows.Children.Clear();
        bool de = Loc.German;
        var all = _ctx.S.FocusProfiles.ToList();
        var p = all.FirstOrDefault(f => string.Equals(f.Name, _shownFocus, StringComparison.OrdinalIgnoreCase))
                ?? _ctx.S.ActiveRules.FirstOrDefault() ?? all.FirstOrDefault();
        _shownFocus = p?.Name ?? "";

        // The choice: one segment per focus, the one that is on marked.
        var segments = new WrapPanel();
        foreach (var f in all)
        {
            var name = f.Name;
            var seg = new RadioButton
            {
                Style = (Style)FindResource("S.Segment"), GroupName = "FocusChoice", IsChecked = ReferenceEquals(f, p),
                Content = f.Name + (_ctx.S.Holds(f) ? (de ? " · aktiv" : " · active") : ""),
            };
            seg.Checked += (_, _) => { if (_shownFocus != name) { _shownFocus = name; Dispatcher.BeginInvoke(BuildFocusProfileRows); } };
            segments.Children.Add(seg);
        }
        if (all.Count > 0) FocusChoice.Children.Add(new Border { Style = (Style)FindResource("S.Segments"), HorizontalAlignment = HorizontalAlignment.Left, Child = segments });
        if (p is null) return;

        var stack = new StackPanel();
        bool active = _ctx.S.Holds(p);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        int at = all.IndexOf(p);
        head.Children.Add(IconButton("Icon.ChevronUp", de ? "Nach oben: gewinnt gegen die darunter" : "Up: wins over the ones below", at > 0,
            () => { _ctx.S.FocusProfiles.Move(at, at - 1); _ctx.S.FocusProfilesChanged(); BuildFocusProfileRows(); }));
        head.Children.Add(IconButton("Icon.ChevronDown", de ? "Nach unten" : "Down", at < all.Count - 1,
            () => { _ctx.S.FocusProfiles.Move(at, at + 1); _ctx.S.FocusProfilesChanged(); BuildFocusProfileRows(); }));
        var on = new CheckBox { Style = (Style)FindResource("S.Switch"), IsChecked = p.Enabled, Margin = new Thickness(10, 0, 4, 0),
            ToolTip = de ? "Automation an oder aus" : "Automation on or off" };
        on.Click += (_, _) => { p.Enabled = on.IsChecked == true; _ctx.S.FocusProfilesChanged(); BuildFocusProfileRows(); };
        head.Children.Add(on);
        head.Children.Add(IconButton("Icon.Trash", de ? "Automation entfernen" : "Remove automation", true,
            () => { _shownFocus = ""; _ctx.S.FocusProfiles.Remove(p); }));
        // The trigger in front, unless the name already says it ("Unterwegs").
        string trigger = p.Trigger == Core.Triggers.Focus ? $"{FocusProfile.TriggerLabel(p.Trigger)} „{p.Name}“"
            : FocusProfile.TriggerLabel(p.Trigger) == p.Name ? "" : FocusProfile.TriggerLabel(p.Trigger);
        string Head() => (trigger.Length > 0 ? trigger + " · " : "") + FocusSummary.Of(p, _ctx.S.AnzeigetafelEnabled, TeamsLabel);
        stack.Children.Add(Row(TriggerIcon(p.Trigger), p.Name + (active ? (de ? " · gerade aktiv" : " · active now") : ""), Head(), head, false, out var summaryText));
        Action changed = () => SetDesc(summaryText, Head());

        AddFocusSection(stack, de ? "Auf diesem PC" : "On this PC");
        stack.Children.Add(Row("Icon.Moon", de ? "Nicht stören" : "Do not disturb",
            de ? "Die Insel bleibt klein, Mitteilungen sammeln sich still." : "The island stays small, notifications gather quietly.",
            FocusSwitch(p.Quiet, v => p.Quiet = v, changed), false));
        stack.Children.Add(Row("Icon.Text", de ? "Mitteilungen ohne Text" : "Notifications without text",
            de ? "Einblendungen zeigen nur die App, nicht den Inhalt." : "Peeks show the app only, not the content.",
            FocusSwitch(p.HideText, v => p.HideText = v, changed), true));
        stack.Children.Add(Row("Icon.EyeOff", de ? "Insel ausblenden" : "Hide the island",
            de ? "Die Insel verschwindet, solange die Automation gilt." : "The island disappears while the automation holds.",
            FocusSwitch(p.HideIsland, v => p.HideIsland = v, changed), true));
        var apps = FocusButton(de ? "Auswählen" : "Choose");
        stack.Children.Add(Row("Icon.BellOff", de ? "Stumme Apps" : "Muted apps", AppsText(p), apps, true, out var appsText));
        apps.Click += (_, _) => AppsMenu(p, apps, appsText, changed);
        var widgets = FocusButton(de ? "Auswählen" : "Choose");
        stack.Children.Add(Row("Icon.Modules", de ? "Widgets ausblenden" : "Hide widgets", WidgetsText(p), widgets, true, out var widgetsText));
        widgets.Click += (_, _) => WidgetsMenu(p, widgets, widgetsText, changed);

        AddFocusSection(stack, "Teams");
        var teams = FocusButton(TeamsLabel(p.TeamsStatus));
        teams.Click += (_, _) => TeamsMenu(p, teams, changed);
        stack.Children.Add(Row("Icon.User", de ? "Status" : "Status",
            de ? "Gilt, solange die Automation gilt; danach wie vorher." : "While the automation holds; afterwards as before.", teams, false));

        if (_ctx.S.AnzeigetafelEnabled) AddPanelRows(stack, p, changed);

        // "S.Card" is both a style and a brush here (the theme brush wins in code): set the card look directly.
        FocusProfileRows.Children.Add(new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("S.Card"), BorderBrush = (System.Windows.Media.Brush)FindResource("S.CardBorder"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8), Child = stack,
        });
    }

    private CheckBox FocusSwitch(bool on, Action<bool> set, Action? changed = null)
    {
        var sw = new CheckBox { Style = (Style)FindResource("S.Switch"), IsChecked = on };
        sw.Click += (_, _) => { set(sw.IsChecked == true); _ctx.S.FocusProfilesChanged(); changed?.Invoke(); };
        return sw;
    }

    /// <summary>A small heading inside a focus card ("Auf diesem PC", "Teams", "Anzeigetafel").</summary>
    private void AddFocusSection(StackPanel card, string title) => card.Children.Add(new Border
    {
        BorderBrush = (System.Windows.Media.Brush)FindResource("S.Divider"), BorderThickness = new Thickness(0, 1, 0, 0),
        Padding = new Thickness(16, 12, 16, 0),
        Child = new TextBlock { Style = (Style)FindResource("ST.Desc"), FontWeight = FontWeights.SemiBold, Text = title },
    });

    private static string ListText(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? (Loc.German ? "Keine" : "None") : string.Join(", ", list);
    }

    private static string TeamsLabel(string availability) =>
        PresenceService.Choices.FirstOrDefault(c => !c.Reset && c.Availability == availability && availability.Length > 0)?.Label
        ?? (Loc.German ? "Nicht ändern" : "Leave as is");

    private void TeamsMenu(FocusProfile p, Button button, Action changed)
    {
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var (value, label) in PresenceService.Choices.Where(c => !c.Reset).Select(c => (c.Availability, c.Label)).Prepend(("", TeamsLabel(""))))
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = p.TeamsStatus == value };
            item.Click += (_, _) =>
            {
                p.TeamsStatus = value;
                button.Content = TeamsLabel(value);
                _ctx.S.FocusProfilesChanged();
                changed();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private static readonly int[] PanelPercents = { 10, 15, 25, 40 };

    /// <summary>The Anzeigetafel part of an automation: what fits its trigger.</summary>
    private void AddPanelRows(StackPanel stack, FocusProfile p, Action changed)
    {
        bool de = Loc.German;
        AddFocusSection(stack, de ? "Anzeigetafel" : "Display board");
        switch (p.Trigger)
        {
            case Core.Triggers.Home:
                stack.Children.Add(Row("Icon.Monitor", de ? "Beim Ankommen" : "When coming home",
                    de ? "Hebt das „aus“ von Unterwegs auf." : "Gives back the \u201coff\u201d of being away.",
                    Choice(p.Panel == "plan" ? "plan" : "", new[] { ("plan", de ? "Wochenplan" : "Weekly plan"), ("", de ? "Nicht ändern" : "Leave as is") },
                        v => p.Panel = v, changed), false));
                break;
            case Core.Triggers.Away:
                stack.Children.Add(Row("Icon.Monitor", de ? "Beim Gehen" : "When leaving",
                    de ? "Bleibt aus, bis du ankommst." : "Stays off until you come home.",
                    Choice(p.Panel == "aus" ? "aus" : "", new[] { ("aus", de ? "Aus" : "Off"), ("", de ? "Nicht ändern" : "Leave as is") },
                        v => p.Panel = v, changed), false));
                stack.Children.Add(Row("Icon.Timer", de ? "Erst nach" : "Only after",
                    de ? "Kurze WLAN-Abbrüche zu Hause zählen nicht." : "Short Wi-Fi drops at home do not count.",
                    Choice(p.PanelDelay.ToString(), new[] { ("0", de ? "Sofort" : "At once"), ("60", "1 min"), ("120", "2 min"), ("300", "5 min") },
                        v => p.PanelDelay = int.Parse(v), changed), true));
                break;
            default:
                string current = p.Panel switch { "dimmen" => "dimmen:" + p.PanelPercent, "aus" or "an" => p.Panel, _ => "" };
                var options = new List<(string, string)> { ("", de ? "Nicht ändern" : "Leave as is"), ("an", de ? "An" : "On") };
                options.AddRange(PanelPercents.Select(n => ("dimmen:" + n, de ? $"Dimmen auf {n} %" : $"Dim to {n} %")));
                options.Add(("aus", de ? "Aus" : "Off"));
                stack.Children.Add(Row("Icon.Monitor", de ? "Solange aktiv" : "While active",
                    de ? "Danach wieder wie vorher." : "Afterwards back as before.",
                    Choice(current, options.ToArray(), v =>
                    {
                        var parts = v.Split(':');
                        p.Panel = parts[0];
                        if (parts.Length > 1) p.PanelPercent = int.Parse(parts[1]);
                    }, changed), false));
                stack.Children.Add(Row("Icon.Sun", de ? "Überblenden" : "Fade",
                    de ? "Wie schnell das Panel wechselt." : "How fast the panel changes.",
                    Choice(p.PanelFade > 0 ? "10" : "0", new[] { ("0", de ? "Wie auf der Tafel" : "As on the board"), ("10", de ? "Schnell (10 s)" : "Quick (10 s)") },
                        v => p.PanelFade = int.Parse(v), changed), true));
                break;
        }
    }

    /// <summary>A button with the chosen label that opens the choices; a choice saves and refreshes the summary.</summary>
    private Button Choice(string current, (string Value, string Label)[] options, Action<string> set, Action changed)
    {
        var button = FocusButton(options.FirstOrDefault(o => o.Value == current).Label ?? options[0].Label);
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button };
            foreach (var (value, label) in options)
            {
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = value == current };
                item.Click += (_, _) =>
                {
                    current = value;
                    set(value);
                    button.Content = label;
                    _ctx.S.FocusProfilesChanged();
                    changed();
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        return button;
    }

    private Button IconButton(string icon, string tip, bool enabled, Action click)
    {
        var b = new Button
        {
            Style = (Style)FindResource("S.Button"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip, IsEnabled = enabled,
            Content = new Controls.Icon { Data = (System.Windows.Media.Geometry)FindResource(icon), Width = 12, Height = 12 },
        };
        b.Click += (_, _) => click();
        return b;
    }

    private static string TriggerIcon(string trigger) => trigger switch
    {
        Core.Triggers.Home => "Icon.MapPin",
        Core.Triggers.Away => "Icon.LogOut",
        Core.Triggers.Camera => "Icon.Camera",
        Core.Triggers.Session => "Icon.Timer",
        Core.Triggers.Quiet => "Icon.Moon",
        _ => "Icon.Phone",
    };

    /// <summary>„Automation hinzufügen“: one of each trigger that is not there yet, an iPhone focus by its name in the field.</summary>
    private void AddAutomation_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (Button)sender };
        foreach (var t in Core.Triggers.All.Where(t => t != Core.Triggers.Focus))
        {
            var trigger = t;
            var item = new MenuItem { Header = FocusProfile.TriggerLabel(t), IsEnabled = !_ctx.S.FocusProfiles.Any(p => p.Trigger == t) };
            item.Click += (_, _) =>
            {
                var p = new FocusProfile { Name = FocusProfile.TriggerLabel(trigger), Trigger = trigger };
                if (trigger == Core.Triggers.Camera) { p.Panel = "aus"; p.PanelFade = 10; }
                if (trigger == Core.Triggers.Home) p.Panel = "plan";
                if (trigger == Core.Triggers.Away) p.Panel = "aus";
                _shownFocus = p.Name;
                _ctx.S.FocusProfiles.Add(p);
                _ctx.S.SaveSoon();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Apps that sent notifications lately, plus the ones already muted in this focus.</summary>
    private void AppsMenu(FocusProfile p, Button button, TextBlock text, Action changed)
    {
        var apps = _ctx.I.Notifications.Items.Select(n => n.App).Concat(p.MutedApps).Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a).ToList();
        ToggleMenu(button, apps.Select(a => (a, a)), p.MutedApps, () => { SetDesc(text, AppsText(p)); changed(); });
    }

    private static string AppsText(FocusProfile p) =>
        (Loc.German ? "Ihre Mitteilungen erscheinen nicht: " : "Their notifications do not show: ") + ListText(p.MutedApps);

    private string WidgetsText(FocusProfile p) =>
        (Loc.German ? "Fehlen in der Insel: " : "Missing from the island: ") + ListText(p.HiddenWidgets.Select(WidgetName));

    /// <summary>The widgets on the Nook and System pages, plus the ones already hidden in this focus.</summary>
    private void WidgetsMenu(FocusProfile p, Button button, TextBlock text, Action changed)
    {
        var placed = _ctx.S.HomeWidgets.Concat(_ctx.S.SystemWidgets).Concat(p.HiddenWidgets).Distinct().ToList();
        var ids = AllWidgetInfo().Select(w => w.id).Where(placed.Contains).Select(id => (id, WidgetName(id)));
        ToggleMenu(button, ids, p.HiddenWidgets, () => { SetDesc(text, WidgetsText(p)); changed(); });
    }

    private void ToggleMenu(Button button, IEnumerable<(string Value, string Label)> items, List<string> chosen, Action changed)
    {
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var (value, label) in items)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, StaysOpenOnClick = true, IsChecked = chosen.Contains(value, StringComparer.OrdinalIgnoreCase) };
            item.Click += (_, _) =>
            {
                chosen.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
                if (item.IsChecked) chosen.Add(value);
                changed();
                _ctx.S.FocusProfilesChanged();
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("Notif.Empty"), IsEnabled = false });
        menu.IsOpen = true;
    }

    private string WidgetName(string id)
    {
        var info = AllWidgetInfo().FirstOrDefault(w => w.id == id);
        return info.id is null ? id : Loc.German ? info.de : info.en;
    }

    private void AddFocusProfile_Click(object sender, RoutedEventArgs e) => AddFocusProfile();

    private void NewFocusName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddFocusProfile();
    }

    private void AddFocusProfile()
    {
        string name = PhoneFocusProtocol.Clean(NewFocusName.Text);
        if (name.Length == 0 || _ctx.S.ProfileFor(name) is not null || _ctx.S.IsPresenceName(name)
            || _ctx.S.FocusProfiles.Count(p => p.Trigger == Core.Triggers.Focus) >= PhoneFocusProtocol.MaxProfiles) return;
        _shownFocus = name; // the new one is shown right away
        _ctx.S.FocusProfiles.Add(new FocusProfile { Name = name });
        _ctx.S.SaveSoon();
        NewFocusName.Text = "";
    }
}
