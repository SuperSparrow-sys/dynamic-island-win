using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;
using Microsoft.Win32;

namespace DynamicBay.Services.Link;

/// <summary>
/// Working together (PC and laptop as one): timer, "Ruhe und Fokus", questions on the main device, battery events and
/// locking together. Each feature has its own switch; this part connects them with the local services.
/// </summary>
public sealed partial class LinkHub
{
    public LinkTimerFeature? Timer { get; private set; }
    public LinkQuietFeature? Quiet { get; private set; }
    public LinkQuestionFeature? Questions { get; private set; }
    public LinkBatteryFeature? BatteryShare { get; private set; }
    public LinkLockFeature? Lock { get; private set; }

    public TimerService? LocalTimer { get; private set; }
    /// <summary>Locks this PC (set by the app: LockWorkStation, only a log line in test instances).</summary>
    public Action? LockThisPc { get; set; }

    /// <summary>The timer card offers to hand the timer over (or take it here): a timer runs and the other device has it, too.</summary>
    [ObservableProperty] private bool _canHandOverTimer;
    [ObservableProperty] private string _handOverText = "";

    /// <summary>The quiet device handed a question over: show it with working buttons, answer with <see cref="LinkQuestionFeature.AnswerAsync"/>.</summary>
    public event Action<LinkQuestionRequest>? QuestionAsked;
    /// <summary>A question from the other device is over (answered there, ran out, device gone): take its peek away.</summary>
    public event Action<string>? QuestionExpired;
    /// <summary>The other device's battery, for the main device (it shows everything).</summary>
    public event Action<LinkBatteryEvent>? RemoteBattery;
    /// <summary>The other device was locked and this one followed (peer name).</summary>
    public event Action<string>? LockedByPeer;

    private DispatcherTimer? _togetherTick;

