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

    public event Action<NotificationItem>? Arrived;
    /// <summary>The user dismissed it in Windows (or it expired).</summary>
    public event Action<NotificationItem>? Removed;
    /// <summary>Every new notification, also with notifications or peeks switched off (Claude questions).</summary>
    public event Action<NotificationItem>? Seen;

    public NotificationService(AppSettings settings)
    {
        _settings = settings;
        Items.CollectionChanged += (_, _) => { Count = Items.Count; IsEmpty = Items.Count == 0; SyncMessages(); };
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

    private async Task PollAsync()
    {
        if (_listener is null) return;
        // Nothing needs them: neither the island (switched off) nor the Claude question detection.
        if (!_settings.NotificationsEnabled && !_settings.ClaudeEnabled) return;
        IReadOnlyList<UserNotification> list;
        try { list = await _listener.GetNotificationsAsync(NotificationKinds.Toast); }
        catch { return; }

        var current = new HashSet<uint>(list.Select(n => n.Id));
        // Drop entries the user dismissed in Windows.
        foreach (var gone in Items.Where(i => !current.Contains(i.Id)).ToList()) { Items.Remove(gone); Removed?.Invoke(gone); }
        if (_seen.Count > 2000) _seen.IntersectWith(current); // only ids still in the notification center matter

        foreach (var n in list.OrderBy(n => n.CreationTime))
        {
            if (!_seen.Add(n.Id)) continue;
            var item = await ToItemAsync(n);
            if (item is null) continue;
            if (_settings.MutedApps.Contains(item.App)) continue;
            if (_primed) Seen?.Invoke(item);
            if (!_settings.NotificationsEnabled) continue; // off: not shown in the island at all
            Items.Insert(0, item);
            if (_primed) Log.Info($"Notification from {item.App} ({item.AppId})");
            if (_primed && _settings.NotificationsEnabled && !_settings.DoNotDisturb) Arrived?.Invoke(item);
        }
        while (Items.Count > 30) Items.RemoveAt(Items.Count - 1);
        _primed = true;
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
        if (item is null) return;
        try { Process.Start("explorer.exe", $"shell:AppsFolder\\{item.AppId}"); } catch { }
    }

    [RelayCommand]
    public void Dismiss(NotificationItem? item)
    {
        if (item is null) return;
        Items.Remove(item);
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
