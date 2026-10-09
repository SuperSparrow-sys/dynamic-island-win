using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DynamicBay.Services.Scripting;

namespace DynamicBay.Tests;

/// <summary>Script widgets: the Scriptable-style API, the sandbox limits and the sample script with real data.</summary>
public class ScriptEngineTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "dynbay-script-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_data, true); } catch { }
    }

    private ScriptResult Run(string code, string family = "medium", bool network = true) =>
        ScriptEngine.Run(code, family, _data, network, "Test");

    private static IEnumerable<string> Texts(JsonNode? node)
    {
        if (node is null) yield break;
        if ((string?)node["t"] == "text") yield return (string)node["text"]!;
        if (node["items"] is JsonArray items)
            foreach (var i in items)
                foreach (var t in Texts(i)) yield return t;
    }

    [Fact]
    public void Builds_a_widget_tree_with_stacks_texts_and_spacers()
    {
        var r = Run("""
            const w = new ListWidget();
            w.backgroundColor = new Color("#141414");
            const s = w.addStack(); s.layoutHorizontally(); s.centerAlignContent();
            const t = s.addText("Hallo"); t.font = Font.boldMonospacedSystemFont(13); t.textColor = Color.dynamic(new Color("#000"), new Color("#EDEDED"));
            s.addSpacer();
            w.refreshAfterDate = new Date(Date.now() + 30 * 60 * 1000);
            Script.setWidget(w);
            """);
        Assert.Null(r.Error);
        Assert.Equal("widget", (string?)r.Widget!["t"]);
        var stack = r.Widget["items"]![0]!;
        Assert.False((bool)stack["vertical"]!);
        Assert.Equal("center", (string?)stack["align"]);
        Assert.Equal("Hallo", (string?)stack["items"]![0]!["text"]);
        Assert.Equal("mono", (string?)stack["items"]![0]!["font"]!["kind"]);
        Assert.Equal("#EDEDED", (string?)stack["items"]![0]!["color"]!["hex"]);   // dark variant: the island is dark
        Assert.Null(stack["items"]![1]!["len"]?.GetValue<double?>());              // flexible spacer
        Assert.InRange(r.NextRun, DateTime.Now.AddMinutes(29), DateTime.Now.AddMinutes(31));
    }

    [Fact]
    public void Script_sees_the_chosen_widget_family()
    {
        var r = Run("""const w = new ListWidget(); w.addText(config.widgetFamily + " " + config.runsInWidget); Script.setWidget(w);""", "accessoryInline");
        Assert.Equal("accessoryInline true", Texts(r.Widget).Single());
    }

    [Fact]
    public void Script_errors_are_reported_with_line_numbers()
    {
        var r = Run("const a = 1;\nnichtDefiniert();");
        Assert.Null(r.Widget);
        Assert.Contains("nichtDefiniert", r.Error);
        Assert.Contains("Zeile 2", r.Error);
    }

    [Fact]
    public void Sandbox_has_no_access_to_dotnet_or_the_system()
    {
        foreach (var probe in new[] { "System.IO.File", "importNamespace('System')", "require('fs')", "process.exit()", "clr", "__host_read('x')" })
        {
            var r = Run($"try {{ const x = {probe}; }} catch (e) {{ }} const w = new ListWidget(); w.addText(typeof System + typeof importNamespace + typeof require + typeof __host_read); Script.setWidget(w);");
            Assert.Null(r.Error);
            Assert.Equal("undefinedundefinedundefinedundefined", Texts(r.Widget).Single());
        }
    }

    [Fact]
    public void Files_stay_inside_the_script_folder()
    {
        var r = Run("""
            const fm = FileManager.local();
            fm.writeString(fm.joinPath(fm.documentsDirectory(), "../../ausbruch.txt"), "x");
            const w = new ListWidget(); w.addText(fm.readString(fm.joinPath(fm.documentsDirectory(), "ausbruch.txt"))); Script.setWidget(w);
            """);
        Assert.Null(r.Error);
        Assert.Equal("x", Texts(r.Widget).Single());
        Assert.True(File.Exists(Path.Combine(_data, "ausbruch.txt")));   // landed in its own folder, path stripped
        Assert.False(File.Exists(Path.Combine(_data, "..", "..", "ausbruch.txt")));
    }

    [Fact]
    public void Endless_loops_and_huge_memory_are_stopped()
    {
        Assert.NotNull(Run("while (true) {}").Error);
        Assert.NotNull(Run("const a = []; while (true) a.push('x'.repeat(100000));").Error);
        Assert.NotNull(Run("function f() { return f(); } f();").Error);
    }

    [Fact]
    public void Only_http_and_https_requests_are_allowed()
    {
        var r = Run("""
            let msg = "";
            try { await new Request("file:///C:/Windows/win.ini").loadString(); } catch (e) { msg = e.message; }
            const w = new ListWidget(); w.addText(msg); Script.setWidget(w);
            """);
        Assert.Contains("http", Texts(r.Widget).Single());
    }

    [Fact]
    public void Network_can_be_switched_off_per_script()
    {
        var r = Run("""
            let msg = "";
            try { await new Request("https://example.com").loadString(); } catch (e) { msg = e.message; }
            const w = new ListWidget(); w.addText(msg); Script.setWidget(w);
            """, network: false);
        Assert.Contains("ausgeschaltet", Texts(r.Widget).Single());
    }

    /// <summary>The sample script against a local server that serves its API answer.</summary>
    [Theory]
    [InlineData("medium", "Raumklima")]
    [InlineData("small", "Raumklima")]
    [InlineData("accessoryInline", "21,4 °C")]
    public void Sample_script_renders_every_size(string family, string expectedText)
    {
        using var server = new HttpListener();
        int port = 43900 + Random.Shared.Next(0, 90);
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var json = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "werte.json"));
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                try
                {
                    var ctx = await server.GetContextAsync();
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(json);
                    ctx.Response.Close();
                }
                catch { break; }
            }
        });
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "Beispiel.js"), Encoding.UTF8)
            .Replace("https://example.com/api/werte", $"http://127.0.0.1:{port}/api/werte");

        var r = Run(code, family);
        Assert.Null(r.Error);
        var texts = Texts(r.Widget).ToList();
        Assert.Contains(expectedText, texts);
        if (family == "medium")
        {
            Assert.Contains(" 22,1", texts);                    // a list row
            Assert.Contains("21,4", texts);                     // current value
            Assert.True(File.Exists(Path.Combine(_data, "werte_cache.json")));
        }
    }
}

