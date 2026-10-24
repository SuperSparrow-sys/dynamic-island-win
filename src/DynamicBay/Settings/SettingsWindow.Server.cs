using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DynamicBay.Core;
using DynamicBay.Services.Server;

namespace DynamicBay.Settings;

/// <summary>The DynamicBay server on the page "Geräte": pair with a code, state, the devices, a code for the next one.</summary>
public sealed partial class SettingsWindow
{
    private bool _serverWired;
    private string _serverRowsKey = "";

    private void BuildServerRows()
    {
        var server = _ctx.I.Server;
        if (!_serverWired)
        {
            _serverWired = true;
            PropertyChangedEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => { if (ServerRowsKey() != _serverRowsKey) BuildServerRows(); });
            System.Collections.Specialized.NotifyCollectionChangedEventHandler devices = (_, _) => Dispatcher.BeginInvoke(BuildServerRows);
            server.PropertyChanged += changed;
            server.Devices.CollectionChanged += devices;
            Closed += (_, _) => { server.PropertyChanged -= changed; server.Devices.CollectionChanged -= devices; };
        }
        _serverRowsKey = ServerRowsKey();
        ServerRows.Children.Clear();
        bool de = Loc.German;
        if (!server.IsPaired)
        {
            var address = new TextBox { Style = (Style)FindResource("S.TextBox"), Width = 260, Text = _ctx.S.ServerAddress };
            ServerRows.Children.Add(Row("Icon.Link", de ? "Adresse" : "Address",
                de ? "Zum Beispiel homeserver.….ts.net:20009" : "For example homeserver.….ts.net:20009", address, false));
            var code = new TextBox { Style = (Style)FindResource("S.TextBox"), Width = 120 };
            var pair = FocusButton(de ? "Koppeln" : "Pair");
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(code);
            pair.Margin = new Thickness(8, 0, 0, 0);
            panel.Children.Add(pair);
            ServerRows.Children.Add(Row("Icon.Key", "Code", server.Error.Length > 0 ? server.Error
                : de ? "Von der Rubus-Seite oder einem gekoppelten Gerät" : "From the Rubus page or a paired device", panel, true, out var hint));
            pair.Click += async (_, _) =>
            {
                pair.IsEnabled = false;
                bool ok = await server.PairAsync(address.Text, code.Text);
                pair.IsEnabled = true;
                if (!ok) SetDesc(hint, server.Error);
            };
            return;
        }

        var unpair = FocusButton(de ? "Trennen" : "Disconnect");
        unpair.ToolTip = de ? "Vergisst den Server auf diesem Gerät." : "Forgets the server on this device.";
        unpair.Click += (_, _) => server.Unpair();
        ServerRows.Children.Add(Row("Icon.Cloud", _ctx.S.ServerAddress, server.Status + (server.Error.Length > 0 ? "\n" + server.Error : ""), unpair, false));

        foreach (var d in server.Devices)
        {
            bool self = d.Id == _ctx.S.ServerDeviceId;
            FrameworkElement control;
            if (self) control = new TextBlock { Style = (Style)FindResource("ST.Desc"), Text = de ? "Dieses Gerät" : "This device" };
            else
            {
                var remove = FocusButton(de ? "Entfernen" : "Remove");
                var id = d.Id;
                remove.Click += async (_, _) => await server.RemoveDeviceAsync(id);
                control = remove;
            }
            string seen = d.Online ? (de ? "Online" : "Online")
                : d.LastSeen > 0 ? (de ? "Zuletzt " : "Last ") + DateTimeOffset.FromUnixTimeSeconds(d.LastSeen).ToLocalTime().ToString("dd.MM. HH:mm") : (de ? "Offline" : "Offline");
            ServerRows.Children.Add(Row(d.Kind == "laptop" ? "Icon.Laptop" : "Icon.Monitor", d.Name, seen, control, true));
        }

        var newCode = FocusButton(de ? "Code erzeugen" : "Create code");
        newCode.IsEnabled = server.IsConnected;
        newCode.Click += async (_, _) => await server.RequestCodeCommand.ExecuteAsync(null);
        ServerRows.Children.Add(Row("Icon.Plus", de ? "Neues Gerät" : "New device",
            server.Code.Length > 0 ? server.Code : de ? "Den Code am neuen Gerät eingeben" : "Enter the code on the new device", newCode, true));

        var own = new CheckBox { Style = (Style)FindResource("S.Switch"), IsChecked = _ctx.S.OverriddenKeys.Contains(SharedSettings.MutedApps) };
        own.Click += async (_, _) => await server.SetOverrideAsync(SharedSettings.MutedApps, own.IsChecked == true);
        ServerRows.Children.Add(Row("Icon.BellOff", de ? "Eigene stumme Apps" : "Own muted apps",
            de ? "Dieses Gerät behält seine Liste, die anderen teilen ihre." : "This device keeps its list, the others share theirs.", own, true));
        var ownFocus = new CheckBox { Style = (Style)FindResource("S.Switch"), IsChecked = _ctx.S.OverriddenKeys.Contains(SharedSettings.FocusProfiles) };
        ownFocus.Click += async (_, _) => await server.SetOverrideAsync(SharedSettings.FocusProfiles, ownFocus.IsChecked == true);
        ServerRows.Children.Add(Row("Icon.Focus", de ? "Eigene Fokus-Profile" : "Own focus profiles",
            de ? "Dieses Gerät schaltet je Fokus, was es selbst einstellt." : "This device switches what it sets itself per focus.", ownFocus, true));
    }

    private string ServerRowsKey()
    {
        var s = _ctx.I.Server;
        return $"{s.IsPaired}|{s.IsConnected}|{s.Status}|{s.Error}|{s.Code}";
    }
}
