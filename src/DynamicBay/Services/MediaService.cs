using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;
using Windows.Media.Control;

namespace DynamicBay.Services;

/// <summary>
/// Now-playing via Windows' System Media Transport Controls. Works for Spotify (no login),
/// browsers, and every other player that publishes a media session.
/// </summary>
public sealed partial class MediaService : ObservableObject
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _tick;
    private DateTime _positionStamp;
    private TimeSpan _positionAtStamp;
    private string _lastTrackKey = "";

    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _album = "";
    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty] private Color _accent = Color.FromRgb(0x30, 0xD1, 0x58);
    [ObservableProperty] private Brush _accentBrush = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
    [ObservableProperty] private string _sourceApp = "";
    [ObservableProperty] private bool _isSpotify;
    [ObservableProperty] private bool _isAppleMusic;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _remainingText = "-0:00";
    [ObservableProperty] private bool _canSkip = true;

    /// <summary>Raised with the new track when the song changes while playing (used for the track peek).</summary>
    public event Action? TrackChanged;

    public MediaService()
    {
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => UpdateInterpolatedPosition();
    }

    public async Task InitAsync()
    {
        InstalledApps.Ready += () => { lock (AppNames) AppNames.Clear(); PickSession(); };
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => _ui.BeginInvoke(PickSession);
            _manager.CurrentSessionChanged += (_, _) => _ui.BeginInvoke(PickSession);
            PickSession();
        }
        catch
        {
            HasSession = false;
        }
    }

    /// <summary>Prefer a playing session, then Spotify, then whatever Windows considers current.</summary>
    private void PickSession()
    {
        if (_manager is null) return;
        GlobalSystemMediaTransportControlsSession? pick = null;
        try
        {
            var sessions = _manager.GetSessions();
            pick = sessions.FirstOrDefault(s => IsPlayingSession(s) && IsSpotifyId(s.SourceAppUserModelId))
                   ?? sessions.FirstOrDefault(IsPlayingSession)
                   ?? sessions.FirstOrDefault(s => IsSpotifyId(s.SourceAppUserModelId))
                   ?? _manager.GetCurrentSession();
        }
        catch { }

        if (!ReferenceEquals(pick, _session))
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            _session = pick;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnMediaChanged;
                _session.PlaybackInfoChanged += OnPlaybackChanged;
                _session.TimelinePropertiesChanged += OnTimelineChanged;
            }
        }

        if (_session is null)
        {
            // Players drop their session for a moment while skipping (notably browser/web apps).
            // Keep showing the last track during a short grace period instead of flashing "not playing".
            if (HasSession && (InHold || _lostSince is null || DateTime.UtcNow - _lostSince < LossGrace))
            {
                _lostSince ??= DateTime.UtcNow;
                ScheduleRecheck(LossGrace);
                return;
            }
            _lostSince = null;
            HasSession = false;
            IsPlaying = false;
            Title = Artist = Album = "";
            Cover = null;
            _tick.Stop();
            return;
        }
        _lostSince = null;
        HasSession = true;
        if (SourceApp != FriendlyName(_session.SourceAppUserModelId))
            Log.Info($"Media session: {_session.SourceAppUserModelId}");
        SourceApp = FriendlyName(_session.SourceAppUserModelId);
        IsSpotify = IsSpotifyId(_session.SourceAppUserModelId);
        IsAppleMusic = IsAppleMusicId(_session.SourceAppUserModelId);
        _ = RefreshMediaAsync();
        RefreshPlayback();
        RefreshTimeline();
    }

    private static bool IsPlayingSession(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    private static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Start-menu name for a media session's app id (resolves browser web apps like Spotify in Chrome).</summary>
    private static string? InstalledName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (AppNames)
        {
            if (AppNames.TryGetValue(id, out var cached)) return cached;
            var apps = InstalledApps.Cached;
            if (apps is null) return null; // still loading in the background - resolved again when ready
            var name = apps.FirstOrDefault(a => a.LaunchPath.EndsWith("\\" + id, StringComparison.OrdinalIgnoreCase))?.Name;
            if (name is not null) name = name.Replace(" (Web-App)", "");
            AppNames[id] = name ?? "";
            return name;
        }
    }

    private static bool IsSpotifyId(string? id) =>
        id?.Contains("spotify", StringComparison.OrdinalIgnoreCase) == true ||
        InstalledName(id)?.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsAppleMusicId(string? id) =>
        id?.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) == true ||
        InstalledName(id)?.Equals("Apple Music", StringComparison.OrdinalIgnoreCase) == true;

    private static string FriendlyName(string id)
    {
        if (IsSpotifyId(id)) return "Spotify";
        if (IsAppleMusicId(id)) return "Apple Music";
        if (InstalledName(id) is { Length: > 0 } installed) return installed;
        if (id.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (id.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (id.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (id.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase)) return "Media Player";
        var name = id.Split('!')[0];
        name = Path.GetFileNameWithoutExtension(name);
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : id;
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        _ui.BeginInvoke(() => _ = RefreshMediaAsync());

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        _ui.BeginInvoke(() => { RefreshPlayback(); PickSession(); });

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        _ui.BeginInvoke(RefreshTimeline);

    private async Task RefreshMediaAsync()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (!ReferenceEquals(session, _session) || props is null) return;
            // Mid-skip the player briefly reports an empty track: keep the old one and look again shortly.
            if (string.IsNullOrEmpty(props.Title))
            {
                if (Title.Length > 0) { ScheduleMediaRetry(); return; }
            }
            else if (props.Title != Title)
            {
                Hold(TimeSpan.FromSeconds(1.2)); // a track change is often followed by a short "stopped" blip
            }
            Title = props.Title ?? "";
            Artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            Album = props.AlbumTitle ?? "";

            string key = $"{Title}|{Artist}";
            // The first track seen after startup is not a "change".
            bool changed = key != _lastTrackKey && Title.Length > 0 && _lastTrackKey.Length > 0;
            _lastTrackKey = key;

            if (props.Thumbnail is not null)
            {
                using var ras = await props.Thumbnail.OpenReadAsync();
                using var stream = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                if (!ReferenceEquals(session, _session)) return;
                OfferCover(ms.ToArray(), Title);
            }
            else if (!InHold)
            {
                // During a skip the artwork often arrives a moment after the title; don't blank it in between.
                Cover = null;
                SetAccent(Color.FromRgb(0x30, 0xD1, 0x58));
            }
            else ScheduleMediaRetry();

            // Players (esp. browser web apps) send title first and the real artwork a moment later, sometimes with the
            // app icon in between. Announce the new track once updates have settled, so the peek shows the cover.
            if (changed) _pendingTrackPeek = true;
            if (_pendingTrackPeek) ScheduleTrackPeek();
        }
        catch { }
    }

    private void SetAccent(Color c)
    {
        Accent = c;
        var b = new SolidColorBrush(c);
        b.Freeze();
        AccentBrush = b;
    }

    private void RefreshPlayback()
    {
        if (_session is null) return;
        try
        {
            var info = _session.GetPlaybackInfo();
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            // Ignore the brief "changing/stopped" state while a skip is in flight; re-check when the hold ends.
            if (!playing && IsPlaying && InHold) { ScheduleRecheck(_holdUntil - DateTime.UtcNow); return; }
            IsPlaying = playing;
            CanSkip = info.Controls.IsNextEnabled;
            if (IsPlaying) _tick.Start(); else _tick.Stop();
            RefreshTimeline();
        }
        catch { }
    }

    private void RefreshTimeline()
    {
        if (_session is null) return;
        // Right after a seek the player may still report the old position once: do not jump back.
        if (_scrubbing || DateTime.UtcNow < _seekSettleUntil) return;
        try
        {
            var t = _session.GetTimelineProperties();
            DurationSeconds = Math.Max(0, (t.EndTime - t.StartTime).TotalSeconds);
            _positionAtStamp = t.Position;
            // LastUpdatedTime tells us when Position was sampled; extrapolate from there.
            _positionStamp = t.LastUpdatedTime.UtcDateTime;
            if (_positionStamp == default || _positionStamp > DateTime.UtcNow) _positionStamp = DateTime.UtcNow;
            UpdateInterpolatedPosition();
        }
        catch { }
    }

    private void UpdateInterpolatedPosition()
    {
        if (_scrubbing) return;
        var pos = _positionAtStamp;
        if (IsPlaying) pos += DateTime.UtcNow - _positionStamp;
        double secs = Math.Clamp(pos.TotalSeconds, 0, DurationSeconds > 0 ? DurationSeconds : double.MaxValue);
        PositionSeconds = secs;
        PositionText = Format(secs);
        RemainingText = "-" + Format(Math.Max(0, DurationSeconds - secs));
    }

    private static string Format(double s)
    {
        var ts = TimeSpan.FromSeconds(s);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
    }

    [RelayCommand]
    private async Task PlayPause()
    {
        if (_session is null) return;
        IsPlaying = !IsPlaying; // optimistic, like iOS
        try { await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    [RelayCommand]
    private async Task Next()
    {
        if (_session is null) return;
        Hold(SkipHold);
        try { await _session.TrySkipNextAsync(); } catch { }
    }

    [RelayCommand]
    private async Task Previous()
    {
        if (_session is null) return;
        Hold(SkipHold);
        try { await _session.TrySkipPreviousAsync(); } catch { }
    }

    // Visible state transitions are logged: a skip should never produce "playing=False" or "session=False" in between.
    partial void OnIsPlayingChanged(bool value) => Log.Info($"Media: playing={value} '{Title}'");
    partial void OnHasSessionChanged(bool value) => Log.Info($"Media: session={value}");

    // ---- bridging the gap while a track changes ----

    private static readonly TimeSpan SkipHold = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan LossGrace = TimeSpan.FromSeconds(1.5);
    private DateTime _holdUntil;
    private DateTime? _lostSince;
    private DispatcherTimer? _recheck, _mediaRetry;

    private bool InHold => DateTime.UtcNow < _holdUntil;

    /// <summary>For a moment after a skip, transient "stopped / no session / empty title" states are not shown.</summary>
    private void Hold(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        if (until > _holdUntil) _holdUntil = until;
        ScheduleRecheck(duration);
    }

    /// <summary>Re-evaluates the real state once the hold/grace period is over.</summary>
    private void ScheduleRecheck(TimeSpan after)
    {
        _recheck ??= new DispatcherTimer(DispatcherPriority.Normal, _ui);
        _recheck.Stop();
        _recheck.Interval = after > TimeSpan.FromMilliseconds(50) ? after + TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(100);
        _recheck.Tick -= OnRecheck;
        _recheck.Tick += OnRecheck;
        _recheck.Start();
    }

    private void OnRecheck(object? sender, EventArgs e)
    {
        _recheck!.Stop();
        PickSession();
        RefreshPlayback();
    }

    // ---- artwork: never show the player's app icon as a cover ----
    // Browser web apps (Spotify in Chrome) report the browser icon as artwork for a moment on every track change.
    // 1) After a title change, artwork is applied only once updates settle (the last image wins).
    // 2) An image that shows up for different titles is the app icon, not a cover, and is ignored from then on.

    private readonly Dictionary<string, HashSet<string>> _artTitles = new();
    private readonly HashSet<string> _placeholderArt = new();
    private byte[]? _pendingArt;
    private string _pendingArtTitle = "";
    private string _coverTitle = "";
    private DispatcherTimer? _artSettle;

    private void OfferCover(byte[] bytes, string title)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes));
        if (!_artTitles.TryGetValue(hash, out var titles)) _artTitles[hash] = titles = new HashSet<string>();
        titles.Add(title);
        if (titles.Count > 1 && _placeholderArt.Add(hash)) Log.Info("Media: ignoring app icon used as artwork");
        if (_placeholderArt.Contains(hash)) { ScheduleMediaRetry(); return; } // keep the old cover, real art follows

        _pendingArt = bytes;
        _pendingArtTitle = title;
        if (title == _coverTitle && _artSettle?.IsEnabled != true) { ApplyPendingArt(); return; } // same track: art update
        _artSettle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(650), DispatcherPriority.Normal, (_, _) =>
        {
            _artSettle!.Stop();
            ApplyPendingArt();
        }, _ui);
        _artSettle.Stop();
        _artSettle.Start();
    }

    private void ApplyPendingArt()
    {
        if (_pendingArt is null) return;
        var img = ImageTools.Load(new MemoryStream(_pendingArt), 300);
        _pendingArt = null;
        if (img is null) return;
        Cover = img;
        _coverTitle = _pendingArtTitle;
        SetAccent(ImageTools.Accent(img, Color.FromRgb(0x30, 0xD1, 0x58)));
    }

    private bool _pendingTrackPeek;
    private DispatcherTimer? _trackPeek;
    private int _peekWaits;

    private void ScheduleTrackPeek()
    {
        _trackPeek ??= new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Normal, (_, _) =>
        {
            _trackPeek!.Stop();
            if (!_pendingTrackPeek) return;
            // Wait until the artwork for the new title has been applied (or give up after a few tries).
            if (_coverTitle != Title && _peekWaits++ < 4) { _trackPeek.Start(); return; }
            _peekWaits = 0;
            _pendingTrackPeek = false;
            if (IsPlaying && Title.Length > 0) TrackChanged?.Invoke();
        }, _ui);
        _trackPeek.Stop();
        _trackPeek.Start(); // restarted by every further update of the same change
    }

    private void ScheduleMediaRetry()
    {
        _mediaRetry ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) =>
        {
            _mediaRetry!.Stop();
            _ = RefreshMediaAsync();
        }, _ui);
        _mediaRetry.Stop();
        _mediaRetry.Start();
    }

    private bool _scrubbing;
    private DateTime _seekSettleUntil;

    /// <summary>Dragging along the timeline: show the time under the cursor without seeking yet.</summary>
    public void Scrub(double seconds)
    {
        _scrubbing = true;
        double secs = Math.Clamp(seconds, 0, DurationSeconds);
        PositionSeconds = secs;
        PositionText = Format(secs);
        RemainingText = "-" + Format(Math.Max(0, DurationSeconds - secs));
    }

    public async Task SeekAsync(double seconds)
    {
        _scrubbing = false;
        if (_session is null) return;
        _seekSettleUntil = DateTime.UtcNow.AddSeconds(1.5);
        try
        {
            _positionAtStamp = TimeSpan.FromSeconds(seconds);
            _positionStamp = DateTime.UtcNow;
            UpdateInterpolatedPosition();
            await _session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(seconds).Ticks);
        }
        catch { }
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo(ImageSource? cover, string title, string artist)
    {
        HasSession = true; IsPlaying = true; Title = title; Artist = artist; SourceApp = "Spotify"; IsSpotify = true;
        Cover = cover;
        if (cover is BitmapSource bs) SetAccent(ImageTools.Accent(bs, Accent));
        DurationSeconds = 214; _positionAtStamp = TimeSpan.FromSeconds(81); _positionStamp = DateTime.UtcNow;
        UpdateInterpolatedPosition();
    }
}