/// <summary>The renderer turns a script's widget tree into WPF without throwing, in every size.</summary>
public class ScriptRendererTests
{
    [Fact]
    public void Renders_cards_and_the_mini_line()
    {
        var data = Path.Combine(Path.GetTempPath(), "dynbay-render-" + Guid.NewGuid().ToString("N")[..8]);
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                foreach (var family in new[] { "medium", "small", "accessoryInline" })
                {
                    var r = ScriptEngine.Run("""
                        const w = new ListWidget(); w.setPadding(14, 16, 14, 16);
                        const h = w.addStack(); h.addText("Titel").font = Font.semiboldSystemFont(12); h.addSpacer();
                        const dot = h.addStack(); dot.size = new Size(7, 7); dot.cornerRadius = 3.5; dot.backgroundColor = Color.green();
                        w.addSpacer(); const v = w.addText("21,4"); v.font = Font.boldRoundedSystemFont(30); v.lineLimit = 1;
                        Script.setWidget(w);
                        """, family, data, false, "Render");
                    var el = DynamicBay.Services.Scripting.ScriptRenderer.Build(r.Widget!, family, vertical: family == "accessoryInline");
                    el.Measure(new System.Windows.Size(400, 200));
                    el.Arrange(new System.Windows.Rect(el.DesiredSize));
                    Assert.True(el.DesiredSize.Width > 0 && el.DesiredSize.Height > 0, family);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(data, true); } catch { }
        Assert.Null(failure);
    }
}
