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

/// <summary>
/// Microsoft To Do: open tasks of all lists, overdue and today's first; tick them off in the island.
/// Refreshed every five minutes and whenever the panel opens.
/// </summary>
public sealed partial class TodoService : ObservableObject
{
    private readonly MicrosoftAccount _account;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(5) };
    private DateTime _lastRefresh;
    private bool _loading;

    public ObservableCollection<TodoItem> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _openCount;

    public TodoService(MicrosoftAccount account)
    {
        _account = account;
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
            var lists = await _account.GetAsync("me/todo/lists?$select=id,displayName,wellknownListName");
            if (lists is null) return;
            var pages = await Task.WhenAll((lists["value"]?.AsArray() ?? new JsonArray()).Select(async l =>
            {
                string id = l!["id"]!.ToString();
                var tasks = await _account.GetAsync($"me/todo/lists/{Uri.EscapeDataString(id)}/tasks?$filter=status ne 'completed'&$top=50");
                return (id, tasks);
            }));
            var all = new List<TodoItem>();
            foreach (var (listId, tasks) in pages)
                foreach (var t in tasks?["value"]?.AsArray() ?? new JsonArray())
                {
                    if (t is null) continue;
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
