using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services;

public sealed partial class NoteItem : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime Created { get; init; } = DateTime.Now;
    [ObservableProperty] private string _text = "";
}

/// <summary>
/// Quick notes in the island: a phone number from a call, a thought for later - written in a second, deleted in one
/// click. Kept on this PC only (notes.json next to the settings), newest first.
/// </summary>
public sealed partial class NotesService : ObservableObject
{
    private static string FilePath => Path.Combine(AppSettings.Folder, "notes.json");
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _demo;

    public ObservableCollection<NoteItem> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>A new note was added (the view puts the cursor in it).</summary>
    public event Action<NoteItem>? Added;

    public NotesService()
    {
        _save.Tick += (_, _) => { _save.Stop(); Save(); };
        Items.CollectionChanged += (_, _) => IsEmpty = Items.Count == 0;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(FilePath)) ?? new();
            foreach (var s in list.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
                Hook(new NoteItem { Id = s.Id, Created = s.Created, Text = s.Text });
        }
        catch (Exception ex) { Log.Error("Notes load", ex); }
    }

    private void Hook(NoteItem n, int index = -1)
    {
        n.PropertyChanged += (_, _) => { _save.Stop(); _save.Start(); };
        if (index < 0) Items.Add(n); else Items.Insert(index, n);
    }

    private void Save()
    {
        if (_demo) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var list = Items.Where(n => !string.IsNullOrWhiteSpace(n.Text)).Select(n => new Stored(n.Id, n.Created, n.Text)).ToList();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Error("Notes save", ex); }
    }

    [RelayCommand]
    private void New()
    {
        // An empty note on top already waits: use that one.
        var note = Items.FirstOrDefault(n => string.IsNullOrWhiteSpace(n.Text)) ?? new NoteItem();
        if (!Items.Contains(note)) Hook(note, 0);
        Added?.Invoke(note);
    }

    [RelayCommand]
    private void Delete(NoteItem? note)
    {
        if (note is null || !Items.Remove(note)) return;
        Save();
    }

    /// <summary>Left empty: the note goes away by itself.</summary>
    public void DropIfEmpty(NoteItem note)
    {
        if (string.IsNullOrWhiteSpace(note.Text) && Items.Remove(note)) Save();
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo()
    {
        _demo = true;
        Items.Clear();
        Hook(new NoteItem { Text = "Rückruf Herr Wagner: 0151 2345678 – wegen Angebot Speicher und der Frage nach der Lieferzeit" });
        Hook(new NoteItem { Text = "Folie 4: Zahlen von Q3 ergänzen" });
        Hook(new NoteItem { Text = "WLAN Gäste: Sommer2026!" });
    }

    private sealed record Stored(string Id, DateTime Created, string Text);
}
