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

/// <summary>There: the file is found. Gone: its drive answers, but the file is not there. Unreachable: the drive itself
/// is away (USB stick unplugged, network share offline) - the entry stays until it comes back.</summary>
public enum ShelfPathState { There, Gone, Unreachable }

/// <summary>NotchNook-style tray: drop files onto the island, drag them out later.</summary>
public sealed partial class ShelfService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly string _folder;
    private readonly string _dataFolder;
    private string IndexPath => System.IO.Path.Combine(_dataFolder, "shelf.json");

    public ObservableCollection<ShelfItem> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private int _count;

    public event Action<int>? FilesAdded;

    /// <param name="dataFolder">Where shelf.json and copied files live (default: the app's data folder; tests pass their own).</param>
    public ShelfService(AppSettings settings, string? dataFolder = null)
    {
        _settings = settings;
        _dataFolder = dataFolder ?? AppSettings.Folder;
        _folder = System.IO.Path.Combine(_dataFolder, "shelf");
        Items.CollectionChanged += (_, _) => { IsEmpty = Items.Count == 0; Count = Items.Count; SyncWatchers(); };
    }

    // ---------- files deleted or renamed outside DynamicBay ----------

    /// <summary>One watcher per folder that holds shelf files: a file deleted there (also into the recycle bin) leaves
    /// the shelf at once, a renamed one keeps its entry - otherwise it could no longer be dragged out.</summary>
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders on drives that did not answer at the last check: no watcher (Directory.Exists would hang the UI).</summary>
    private HashSet<string> _offline = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>While the entries are checked in the background (start, panel opens): no watchers yet.</summary>
    private bool _checking;
    /// <summary>shelf.json could not be read and not be copied aside either: it is not overwritten.</summary>
    private bool _keepFile;

    private static string? DirOf(string path) => System.IO.Path.GetDirectoryName(path.TrimEnd('\\'));

    private void SyncWatchers()
    {
        if (_checking) return;
        var dirs = Items.Select(i => DirOf(i.Path))
                        .Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _watchers.Keys.Where(d => !dirs.Contains(d)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }
        foreach (var dir in dirs.Where(d => !_watchers.ContainsKey(d) && !_offline.Contains(d)))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var w = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                w.Deleted += (_, e) => OnUi(() => Gone(e.FullPath));
                w.Renamed += (_, e) => OnUi(() => Renamed(e.OldFullPath, e.FullPath));
                w.EnableRaisingEvents = true;
                _watchers[dir] = w;
            }
            catch (Exception ex) { Log.Info($"Shelf watcher for {dir}: {ex.Message}"); }
        }
    }

    private static void OnUi(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    private void Gone(string path)
    {
        var hit = Items.Where(i => string.Equals(i.Path.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count == 0 || File.Exists(path) || Directory.Exists(path)) return;
        foreach (var i in hit) Items.Remove(i);
        Save();
    }

    private void Renamed(string oldPath, string newPath)
    {
        for (int n = 0; n < Items.Count; n++)
        {
            var i = Items[n];
            if (!string.Equals(i.Path.TrimEnd('\\'), oldPath, StringComparison.OrdinalIgnoreCase)) continue;
            Items[n] = new ShelfItem { Path = newPath, IsCopy = i.IsCopy, Added = i.Added, Icon = i.Icon };
        }
        Save();
    }

    /// <summary>Safety net when the panel opens (a missed event): drops entries whose file is gone.</summary>
    public void PruneMissing() => _ = CheckAsync();

    /// <summary>
    /// Checks every entry off the UI thread (File.Exists on a sleeping network share can take many seconds): entries
    /// whose file is gone leave, entries on a drive that is away stay; found files get their thumbnail.
    /// </summary>
    private async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var paths = Items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var states = await Task.Run(() => paths.ToDictionary(p => p, StateOf, StringComparer.OrdinalIgnoreCase));
            _offline = states.Where(s => s.Value == ShelfPathState.Unreachable).Select(s => DirOf(s.Key))
                             .Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gone = Items.Where(i => states.TryGetValue(i.Path, out var st) && st == ShelfPathState.Gone).ToList();
            foreach (var i in gone) Items.Remove(i);
            if (gone.Count > 0) Save();
            // Thumbnails come from the shell (STA COM): fill them in after startup, one per idle slot.
            foreach (var i in Items.Where(i => i.Icon is null && states.TryGetValue(i.Path, out var st) && st == ShelfPathState.There).ToList())
            {
                var item = i;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    () => item.Icon = ShellThumbnail.Get(item.Path, 96));
            }
        }
        catch (Exception ex) { Log.Error("Shelf check", ex); }
        finally
        {
            _checking = false;
            SyncWatchers();
        }
    }

    public static ShelfPathState StateOf(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path)) return ShelfPathState.There;
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root) && Directory.Exists(root) ? ShelfPathState.Gone : ShelfPathState.Unreachable;
        }
        catch { return ShelfPathState.Unreachable; }
    }

    public void Load()
    {
        List<ShelfItem> list;
        try
        {
            if (!File.Exists(IndexPath)) return;
            list = JsonSerializer.Deserialize<List<ShelfItem>>(File.ReadAllText(IndexPath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Shelf load", ex);
            // Keep a copy of the broken file before the next save replaces it.
            _keepFile = !AtomicFile.SetAside(IndexPath);
            return;
        }
        // Shown right away; which files are still there is checked in the background.
        _checking = true;
        foreach (var i in list.Where(i => !string.IsNullOrWhiteSpace(i.Path))) Items.Add(i);
        _checking = false;
        _ = CheckAsync();
    }

    private void Save()
    {
        if (_keepFile) return;
        try { AtomicFile.WriteAllText(IndexPath, JsonSerializer.Serialize(Items.ToList()), backup: false); }
        catch (Exception ex) { Log.Error("Shelf save", ex); }
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
