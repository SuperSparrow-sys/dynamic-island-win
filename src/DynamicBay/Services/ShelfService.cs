using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services;

public sealed partial class ShelfItem : ObservableObject
{
    public string Path { get; set; } = "";
    public bool IsCopy { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
    [System.Text.Json.Serialization.JsonIgnore] public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private ImageSource? _icon;
    [System.Text.Json.Serialization.JsonIgnore] public bool Exists => File.Exists(Path) || Directory.Exists(Path);
}

/// <summary>NotchNook-style tray: drop files onto the island, drag them out later.</summary>
public sealed partial class ShelfService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly string _folder = System.IO.Path.Combine(AppSettings.Folder, "shelf");
    private string IndexPath => System.IO.Path.Combine(AppSettings.Folder, "shelf.json");

    public ObservableCollection<ShelfItem> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private int _count;

    public event Action<int>? FilesAdded;

    public ShelfService(AppSettings settings)
    {
        _settings = settings;
        Items.CollectionChanged += (_, _) => { IsEmpty = Items.Count == 0; Count = Items.Count; };
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return;
            foreach (var i in JsonSerializer.Deserialize<List<ShelfItem>>(File.ReadAllText(IndexPath)) ?? new())
            {
                if (!i.Exists) continue;
                Items.Add(i);
                // Thumbnails come from the shell (STA COM): fill them in after startup, one per idle slot.
                var item = i;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    () => item.Icon = ShellThumbnail.Get(item.Path, 96));
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(Items.ToList()));
        }
        catch { }
    }

    public void Add(IEnumerable<string> paths) => Add(paths, announce: true);

    /// <summary>announce = false: the caller shows its own message (e.g. files received via Taildrop).</summary>
    public void Add(IEnumerable<string> paths, bool announce)
    {
        int added = 0;
        foreach (var p in paths)
        {
            if (Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            string target = p;
            bool copy = false;
            if (_settings.ShelfCopyFiles && File.Exists(p))
            {
                try
                {
                    Directory.CreateDirectory(_folder);
                    target = UniquePath(System.IO.Path.Combine(_folder, System.IO.Path.GetFileName(p)));
                    File.Copy(p, target);
                    copy = true;
                }
                catch { target = p; }
            }
            Items.Insert(0, new ShelfItem { Path = target, IsCopy = copy, Icon = ShellThumbnail.Get(target, 96) });
            added++;
        }
        if (added > 0)
        {
            Save();
            if (announce) FilesAdded?.Invoke(added);
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = System.IO.Path.GetDirectoryName(path)!, name = System.IO.Path.GetFileNameWithoutExtension(path), ext = System.IO.Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public DataObject CreateDragData(IEnumerable<ShelfItem> items)
    {
        var list = new StringCollection();
        foreach (var i in items) list.Add(i.Path);
        var data = new DataObject();
        data.SetFileDropList(list);
        return data;
    }

    public void AfterDragOut(IEnumerable<ShelfItem> items, DragDropEffects result)
    {
        if (result == DragDropEffects.None) return;
        if (_settings.ShelfRemoveAfterDrag || result == DragDropEffects.Move)
            foreach (var i in items.ToList()) Remove(i);
    }

    [RelayCommand]
    public void Remove(ShelfItem? item)
    {
        if (item is null) return;
        Items.Remove(item);
        if (item.IsCopy) try { File.Delete(item.Path); } catch { }
        Save();
    }

    [RelayCommand]
    public void Clear()
    {
        foreach (var i in Items.ToList()) Remove(i);
    }

    [RelayCommand]
    public void Open(ShelfItem? item)
    {
        if (item is null) return;
        try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    public void ShowInFolder(ShelfItem? item)
    {
        if (item is null) return;
        try { Process.Start("explorer.exe", $"/select,\"{item.Path}\""); } catch { }
    }

    /// <summary>For design snapshots only.</summary>
    public void AddDemo(ShelfItem item) => Items.Add(item);
}
