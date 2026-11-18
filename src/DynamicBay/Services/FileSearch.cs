using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using DynamicBay.Core;

namespace DynamicBay.Services;

public sealed record FileHit(string Path, bool IsFolder);

/// <summary>
/// Files and folders by name: the Windows search index (OLE DB provider Search.CollatorDSO, late bound through ADO, so no
/// package), and Everything when it runs (its window message interface, voidtools' everything_ipc.h). Both run off the
/// UI thread; the caller drops answers to queries that are no longer current.
/// </summary>
public sealed class FileSearch : IDisposable
{
    private readonly StaWorker _index = new("File search");
    private dynamic? _connection; // lives on the worker thread
    private HwndSource? _reply;   // receives Everything's answer on the UI thread
    private int _replyId = 0x44420000;
    private readonly Dictionary<int, TaskCompletionSource<List<FileHit>>> _pending = new();

    /// <summary>Both sources merged (Everything first, same path once), at most <paramref name="max"/>.</summary>
    public async Task<List<FileHit>> FindAsync(string query, int max)
    {
        var terms = Terms(query);
        if (terms.Count == 0) return new();
        var index = _index.Run(() => FromIndex(terms, max * 4)); // room for what IsClutter drops
        var everything = FromEverythingAsync(string.Join(' ', terms), max);
        var found = new List<FileHit>();
        try { found.AddRange(await everything); } catch (Exception ex) { Log.Info($"Search: Everything failed ({ex.Message})"); }
        try { found.AddRange(await index); } catch (Exception ex) { Log.Info($"Search: index failed ({ex.Message})"); }
        return found.Where(f => !IsClutter(f.Path)).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    /// <summary>Words of the query without the characters the index query language would read as syntax.</summary>
    internal static List<string> Terms(string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(t => new string(t.Where(c => !char.IsControl(c) && c is not ('\'' or '"' or '*' or '%' or '(' or ')' or '[' or ']')).ToArray()))
             .Where(t => t.Length > 0).Take(6).ToList();

    private static readonly string[] ClutterFolders = { "node_modules", "AppData", "__pycache__", "site-packages", "$Recycle.Bin" };

    /// <summary>Inside folders nobody opens by hand: hidden dot folders (.rustup, .git), packages, caches.</summary>
    internal static bool IsClutter(string path) =>
        path.Split('\\', '/').Any(s => (s.Length > 1 && s[0] == '.') || ClutterFolders.Contains(s, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// System.ItemUrl ("file:C:/Users/…") as a path. System.ItemPathDisplay is no path: it shows folders by their
    /// translated names ("C:\Benutzer\…"), which do not open.
    /// </summary>
    internal static string? PathFromUrl(string? url) =>
        url is not null && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && url.Length > 7 ? url[5..].Replace('/', '\\') : null;

    /// <summary>Every word starts a word of the file name; links (the Start menu's own) are left out, newest first.</summary>
    internal static string IndexQuery(IReadOnlyList<string> terms, int max) =>
        $"SELECT TOP {max} System.ItemUrl, System.ItemType FROM SystemIndex WHERE SCOPE='file:' " +
        $"AND CONTAINS(System.FileName, '{string.Join(" AND ", terms.Select(t => $"\"{t}*\""))}') " +
        "AND System.FileExtension <> '.lnk' ORDER BY System.DateModified DESC";

    private List<FileHit> FromIndex(List<string> terms, int max)
    {
        var list = new List<FileHit>();
        try
        {
            if (_connection is null)
            {
                var type = Type.GetTypeFromProgID("ADODB.Connection");
                if (type is null) return list;
                dynamic c = Activator.CreateInstance(type)!;
                c.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
                _connection = c;
            }
            dynamic rs = _connection.Execute(IndexQuery(terms, max));
            try
            {
                while (!(bool)rs.EOF)
                {
                    string? path = PathFromUrl(rs.Fields.Item(0).Value as string);
                    string? kind = rs.Fields.Item(1).Value as string;
                    if (!string.IsNullOrEmpty(path)) list.Add(new FileHit(path, string.Equals(kind, "Directory", StringComparison.OrdinalIgnoreCase)));
                    rs.MoveNext();
                }
            }
            finally { rs.Close(); }
        }
        catch (Exception ex)
        {
            _connection = null; // the search service restarted or is off: connect again next time
            Log.Info($"Search: Windows index not readable ({ex.Message})");
        }
        return list;
    }

    // ---------- Everything ----------

    private const int WmCopyData = 0x004A, CopyDataQuery2W = 18;
    private const uint RequestFullPathAndName = 0x4, SortDateModifiedDescending = 14, ItemFolder = 0x1;

    /// <summary>Everything is running: its hidden window takes queries (1.4 and the 1.5 alpha).</summary>
    public static IntPtr EverythingWindow()
    {
        var hwnd = Native.FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
        return hwnd != IntPtr.Zero ? hwnd : Native.FindWindow("EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)", null);
    }

    private async Task<List<FileHit>> FromEverythingAsync(string query, int max)
    {
        var target = EverythingWindow();
        if (target == IntPtr.Zero) return new();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return new();
        var (replyHwnd, id, done) = await dispatcher.InvokeAsync(() =>
        {
            if (_reply is null)
            {
                _reply = new HwndSource(new HwndSourceParameters("DynamicBay.Search") { Width = 0, Height = 0, WindowStyle = 0 });
                _reply.AddHook(OnReply);
            }
            int next = ++_replyId;
            var tcs = new TaskCompletionSource<List<FileHit>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[next] = tcs;
            return (_reply.Handle, next, tcs);
        });
        try
        {
            byte[] request = EverythingRequest(query, (uint)replyHwnd.ToInt64(), (uint)id, (uint)max);
            var handle = GCHandle.Alloc(request, GCHandleType.Pinned);
            try
            {
                var cds = new CopyData { Data = CopyDataQuery2W, Size = request.Length, Pointer = handle.AddrOfPinnedObject() };
                // From the thread pool: Everything answers to the UI thread's window while this waits.
                bool sent = await Task.Run(() => SendMessageTimeout(target, WmCopyData, replyHwnd, ref cds, 2 /* ABORTIFHUNG */, 1000, out var ok) != IntPtr.Zero && ok != IntPtr.Zero);
                if (!sent) return new();
            }
            finally { handle.Free(); }
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { return new(); }
        finally { await dispatcher.InvokeAsync(() => _pending.Remove(id)); }
    }

    /// <summary>EVERYTHING_IPC_QUERY2 (seven DWORDs) followed by the search text in UTF-16 with its terminating zero.</summary>
    internal static byte[] EverythingRequest(string query, uint replyHwnd, uint replyId, uint max)
    {
        var text = Encoding.Unicode.GetBytes(query + "\0");
        var data = new byte[7 * 4 + text.Length];
        uint[] head = { replyHwnd, replyId, 0 /* search flags */, 0 /* offset */, max, RequestFullPathAndName, SortDateModifiedDescending };
        Buffer.BlockCopy(head, 0, data, 0, 7 * 4);
        Buffer.BlockCopy(text, 0, data, 7 * 4, text.Length);
        return data;
    }

    /// <summary>
    /// EVERYTHING_IPC_LIST2: five DWORDs (total, count, offset, request flags, sort), then per item its flags and the
    /// offset of its data; there a DWORD length in characters and the full path in UTF-16.
    /// </summary>
    internal static List<FileHit> ParseEverythingReply(byte[] data)
    {
        var list = new List<FileHit>();
        if (data.Length < 20) return list;
        int count = (int)BitConverter.ToUInt32(data, 4);
        for (int i = 0; i < count; i++)
        {
            int item = 20 + i * 8;
            if (item + 8 > data.Length) break;
            uint flags = BitConverter.ToUInt32(data, item);
            int at = (int)BitConverter.ToUInt32(data, item + 4);
            if (at < 0 || at + 4 > data.Length) continue;
            int len = (int)BitConverter.ToUInt32(data, at);
            if (len <= 0 || at + 4 + len * 2 > data.Length) continue;
            list.Add(new FileHit(Encoding.Unicode.GetString(data, at + 4, len * 2), (flags & ItemFolder) != 0));
        }
        return list;
    }

    private IntPtr OnReply(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmCopyData) return IntPtr.Zero;
        var cds = Marshal.PtrToStructure<CopyData>(lParam);
        if (!_pending.Remove((int)cds.Data, out var tcs)) return IntPtr.Zero;
        var bytes = new byte[cds.Size];
        if (cds.Size > 0) Marshal.Copy(cds.Pointer, bytes, 0, cds.Size);
        tcs.TrySetResult(ParseEverythingReply(bytes));
        handled = true;
        return new IntPtr(1);
    }

    public void Dispose()
    {
        _index.Dispose();
        _reply?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData { public IntPtr Data; public int Size; public IntPtr Pointer; }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, ref CopyData lParam, int flags, int timeout, out IntPtr result);
}
