using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// A ringing Teams call: new Teams shows it in its own small window (class "TeamsWebView", title "Microsoft Teams",
/// bottom right) with the buttons "Mit Audio annehmen" and "Anruf ablehnen". This finds that window, moves it out of
/// sight while the island shows the call, and presses its buttons for the island's "Annehmen" / "Ablehnen" (only on a
/// click).
/// </summary>
public sealed class TeamsCalls
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private IntPtr _ringing;
    private bool _checking;

    /// <summary>A call started ringing: the caller as Teams shows it ("" when it shows none).</summary>
    public event Action<string>? Ringing;
    /// <summary>It stopped ringing (answered, declined, missed) - here or in Teams.</summary>
    public event Action? Ended;

    /// <summary>Teams' window goes out of sight: the island shows the call (here, or on the paired main device).</summary>
    public Func<bool> Hide { get; set; } = () => false;

    public TeamsCalls(AppSettings settings)
    {
        _settings = settings;
        _poll.Tick += async (_, _) => await CheckAsync();
    }

    public void Start() => _poll.Start();
    public void Stop() => _poll.Stop();

    private async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            bool hide = Hide();
            var found = await Task.Run(() => FindCall(hide));
            if (found is { } call && _ringing == IntPtr.Zero)
            {
                _ringing = call.Window;
                Log.Info("Teams call: ringing");
                Ringing?.Invoke(call.Caller);
            }
            else if (found is null && _ringing != IntPtr.Zero)
            {
                _ringing = IntPtr.Zero;
                Log.Info("Teams call: no longer ringing");
                Ended?.Invoke();
            }
        }
        catch (Exception ex) { Log.Error("Teams call", ex); }
        finally { _checking = false; }
    }

    // ---------- the call window ----------

    internal static readonly string[] AcceptNames = { "Mit Audio annehmen", "Accept with audio" };
    internal static readonly string[] DeclineNames = { "Anruf ablehnen", "Decline call", "Decline" };
    private static readonly string[] OwnLabels =
        { "Microsoft Teams", "Weitere Optionen", "More options", "Benachrichtigung schließen", "Close notification", "Mit Video annehmen", "Accept with video" };

    /// <summary>A Teams toast: small, titled "Microsoft Teams" (the main window carries the chat and tenant in its title).</summary>
    internal static bool IsToast(string title, int width, int height) =>
        title == "Microsoft Teams" && width is > 200 and < 700 && height is > 60 and < 600;

    /// <summary>Out of sight: left of all screens. Still a normal window, so its buttons can be pressed.</summary>
    internal static int HiddenX(int virtualLeft, int width) => virtualLeft - width - 200;

    /// <summary>The caller: the first text of the toast that is not one of Teams' own labels.</summary>
    internal static string CallerFrom(IEnumerable<string> texts) =>
        texts.Select(t => t.Trim()).FirstOrDefault(t => t.Length > 0 && !OwnLabels.Contains(t, StringComparer.OrdinalIgnoreCase)
            && !AcceptNames.Concat(DeclineNames).Contains(t, StringComparer.OrdinalIgnoreCase)) ?? "";

    private static (IntPtr Window, string Caller)? FindCall(bool hide)
    {
        for (IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "TeamsWebView", null); h != IntPtr.Zero;
             h = FindWindowEx(IntPtr.Zero, h, "TeamsWebView", null))
        {
            if (!Native.IsWindowVisible(h) || !Native.GetWindowRect(h, out var r)) continue;
            var title = new StringBuilder(64);
            GetWindowText(h, title, title.Capacity);
            if (!IsToast(title.ToString(), r.Right - r.Left, r.Bottom - r.Top)) continue;
            var root = AutomationElement.FromHandle(h);
            if (Button(root, AcceptNames) is null) continue; // a chat message, not a call
            int hiddenX = HiddenX(GetSystemMetrics(76), r.Right - r.Left); // SM_XVIRTUALSCREEN
            if (hide && r.Left != hiddenX)
                Native.SetWindowPos(h, IntPtr.Zero, hiddenX, r.Top, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
            var texts = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Take(12).Select(e => { try { return e.Current.Name; } catch { return ""; } });
            return (h, CallerFrom(texts));
        }
        return null;
    }

    private static AutomationElement? Button(AutomationElement root, string[] names)
    {
        foreach (var name in names)
        {
            var b = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, name)));
            if (b is not null) return b;
        }
        return null;
    }

    /// <summary>Presses "Mit Audio annehmen" or "Anruf ablehnen" in Teams' call window. False: it is gone.</summary>
    public Task<bool> AnswerAsync(bool accept) => Task.Run(() =>
    {
        var window = _ringing;
        if (window == IntPtr.Zero) return false;
        try
        {
            if (Button(AutomationElement.FromHandle(window), accept ? AcceptNames : DeclineNames) is not { } b
                || !b.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) return false;
            ((InvokePattern)p).Invoke();
            Log.Info(accept ? "Teams call: accepted from the island" : "Teams call: declined from the island");
            return true;
        }
        catch (Exception ex) { Log.Error("Teams call answer", ex); return false; }
    });

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder buf, int max);
}
