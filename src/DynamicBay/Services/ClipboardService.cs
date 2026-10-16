using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services;

public enum ClipKind { Image, Text }

public sealed partial class ClipItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public string? ImagePath { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public bool IsScreenshot { get; set; }
    /// <summary>The screenshot file Windows saved in the Screenshots folder (deleted together with the item).</summary>
    public string? SavedPath { get; set; }
    public string? SourceApp { get; set; }
    /// <summary>Another PC it came from (CursorHop), shown next to the kind ("Text · PC-Jonathan").</summary>
    public string? SourceDevice
    {
        get => _sourceDevice;
        set { if (SetProperty(ref _sourceDevice, value)) OnPropertyChanged(nameof(Label)); }
    }
    private string? _sourceDevice;
    public DateTime Created { get; set; } = DateTime.Now;
    [ObservableProperty] private bool _pinned;

    [System.Text.Json.Serialization.JsonIgnore] public ImageSource? Thumbnail { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsImage => Kind == ClipKind.Image;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsText => Kind == ClipKind.Text;
    [System.Text.Json.Serialization.JsonIgnore] public string Preview =>
        Text is null ? "" : (Text.Length > 240 ? Text[..240] + "…" : Text).Replace("\r", "").Trim();
    [System.Text.Json.Serialization.JsonIgnore] public string Label =>
        (Kind == ClipKind.Image ? (IsScreenshot ? Loc.T("Clip.Screenshot") : Loc.T("Clip.Image")) : Loc.T("Clip.Text"))
        + (string.IsNullOrEmpty(SourceDevice) ? "" : " · " + SourceDevice);
    [System.Text.Json.Serialization.JsonIgnore] public string Age
    {
        get
        {
            var d = DateTime.Now - Created;
            if (d.TotalMinutes < 1) return Loc.German ? "jetzt" : "now";
            if (d.TotalHours < 1) return Loc.German ? $"vor {(int)d.TotalMinutes} Min." : $"{(int)d.TotalMinutes} min ago";
            if (d.TotalDays < 1) return Created.ToString("HH:mm");
            return Created.ToString("dd.MM.");
        }
    }
}

/// <summary>
/// Clipboard history (images + text). Snipping Tool screenshots (Win+Shift+S) arrive here as images;
/// Win+PrtScn files from the Screenshots folder are merged in so they don't show up twice.
/// </summary>
public sealed partial class ClipboardService : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly string _folder = Path.Combine(AppSettings.Folder, "clipboard");
    private string IndexPath => Path.Combine(_folder, "index.json");
    private readonly uint _fmtExclude = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint _fmtNoHistory = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private readonly uint _fmtIgnore = Native.RegisterClipboardFormat("Clipboard Viewer Ignore");
    private bool _suppressNext;
    private string? _lastText;
    private DispatcherTimer? _debounce;

    public ObservableCollection<ClipItem> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>A new screenshot was captured (Snipping Tool or PrtScn).</summary>
    public event Action<ClipItem>? ScreenshotCaptured;
    /// <summary>
    /// Something was copied on this PC and joined the history (not entries from paired devices, not the screenshot
    /// folder). Raised right away; the source device (CursorHop) may be set on the item a moment later.
    /// </summary>
    public event Action<ClipItem>? CopiedHere;

    public ClipboardService(AppSettings settings)
    {
        _settings = settings;
        Items.CollectionChanged += (_, _) => IsEmpty = Items.Count == 0;
        _settings.PropertyChanged += (_, e) =>
        {
            // A smaller history applies right away, not only with the next new entry.
            if (e.PropertyName == nameof(AppSettings.ClipboardMax)) { Trim(); SaveIndexSoon(); }
            // "Keep after restart" off: also forget what was saved before.
            if (e.PropertyName == nameof(AppSettings.ClipboardPersist) && !_settings.ClipboardPersist)
                try { File.Delete(IndexPath); } catch { }
        };
    }