    private void CreateTogether(LinkService link)
    {
        Timer = new LinkTimerFeature(link, _settings, LocalTimerState);
        Timer.Received += OnRemoteTimer;
        Timer.Peer.Changed += UpdateTimerFlags;

        Quiet = new LinkQuietFeature(link, _settings);

        Questions = new LinkQuestionFeature(link, _settings);
        Questions.Asked += q => QuestionAsked?.Invoke(q);
        Questions.Expired += id => QuestionExpired?.Invoke(id);

        BatteryShare = new LinkBatteryFeature(link, _settings);
        BatteryShare.Received += e =>
        {
            // The main device shows it; a quiet one leaves it to the main device (which is the other one then).
            if (Role?.IsMain ?? true) RemoteBattery?.Invoke(e);
        };

        Lock = new LinkLockFeature(link, _settings, IdleSeconds, () => LockThisPc?.Invoke());
        Lock.LockedByPeer += name => LockedByPeer?.Invoke(name);

        link.PeerOnline += _ => UpdateTimerFlags();
        link.PeerOffline += _ => UpdateTimerFlags();
        _togetherTick = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(1) };
        _togetherTick.Tick += (_, _) => Questions?.Expire(DateTime.UtcNow);
        _togetherTick.Start();
    }

    private void DisposeTogether()
    {
        _togetherTick?.Stop();
        _togetherTick = null;
        foreach (var f in new LinkFeature?[] { Timer, Quiet, Questions, BatteryShare, Lock }) f?.Dispose();
        Timer = null; Quiet = null; Questions = null; BatteryShare = null; Lock = null;
        // A copy of the other device's timer stays as this device's own (it rings here now).
        if (LocalTimer is { RemoteOwner: not null } t) t.RemoteOwner = null;
        UpdateTimerFlags();
    }

    /// <summary>Hooks the timer, the battery events and the session lock once (after <see cref="Attach"/>).</summary>
    public void AttachTogether(TimerService timer)
    {
        LocalTimer = timer;
        timer.Changed += PublishTimer;
        timer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TimerService.IsActive) or nameof(TimerService.RemoteOwner)) UpdateTimerFlags();
        };
        if (Battery is { } battery)
            battery.Changed += ev =>
            {
                var kind = ev switch { BatteryEvent.Low => "low", BatteryEvent.PluggedIn => "plugged", BatteryEvent.Unplugged => "unplugged", _ => "full" };
                if (BatteryShare is { } b) _ = b.PublishAsync(kind, battery.Percent);
            };
        _settings.PropertyChanged += OnTogetherSetting;
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock) _ui.BeginInvoke(() => { if (Lock is { } l) _ = l.LockedHereAsync(); });
        };
    }

    private void OnTogetherSetting(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.LinkShareTimer):
                if (Timer is { } t)
                {
                    _ = t.Peer.AnnounceAsync();
                    if (_settings.LinkShareTimer) PublishTimer();
                    else if (LocalTimer is { RemoteOwner: not null } lt) lt.RemoteOwner = null; // the copy rings here now
                }
                UpdateTimerFlags();
                break;
            case nameof(AppSettings.LinkShareQuiet):
                if (Quiet is { } q) { if (_settings.LinkShareQuiet) _ = q.AnnounceAsync(); q.UpdatePeerQuiet(); }
                break;
            case nameof(AppSettings.DoNotDisturb):
                if (Quiet is { } dnd) _ = dnd.DoNotDisturbChangedAsync();
                break;
            case nameof(AppSettings.FocusActive) or nameof(AppSettings.InMeeting):
                if (Quiet is { } busy) _ = busy.AnnounceAsync();
                break;
            case nameof(AppSettings.LinkShareQuestions):
                if (Questions is { } qs) _ = qs.Peer.AnnounceAsync();
                break;
            case nameof(AppSettings.LinkShareBattery):
                if (BatteryShare is { } bs) _ = bs.Peer.AnnounceAsync();
                break;
        }
    }

    // ---------- timer ----------

    internal static string ModeName(TimerMode m) => m switch { TimerMode.Focus => "focus", TimerMode.Break => "break", _ => "timer" };

    internal static TimerMode ModeOf(string m) => m switch { "focus" => TimerMode.Focus, "break" => TimerMode.Break, _ => TimerMode.Timer };

    private LinkTimerState? LocalTimerState()
    {
        if (LocalTimer is not { } t || Service is not { } s) return null;
        return new LinkTimerState(t.IsActive, t.IsRunning, ModeName(t.Mode), t.Total.TotalSeconds, t.TimeLeft.TotalSeconds, t.RemoteOwner ?? s.DeviceId);
    }

    private void PublishTimer()
    {
        if (Timer is not { Enabled: true } f || LocalTimerState() is not { } s) return;
        _ = Send();
        async Task Send()
        {
            int n = await f.PublishAsync(s);
            if (n > 0) Log.Info($"Link: timer sent ({(s.Active ? (s.Running ? "running" : "paused") + $", {s.Mode}, {Math.Round(s.RemainingSeconds)} s left" : "stopped")}) to {n} device(s)");
        }
    }

    private void OnRemoteTimer(string peerId, string peerName, LinkTimerState s)
    {
        if (LocalTimer is not { } t || Service is not { } link) return;
        string? owner = s.Owner == link.DeviceId ? null : s.Owner.Length > 0 ? s.Owner : peerId;
        t.Apply(s.Active, s.Running, ModeOf(s.Mode), TimeSpan.FromSeconds(s.TotalSeconds), TimeSpan.FromSeconds(s.RemainingSeconds), owner);
        UpdateTimerFlags();
    }

    /// <summary>The connected device that has the timer, too (its switch on); null: none.</summary>
    private PairedDevice? TimerPeer() =>
        Timer is { Enabled: true } f ? f.OnlinePeers.FirstOrDefault(p => f.Peer.IsOn(p.Id)) : null;

    private void UpdateTimerFlags()
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(UpdateTimerFlags); return; }
        var peer = TimerPeer();
        bool can = LocalTimer is { IsActive: true } && peer is not null;
        CanHandOverTimer = can;
        HandOverText = !can ? "" : LocalTimer!.RemoteOwner is null
            ? (Loc.German ? $"An {peer!.Name} übergeben" : $"Hand over to {peer!.Name}")
            : (Loc.German ? "Hierher holen" : "Take it here");
    }

    /// <summary>"Timer übernehmen": the running timer moves to the other device (it rings there) or back here.</summary>
    [RelayCommand]
    private void HandOverTimer()
    {
        if (LocalTimer is not { IsActive: true } t || TimerPeer() is not { } peer) return;
        t.RemoteOwner = t.RemoteOwner is null ? peer.Id : null;
        Log.Info(t.RemoteOwner is null ? "Link: timer taken over here" : $"Link: timer handed over to {peer.Name}");
        PublishTimer();
    }

    /// <summary>
    /// The timer ran out here: show its end? Once for both devices - on the main device, or without one on the device
    /// it belongs to. A timer the other device does not have is announced here as always.
    /// </summary>
    public bool ShowsTimerEnd() =>
        LocalTimer is not { } t || LinkTimerFeature.ShowsEnd(TimerPeer() is not null, _settings.LinkMain.Length > 0, Role?.IsMain ?? true, t.RemoteOwner is null);

    // ---------- questions, battery ----------

    /// <summary>
    /// The quiet device hands a question to the main device. True: it went (or is going) there and
    /// <paramref name="done"/> runs once with the answer; if sending fails it runs with <see cref="LinkAnswer.Lost"/>.
    /// False: show it here.
    /// </summary>
    public bool TryAskMainDevice(LinkQuestion q, Action<LinkAnswer> done)
    {
        if (Role is not { IsMain: false, MainPeer: { } main } || Questions is not { } f || !f.CanAsk(main)) return false;
        _ = Ask();
        return true;

        async Task Ask()
        {
            if (!await f.AskAsync(main, q, done)) done(LinkAnswer.Lost);
        }
    }

    /// <summary>Show this device's own battery peek? Not on the quiet device when the main device shows it.</summary>
    public bool ShowsOwnBatteryPeek()
    {
        bool shared = BatteryShare is { Enabled: true } b && Role?.MainPeer is { } main && b.Peer.IsOn(main) && Service?.IsOnline(main) == true;
        return LinkBatteryFeature.ShowsOwnPeek(Role?.IsMain ?? true, shared);
    }
}
