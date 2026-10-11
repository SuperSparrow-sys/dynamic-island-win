using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DynamicBay.Core;

namespace DynamicBay.Settings;

/// <summary>
/// Asks for the numeric code of a settings export. New code: entered twice. Existing code: <c>check</c> gets it and
/// returns an error text (e.g. wrong code) to stay open, or null to close.
/// </summary>
public partial class PinWindow : Window
{
    private readonly bool _confirm;
    private readonly Func<string, string?>? _check;
    public string Code { get; private set; } = "";

    public PinWindow(string heading, string hint, string okText, bool confirm, Func<string, string?>? check = null)
    {
        InitializeComponent();
        Resources.MergedDictionaries.Add(SettingsTheme.Create(SettingsTheme.IsLight()));
        Title = heading;
        Heading.Text = heading;
        Hint.Text = hint;
        OkButton.Content = okText;
        _confirm = confirm;
        _check = check;
        RepeatPanel.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = SettingsTheme.IsLight() ? 0 : 1, mica = 2;
            Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            if (Environment.OSVersion.Version.Build >= 22000) Native.DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int));
            else Background = (Brush)FindResource("S.Window");
        };
        Loaded += (_, _) => Pin.Focus();
    }

    private void Pin_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void Pin_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is not string s || !s.All(char.IsAsciiDigit)) e.CancelCommand();
    }

    private void Pin_Changed(object sender, RoutedEventArgs e)
    {
        Error.Visibility = Visibility.Collapsed;
        OkButton.IsEnabled = SettingsTransfer.IsValidPin(Pin.Password) && (!_confirm || Repeat.Password.Length > 0);
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.Visibility = Visibility.Visible;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_confirm && Repeat.Password != Pin.Password)
        {
            ShowError(Loc.German ? "Die Codes stimmen nicht überein." : "The codes do not match.");
            return;
        }
        if (_check is not null)
        {
            Cursor = Cursors.Wait;
            string? error;
            try { error = _check(Pin.Password); }
            finally { Cursor = null; }
            if (error is not null)
            {
                Pin.Clear(); // first: clearing fires PasswordChanged, which hides the message
                Pin.Focus();
                ShowError(error);
                return;
            }
        }
        Code = Pin.Password;
        DialogResult = true;
    }
}