    public void Start(MessageWindow msg)
    {
        Directory.CreateDirectory(_folder);
        if (_settings.ClipboardPersist) _ = LoadIndexAsync();
        else { _loaded = true; DeleteOrphans(); } // nothing kept: images from the last run (or a crash) go
        Native.AddClipboardFormatListener(msg.Handle);
        msg.Message += (m, _, _) =>
        {
            if (m != Native.WM_CLIPBOARDUPDATE) return false;
            // Snipping Tool writes several formats in a burst; read once it settles.
            _debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Normal, (_, _) =>
            {
                _debounce!.Stop();
                OnClipboardChanged();
            }, _ui);
            _debounce.Stop();
            _debounce.Start();
            return true;
        };
        _ = Task.Run(() => ScreenshotFolders().ToList()).ContinueWith(t => _ui.BeginInvoke(() => WatchScreenshotFolder(t.Result)), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>CLIPBRD_E_CANT_OPEN: another app holds the clipboard open right now.</summary>
    private const int ClipboardCantOpen = unchecked((int)0x800401D0);

    private void OnClipboardChanged() => OnClipboardChanged(0);

    /// <summary>
    /// Reads the clipboard and saves images on their own STA thread: the app that copied renders the data only when
    /// asked, and a 4K screenshot takes about 450 ms to encode. On the UI thread that stalled the island for up to 1.9 s.
    /// </summary>
    private readonly StaWorker _reader = new("Clipboard");

    /// <summary>What the clipboard held: an image or a text (the other is null), and the app that put it there.</summary>
    private sealed record ClipRead(BitmapSource? Image, string? Text, string? Owner);

    private async void OnClipboardChanged(int attempt)
    {
        if (!_settings.ClipboardEnabled) return;
        if (attempt == 0 && _suppressNext) { _suppressNext = false; return; }
        bool wantText = _settings.ClipboardText;
        ClipRead? read;
        try { read = await _reader.Run(() => ReadClipboard(wantText)); }
        catch (System.Runtime.InteropServices.ExternalException ex) when (ex.HResult == ClipboardCantOpen && attempt < 2)
        {
            // Still held open by the app that wrote it: read again in a moment instead of losing the copy.
            _ = RetryAsync(attempt + 1);
            return;
        }
        catch { return; } // clipboard is often locked by another process; skip this change
        if (read is null || !_settings.ClipboardEnabled) return;
        try
        {
            string? owner = read.Owner;
            if (read.Image is { } img)
            {
                bool screenshot = owner is not null &&
                                  (owner.Contains("ScreenClipping", StringComparison.OrdinalIgnoreCase) ||
                                   owner.Contains("SnippingTool", StringComparison.OrdinalIgnoreCase) ||
                                   owner.Contains("ShellExperienceHost", StringComparison.OrdinalIgnoreCase));
                // Win+PrtScn writes the file and the clipboard; the folder watcher may have added it already.
                if (IsRecentImage(img)) return;
                var item = NewImageItem(img, screenshot, owner, null);
                item.Thumbnail = await _reader.Run(() => { ImageTools.SavePng(img, item.ImagePath!); return MakeThumb(img); });
                if (IsRecentImage(img)) { try { File.Delete(item.ImagePath!); } catch { } return; } // the folder watcher was faster
                AddImage(item);
                CopiedHere?.Invoke(item);
                return;
            }
            if (read.Text is { } text)
            {
                if (text == _lastText) return;
                _lastText = text;
                var existing = Items.FirstOrDefault(i => i.IsText && i.Text == text);
                if (existing is not null) Items.Remove(existing);
                var item = new ClipItem { Kind = ClipKind.Text, Text = text, SourceApp = owner };
                Insert(item);
                CopiedHere?.Invoke(item);
            }
        }
        catch (Exception ex) { Log.Error("Clipboard", ex); }
    }

    private bool IsRecentImage(BitmapSource img) =>
        Items.Any(i => i.IsImage && (DateTime.Now - i.Created).TotalSeconds < 4 && i.PixelWidth == img.PixelWidth && i.PixelHeight == img.PixelHeight);

    /// <summary>On the clipboard thread: null for nothing to keep (excluded, empty, too long).</summary>
    private ClipRead? ReadClipboard(bool wantText)
    {
        if (Native.IsClipboardFormatAvailable(_fmtExclude) || Native.IsClipboardFormatAvailable(_fmtIgnore)) return null;
        if (Native.IsClipboardFormatAvailable(_fmtNoHistory))
        {
            // Password managers set CanIncludeInClipboardHistory = 0.
            if (Clipboard.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4];
                ms.Read(b, 0, 4);
                if (BitConverter.ToInt32(b, 0) == 0) return null;
            }
        }
        string? owner = Native.ProcessName(Native.GetClipboardOwner());
        if (Clipboard.ContainsImage()) return ReadImage() is { } img ? new ClipRead(img, null, owner) : null;
        if (!wantText || !Clipboard.ContainsText()) return null;
        var text = Clipboard.GetText();
        return string.IsNullOrWhiteSpace(text) || text.Length > 200_000 ? null : new ClipRead(null, text, owner);
    }

