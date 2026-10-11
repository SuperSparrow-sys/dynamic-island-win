using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicBay.Core;
using DynamicBay.Services.Link;

namespace DynamicBay.Settings;

/// <summary>
/// The six-digit code of a pairing, big, with "Bestätigen" / "Ablehnen". Both devices show the same code; the
/// pairing is stored only when both users confirmed. Shows the result briefly and closes by itself.
/// </summary>
public partial class PairingWindow : Window
{
    private readonly PairingSession? _session;
    private bool _answered;

    public PairingWindow(PairingSession? session, string peerName, string code)
    {
        InitializeComponent();
        Resources.MergedDictionaries.Add(SettingsTheme.Create(SettingsTheme.IsLight()));
        _session = session;
        Title = Loc.German ? "Gerät koppeln" : "Pair device";
        Hint.Text = Loc.German ? $"Zeigt {peerName} denselben Code?" : $"Does {peerName} show the same code?";
        Code.Text = PairingCodeText.Format(code);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = SettingsTheme.IsLight() ? 0 : 1, mica = 2;
            Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            if (Environment.OSVersion.Version.Build >= 22000) Native.DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int));
            else Background = (Brush)FindResource("S.Window");
        };
        // Closed with the X before answering: that is a no.
        Closing += (_, _) => { if (!_answered) _session?.Confirm(false); };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        _answered = true;
        _session?.Confirm(true);
        Buttons.IsEnabled = false;
        Status.Text = Loc.German ? "Warte auf das andere Gerät …" : "Waiting for the other device …";
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        _answered = true;
        _session?.Confirm(false);
        Close();
    }

    /// <summary>The pairing ended (here or on the other device): show how, then close.</summary>
    public void ShowResult(PairingResult result, string peerName)
    {
        _answered = true;
        Buttons.Visibility = Visibility.Collapsed;
        Status.Text = LinkHub.ResultText(result, peerName);
        Status.Foreground = (Brush)FindResource(result == PairingResult.Paired ? "S.Green" : "S.Text2");
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        t.Tick += (_, _) => { t.Stop(); if (IsLoaded) Close(); };
        t.Start();
    }
}
