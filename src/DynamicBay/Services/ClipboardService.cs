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
    public string? SourceApp { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    [ObservableProperty] private bool _pinned;

    [System.Text.Json.Serialization.JsonIgnore] public ImageSource? Thumbnail { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsImage => Kind == ClipKind.Image;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsText => Kind == ClipKind.Text;
    [System.Text.Json.Serialization.JsonIgnore] public string Preview =>
        Text is null ? "" : (Text.Length > 240 ? Text[..240] + "…" : Text).Replace("\r", "").Trim();
    [System.Text.Json.Serialization.JsonIgnore] public string Label =>
        Kind == ClipKind.Image ? (IsScreenshot ? Loc.T("Clip.Screenshot") : Loc.T("Clip.Image")) : Loc.T("Clip.Text");
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

    public ClipboardService(AppSettings settings)
    {
        _settings = settings;
        Items.CollectionChanged += (_, _) => IsEmpty = Items.Count == 0;
    }

    public void Start(MessageWindow msg)
    {
        Directory.CreateDirectory(_folder);
        if (_settings.ClipboardPersist) LoadIndex();
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
        WatchScreenshotFolder();
    }

    private void OnClipboardChanged()
    {
        if (!_settings.ClipboardEnabled) return;
        if (_suppressNext) { _suppressNext = false; return; }
        try
        {
            if (Native.IsClipboardFormatAvailable(_fmtExclude) || Native.IsClipboardFormatAvailable(_fmtIgnore)) return;
            if (Native.IsClipboardFormatAvailable(_fmtNoHistory))
            {
                // Password managers set CanIncludeInClipboardHistory = 0.
                if (Clipboard.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
                {
                    var b = new byte[4];
                    ms.Read(b, 0, 4);
                    if (BitConverter.ToInt32(b, 0) == 0) return;
                }
            }

            string? owner = Native.ProcessName(Native.GetClipboardOwner());
            if (Clipboard.ContainsImage())
            {
                var img = ReadImage();
                if (img is null) return;
                bool screenshot = owner is not null &&
                                  (owner.Contains("ScreenClipping", StringComparison.OrdinalIgnoreCase) ||
                                   owner.Contains("SnippingTool", StringComparison.OrdinalIgnoreCase) ||
                                   owner.Contains("ShellExperienceHost", StringComparison.OrdinalIgnoreCase));
                // Win+PrtScn writes the file and the clipboard; the folder watcher may have added it already.
                var dup = Items.FirstOrDefault(i => i.IsImage && (DateTime.Now - i.Created).TotalSeconds < 4 &&
                                                    i.PixelWidth == img.PixelWidth && i.PixelHeight == img.PixelHeight);
                if (dup is not null) return;
                AddImage(img, screenshot, owner);
                return;
            }
            if (_settings.ClipboardText && Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text) || text.Length > 200_000 || text == _lastText) return;
                _lastText = text;
                var existing = Items.FirstOrDefault(i => i.IsText && i.Text == text);
                if (existing is not null) Items.Remove(existing);
                Insert(new ClipItem { Kind = ClipKind.Text, Text = text, SourceApp = owner });
            }
        }
        catch { /* clipboard is often locked by another process; skip this change */ }
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

    private void AddImage(BitmapSource img, bool screenshot, string? source, string? existingPath = null)
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
        if (existingPath is null) ImageTools.SavePng(img, item.ImagePath);
        item.Thumbnail = MakeThumb(img);
        Insert(item);
        if (screenshot) ScreenshotCaptured?.Invoke(item);
    }

    private static ImageSource MakeThumb(BitmapSource img)
    {
        double scale = Math.Min(1, 320.0 / Math.Max(img.PixelWidth, img.PixelHeight));
        var t = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        var wb = new WriteableBitmap(t);
        wb.Freeze();
        return wb;
    }

    private void Insert(ClipItem item)
    {
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

    private void WatchScreenshotFolder()
    {
        foreach (var dir in ScreenshotFolders())
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
        if (dup is not null) return;
        AddImage(img, true, "Screenshots", path);
    }

    // ---- actions ----

    [RelayCommand]
    public void Copy(ClipItem? item)
    {
        if (item is null) return;
        try
        {
            _suppressNext = true;
            if (item.IsText) { Clipboard.SetText(item.Text ?? ""); _lastText = item.Text; }
            else if (item.ImagePath is not null && ImageTools.Load(item.ImagePath) is { } img)
            {
                var data = new DataObject();
                data.SetImage(img);
                data.SetFileDropList(new StringCollection { item.ImagePath });
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
        SaveIndexSoon();
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

    private void SaveIndexSoon()
    {
        if (!_settings.ClipboardPersist) return;
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            try { File.WriteAllText(IndexPath, JsonSerializer.Serialize(Items.ToList())); } catch { }
        }, _ui);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath)) return;
            var list = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(IndexPath)) ?? new();
            foreach (var i in list)
            {
                if (i.IsImage)
                {
                    if (i.ImagePath is null || !File.Exists(i.ImagePath)) continue;
                    i.Thumbnail = ImageTools.Load(i.ImagePath, 320);
                }
                Items.Add(i);
            }
            _lastText = Items.FirstOrDefault(i => i.IsText)?.Text;
        }
        catch { }
    }

    /// <summary>For design snapshots only.</summary>
    public void AddDemo(ClipItem item) => Items.Add(item);
}