    private async Task RetryAsync(int attempt)
    {
        await Task.Delay(100); // back on the UI thread afterwards
        OnClipboardChanged(attempt);
    }

    private static BitmapSource? ReadImage()
    {
        // Prefer the PNG stream (keeps alpha, avoids WPF's DIB decoding quirks).
        try
        {
            if (Clipboard.GetData("PNG") is MemoryStream png)
            {
                var dec = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = dec.Frames[0];
                frame.Freeze();
                return frame;
            }
        }
        catch { }
        try
        {
            var img = Clipboard.GetImage();
            if (img is null) return null;
            // WPF returns Bgra32 with a garbage alpha channel for DIBs; drop it.
            var opaque = new FormatConvertedBitmap(img, PixelFormats.Bgr32, null, 0);
            opaque.Freeze();
            return opaque;
        }
        catch { return null; }
    }

    private ClipItem NewImageItem(BitmapSource img, bool screenshot, string? source, string? existingPath)
    {
        var item = new ClipItem
        {
            Kind = ClipKind.Image,
            IsScreenshot = screenshot,
            SourceApp = source,
            PixelWidth = img.PixelWidth,
            PixelHeight = img.PixelHeight,
        };
        item.ImagePath = existingPath ?? Path.Combine(_folder, item.Id + ".png");
        item.SavedPath = existingPath;
        return item;
    }

    /// <summary>A saved image with its thumbnail joins the history.</summary>
    private void AddImage(ClipItem item)
    {
        Insert(item);
        if (item.IsScreenshot) ScreenshotCaptured?.Invoke(item);
    }

