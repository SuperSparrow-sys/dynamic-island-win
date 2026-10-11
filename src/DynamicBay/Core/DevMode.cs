namespace DynamicBay.Core;

/// <summary>
/// A second DynamicBay for tests next to the installed one: <c>DYNAMICBAY_DEV=a</c> (letters and digits) runs the build
/// with its own data folder (%AppData%\DynamicBay-dev-a) and its own single-instance lock, and leaves Windows alone:
/// no autostart, no Windows banner changes, no identity package, no update check, no hotkeys.
/// <c>DYNAMICBAY_LINK_PORT</c> / <c>DYNAMICBAY_LINK_DISCOVERY_PORT</c> move the link ports so two test instances fit
/// on one PC. <c>DYNAMICBAY_DEV_AUTOCONFIRM=1</c> confirms pairing codes by itself (tests without a person).
/// </summary>
public static class DevMode
{
    public static string? Name { get; } = Read();

    public static bool On => Name is not null;

    public static int? LinkPort => Port("DYNAMICBAY_LINK_PORT");

    public static int? DiscoveryPort => Port("DYNAMICBAY_LINK_DISCOVERY_PORT");

    public static bool AutoConfirmPairing => On && Environment.GetEnvironmentVariable("DYNAMICBAY_DEV_AUTOCONFIRM") == "1";

    /// <summary><c>DYNAMICBAY_DEV_ALLOWPAIRING=1</c>: open the pairing window right after the start (test instances).</summary>
    public static bool AllowPairingAtStart => On && Environment.GetEnvironmentVariable("DYNAMICBAY_DEV_ALLOWPAIRING") == "1";

    /// <summary><c>DYNAMICBAY_DEV_PAIR=127.0.0.1:43901</c>: pair with that address right after the start (test instances).</summary>
    public static string? PairAtStart => On ? Environment.GetEnvironmentVariable("DYNAMICBAY_DEV_PAIR") is { Length: > 0 } a ? a : null : null;

    /// <summary><c>DYNAMICBAY_DEV_ANYFULLSCREEN=1</c>: every fullscreen window counts as a game (FullscreenGames on a video or chat app).</summary>
    public static bool AnyFullscreen => On && Environment.GetEnvironmentVariable("DYNAMICBAY_DEV_ANYFULLSCREEN") == "1";

    /// <summary><c>DYNAMICBAY_DEV_LOOPBACK=1</c>: the link listens on 127.0.0.1 only, without discovery (two instances on one PC, no firewall prompt).</summary>
    public static bool LoopbackOnly => On && Environment.GetEnvironmentVariable("DYNAMICBAY_DEV_LOOPBACK") == "1";

    /// <summary><c>DYNAMICBAY_DEV_SETTINGS=Devices</c>: open the settings window on that page right after the start (test instances).</summary>
    public static SettingsPage? SettingsAtStart =>
        On && Enum.TryParse<SettingsPage>(Environment.GetEnvironmentVariable("DYNAMICBAY_DEV_SETTINGS"), true, out var p) ? p : null;

    internal static string? Parse(string? value) =>
        value is { Length: > 0 and <= 16 } v && v.All(char.IsAsciiLetterOrDigit) ? v.ToLowerInvariant() : null;

    private static string? Read() => Parse(Environment.GetEnvironmentVariable("DYNAMICBAY_DEV"));

    private static int? Port(string name) =>
        On && int.TryParse(Environment.GetEnvironmentVariable(name), out int p) && p is > 1024 and < 65536 ? p : null;
}
