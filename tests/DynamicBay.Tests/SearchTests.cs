using System.IO;
using System.Text;
using DynamicBay.Core;
using DynamicBay.Services;

namespace DynamicBay.Tests;

/// <summary>
/// The search: matching names, the task table (Assets/SearchSynonyms.txt, embedded) against the Start menu of a real PC
/// (Fixtures/Search/start-apps.txt), the Windows key tap, the file sources' wire formats and the frosted backdrop.
/// </summary>
public class SearchTests
{
    private static readonly List<SynonymRule> Rules = SearchService.LoadRules();

    private static readonly List<SearchCandidate> StartApps =
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Search", "start-apps.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !SearchMath.IsNoise(l))
            .Select(n => SearchCandidate.Of(n, n)).ToList();

    private static List<string> Top(string query, int n, Func<string, int>? launches = null) =>
        SearchMath.Rank(query, StartApps, Rules, launches ?? (_ => 0), n).Select(h => h.Item.Name).ToList();

    [Theory]
    [InlineData("Geräte-Manager", "gerate manager")]
    [InlineData("Straße", "strasse")]
    [InlineData("  DB Browser (SQLite) ", "db browser sqlite")]
    public void Names_are_normalized(string name, string expected) => Assert.Equal(expected, SearchMath.Normalize(name));

    [Fact]
    public void Words_split_at_capitals_and_give_initials()
    {
        Assert.Equal(new[] { "modbus", "master" }, SearchMath.Words("ModbusMaster"));
        Assert.Equal("vsc", SearchKey.Of("Visual Studio Code").Initials);
        Assert.Equal("ss", SearchKey.Of("SQLiteStudio").Initials); // "SQLite" stays one word
        Assert.Equal(new[] { "sqlite", "studio" }, SearchMath.Words("SQLiteStudio"));
    }

    [Fact]
    public void Better_matches_score_higher()
    {
        var key = SearchKey.Of("Visual Studio Code");
        int exact = SearchMath.Score("visual studio code", key), start = SearchMath.Score("visu", key), word = SearchMath.Score("stud", key),
            words = SearchMath.Score("vis cod", key), initials = SearchMath.Score("vsc", key), inside = SearchMath.Score("udio", key),
            typo = SearchMath.Score("visaul", key);
        Assert.True(exact > start && start > word && word > words && words > initials && initials > inside && inside > typo && typo > 0,
            $"{exact} {start} {word} {words} {initials} {inside} {typo}");
        Assert.Equal(0, SearchMath.Score("xyz", key));
        Assert.Equal(0, SearchMath.Score("vi", SearchKey.Of("Explorer"))); // two letters never match inside a word
    }

    [Theory]
    [InlineData("spotfy", "spotify", 1)]
    [InlineData("tpyo", "typo", 1)]
    [InlineData("kitten", "sitting", 3)]
    public void Distance_counts_edits(string a, string b, int expected) => Assert.Equal(expected, SearchMath.Distance(a, b));

    [Theory]
    [InlineData("vsc", "Visual Studio Code")]
    [InlineData("pdf", "Foxit PDF Reader")]
    [InlineData("entpacken", "WinRAR")]
    [InlineData("zip", "WinRAR")]
    [InlineData("netzwerk scannen", "Advanced IP Scanner")]
    [InlineData("literatur", "Zotero")]
    [InlineData("noten", "MuseScore Studio 4")]
    [InlineData("audio schneiden", "Audacity 4.0")]
    [InlineData("screenshot", "Snipping Tool")]
    [InlineData("spotfy", "Spotify")]
    [InlineData("visul studio", "Visual Studio Code")]
    [InlineData("teams", "Microsoft Teams")]
    [InlineData("rechner", "Rechner")]
    public void Owner_apps_come_first(string query, string expected) => Assert.Equal(expected, Top(query, 1).Single());

    [Theory]
    [InlineData("bild bearbeiten", 4, "Paint", "Affinity", "Fotos")]
    [InlineData("cad", 4, "SketchUp", "DWG TrueView 2027 - English", "CADCraft")]
    [InlineData("modbus", 2, "ModbusMaster", "ModbusSlave")]
    [InlineData("fernwartung", 6, "RealVNC Viewer", "Radmin Viewer 3", "Remotedesktop", "WinSCP")]
    [InlineData("datenbank", 3, "DB Browser (SQLite)", "SQLiteStudio", "Access")]
    [InlineData("sql", 6, "DB Browser (SQLite)", "SQLiteStudio")] // after the four SQL Server tools
    [InlineData("notizen", 5, "Obsidian", "OneNote", "Kurznotizen", "Journal")]
    [InlineData("sd-karte flashen", 3, "Raspberry Pi Imager", "balenaEtcher", "Win32DiskImager")]
    [InlineData("mathe", 3, "GeoGebra Calculator Suite", "Rechner")]
    [InlineData("python", 6, "Python 3.13", "Spyder", "Jupyter Notebook", "IDLE (Python 3.13)", "Anaconda Navigator")]
    [InlineData("vpn", 4, "Tailscale", "Mobile VPN with SSL client")]
    [InlineData("edit photo", 4, "Paint", "Affinity")]
    [InlineData("bild", 6, "Paint", "Fotos")] // typed so far: the task already shows
    [InlineData("fernwarung", 6, "RealVNC Viewer")] // typo in the task
    public void Tasks_find_the_owner_apps(string query, int within, params string[] expected)
    {
        var top = Top(query, within);
        foreach (var app in expected) Assert.Contains(app, top);
    }

    [Fact]
    public void Documents_in_the_start_menu_are_left_out()
    {
        Assert.True(SearchMath.IsNoise("VPN Documentation"));
        Assert.True(SearchMath.IsNoise("Git Release Notes"));
        Assert.True(SearchMath.IsNoise("Lazarus Wiki Help"));
        Assert.True(SearchMath.IsNoise("Node.js website"));
        Assert.False(SearchMath.IsNoise("Remotehilfe"));
        Assert.False(SearchMath.IsNoise("Visual Studio Code"));
    }

    [Fact]
    public void Launched_often_climbs()
    {
        Assert.Equal("ModbusSlave", Top("modbus", 1).Single()); // the shorter name
        Assert.Equal("ModbusMaster", Top("modbus", 1, id => id == "ModbusMaster" ? 3 : 0).Single());
        Assert.Equal(0, SearchMath.Boost(0));
        Assert.True(SearchMath.Boost(1000) <= 150);
    }

    [Fact]
    public void Empty_query_finds_nothing() => Assert.Empty(Top("  ", 5));

    // ---------- the table ----------

    [Fact]
    public void Every_line_of_the_table_is_a_rule_or_a_comment()
    {
        using var stream = typeof(SearchService).Assembly.GetManifestResourceStream("DynamicBay.Assets.SearchSynonyms.txt");
        Assert.NotNull(stream);
        var lines = new StreamReader(stream!).ReadToEnd().Split('\n').Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToList();
        Assert.All(lines, l => Assert.Single(SearchMath.ParseSynonyms(l)));
        Assert.Equal(lines.Count, Rules.Count);
        Assert.True(Rules.Count >= 60);
    }

    [Fact]
    public void Table_parses_phrases_and_apps()
    {
        var rules = SearchMath.ParseSynonyms("# comment\nBild bearbeiten, edit photo = Paint; Affinity Photo\nno rule here\n= Paint\nx =\n");
        var rule = Assert.Single(rules);
        Assert.Equal(new[] { "bild bearbeiten", "edit photo" }, rule.Phrases);
        Assert.Equal(new[] { "paint", "affinity photo" }, rule.Targets);
    }

    [Theory]
    [InlineData("pdf", "pdf", 650)]
    [InlineData("pdf offnen", "pdf", 620)]
    [InlineData("bild", "bild bearbeiten", 560)]
    [InlineData("fernwarung", "fernwartung", 450)]
    [InlineData("bi", "bild bearbeiten", 0)]
    public void Phrases_match_whole_typed_or_with_a_typo(string query, string phrase, int expected) =>
        Assert.Equal(expected, SearchMath.PhraseScore(query, phrase));

    [Fact]
    public void Apps_are_named_by_the_start_of_their_name()
    {
        Assert.True(SearchMath.TargetMatches("python", SearchKey.Of("Python 3.13")));
        Assert.False(SearchMath.TargetMatches("python", SearchKey.Of("IDLE (Python 3.13)")));
        Assert.False(SearchMath.TargetMatches("access", SearchKey.Of("Autodesk Access User Interface")));
        Assert.False(SearchMath.TargetMatches("paint", SearchKey.Of("Painter")));
    }

    // ---------- Windows key ----------

    [Fact]
    public void Windows_key_alone_is_a_tap()
    {
        var gate = new WinKeyGate();
        gate.Down(WinKeyGate.LWin, false);
        gate.Down(WinKeyGate.LWin, false); // key repeat
        Assert.True(gate.Up(WinKeyGate.LWin));
    }

    [Theory]
    [InlineData(0x45)] // Win+E
    [InlineData(0x58)] // Win+X
    [InlineData(0x4C)] // Win+L
    [InlineData(0x25)] // Win+Left
    [InlineData(0x10)] // Win+Shift(+S)
    public void Windows_key_combinations_pass(int other)
    {
        var gate = new WinKeyGate();
        gate.Down(WinKeyGate.LWin, false);
        gate.Down(other, false);
        Assert.False(gate.Up(other));
        Assert.False(gate.Up(WinKeyGate.LWin));
        // The next tap is a tap again.
        gate.Down(WinKeyGate.RWin, false);
        Assert.True(gate.Up(WinKeyGate.RWin));
    }

    [Fact]
    public void Windows_key_after_a_held_modifier_or_unseen_press_is_no_tap()
    {
        var gate = new WinKeyGate();
        gate.Down(WinKeyGate.LWin, othersHeld: true); // Ctrl+Win, Shift+Win
        Assert.False(gate.Up(WinKeyGate.LWin));
        Assert.False(gate.Up(WinKeyGate.RWin)); // pressed before the hook was there
    }

    [Fact]
    public void Both_windows_keys_open_once_when_the_last_goes_up()
    {
        var gate = new WinKeyGate();
        gate.Down(WinKeyGate.LWin, false);
        gate.Down(WinKeyGate.RWin, false);
        Assert.False(gate.Up(WinKeyGate.RWin));
        Assert.True(gate.Up(WinKeyGate.LWin));
    }

    [Fact]
    public void A_click_or_wheel_while_the_windows_key_is_held_is_no_tap()
    {
        var gate = new WinKeyGate();
        gate.Down(WinKeyGate.LWin, false);
        gate.Mouse();
        Assert.False(gate.Up(WinKeyGate.LWin));
        gate.Mouse(); // the key is up: clicks change nothing
        gate.Down(WinKeyGate.LWin, false);
        Assert.True(gate.Held);
        Assert.True(gate.Up(WinKeyGate.LWin));
        Assert.False(gate.Held);
    }

    // ---------- service ----------

    [Fact]
    public async Task File_icons_go_when_the_search_closes_app_icons_stay()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dynamicbay-icon-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        using var service = new SearchService();
        try
        {
            string exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            await service.LoadIcon(new SearchItem { Name = "a.txt", Subtitle = "", Path = file, IconPath = file, Kind = SearchKind.File });
            await service.LoadIcon(new SearchItem { Name = "Editor", Subtitle = "", Path = exe, IconPath = exe, Kind = SearchKind.App });
            Assert.Equal(1, service.FileIconCount);
            Assert.Equal(1, service.AppIconCount);
            service.ForgetFileIcons();
            Assert.Equal(0, service.FileIconCount);
            Assert.Equal(1, service.AppIconCount);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Launches_in_quick_succession_all_land_on_disk()
    {
        File.Delete(Path.Combine(AppSettings.Folder, "search-usage.json"));
        using var service = new SearchService();
        for (int i = 0; i < 40; i++) service.Count(@"C:\Apps\Quick.exe");
        service.FlushUsage();
        Assert.Equal(40, SearchService.ReadUsage()[@"C:\Apps\Quick.exe"]);
    }

    // ---------- files ----------

    [Fact]
    public void Index_query_has_no_way_out_of_its_quotes()
    {
        var terms = FileSearch.Terms("rech'nung \"2024\" ) OR 1=1 --");
        Assert.DoesNotContain(terms, t => t.Contains('\'') || t.Contains('"') || t.Contains(')'));
        string sql = FileSearch.IndexQuery(FileSearch.Terms("Rechnung 2024"), 8);
        Assert.Contains("SELECT TOP 8", sql);
        Assert.Contains("CONTAINS(System.FileName, '\"Rechnung*\" AND \"2024*\"')", sql);
        Assert.Empty(FileSearch.Terms("  ' \" * "));
    }

    [Fact]
    public void Index_paths_come_from_the_url_not_the_translated_display()
    {
        Assert.Equal(@"C:\Users\jonat\My Docs\a.md", FileSearch.PathFromUrl("file:C:/Users/jonat/My Docs/a.md"));
        Assert.Null(FileSearch.PathFromUrl("mapi16://{S-1-5}/inbox"));
        Assert.Null(FileSearch.PathFromUrl(null));
    }

    [Theory]
    [InlineData(@"C:\Users\jonat\.rustup\toolchains\doc.html", true)]
    [InlineData(@"C:\Users\jonat\code\app\node_modules\x\readme.md", true)]
    [InlineData(@"C:\Users\jonat\AppData\Local\Temp\a.txt", true)]
    [InlineData(@"C:\Users\jonat\Documents\Rechnung.2024.pdf", false)]
    public void Hidden_and_package_folders_are_left_out(string path, bool clutter) => Assert.Equal(clutter, FileSearch.IsClutter(path));

    [Fact]
    public void Everything_request_and_reply_follow_its_ipc_layout()
    {
        var request = FileSearch.EverythingRequest("bericht", 0x1234, 77, 6);
        Assert.Equal(0x1234u, BitConverter.ToUInt32(request, 0));
        Assert.Equal(77u, BitConverter.ToUInt32(request, 4));
        Assert.Equal(6u, BitConverter.ToUInt32(request, 16));
        Assert.Equal(4u, BitConverter.ToUInt32(request, 20)); // full path and name
        Assert.Equal("bericht\0", Encoding.Unicode.GetString(request, 28, request.Length - 28));

        // A reply with a file and a folder, built as Everything writes it.
        string[] paths = { @"C:\Docs\Bericht.docx", @"C:\Docs\Berichte" };
        var reply = new List<byte>();
        void U(uint v) => reply.AddRange(BitConverter.GetBytes(v));
        U(2); U(2); U(0); U(4); U(14);
        int data = 20 + paths.Length * 8;
        var blobs = new List<byte>();
        for (int i = 0; i < paths.Length; i++)
        {
            U(i == 1 ? 1u : 0u);
            U((uint)(data + blobs.Count));
            blobs.AddRange(BitConverter.GetBytes((uint)paths[i].Length));
            blobs.AddRange(Encoding.Unicode.GetBytes(paths[i] + "\0"));
        }
        reply.AddRange(blobs);
        var hits = FileSearch.ParseEverythingReply(reply.ToArray());
        Assert.Equal(new FileHit(paths[0], false), hits[0]);
        Assert.Equal(new FileHit(paths[1], true), hits[1]);
        Assert.Empty(FileSearch.ParseEverythingReply(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Index_query_finds_words_inside_names_on_the_second_pass()
    {
        string sql = FileSearch.IndexQuery(FileSearch.Terms("lupe 4711"), 16, anywhere: true);
        Assert.Contains("System.FileName LIKE '%lupe%' AND System.FileName LIKE '%4711%'", sql);
        Assert.Contains("System.FileExtension <> '.lnk'", sql);
        Assert.DoesNotContain("CONTAINS", sql);
    }

    /// <summary>
    /// The whole round trip through window messages against a stand-in for Everything's window: it reads the QUERY2 the
    /// way everything_ipc.h lays it out and answers with a LIST2 to the reply window, as Everything does.
    /// </summary>
    [Fact]
    public async Task Everything_round_trip_through_window_messages()
    {
        System.Windows.Threading.Dispatcher? ui = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() => { ui = System.Windows.Threading.Dispatcher.CurrentDispatcher; ready.Set(); System.Windows.Threading.Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        string? asked = null;
        var fake = ui!.Invoke(() =>
        {
            var s = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Everything stand-in") { Width = 0, Height = 0, WindowStyle = 0 });
            s.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg != 0x004A) return IntPtr.Zero;
                var cds = System.Runtime.InteropServices.Marshal.PtrToStructure<CopyData>(l);
                if (cds.Data != 18) return IntPtr.Zero;
                var request = new byte[cds.Size];
                System.Runtime.InteropServices.Marshal.Copy(cds.Pointer, request, 0, cds.Size);
                var replyTo = (IntPtr)BitConverter.ToUInt32(request, 0);
                uint id = BitConverter.ToUInt32(request, 4);
                asked = Encoding.Unicode.GetString(request, 28, request.Length - 30);
                byte[] reply = List2(@"C:\Docs\Suchtest-Lupe.txt");
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(reply, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    var answer = new CopyData { Data = (IntPtr)id, Size = reply.Length, Pointer = handle.AddrOfPinnedObject() };
                    SendMessage(replyTo, 0x004A, hwnd, ref answer);
                }
                finally { handle.Free(); }
                handled = true;
                return new IntPtr(1);
            });
            return s;
        });
        var search = new FileSearch();
        try
        {
            var hits = await search.AskEverythingAsync(fake.Handle, "suchtest lupe", 4, ui);
            Assert.Equal("suchtest lupe", asked);
            Assert.Equal(new[] { new FileHit(@"C:\Docs\Suchtest-Lupe.txt", false) }, hits);
            // Nobody answers: empty after the wait, not an exception.
            ui.Invoke(() => fake.Dispose());
            Assert.Empty(await search.AskEverythingAsync(IntPtr.Zero, "x", 4, ui));
        }
        finally
        {
            ui.Invoke(search.Dispose);
            ui.InvokeShutdown();
        }
    }

    private static byte[] List2(string path)
    {
        var data = new List<byte>();
        void U(uint v) => data.AddRange(BitConverter.GetBytes(v));
        U(1); U(1); U(0); U(4); U(14); // total, count, offset, request flags, sort
        U(0); U(28);                     // the item: flags (file), offset of its data
        U((uint)path.Length);
        data.AddRange(Encoding.Unicode.GetBytes(path + "\0"));
        return data.ToArray();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CopyData { public IntPtr Data; public int Size; public IntPtr Pointer; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref CopyData lParam);

    // ---------- backdrop ----------

    [Fact]
    public void Frost_shrinks_and_keeps_a_plain_colour()
    {
        int w = 40, h = 24;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 100; px[i + 2] = 50; px[i + 3] = 255; }
        var (frost, fw, fh) = ImageTools.Frost(px, w, h, 4, 2);
        Assert.Equal((10, 6), (fw, fh));
        for (int i = 0; i < frost.Length; i += 4) Assert.Equal(new byte[] { 200, 100, 50, 255 }, frost[i..(i + 4)]);
    }
}