    /// <summary>
    /// An entry from a paired device (the link): joins the history with the device as its source, without touching the
    /// Windows clipboard and without counting as copied here. Content that is already there (CursorHop brought it a
    /// moment ago) only gets the device as source. True: a new entry.
    /// </summary>
    public bool AddFromDevice(string device, string? text, byte[]? png)
    {
        if (!_settings.ClipboardEnabled) return false;
        if (text is not null)
        {
            if (!_settings.ClipboardText || string.IsNullOrWhiteSpace(text)) return false;
            var existing = Items.FirstOrDefault(i => i.IsText && i.Text == text);
            if (existing is not null)
            {
                existing.SourceDevice ??= device;
                SaveIndexSoon();
                return false;
            }
            Insert(new ClipItem { Kind = ClipKind.Text, Text = text, SourceDevice = device });
            return true;
        }
        if (png is null) return false;
        BitmapSource img;
        try
        {
            var frame = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            img = frame;
        }
        catch { return false; }
        var dup = Items.FirstOrDefault(i => i.IsImage && (DateTime.Now - i.Created).TotalSeconds < 10 &&
                                            i.PixelWidth == img.PixelWidth && i.PixelHeight == img.PixelHeight);
        if (dup is not null)
        {
            dup.SourceDevice ??= device;
            SaveIndexSoon();
            return false;
        }
        var item = new ClipItem { Kind = ClipKind.Image, SourceDevice = device, PixelWidth = img.PixelWidth, PixelHeight = img.PixelHeight };
        item.ImagePath = Path.Combine(_folder, item.Id + ".png");
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(item.ImagePath, png);
        }
        catch (Exception ex) { Log.Error("Clipboard from device", ex); return false; }
        item.Thumbnail = MakeThumb(img);
        Insert(item);
        return true;
    }

    /// <summary>The picture of an entry as PNG bytes (null when the file is gone).</summary>
    public static byte[]? PngOf(ClipItem item)
    {
        try { return item.ImagePath is { } p && File.Exists(p) ? File.ReadAllBytes(p) : null; }
        catch { return null; }
    }

    private static ImageSource MakeThumb(BitmapSource img)
    {
        double scale = Math.Min(1, 320.0 / Math.Max(img.PixelWidth, img.PixelHeight));
        var t = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        var wb = new WriteableBitmap(t);
        wb.Freeze();
        return wb;
    }

    private string? _remoteDevice;
    private DateTime _remoteUntil;

    /// <summary>
    /// CursorHop put clipboard content from another PC here. Its log line and our clipboard event come within a
    /// second of each other, in either order: tag the entry that just arrived, or the next one within 3 s.
    /// </summary>
    public void MarkFromDevice(string device)
    {
        var newest = Items.FirstOrDefault(); // new entries always go to the top
        if (newest is not null && newest.SourceDevice is null && (DateTime.Now - newest.Created).TotalSeconds < 3)
        {
            newest.SourceDevice = device;
            SaveIndexSoon();
            return;
        }
        _remoteDevice = device;
        _remoteUntil = DateTime.UtcNow.AddSeconds(3);
    }

    private void Insert(ClipItem item)
    {
        if (_remoteDevice is not null && DateTime.UtcNow < _remoteUntil && item.SourceDevice is null) item.SourceDevice = _remoteDevice;
        _remoteDevice = null;
        int firstUnpinned = 0;
        Items.Insert(firstUnpinned, item);
        Trim();
        SaveIndexSoon();
    }

    private void Trim()
    {
        while (Items.Count(i => !i.Pinned) > Math.Max(5, _settings.ClipboardMax))
        {
            var victim = Items.Last(i => !i.Pinned);
            Items.Remove(victim);
            DeleteFile(victim);
        }
    }

    private void DeleteFile(ClipItem i)
    {
        if (i.ImagePath is not null && i.ImagePath.StartsWith(_folder, StringComparison.OrdinalIgnoreCase))
            try { File.Delete(i.ImagePath); } catch { }
    }

    private readonly List<FileSystemWatcher> _watchers = new();

    /// <summary>
    /// Screenshot folders: the Windows "Screenshots" known folder (often redirected into OneDrive),
    /// plus OneDrive's own "save screenshots to OneDrive" folders.
    /// </summary>
    public static IEnumerable<string> ScreenshotFolders()
    {
        var dirs = new List<string>();
        if (KnownFolder(new Guid("b7bede81-df94-4682-a7d8-57a52620b86f")) is { } known) dirs.Add(known);
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"));
        // Every OneDrive account (personal + work) can have its own screenshot folder.
        foreach (var root in CloudTargets.Detect().Where(t => t.Kind == "onedrive").Select(t => t.Root))
        {
            foreach (var pics in new[] { "Pictures", "Bilder" })
            {
                dirs.Add(Path.Combine(root, pics, "Screenshots"));
                dirs.Add(Path.Combine(root, pics, "Bildschirmfotos"));
            }
        }
        return dirs.Where(Directory.Exists).Select(d => Path.GetFullPath(d).TrimEnd('\\'))
                   .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
            var s = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
            return s;
        }
        catch { return null; }
    }

    private void WatchScreenshotFolder(IEnumerable<string> dirs)
    {
        foreach (var dir in dirs)
        {
            try
            {
                var w = new FileSystemWatcher(dir) { EnableRaisingEvents = true, IncludeSubdirectories = false };
                w.Filters.Add("*.png");
                w.Filters.Add("*.jpg");
                w.Created += (_, e) => _ui.BeginInvoke(async () =>
                {
                    await Task.Delay(700); // let the writer (or OneDrive sync) finish
                    OnScreenshotFile(e.FullPath);
                });
                _watchers.Add(w);
                Log.Info($"Watching screenshots in {dir}");
            }
            catch (Exception ex) { Log.Error("ScreenshotWatch", ex); }
        }
    }

    private void OnScreenshotFile(string path)
    {
        if (!_settings.ClipboardEnabled) return;
        var img = ImageTools.Load(path);
        if (img is null) return;
        // Snipping Tool auto-saves into this folder too: attach the file to the clipboard item instead of duplicating.
        var dup = Items.FirstOrDefault(i => i.IsImage && (DateTime.Now - i.Created).TotalSeconds < 6 &&
                                            i.PixelWidth == img.PixelWidth && i.PixelHeight == img.PixelHeight);
        if (dup is not null)
        {
            dup.SavedPath ??= path;
            SaveIndexSoon();
            return;
        }
        var item = NewImageItem(img, true, "Screenshots", path);
        item.Thumbnail = MakeThumb(img);
        AddImage(item);
    }

    // ---- actions ----

    [RelayCommand]
    public void Copy(ClipItem? item)
    {
        if (item is null) return;
        try
        {
            // Only skip our own change when there is one (an image that no longer loads sets nothing).
            if (item.IsText) { _suppressNext = true; Clipboard.SetText(item.Text ?? ""); _lastText = item.Text; }
            else if (item.ImagePath is not null && ImageTools.Load(item.ImagePath) is { } img)
            {
                var data = new DataObject();
                data.SetImage(img);
                data.SetFileDropList(new StringCollection { item.ImagePath });
                _suppressNext = true;
                Clipboard.SetDataObject(data, true);
            }
            // Move to top like a recently used item.
            Items.Remove(item);
            item.Created = DateTime.Now;
            Items.Insert(0, item);
            SaveIndexSoon();
        }
        catch { _suppressNext = false; }
    }

    [RelayCommand]
    public void Delete(ClipItem? item)
    {
        if (item is null) return;
        Items.Remove(item);
        DeleteFile(item);
        DeleteSavedScreenshot(item);
        SaveIndexSoon();
    }

    /// <summary>
    /// Deleting a screenshot in the island also removes the file Windows saved for it - without asking, into the
    /// recycle bin (so a slip can still be undone). Only files inside a screenshot folder are touched.
    /// </summary>
    private static void DeleteSavedScreenshot(ClipItem item)
    {
        var path = item.SavedPath;
        if (path is null || !File.Exists(path)) return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd((char)92);
        if (dir is null || !ScreenshotFolders().Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            Log.Info($"Deleted screenshot file {Path.GetFileName(path)}");
        }
        catch (Exception ex) { Log.Error("ScreenshotDelete", ex); }
    }

    [RelayCommand]
    public void TogglePin(ClipItem? item)
    {
        if (item is null) return;
        item.Pinned = !item.Pinned;
        SaveIndexSoon();
    }

    [RelayCommand]
    public void SaveAs(ClipItem? item)
    {
        if (item is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = item.IsImage ? $"Screenshot {item.Created:yyyy-MM-dd HHmmss}.png" : "Text.txt",
            Filter = item.IsImage ? "PNG|*.png" : "Text|*.txt",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (item.IsImage && item.ImagePath is not null) File.Copy(item.ImagePath, dlg.FileName, true);
            else File.WriteAllText(dlg.FileName, item.Text ?? "");
        }
        catch { }
    }

    [RelayCommand]
    public void Clear()
    {
        foreach (var i in Items.Where(i => !i.Pinned).ToList())
        {
            Items.Remove(i);
            DeleteFile(i);
        }
        SaveIndexSoon();
    }

    /// <summary>Data for dragging an item out into another app.</summary>
    public static DataObject CreateDragData(ClipItem item)
    {
        var data = new DataObject();
        if (item.IsText) data.SetText(item.Text ?? "");
        else if (item.ImagePath is not null)
        {
            data.SetFileDropList(new StringCollection { item.ImagePath });
            if (item.Thumbnail is BitmapSource bs) data.SetImage(bs);
        }
        return data;
    }

    // ---- persistence ----

    private DispatcherTimer? _saveTimer;
    /// <summary>The saved history is in (or there is none to load): before that a save would drop it.</summary>
    private bool _loaded;
    /// <summary>index.json could not be read and not be copied aside either: it is not overwritten.</summary>
    private bool _keepIndex;

    private void SaveIndexSoon()
    {
        if (!_settings.ClipboardPersist) return;
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            SaveIndex();
        }, _ui);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveIndex()
    {
        if (!_settings.ClipboardPersist || !_loaded || _keepIndex) return;
        try { AtomicFile.WriteAllText(IndexPath, JsonSerializer.Serialize(Items.ToList()), backup: false); }
        catch (Exception ex) { Log.Error("Clipboard save", ex); }
    }

    /// <summary>Saves a change still waiting for its delay right away (app exit).</summary>
    public void Flush()
    {
        if (_saveTimer?.IsEnabled != true) return;
        _saveTimer.Stop();
        SaveIndex();
    }

    /// <summary>Images in the clipboard folder no entry points to (not kept, a crash, an old index): deleted in the background.</summary>
    private void DeleteOrphans()
    {
        try
        {
            // A history that could not be read was set aside: its images stay two weeks, time to sort it out by hand.
            if (Directory.EnumerateFiles(_folder, "index.corrupt-*").Any(f => DateTime.Now - File.GetLastWriteTime(f) < TimeSpan.FromDays(14))) return;
            var used = Items.Where(i => i.ImagePath is not null).Select(i => Path.GetFullPath(i.ImagePath!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Listed here (on the UI thread), so an image saved a moment later is never in the list.
            var orphans = Directory.EnumerateFiles(_folder, "*.png").Where(f => !used.Contains(Path.GetFullPath(f))).ToList();
            if (orphans.Count == 0) return;
            _ = Task.Run(() =>
            {
                foreach (var f in orphans) try { File.Delete(f); } catch { }
                Log.Info($"Clipboard: {orphans.Count} unused images deleted");
            });
        }
        catch (Exception ex) { Log.Error("Clipboard cleanup", ex); }
    }

    /// <summary>Reads the history and decodes thumbnails off the UI thread (images are frozen, so they can cross threads).</summary>
    private async Task LoadIndexAsync()
    {
        try
        {
            var list = await Task.Run(() =>
            {
                if (!File.Exists(IndexPath)) return new List<ClipItem>();
                var items = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(IndexPath)) ?? new();
                var keep = new List<ClipItem>();
                foreach (var i in items)
                {
                    if (i.IsImage)
                    {
                        if (i.ImagePath is null || !File.Exists(i.ImagePath)) continue;
                        // Longer side 320 px, like MakeThumb: by width alone a tall screenshot (scrolling capture) kept
                        // thousands of rows, and small pictures were blown up to 320 px.
                        var (dw, dh) = ImageTools.DecodeSize(i.PixelWidth, i.PixelHeight, 320);
                        i.Thumbnail = ImageTools.Load(i.ImagePath, dw, dh);
                    }
                    keep.Add(i);
                }
                return keep;
            });
            foreach (var i in list) Items.Add(i);
            _lastText = Items.FirstOrDefault(i => i.IsText)?.Text;
            _loaded = true;
            DeleteOrphans();
            SaveIndexSoon(); // copies made while loading
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard load", ex);
            // A broken index is copied aside first; its images stay (no cleanup) until the next start.
            _keepIndex = !AtomicFile.SetAside(IndexPath);
            _loaded = true;
            SaveIndexSoon();
        }
    }

    /// <summary>For design snapshots only.</summary>
    public void AddDemo(ClipItem item) => Items.Add(item);
}
