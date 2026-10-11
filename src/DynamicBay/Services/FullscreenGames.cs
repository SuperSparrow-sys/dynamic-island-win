using System.Windows.Threading;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>What to do so "Über allem" also shows above games in real (exclusive) fullscreen.</summary>
public enum FullscreenFix { Off, Taller, Shorter, Nvidia }

/// <summary>
/// Games in real fullscreen (OpenGL like Minecraft with F11, exclusive Direct3D) hand their picture straight to the
/// graphics card, and Windows draws no other window above them, not even a topmost one. Drivers pick that path when the
/// window covers its monitor exactly; one pixel more or less keeps the game composed by Windows, with any graphics card.
/// Only with the layer "Über allem"; undone when the choice changes and when DynamicBay ends. Logs every fullscreen
/// window it sees, so a test run shows what a game does.
/// </summary>
public sealed class FullscreenGames : IDisposable
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IntPtr _logged;
    private IntPtr _adjusted, _gaveUp;
    private Native.RECT _original, _target;
    private DateTime _appliedAt;
    private int _resets;

    public FullscreenGames(AppSettings settings)
    {
        _settings = settings;
        _timer.Tick += (_, _) => Tick();
        _settings.PropertyChanged += OnSettingChanged;
        Update();
    }

    private bool Active => _settings.Layer == LayerMode.AboveAll;

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.Layer) or nameof(AppSettings.FullscreenFix)) Update();
    }

    private void Update()
    {
        if (_settings.FullscreenFix is not (FullscreenFix.Taller or FullscreenFix.Shorter) || !Active) Restore();
        if (Active) _timer.Start(); else _timer.Stop();
    }

    /// <summary>The window rectangle for <paramref name="mode"/>, or null when the window does not cover its monitor exactly.</summary>
    public static Native.RECT? Adjusted(Native.RECT window, Native.RECT monitor, FullscreenFix mode)
    {
        if (!Same(window, monitor)) return null;
        return mode switch
        {
            FullscreenFix.Taller => window with { Bottom = window.Bottom + 1 },
            FullscreenFix.Shorter => window with { Bottom = window.Bottom - 1 },
            _ => null,
        };
    }

    /// <summary>
    /// Game windows: Windows reports exclusive Direct3D fullscreen (QUNS_RUNNING_D3D_FULL_SCREEN), or the window class of
    /// a common game library (GLFW: Minecraft Java; SDL; Unity; Unreal). Browsers and players in fullscreen are left alone.
    /// </summary>
    public static bool IsGame(string windowClass, int notificationState) =>
        notificationState == 3
        || windowClass.StartsWith("GLFW", StringComparison.Ordinal)
        || windowClass is "SDL_app" or "UnityWndClass" or "UnrealWindow" or "LWJGL";

    /// <summary>Start, search, task view and the rest of Explorer cover the screen too, but are never touched.</summary>
    public static bool IsShell(string windowClass, string exe) =>
        windowClass is "XamlExplorerHostIslandWindow" or "ForegroundStaging" or "MultitaskingViewFrame" or "Windows.UI.Core.CoreWindow"
        || exe.Equals("explorer", StringComparison.OrdinalIgnoreCase) || exe.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
        || exe.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase);

    public static bool Same(Native.RECT a, Native.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private void Tick()
    {
        if (_adjusted != IntPtr.Zero && !Native.IsWindow(_adjusted)) _adjusted = IntPtr.Zero; // game closed
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || !Native.GetWindowRect(fg, out var rect)) return;
        long style = Native.GetWindowLongPtr(fg, Native.GWL_STYLE).ToInt64();
        if ((style & Native.WS_CAPTION) == Native.WS_CAPTION) return;
        var monitor = Monitors.Describe(Native.MonitorFromWindow(fg, 2 /* MONITOR_DEFAULTTONEAREST */)).Bounds;
        bool covers = Same(rect, monitor);
        if (!covers) return; // also our own adjusted window: nothing to do
        if (Native.ProcessId(fg) == Environment.ProcessId) return;

        string cls = Native.ClassName(fg);
        string exe = Native.ProcessName(fg) ?? "";
        Native.SHQueryUserNotificationState(out int quns);
        bool game = IsGame(cls, quns) || (DevMode.AnyFullscreen && !IsShell(cls, exe));
        if (fg != _logged)
        {
            _logged = fg;
            Log.Info($"Fullscreen window: {exe} / {cls}, state {quns}, style 0x{style:X8}, {rect.Width}x{rect.Height}, game {game}, fix {_settings.FullscreenFix}");
        }
        if (!game) return;
        if (!string.Equals(_settings.LastFullscreenGame, exe, StringComparison.OrdinalIgnoreCase)) _settings.LastFullscreenGame = exe;

        if (_settings.FullscreenFix is not (FullscreenFix.Taller or FullscreenFix.Shorter) || fg == _gaveUp) return;
        if (fg == _adjusted)
        {
            // The game set its size back. Right after our change that is a game that insists: stop after three rounds.
            if (DateTime.Now - _appliedAt < TimeSpan.FromSeconds(2) && ++_resets >= 3)
            {
                Log.Info($"Fullscreen fix: {exe} keeps its size, giving up");
                _gaveUp = fg;
                _adjusted = IntPtr.Zero;
                return;
            }
        }
        else
        {
            Restore();
            _resets = 0;
            _original = rect;
        }
        var target = Adjusted(rect, monitor, _settings.FullscreenFix)!.Value;
        bool ok = Native.SetWindowPos(fg, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        _adjusted = fg; _target = target; _appliedAt = DateTime.Now;
        Native.GetWindowRect(fg, out var now);
        Log.Info($"Fullscreen fix {_settings.FullscreenFix}: {exe} {(ok ? "set" : "failed")} to {target.Width}x{target.Height}, now {now.Width}x{now.Height}");
    }

    /// <summary>Gives the game its own size back, if it still has ours.</summary>
    private void Restore()
    {
        var hwnd = _adjusted;
        _adjusted = IntPtr.Zero;
        _gaveUp = IntPtr.Zero;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || !Native.GetWindowRect(hwnd, out var r) || !Same(r, _target)) return;
        Native.SetWindowPos(hwnd, IntPtr.Zero, _original.Left, _original.Top, _original.Width, _original.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        Log.Info("Fullscreen fix: game size restored");
    }

    public void Dispose()
    {
        _timer.Stop();
        _settings.PropertyChanged -= OnSettingChanged;
        Restore();
    }
}
