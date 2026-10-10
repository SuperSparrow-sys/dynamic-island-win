using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services.M365;

public sealed partial class TodoItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string ListId { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime? Due { get; init; }
    public bool Important { get; init; }
    [ObservableProperty] private bool _done;
    public bool Overdue => Due is { } d && d.Date < DateTime.Today;
    public string DueText => Due is not { } d ? ""
        : d.Date == DateTime.Today ? (Loc.German ? "Heute" : "Today")
        : d.Date == DateTime.Today.AddDays(1) ? (Loc.German ? "Morgen" : "Tomorrow")
        : d.ToString(Loc.German ? "ddd, d. MMM" : "ddd, MMM d");
}

/// <summary>A Microsoft To Do list, for choosing the one the widget shows.</summary>
public sealed record TodoList(string Id, string Name);

/// <summary>
/// Microsoft To Do: open tasks of the chosen list (or all lists), overdue and today's first; tick them off in the island.
/// Refreshed every five minutes and whenever the panel opens. A list that cannot be read (Graph refuses filters on
/// some of them) is read unfiltered or skipped - it no longer breaks the whole widget.
/// </summary>
public sealed partial class TodoService : ObservableObject
{
    private readonly MicrosoftAccount _account;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(5) };
    private DateTime _lastRefresh;
    private bool _loading;

    public ObservableCollection<TodoItem> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _openCount;

    /// <summary>All lists of the account (read with every refresh).</summary>
    public ObservableCollection<TodoList> Lists { get; } = new();

    public TodoService(MicrosoftAccount account, AppSettings settings)
    {
        _account = account;
        _settings = settings;
        _settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.TodoListId)) _ = RefreshAsync(); };
        _refresh.Tick += async (_, _) => await RefreshAsync();
        _account.Connected += () => _ = RefreshAsync();
        Items.CollectionChanged += (_, _) => { IsEmpty = Items.Count == 0; OpenCount = Items.Count(i => !i.Done); };
    }

    public void Start()
    {
        _refresh.Start();
        _ = RefreshAsync();
    }

    /// <summary>The panel opened: refresh if the last load is older than a minute.</summary>
    public void PanelOpened()
    {
        if (DateTime.Now - _lastRefresh > TimeSpan.FromMinutes(1)) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!_account.IsConnected || _loading) return;
        _loading = true;
        try
        {
            var lists = await _account.GetAsync("me/todo/lists");
            if (lists is null) return;
            var found = (lists["value"]?.AsArray() ?? new JsonArray())
                .Select(l => new TodoList(l!["id"]!.ToString(), ListName(l)))
                .ToList();
            if (!found.SequenceEqual(Lists)) { Lists.Clear(); foreach (var l in found) Lists.Add(l); }
            var chosen = found.Where(l => _settings.TodoListId.Length == 0 || l.Id == _settings.TodoListId).ToList();
            if (chosen.Count == 0) chosen = found; // the chosen list was deleted: show all
            var pages = await Task.WhenAll(chosen.Select(async l => (l.Id, tasks: await ReadTasksAsync(l))));
            var all = new List<TodoItem>();
            foreach (var (listId, tasks) in pages)
                foreach (var t in tasks)
                {
                    if (t is null || t["status"]?.ToString() == "completed") continue;
                    DateTime? due = DateTime.TryParse(t["dueDateTime"]?["dateTime"]?.ToString(), out var d) ? d.Date : null;
                    all.Add(new TodoItem
                    {
                        Id = t["id"]!.ToString(), ListId = listId, Title = t["title"]?.ToString() ?? "",
                        Due = due, Important = t["importance"]?.ToString() == "high",
                    });
                }
            // Overdue and today first, then the rest by due date, then important ones without a date.
            var sorted = all.OrderBy(i => i.Due is null ? 1 : 0).ThenBy(i => i.Due).ThenByDescending(i => i.Important).Take(12).ToList();
            if (!sorted.Select(i => i.Id).SequenceEqual(Items.Select(i => i.Id)))
            {
                Items.Clear();
                foreach (var i in sorted) Items.Add(i);
            }
            Status = "";
            _lastRefresh = DateTime.Now;
        }
        catch (Exception ex)
        {
            Log.Error("To Do", ex);
            Status = (Loc.German ? "To Do: " : "To Do: ") + ex.Message;
        }
        finally { _loading = false; }
    }

    /// <summary>Open tasks of one list; without the filter if Graph refuses it for this list; nothing if it cannot be read.</summary>
    private async Task<List<JsonNode?>> ReadTasksAsync(TodoList list)
    {
        string path = $"me/todo/lists/{Uri.EscapeDataString(list.Id)}/tasks";
        try
        {
            var res = await _account.GetAsync(path + "?$filter=status ne 'completed'&$top=50");
            return res?["value"]?.AsArray().ToList() ?? new();
        }
        catch (GraphException first)
        {
            try
            {
                var res = await _account.GetAsync(path + "?$top=100");
                return res?["value"]?.AsArray().ToList() ?? new();
            }
            catch (GraphException ex)
            {
                Log.Info($"To Do list '{list.Name}' skipped: {first.Message} / {ex.Message}");
                return new();
            }
        }
    }

    /// <summary>"Aufgaben" for the default list, "Gekennzeichnete E-Mails" for flagged mails, else the list's own name.</summary>
    private static string ListName(JsonNode l) => l["wellknownListName"]?.ToString() switch
    {
        "defaultList" => Loc.German ? "Aufgaben" : "Tasks",
        "flaggedEmails" => Loc.German ? "Gekennzeichnete E-Mails" : "Flagged emails",
        _ => l["displayName"]?.ToString() ?? "",
    };

    /// <summary>Ticks a task off (it fades out of the list a moment later).</summary>
    [RelayCommand]
    private async Task Complete(TodoItem? item)
    {
        if (item is null || item.Done) return;
        item.Done = true;
        try
        {
            await _account.SendAsync(HttpMethod.Patch, $"me/todo/lists/{Uri.EscapeDataString(item.ListId)}/tasks/{Uri.EscapeDataString(item.Id)}",
                new { status = "completed" });
            await Task.Delay(900);
            Items.Remove(item);
        }
        catch (Exception ex)
        {
            item.Done = false;
            Log.Error("To Do complete", ex);
            Status = Loc.German ? "Konnte die Aufgabe nicht abhaken" : "Could not complete the task";
        }
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo()
    {
        Items.Clear();
        Items.Add(new TodoItem { Id = "1", Title = "Angebot an Müller GmbH schicken", Due = DateTime.Today.AddDays(-1) });
        Items.Add(new TodoItem { Id = "2", Title = "Folien für das Team-Meeting", Due = DateTime.Today, Important = true });
        Items.Add(new TodoItem { Id = "3", Title = "Reisekosten abrechnen", Due = DateTime.Today.AddDays(1) });
        Items.Add(new TodoItem { Id = "4", Title = "Urlaub eintragen" });
    }

    [RelayCommand]
    private void OpenApp()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-todo:") { UseShellExecute = true }); }
        catch { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://to-do.office.com/tasks/") { UseShellExecute = true }); } catch { } }
    }
}
