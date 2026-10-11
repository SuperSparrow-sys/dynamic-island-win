using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicBay.Services;

public sealed class NotificationItem
{
    public uint Id { get; init; }
    public string App { get; init; } = "";
    public string AppId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime Time { get; init; }
    public ImageSource? Logo { get; init; }
    /// <summary>A paired device it came from over the link (null: from this PC). Never forwarded again.</summary>
    public string? Device { get; init; }
    /// <summary>When it joined the list here (for "the same message from both devices shows once").</summary>
    public DateTime Added { get; init; } = DateTime.Now;
    public bool SameAs(string app, string title, string body) => App == app && Title == title && Body == body;
    /// <summary>The app, and for notifications of another device "von Laptop".</summary>
    public string AppLabel => Device is null ? App : $"{App} · {(Loc.German ? "von" : "from")} {Device}";
    public string TimeText => (DateTime.Now - Time).TotalMinutes < 1
        ? (Loc.German ? "jetzt" : "now")
        : Time.ToString("HH:mm");
}

public enum NotificationAccess { Unknown, Unavailable, Denied, Allowed }

/// <summary>
/// Mirrors toast notifications from other apps. Requires package identity (sparse package registered by the installer);
/// without it the service reports Unavailable and the island only shows its own peeks.
/// </summary>
public sealed partial class NotificationService : ObservableObject
{
    private readonly AppSettings _settings;
    private UserNotificationListener? _listener;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly HashSet<uint> _seen = new();
    private bool _primed;

    public ObservableCollection<NotificationItem> Items { get; } = new();

    /// <summary>Latest chat messages (WhatsApp, Telegram, Signal, Discord, Teams, ...) for the messenger widget.</summary>
    public ObservableCollection<NotificationItem> Messages { get; } = new();

    private static readonly string[] MessengerApps =
        { "whatsapp", "telegram", "signal", "discord", "teams", "messenger", "slack", "threema", "skype", "element", "instagram" };

    public static bool IsMessenger(NotificationItem n) =>
        MessengerApps.Any(m => n.App.Contains(m, StringComparison.OrdinalIgnoreCase) || n.AppId.Contains(m, StringComparison.OrdinalIgnoreCase));

    private void SyncMessages()
    {
        var latest = Items.Where(IsMessenger).Take(4).ToList();
        if (latest.Select(m => m.Id).SequenceEqual(Messages.Select(m => m.Id))) return;
        Messages.Clear();
        foreach (var m in latest) Messages.Add(m);
    }
    [ObservableProperty] private NotificationAccess _access = NotificationAccess.Unknown;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isEmpty = true;
    /// <summary>The list shows: with access to Windows' notifications, or when a paired device sent some.</summary>
    [ObservableProperty] private bool _showList;
    /// <summary>No access and nothing from other devices: the tab explains why it stays empty.</summary>
    [ObservableProperty] private bool _showUnavailable;
    [ObservableProperty] private bool _showDenied;
    private uint _remoteId = 0xF000_0000;

    partial void OnAccessChanged(NotificationAccess value) => UpdateShow();

    private void UpdateShow()
    {
        ShowList = Access == NotificationAccess.Allowed || Items.Any(i => i.Device is not null);
        ShowUnavailable = !ShowList && Access is NotificationAccess.Unavailable or NotificationAccess.Unknown;
        ShowDenied = !ShowList && Access == NotificationAccess.Denied;
    }

    /// <summary>
    /// A notification of a paired device: into the list like a local one (muted apps stay out). True: it was added and
    /// may be announced (the caller checks quiet hours like for local ones).
    /// </summary>
    public bool AddRemote(string device, string app, string title, string body, ImageSource? logo)
    {
        if (!_settings.NotificationsEnabled || _settings.MutedApps.Contains(app)) return false;
        // WhatsApp, Teams … on both devices: the copy from the other one is the same message, shown once.
        if (IsRepeat(app, title, body)) return false;
        var item = new NotificationItem { Id = _remoteId++, App = app, Title = title, Body = body, Time = DateTime.Now, Logo = logo, Device = device };
        Items.Insert(0, item);
        while (Items.Count > 30) Items.RemoveAt(Items.Count - 1);
        Log.Info($"Notification from {app} on {device}");
        return true;
    }

    /// <summary>The same app, title and text joined the list in the last 10 s (from here or a paired device).</summary>
    internal bool IsRepeat(string app, string title, string body) =>
        Items.Any(i => i.SameAs(app, title, body) && (DateTime.Now - i.Added).TotalSeconds < 10);

    public event Action<NotificationItem>? Arrived;
    /// <summary>The user dismissed it in Windows (or it expired).</summary>
    public event Action<NotificationItem>? Removed;
    /// <summary>Every new notification, also with notifications or peeks switched off (Claude questions).</summary>
    public event Action<NotificationItem>? Seen;

    public NotificationService(AppSettings settings)
    {
        _settings = settings;
        Items.CollectionChanged += (_, _) => { Count = Items.Count; IsEmpty = Items.Count == 0; SyncMessages(); UpdateShow(); };
        UpdateShow();
    }

    public async Task StartAsync()
    {
        try
        {
            if (!HasPackageIdentity()) { Access = NotificationAccess.Unavailable; Log.Info("Notification access: unavailable (no package identity)"); return; }
            _listener = UserNotificationListener.Current;
            var status = await _listener.RequestAccessAsync();
            Access = status == UserNotificationListenerAccessStatus.Allowed ? NotificationAccess.Allowed : NotificationAccess.Denied;
            Log.Info($"Notification access: {Access}");
            if (Access != NotificationAccess.Allowed) return;
            _poll.Tick += async (_, _) => await PollAsync();
            _poll.Start();
            await PollAsync();
        }
        catch
        {
            Access = NotificationAccess.Unavailable;
        }
    }

