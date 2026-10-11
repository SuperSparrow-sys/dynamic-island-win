using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>A paired device as the settings page and the island card show it.</summary>
public sealed partial class LinkDeviceView : ObservableObject
{
    public LinkDeviceView(string id, string name) { Id = id; _name = name; }

    public string Id { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private LinkPeerState _state;
    /// <summary>"LAN", "Tailscale" or "" (not connected).</summary>
    [ObservableProperty] private string _via = "";
    [ObservableProperty] private int? _battery;
    [ObservableProperty] private bool _charging;
    [ObservableProperty] private string _version = "";
    /// <summary>"Entkoppeln" was pressed once: the row asks again in place.</summary>
    [ObservableProperty] private bool _confirmUnpair;

    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(Glyph)); OnPropertyChanged(nameof(IsLaptop)); }
    partial void OnStateChanged(LinkPeerState value) { OnPropertyChanged(nameof(IsOnline)); OnPropertyChanged(nameof(IsRejected)); OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(ShortState)); }
    partial void OnViaChanged(string value) { OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(ShortState)); }
    partial void OnBatteryChanged(int? value) { OnPropertyChanged(nameof(HasBattery)); OnPropertyChanged(nameof(BatteryText)); OnPropertyChanged(nameof(BatteryLevel)); OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(ShortState)); OnPropertyChanged(nameof(Glyph)); OnPropertyChanged(nameof(IsLaptop)); }
    partial void OnChargingChanged(bool value) => OnPropertyChanged(nameof(StateText));

    public bool IsOnline => State == LinkPeerState.Online;
    public bool IsRejected => State == LinkPeerState.Rejected;
    public bool HasBattery => Battery is not null;
    public string BatteryText => Battery is int b ? $"{b} %" : "";
    public double BatteryLevel => (Battery ?? 0) / 100.0;
    /// <summary>Icon key: a laptop for names that sound like one or devices with a battery, else a monitor.</summary>
    public string Glyph => Battery is not null ? "Icon.Laptop" : CursorHopService.IconFor(Name);
    public bool IsLaptop => Glyph == "Icon.Laptop";

    /// <summary>For the narrow island card: "LAN · 76 %".</summary>
    public string ShortState => State switch
    {
        LinkPeerState.Online => (Via.Length > 0 ? Via : (Loc.German ? "Verbunden" : "Connected")) + (Battery is int b ? $" · {b} %" : ""),
        LinkPeerState.Connecting => Loc.German ? "Verbindet …" : "Connecting …",
        LinkPeerState.Rejected => Loc.German ? "Abgelehnt" : "Rejected",
        _ => Loc.German ? "Nicht verbunden" : "Not connected",
    };

    public string StateText => State switch
    {
        LinkPeerState.Online => (Loc.German ? "Verbunden" : "Connected") + (Via.Length > 0 ? " · " + Via : "")
                                + (Battery is int b ? $" · {b} %" + (Charging ? (Loc.German ? " lädt" : " charging") : "") : ""),
        LinkPeerState.Connecting => Loc.German ? "Verbindet …" : "Connecting …",
        LinkPeerState.Rejected => Loc.German ? "Abgelehnt, bitte neu koppeln" : "Rejected, pair again",
        _ => Loc.German ? "Nicht verbunden" : "Not connected",
    };
}

/// <summary>A DynamicBay nearby that is not paired yet.</summary>
public sealed record LinkFoundView(string Id, string Name, string Address, string ViaText);

/// <summary>What plays on another device, for the media card.</summary>
public sealed partial class RemoteMediaView : ObservableObject
{
    [ObservableProperty] private bool _has;
    [ObservableProperty] private string _peerId = "";
    [ObservableProperty] private string _device = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _app = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty] private double _progress;

    public string DeviceText => (Loc.German ? "Auf " : "On ") + Device;
    partial void OnDeviceChanged(string value) => OnPropertyChanged(nameof(DeviceText));
}

