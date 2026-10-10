using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services.M365;

public sealed class ContactItem
{
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpper(p[0])));
    public bool HasEmail => Email.Length > 0;
    public bool HasPhone => Phone.Length > 0;
    public string Detail => Phone.Length > 0 ? Phone : Email;
}

/// <summary>
/// The people you work with most (Microsoft's ranking from mail, chats and meetings), then your Outlook contacts.
/// Each one can be reached with a Teams chat, a Teams call or a phone call - "tel:" goes to the phone linked with
/// Smartphone-Link (Phone Link) when that is the default app for phone numbers.
/// </summary>
public sealed partial class ContactsService : ObservableObject
{
    private readonly MicrosoftAccount _account;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(30) };
    private bool _loading;

    public ObservableCollection<ContactItem> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty = true;

    public ContactsService(MicrosoftAccount account)
    {
        _account = account;
        _refresh.Tick += async (_, _) => await RefreshAsync();
        _account.Connected += () => _ = RefreshAsync();
        Items.CollectionChanged += (_, _) => IsEmpty = Items.Count == 0;
    }

    public void Start()
    {
        _refresh.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!_account.IsConnected || _loading) return;
        _loading = true;
        try
        {
            var list = new List<ContactItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(ContactItem c)
            {
                string key = c.Email.Length > 0 ? c.Email : c.Name;
                if (c.Name.Length == 0 || !seen.Add(key)) return;
                list.Add(c);
            }
            // Most relevant people first (only real persons, not groups or rooms).
            try
            {
                var people = await _account.GetAsync("me/people?$top=15&$select=displayName,scoredEmailAddresses,phones,personType");
                foreach (var p in people?["value"]?.AsArray() ?? new JsonArray())
                {
                    if (p?["personType"]?["class"]?.ToString() is { } cls && cls != "Person") continue;
                    Add(new ContactItem
                    {
                        Name = p!["displayName"]?.ToString() ?? "",
                        Email = p["scoredEmailAddresses"]?.AsArray().FirstOrDefault()?["address"]?.ToString() ?? "",
                        Phone = BestPhone(p["phones"]?.AsArray(), "type", "number"),
                    });
                }
            }
            catch (Exception ex) { Log.Info($"People: {ex.Message}"); }
            // Saved contacts (personal accounts have no people ranking; work accounts get the rest).
            try
            {
                var contacts = await _account.GetAsync("me/contacts?$top=60&$orderby=displayName&$select=displayName,emailAddresses,mobilePhone,businessPhones,homePhones");
                foreach (var c in contacts?["value"]?.AsArray() ?? new JsonArray())
                {
                    if (c is null) continue;
                    string phone = c["mobilePhone"]?.ToString() ?? "";
                    if (phone.Length == 0) phone = c["businessPhones"]?.AsArray().FirstOrDefault()?.ToString() ?? c["homePhones"]?.AsArray().FirstOrDefault()?.ToString() ?? "";
                    Add(new ContactItem
                    {
                        Name = c["displayName"]?.ToString() ?? "",
                        Email = c["emailAddresses"]?.AsArray().FirstOrDefault()?["address"]?.ToString() ?? "",
                        Phone = phone,
                    });
                }
            }
            catch (Exception ex) { Log.Info($"Contacts: {ex.Message}"); }

            var top = list.Take(30).ToList();
            if (!top.Select(c => c.Name + c.Email + c.Phone).SequenceEqual(Items.Select(c => c.Name + c.Email + c.Phone)))
            {
                Items.Clear();
                foreach (var c in top) Items.Add(c);
            }
        }
        finally { _loading = false; }
    }

    /// <summary>Mobile first, then business, then any.</summary>
    private static string BestPhone(JsonArray? phones, string typeKey, string numberKey)
    {
        if (phones is null) return "";
        foreach (var type in new[] { "mobile", "business" })
            if (phones.FirstOrDefault(p => p?[typeKey]?.ToString() == type)?[numberKey]?.ToString() is { Length: > 0 } n) return n;
        return phones.FirstOrDefault()?[numberKey]?.ToString() ?? "";
    }

    // ---------- reaching someone ----------

    [RelayCommand] private void Chat(ContactItem? c) { if (c?.HasEmail == true) OpenTeams("chat", c.Email); }
    [RelayCommand] private void TeamsCall(ContactItem? c) { if (c?.HasEmail == true) OpenTeams("call", c.Email); }

    /// <summary>Phone call through the default app for phone numbers - Smartphone-Link dials on the linked phone.</summary>
    [RelayCommand]
    private void Call(ContactItem? c)
    {
        if (c?.HasPhone != true) return;
        string number = new string(c.Phone.Where(ch => char.IsDigit(ch) || ch == '+').ToArray());
        try { Process.Start(new ProcessStartInfo("tel:" + number) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("tel", ex); }
    }

    /// <summary>Teams deep link (chat or call with one person), straight into the Teams app; the browser if Teams is missing.</summary>
    private static void OpenTeams(string what, string email)
    {
        string path = $"/l/{what}/0/0?users={Uri.EscapeDataString(email)}";
        try { Process.Start(new ProcessStartInfo("msteams:" + path) { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo("https://teams.microsoft.com" + path) { UseShellExecute = true }); } catch { } }
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo()
    {
        Items.Clear();
        Items.Add(new ContactItem { Name = "Anna Schmidt", Email = "anna@example.com", Phone = "+49 151 2345678" });
        Items.Add(new ContactItem { Name = "Lukas Weber", Email = "lukas@example.com" });
        Items.Add(new ContactItem { Name = "Mia Fischer", Email = "mia@example.com", Phone = "+49 160 9876543" });
        Items.Add(new ContactItem { Name = "Jonas Becker", Email = "jonas@example.com" });
    }
}