    private static bool HasPackageIdentity()
    {
        try { return Windows.ApplicationModel.Package.Current is not null; }
        catch { return false; }
    }

    private bool _polling;

    /// <summary>One poll at a time: a slow one (many logos to load) must not overlap the next tick and show items twice.</summary>
    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try { await PollCoreAsync(); }
        finally { _polling = false; }
    }

    private async Task PollCoreAsync()
    {
        if (_listener is null) return;
        // Nothing needs them: neither the island (switched off) nor the Claude question detection.
        if (!_settings.NotificationsEnabled && !_settings.ClaudeEnabled) return;
        // Off the UI thread: the list and each of its properties are calls into Windows' notification service, and new
        // ones need their app logo. Every 1.2 s on the UI thread that held it for up to 900 ms on a busy PC.
        var listener = _listener;
        var seen = new HashSet<uint>(_seen);
        (HashSet<uint> Current, List<(uint Id, NotificationItem? Item)> New)? read;
        try { read = await Task.Run(() => ReadAsync(listener, seen)); }
        catch { return; }
        if (read is not { } r) return;

        var current = r.Current;
        // Drop entries the user dismissed in Windows.
        // (Notifications of paired devices are not in this list: they stay until dismissed here.)
        foreach (var gone in Items.Where(i => i.Device is null && !current.Contains(i.Id)).ToList()) { Items.Remove(gone); Removed?.Invoke(gone); }
        if (_seen.Count > 2000) _seen.IntersectWith(current); // only ids still in the notification center matter

        foreach (var (id, item) in r.New)
        {
            if (!_seen.Add(id)) continue;
            if (item is null) continue;
            if (_settings.MutedApps.Contains(item.App)) continue;
            if (Items.Any(i => i.Device is not null && i.SameAs(item.App, item.Title, item.Body) && (DateTime.Now - i.Added).TotalSeconds < 10)) continue; // the paired device's copy came first
            if (_primed) Seen?.Invoke(item);
            if (!_settings.NotificationsEnabled) continue; // off: not shown in the island at all
            Items.Insert(0, item);
            if (_primed) Log.Info($"Notification from {item.App} ({item.AppId})");
            if (_primed && _settings.NotificationsEnabled && !_settings.Quiet) Arrived?.Invoke(item);
        }
        while (Items.Count > 30) Items.RemoveAt(Items.Count - 1);
        _primed = true;
    }

    /// <summary>
    /// Reads the notification center on a worker thread: the ids of all toasts and the new ones (not in
    /// <paramref name="seen"/>) as items, oldest first. Null when Windows does not answer.
    /// </summary>
    private static async Task<(HashSet<uint> Current, List<(uint Id, NotificationItem? Item)> New)?> ReadAsync(UserNotificationListener listener, HashSet<uint> seen)
    {
        IReadOnlyList<UserNotification> list;
        try { list = await listener.GetNotificationsAsync(NotificationKinds.Toast); }
        catch { return null; }
        var current = new HashSet<uint>();
        var fresh = new List<(UserNotification N, uint Id, DateTimeOffset Time)>();
        foreach (var n in list)
        {
            try
            {
                uint id = n.Id;
                current.Add(id);
                if (!seen.Contains(id)) fresh.Add((n, id, n.CreationTime));
            }
            catch { } // gone meanwhile
        }
        var items = new List<(uint, NotificationItem?)>();
        foreach (var f in fresh.OrderBy(f => f.Time)) items.Add((f.Id, await ToItemAsync(f.N)));
        return (current, items);
    }

    private static async Task<NotificationItem?> ToItemAsync(UserNotification n)
    {
        try
        {
            var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new();
            string app = n.AppInfo.DisplayInfo.DisplayName;
            ImageSource? logo = null;
            try
            {
                var stream = await n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64)).OpenReadAsync();
                using var s = stream.AsStreamForRead();
                var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                ms.Position = 0;
                logo = ImageTools.Load(ms, 64);
            }
            catch { }
            return new NotificationItem
            {
                Id = n.Id,
                App = app,
                AppId = n.AppInfo.AppUserModelId,
                Title = texts.FirstOrDefault() ?? app,
                Body = string.Join("\n", texts.Skip(1)),
                Time = n.CreationTime.LocalDateTime,
                Logo = logo,
            };
        }
        catch { return null; }
    }

    [RelayCommand]
    public void Open(NotificationItem? item)
    {
        if (item is null || item.Device is not null || item.AppId.Length == 0) return; // the app of another device is not here
        try { Process.Start("explorer.exe", $"shell:AppsFolder\\{item.AppId}"); } catch { }
    }

    [RelayCommand]
    public void Dismiss(NotificationItem? item)
    {
        if (item is null) return;
        Items.Remove(item);
        if (item.Device is not null) return;
        try { _listener?.RemoveNotification(item.Id); } catch { }
    }

    [RelayCommand]
    public void ClearAll()
    {
        foreach (var i in Items.ToList()) Dismiss(i);
    }

    /// <summary>Opens Windows notification settings so users can turn off duplicate banners.</summary>
    public static void OpenWindowsSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); } catch { }
    }

    /// <summary>For design snapshots only.</summary>
    public void AddDemo(NotificationItem item) => Items.Add(item);
}