/// <summary>
/// The link in the app: starts and stops <see cref="LinkService"/> with "Geräte koppeln", owns the mirroring features
/// (each with its own switch), keeps the lists the settings page and the island show, and connects the features to
/// the local services (clipboard, notifications, music, shelf, battery). Everything here runs on the UI thread.
/// </summary>
public sealed partial class LinkHub : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private DeviceIdentity? _identity;
    private readonly DispatcherTimer _tick;
    private CursorHopAnnouncer _announcer = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
    private readonly Dictionary<string, LinkNowPlaying> _remoteMedia = new(StringComparer.Ordinal);
    private DispatcherTimer? _mediaDebounce;
    private ImageSource? _coverSource;
    private byte[]? _coverJpeg;

    public LinkService? Service { get; private set; }
    public LinkStatusFeature? Status { get; private set; }
    public LinkFilesFeature? Files { get; private set; }
    public LinkClipboardFeature? Clipboard { get; private set; }
    public LinkNotificationsFeature? Notifications { get; private set; }
    public LinkMediaFeature? Media { get; private set; }
    public LinkRoleFeature? Role { get; private set; }
    /// <summary>For "Hauptgerät: automatisch" (the mouse is here or on the other device).</summary>
    public CursorHopService? CursorHop { get; set; }

    /// <summary>This device shows everything (no main device chosen, nothing connected, or it is the main one).</summary>
    [ObservableProperty] private bool _isMain = true;
    /// <summary>The island makes way: another device is main and "Insel ausblenden" is on.</summary>
    [ObservableProperty] private bool _hideIsland;
    /// <summary>Name of the first paired device, for the choice "Hauptgerät".</summary>
    [ObservableProperty] private string _otherName = "";
    public event Action? RoleChanged;
    /// <summary>A peek the other (quiet) device handed over.</summary>
    public event Action<LinkPeek>? RemotePeek;
    private double _lastIdle;
    private int _ticks;

    /// <summary>"Hauptgerät": 0 none, 1 this device, 2 the other device, 3 automatic.</summary>
    public int MainChoice
    {
        get
        {
            return _settings.LinkMain switch { LinkRoleFeature.This => 1, LinkRoleFeature.Other => 2, LinkRoleFeature.Auto => 3, _ => 0 };
        }
        set
        {
            // Stored relative to this PC ("this" / "other"), so it still makes sense after a settings export.
            string main = value switch { 1 => LinkRoleFeature.This, 2 => LinkRoleFeature.Other, 3 => LinkRoleFeature.Auto, _ => "" };
            if (main == _settings.LinkMain) return;
            if (Role is { } r) _ = r.ChooseAsync(main);
            else { _settings.LinkMainChanged = DateTime.UtcNow; _settings.LinkMain = main; }
            OnPropertyChanged();
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Seconds since the last keyboard or mouse input on this PC.</summary>
    internal static double IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return Math.Max(0, unchecked((uint)Environment.TickCount - info.Time) / 1000.0);
    }

    private void UpdateRole()
    {
        bool main = Role?.IsMain ?? true;
        IsMain = main;
        HideIsland = !main && _settings.LinkHideWhenNotMain;
        RoleChanged?.Invoke();
    }

    /// <summary>
    /// The main device does not forward its own notifications (the quiet one does, to it); without a main device
    /// both share theirs.
    /// </summary>
    private bool ForwardsNotifications => Role is not { IsMain: true } || _settings.LinkMain.Length == 0 || !AnyOnline;

    // Local services (set once by the app).
    public MediaService? LocalMedia { get; set; }
    public ClipboardService? LocalClipboard { get; set; }
    public NotificationService? LocalNotifications { get; set; }
    public ShelfService? Shelf { get; set; }
    public BatteryService? Battery { get; set; }

    public ObservableCollection<LinkDeviceView> Devices { get; } = new();
    public ObservableCollection<LinkFoundView> Found { get; } = new();
    public RemoteMediaView Remote { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasDevices;
    [ObservableProperty] private bool _anyOnline;
    /// <summary>Files can go to a device right now (link on, Dateien on, one online).</summary>
    [ObservableProperty] private bool _canSendFiles;
    [ObservableProperty] private bool _pairingOpen;
    [ObservableProperty] private string _pairingRemaining = "";
    [ObservableProperty] private bool _busy;
    /// <summary>Result of the last pairing or why it did not work (shown under "Gerät hinzufügen").</summary>
    [ObservableProperty] private string _pairStatus = "";
    [ObservableProperty] private bool _pairStatusIsError;
    [ObservableProperty] private bool _hasFound;
    /// <summary>The media card shows the other device's music (nothing plays here, or the chip was pressed).</summary>
    [ObservableProperty] private bool _mediaShowsRemote;
    [ObservableProperty] private bool _preferRemote;

    public event Action<PairingSession>? PairingRequested;
    public event Action<PairingSession, PairingResult>? PairingFinished;
    /// <summary>A device connected (true) or left (false), debounced and not already announced by CursorHop.</summary>
    public event Action<string, bool>? DeviceAnnounced;
    public event Action<IncomingFileOffer>? FileStarted;
    public event Action<IncomingFileOffer, long>? FileProgress;
    public event Action<IncomingFileOffer, string>? FileReceived;
    public event Action<IncomingFileOffer, string>? FileFailed;
    /// <summary>A file went out: file name, device name, result.</summary>
    public event Action<string, string, FileTransferResult>? FileSent;
    /// <summary>A notification of another device was added to the list (peek it like a local one).</summary>
    public event Action<NotificationItem>? RemoteNotification;

    public LinkHub(AppSettings settings, Dispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        _tick = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Tick();
        settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.LinkDeviceName):
                    if (Service is { } s) { s.DeviceName = settings.LinkDeviceName; _ = Status?.PublishAsync(); }
                    break;
                case nameof(AppSettings.LinkShareFiles):
                    UpdateFlags();
                    break;
                case nameof(AppSettings.LinkShareStatus):
                    if (settings.LinkShareStatus) _ = Status?.PublishAsync(force: true);
                    else foreach (var d in Devices) { d.Battery = null; d.Charging = false; }
                    break;
                case nameof(AppSettings.LinkShareMedia):
                    if (settings.LinkShareMedia) PublishMediaSoon(force: true);
                    else { _remoteMedia.Clear(); UpdateRemote(); }
                    break;
                case nameof(AppSettings.PairedDevices):
                    SyncDevices();
                    break;
                case nameof(AppSettings.LinkMain):
                    Role?.Update();
                    OnPropertyChanged(nameof(MainChoice));
                    break;
                case nameof(AppSettings.LinkHideWhenNotMain):
                    if (Role is { } role) _ = role.HideChangedAsync();
                    UpdateRole();
                    break;
            }
        };
        SyncDevices();
    }

    // ---------- start / stop ----------

    /// <summary>"Geräte koppeln" on or off. Throws when the port is taken (nothing half started stays behind).</summary>
    public void SetEnabled(bool on)
    {
        if (!on) { Stop(); return; }
        if (Service is not null) return;
        _identity = DeviceIdentity.LoadOrCreate();
        var options = new LinkOptions
        {
            Port = DevMode.LinkPort ?? LinkProtocol.Port,
            DiscoveryPort = DevMode.DiscoveryPort ?? LinkProtocol.DiscoveryPort,
            // Test instances keep received files in their own data folder, never in the real downloads.
            DownloadFolder = DevMode.On ? Path.Combine(AppSettings.Folder, "Downloads") : new LinkOptions().DownloadFolder,
        };
        var link = new LinkService(_identity, _settings.LinkDeviceName, _settings.PairedDevices, options, new DispatcherSynchronizationContext(_ui));
        Service = link;
        link.PairingRequested += s => PairingRequested?.Invoke(s);
        link.PairingFinished += OnPairingFinished;
        link.PairedDevicesChanged += () => _settings.PairedDevices = new ObservableCollection<PairedDevice>(link.PairedDevices);
        link.PeerStateChanged += (_, _) => SyncDevices();
        link.PeerOnline += p => { _announcer.Connected(p.Name, DateTime.UtcNow); SyncDevices(); };
        link.PeerOffline += p => { _announcer.Disconnected(p.Name, DateTime.UtcNow); SyncDevices(); };
        link.DiscoveredChanged += SyncFound;
        CreateFeatures(link);
        try
        {
            link.Start(); // logs its state changes itself (never keys)
        }
        catch
        {
            Stop();
            throw;
        }
        IsRunning = true;
        _tick.Start();
        SyncDevices();
        PublishMediaSoon(force: true);
    }

    public void Stop()
    {
        _tick.Stop();
        foreach (var f in new LinkFeature?[] { Status, Clipboard, Files, Notifications, Media, Role }) f?.Dispose();
        Status = null; Files = null; Clipboard = null; Notifications = null; Media = null; Role = null;
        UpdateRole(); // offline: this device shows everything again
        try { Service?.Dispose(); } catch (Exception ex) { Log.Error("Link stop", ex); }
        Service = null;
        _identity?.Dispose();
        _identity = null;
        _announcer = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
        _remoteMedia.Clear();
        UpdateRemote();
        Found.Clear();
        HasFound = false;
        PairingOpen = false;
        IsRunning = false;
        SyncDevices();
    }

    private void CreateFeatures(LinkService link)
    {
        Status = new LinkStatusFeature(link, _settings, LocalStatus);
        Status.Received += (id, s) =>
        {
            Log.Info($"Link: status from {s.Name}: battery {(s.Battery is int pct ? pct + " %" : "none")}, version {s.Version}");
            if (Devices.FirstOrDefault(d => d.Id == id) is { } d) { d.Battery = s.Battery; d.Charging = s.Charging; d.Version = s.Version; }
        };

        Files = new LinkFilesFeature(link, _settings)
        {
            Shelf = Shelf,
            Folder = DevMode.On ? Path.Combine(AppSettings.Folder, "Downloads") : null,
        };
        Files.Started += o => FileStarted?.Invoke(o);
        Files.Progress += (o, b) => FileProgress?.Invoke(o, b);
        Files.Received += (o, p) => FileReceived?.Invoke(o, p);
        Files.Failed += (o, r) => FileFailed?.Invoke(o, r);

        Clipboard = new LinkClipboardFeature(link, _settings, Files, Path.Combine(AppSettings.Folder, "link-temp"));
        Clipboard.Received += c =>
        {
            bool added = LocalClipboard?.AddFromDevice(c.PeerName, c.Text, c.Png) == true;
            Log.Info($"Link: clipboard {(c.Text is not null ? $"text ({c.Text.Length} chars)" : $"picture ({c.Png?.Length} bytes)")} from {c.PeerName}{(added ? "" : ", already in the history")}");
        };

        Notifications = new LinkNotificationsFeature(link, _settings);
        Notifications.Received += n =>
        {
            if (LocalNotifications is null) return;
            var logo = n.Icon is { } icon ? ImageTools.Load(new MemoryStream(icon), 64) : null;
            if (LocalNotifications.AddRemote(n.PeerName, n.App, n.Title, n.Body, logo))
                RemoteNotification?.Invoke(LocalNotifications.Items[0]);
        };

        Media = new LinkMediaFeature(link, _settings);
        Media.RemoteChanged += (id, name, np) =>
        {
            if (np is null) _remoteMedia.Remove(id); else _remoteMedia[id] = np;
            Log.Info(np is null ? $"Link: nothing plays on {name}" : $"Link: {name} plays ({(np.Playing ? "playing" : "paused")}, {np.App})");
            UpdateRemote();
        };
        Media.CommandReceived += (id, cmd) => RunMediaCommand(cmd);

        Role = new LinkRoleFeature(link, _settings, IdleSeconds, () => CursorHop is { IsConnected: true } h ? h.CursorHere : null);
        Role.RoleChanged += UpdateRole;
        Role.ModeAdopted += () => OnPropertyChanged(nameof(MainChoice));
        Role.PeekReceived += p => RemotePeek?.Invoke(p);
    }

    /// <summary>Hooks the local services once (after they exist).</summary>
    public void Attach(MediaService media, ClipboardService clipboard, NotificationService notifications, ShelfService shelf, BatteryService battery)
    {
        LocalMedia = media; LocalClipboard = clipboard; LocalNotifications = notifications; Shelf = shelf; Battery = battery;
        battery.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BatteryService.Percent) or nameof(BatteryService.IsCharging) or nameof(BatteryService.HasBattery))
                _ = Status?.PublishAsync();
        };
        clipboard.CopiedHere += item =>
        {
            if (Clipboard is null) return;
            // CursorHop tags its entries a moment later: wait for that, then share only what was really copied here.
            var t = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (Clipboard is not { } c) return;
                if (item.IsText && item.Text is { } text) _ = c.ShareTextAsync(text, item.SourceDevice);
                else if (item.IsImage && ClipboardService.PngOf(item) is { } png) _ = c.ShareImageAsync(png, item.SourceDevice);
            };
            t.Start();
        };
        notifications.Seen += n =>
        {
            if (Notifications is null || n.Device is not null || !ForwardsNotifications) return;
            _ = Notifications.ForwardAsync(n.App, n.Title, n.Body, Png(n.Logo, 48), n.Device);
        };
        media.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaService.Title) or nameof(MediaService.Artist) or nameof(MediaService.IsPlaying)
                or nameof(MediaService.HasSession) or nameof(MediaService.Cover) or nameof(MediaService.DurationSeconds))
                PublishMediaSoon();
            if (e.PropertyName == nameof(MediaService.HasSession)) UpdateRemote();
        };
        UpdateMediaFlags();
    }

    private LinkStatus LocalStatus()
    {
        var b = Battery;
        bool has = b?.HasBattery == true;
        return new LinkStatus(Service?.DeviceName ?? LinkService.CleanName(_settings.LinkDeviceName), has ? b!.Percent : null, has && b!.IsCharging, App.Version);
    }

    // ---------- devices ----------

    private void SyncDevices()
    {
        var paired = Service?.PairedDevices ?? _settings.PairedDevices.ToList();
        foreach (var gone in Devices.Where(d => paired.All(p => p.Id != d.Id)).ToList()) Devices.Remove(gone);
        foreach (var p in paired)
        {
            var view = Devices.FirstOrDefault(d => d.Id == p.Id);
            if (view is null) Devices.Add(view = new LinkDeviceView(p.Id, p.Name));
            view.Name = p.Name;
            view.State = Service is { } s ? s.GetState(p.Id) : p.Rejected ? LinkPeerState.Rejected : LinkPeerState.Offline;
            view.Via = view.IsOnline && Service?.ConnectionInfo(p.Id)?.Remote is { } r ? ViaText(r.Address) : "";
            if (!view.IsOnline) { view.Battery = null; view.Charging = false; }
            else if (Status?.StatusOf(p.Id) is { } st && _settings.LinkShareStatus) { view.Battery = st.Battery; view.Charging = st.Charging; view.Version = st.Version; }
        }
        HasDevices = Devices.Count > 0;
        OtherName = Devices.FirstOrDefault()?.Name ?? (Loc.German ? "Anderes Gerät" : "Other device");
        UpdateFlags();
    }

    private void UpdateFlags()
    {
        AnyOnline = IsRunning && Devices.Any(d => d.IsOnline);
        CanSendFiles = AnyOnline && _settings.LinkShareFiles;
        OnPropertyChanged(nameof(OnlineDevices));
    }

    /// <summary>"Tailscale" for 100.64.0.0/10 and fd7a:115c:a1e0::/48, "LAN" for the rest.</summary>
    public static string ViaText(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return LinkDiscovery.IsTailscale(ip) ? "Tailscale" : IPAddress.IsLoopback(ip) ? (Loc.German ? "dieser PC" : "this PC") : "LAN";
    }

    public IEnumerable<LinkDeviceView> OnlineDevices => Devices.Where(d => d.IsOnline);

    private void SyncFound()
    {
        if (Service is null) return;
        var list = Service.Discovered.Where(d => d.Known && !d.Paired).ToList();
        Found.Clear();
        foreach (var d in list)
        {
            var via = d.Via.HasFlag(LinkVia.Tailscale) ? "Tailscale" : d.Via.HasFlag(LinkVia.Lan) ? "LAN" : (Loc.German ? "Adresse" : "Address");
            Found.Add(new LinkFoundView(d.Id, d.Name, d.Addresses.FirstOrDefault() ?? "", via));
        }
        HasFound = Found.Count > 0;
    }

    private void Tick()
    {
        if (Service is { } s)
        {
            bool open = s.PairingWindowOpen;
            PairingOpen = open;
            var left = s.PairingWindowEndsUtc - DateTime.UtcNow;
            PairingRemaining = open ? $"{(int)left.TotalMinutes}:{left.Seconds:00}" : "";
        }
        foreach (var (name, connected) in _announcer.Due(DateTime.UtcNow))
            if (DeviceAnnouncements.TryAnnounce(name, connected, DateTime.UtcNow)) DeviceAnnounced?.Invoke(name, connected);

        // "Hauptgerät: automatisch": tell the other device how recently this one was used - every 5 s, and at once
        // when someone starts typing or moving the mouse here again.
        if (Role is { } role && _settings.LinkMain == LinkRoleFeature.Auto && AnyOnline)
        {
            double idle = IdleSeconds();
            bool resumed = idle < 1.5 && _lastIdle > 5;
            _lastIdle = idle;
            if (resumed || ++_ticks % 5 == 0) _ = role.AnnounceAsync();
            role.Update();
        }
    }

    // ---------- pairing ----------

    [RelayCommand]
    public void AllowPairing()
    {
        if (Service is not { } s) return;
        s.AllowPairing();
        s.RefreshDiscovery();
        PairStatus = "";
        Tick();
    }

    [RelayCommand]
    public void ClosePairing()
    {
        Service?.ClosePairingWindow();
        Tick();
    }

    [RelayCommand]
    public void Refresh() => Service?.RefreshDiscovery();

    /// <summary>Pairs with a found device or a typed address (IP, host or Tailscale name, optional ":port").</summary>
    public async Task PairWithAsync(string address)
    {
        if (Service is not { } s || Busy) return;
        address = address.Trim();
        if (!LinkProtocol.TryParseAddress(address, LinkProtocol.Port, out _, out _))
        {
            SetStatus(Loc.German ? "Die Adresse stimmt nicht." : "That address is not valid.", error: true);
            return;
        }
        Busy = true;
        SetStatus(Loc.German ? "Verbinde …" : "Connecting …", error: false);
        try
        {
            await s.PairAsync(address);
            SetStatus(Loc.German ? "Code auf beiden Geräten vergleichen." : "Compare the code on both devices.", error: false);
        }
        catch (LinkException ex) { SetStatus(PairErrorText(ex.Reason), error: true); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { SetStatus(PairErrorText("protocol"), error: true); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private Task PairFound(LinkFoundView? found) => found is null ? Task.CompletedTask : PairWithAsync(found.Address);

    public static string PairErrorText(string reason) => reason switch
    {
        "pair-closed" or "auth" => Loc.German ? "Auf dem anderen Gerät zuerst „Koppeln erlauben“ wählen." : "Choose “Allow pairing” on the other device first.",
        "rate-limited" => Loc.German ? "Zu viele Versuche. Bitte später noch einmal." : "Too many attempts. Please try again later.",
        "busy" => Loc.German ? "Gerade läuft schon eine Kopplung." : "A pairing is already in progress.",
        "unreachable" or "wrong-device" => Loc.German ? "Gerät nicht erreichbar." : "Device not reachable.",
        _ => Loc.German ? "Koppeln hat nicht geklappt." : "Pairing did not work.",
    };

    public static string SendErrorText(FileTransferResult r) => r switch
    {
        FileTransferResult.Offline => Loc.German ? "Gerät nicht verbunden." : "Device not connected.",
        FileTransferResult.Declined => Loc.German ? "Auf dem Gerät ist der Empfang von Dateien aus." : "Receiving files is off on that device.",
        FileTransferResult.TooLarge => Loc.German ? "Die Datei ist zu groß." : "The file is too large.",
        FileTransferResult.Cancelled => Loc.German ? "Abgebrochen." : "Cancelled.",
        _ => Loc.German ? "Übertragung unterbrochen." : "Transfer interrupted.",
    };

    public static string ResultText(PairingResult r, string peer) => r switch
    {
        PairingResult.Paired => Loc.German ? $"Mit {peer} gekoppelt." : $"Paired with {peer}.",
        PairingResult.Declined => Loc.German ? "Kopplung abgelehnt." : "Pairing declined.",
        PairingResult.TimedOut => Loc.German ? "Zeit abgelaufen." : "Time ran out.",
        _ => Loc.German ? "Verbindung unterbrochen." : "Connection lost.",
    };

    private void OnPairingFinished(PairingSession session, PairingResult result)
    {
        SetStatus(ResultText(result, session.PeerName), error: result != PairingResult.Paired);
        if (result == PairingResult.Paired)
        {
            _settings.LinkEnabled = true;
            // First pairing: the card with the devices joins the System page once (it can be moved or removed).
            if (!_settings.HomeWidgets.Contains(Widgets.Paired) && !_settings.SystemWidgets.Contains(Widgets.Paired) && _settings.PairedDevices.Count <= 1)
                _settings.SystemWidgets.Insert(0, Widgets.Paired);
        }
        Tick();
        SyncDevices();
        SyncFound();
        PairingFinished?.Invoke(session, result);
    }

    private void SetStatus(string text, bool error)
    {
        PairStatus = text;
        PairStatusIsError = error;
    }

    [RelayCommand]
    private void AskUnpair(LinkDeviceView? d) { if (d is not null) d.ConfirmUnpair = true; }

    [RelayCommand]
    private void CancelUnpair(LinkDeviceView? d) { if (d is not null) d.ConfirmUnpair = false; }

    [RelayCommand]
    private async Task Unpair(LinkDeviceView? d)
    {
        if (d is null) return;
        d.ConfirmUnpair = false;
        if (Service is { } s) await s.UnpairAsync(d.Id);
        else
        {
            var left = _settings.PairedDevices.Where(p => p.Id != d.Id).ToList();
            _settings.PairedDevices = new ObservableCollection<PairedDevice>(left);
        }
        _remoteMedia.Remove(d.Id);
        UpdateRemote();
        SyncDevices();
    }

    // ---------- files ----------

    /// <summary>Sends shelf files to a device; the result comes as <see cref="FileSent"/>.</summary>
    public async Task SendFilesAsync(string deviceId, IEnumerable<string> paths)
    {
        if (Files is not { } f) return;
        var device = Devices.FirstOrDefault(d => d.Id == deviceId)?.Name ?? "";
        foreach (var path in paths.Where(File.Exists).ToList())
        {
            var result = await f.SendAsync(deviceId, path);
            FileSent?.Invoke(Path.GetFileName(path), device, result);
        }
    }

    // ---------- music ----------

    private void PublishMediaSoon(bool force = false)
    {
        if (Media is null || LocalMedia is null) return;
        _mediaDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _mediaDebounce!.Stop();
            if (Media is { } m && LocalMedia is { } local) _ = m.PublishAsync(NowPlaying(local), (bool)(_mediaDebounce.Tag ?? false));
            _mediaDebounce.Tag = false;
        }, _ui);
        if (force) _mediaDebounce.Tag = true;
        _mediaDebounce.Stop();
        _mediaDebounce.Start();
    }

    private LinkNowPlaying NowPlaying(MediaService m)
    {
        if (!m.HasSession || m.Title.Length == 0) return LinkNowPlaying.None;
        if (!ReferenceEquals(m.Cover, _coverSource))
        {
            _coverSource = m.Cover;
            _coverJpeg = Jpeg(m.Cover as BitmapSource, 300);
        }
        return new LinkNowPlaying(true, m.Title, m.Artist, m.SourceApp, m.IsPlaying, m.PositionSeconds, m.DurationSeconds, _coverJpeg);
    }

    private void UpdateRemote()
    {
        // The one that plays, else the latest; a device that left takes its music along.
        var pick = _remoteMedia.OrderByDescending(kv => kv.Value.Playing).ThenByDescending(kv => kv.Value.Stamp).FirstOrDefault();
        if (pick.Value is null || !_settings.LinkShareMedia)
        {
            Remote.Has = false;
            PreferRemote = false;
        }
        else
        {
            var np = pick.Value;
            Remote.PeerId = pick.Key;
            Remote.Device = Devices.FirstOrDefault(d => d.Id == pick.Key)?.Name ?? "";
            Remote.Title = np.Title;
            Remote.Artist = np.Artist;
            Remote.App = np.App;
            Remote.IsPlaying = np.Playing;
            Remote.Progress = np.Duration > 0 ? np.Position / np.Duration : 0;
            Remote.Cover = np.Cover is { } c ? ImageTools.Load(new MemoryStream(c), 300) : null;
            Remote.Has = true;
        }
        UpdateMediaFlags();
    }

    partial void OnPreferRemoteChanged(bool value) => UpdateMediaFlags();

    /// <summary>The media card shows one of three: the music here, the other device's, or "nothing plays".</summary>
    [ObservableProperty] private bool _mediaShowsLocal;
    [ObservableProperty] private bool _mediaShowsNothing = true;

    private void UpdateMediaFlags()
    {
        bool local = LocalMedia?.HasSession == true;
        MediaShowsRemote = Remote.Has && (!local || PreferRemote);
        MediaShowsLocal = local && !MediaShowsRemote;
        MediaShowsNothing = !local && !MediaShowsRemote;
    }

    /// <summary>The chip in the media card: switch between the music here and on the other device.</summary>
    [RelayCommand]
    private void ToggleRemote() => PreferRemote = !PreferRemote;

    [RelayCommand] private Task RemotePlayPause() => RemoteCommand("playpause");
    [RelayCommand] private Task RemoteNext() => RemoteCommand("next");
    [RelayCommand] private Task RemotePrevious() => RemoteCommand("previous");

    private async Task RemoteCommand(string cmd)
    {
        if (Media is null || !Remote.Has) return;
        if (cmd == "playpause") Remote.IsPlaying = !Remote.IsPlaying; // optimistic, the other side reports back
        await Media.SendCommandAsync(Remote.PeerId, cmd);
    }

    /// <summary>Another device steers the music here. Only a player that is there already is touched: nothing starts by itself.</summary>
    private void RunMediaCommand(string cmd)
    {
        if (LocalMedia is not { HasSession: true } m || !_settings.MediaEnabled) return;
        Log.Info($"Link: media command {cmd}");
        switch (cmd)
        {
            case "playpause": m.PlayPauseCommand.Execute(null); break;
            case "next": m.NextCommand.Execute(null); break;
            case "previous": m.PreviousCommand.Execute(null); break;
        }
    }

    // ---------- images ----------

    private static byte[]? Png(ImageSource? img, int size)
    {
        if (img is not BitmapSource bs) return null;
        try
        {
            var scaled = Scale(bs, size);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(scaled));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private static byte[]? Jpeg(BitmapSource? bs, int size)
    {
        if (bs is null) return null;
        try
        {
            var enc = new JpegBitmapEncoder { QualityLevel = 82 };
            enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(Scale(bs, size), PixelFormats.Bgr24, null, 0)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private static BitmapSource Scale(BitmapSource bs, int size)
    {
        double k = Math.Min(1, (double)size / Math.Max(bs.PixelWidth, bs.PixelHeight));
        return k >= 1 ? bs : new TransformedBitmap(bs, new ScaleTransform(k, k));
    }

    // ---------- snapshots ----------

    /// <summary>Snapshot mode: two paired devices, one found, the pairing window open, music on the laptop.</summary>
    internal void LoadDemo(ImageSource? cover)
    {
        IsRunning = true;
        Devices.Clear();
        Devices.Add(new LinkDeviceView("A1B2C3D4E5F60708", "LaptopJonathan") { State = LinkPeerState.Online, Via = "LAN", Battery = 76, Charging = true });
        Devices.Add(new LinkDeviceView("0F1E2D3C4B5A6978", "PC-Büro") { State = LinkPeerState.Offline });
        Found.Clear();
        Found.Add(new LinkFoundView("1122334455667788", "Surface-Go", "100.64.10.20", "Tailscale"));
        HasDevices = true; HasFound = true; OtherName = "LaptopJonathan";
        PairingOpen = true; PairingRemaining = "1:47";
        AnyOnline = true; CanSendFiles = true;
        Remote.PeerId = "A1B2C3D4E5F60708"; Remote.Device = "LaptopJonathan"; Remote.Title = "Ocean Lights"; Remote.Artist = "Lumen Drift";
        Remote.App = "Spotify"; Remote.IsPlaying = true; Remote.Progress = 0.42; Remote.Cover = cover; Remote.Has = true;
        UpdateMediaFlags();
    }
}
