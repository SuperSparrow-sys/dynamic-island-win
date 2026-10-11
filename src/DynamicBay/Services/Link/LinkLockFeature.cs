using System.Text.Json.Nodes;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>
/// Gemeinsam sperren (Einstellungen → Geräte, off by default): locking one device (Win+L) locks the other one, too -
/// but only when nobody used it for a minute, so it never locks under someone's hands. Unlocking is never sent (it
/// could not be done and must not be). The device that was locked this way does not send its own lock back.
/// </summary>
public sealed class LinkLockFeature : LinkFeature
{
    public const string Type = "lock";
    /// <summary>The other device locks only after this long without keyboard or mouse input.</summary>
    internal static readonly TimeSpan MinIdle = TimeSpan.FromMinutes(1);
    /// <summary>A lock right after one in the other direction is the echo of it.</summary>
    internal static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(20);

    private readonly Func<double> _idleSeconds;
    private readonly Action _lockNow;
    private DateTime _lockedByPeer = DateTime.MinValue, _sent = DateTime.MinValue;

    /// <summary>The other device locked and this one followed (peer name).</summary>
    public event Action<string>? LockedByPeer;

    /// <param name="idleSeconds">Seconds since the last keyboard or mouse input here.</param>
    /// <param name="lockNow">Locks this PC (LockWorkStation; a fake in tests and test instances).</param>
    public LinkLockFeature(LinkService link, AppSettings settings, Func<double> idleSeconds, Action lockNow) : base(link, settings)
    {
        _idleSeconds = idleSeconds;
        _lockNow = lockNow;
        Handle(Type, OnLock);
    }

    public override bool Enabled => Settings.LinkLockTogether;

    /// <summary>This PC was locked: ask the connected devices to follow. Nothing when the lock came from them.</summary>
    public Task<int> LockedHereAsync(DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        lock (this)
        {
            if (t - _lockedByPeer < EchoWindow) return Task.FromResult(0);
            _sent = t;
        }
        return BroadcastAsync(Type, new JsonObject());
    }

    private void OnLock(LinkMessage m)
    {
        var now = DateTime.UtcNow;
        lock (this)
        {
            if (now - _sent < EchoWindow) return; // this device just locked and asked the other: that is its echo
        }
        double idle = _idleSeconds();
        if (!ShouldLock(idle))
        {
            Log.Info($"Link: {m.PeerName} locked, this PC stays unlocked (in use {idle:0} s ago)");
            return;
        }
        lock (this) _lockedByPeer = now;
        Log.Info($"Link: {m.PeerName} locked, locking this PC too");
        _lockNow();
        LockedByPeer?.Invoke(m.PeerName);
    }

    /// <summary>Never under someone's hands: only after a minute without input.</summary>
    internal static bool ShouldLock(double idleSeconds) => idleSeconds >= MinIdle.TotalSeconds;
}
