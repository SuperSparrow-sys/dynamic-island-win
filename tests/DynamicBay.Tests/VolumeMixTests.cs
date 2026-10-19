using System.Text.Json.Nodes;
using DynamicBay.Core;
using DynamicBay.Services;
using DynamicBay.Services.Link.Audio;

namespace DynamicBay.Tests;

/// <summary>Sound on several devices: one slider for all of them, one each, and the volume of a paired device.</summary>
public class VolumeMixTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 21, 0, 0, DateTimeKind.Utc);
    private static OutputLevel Out(string id, float peak, float level = 0.5f) => new(id, id, false, peak, level);

    [Fact]
    public void Rows_are_the_default_output_and_every_output_that_plays()
    {
        var heard = new Dictionary<string, DateTime>();
        var outputs = new[] { Out("jbl", 0.3f), Out("speakers", 0), Out("airpods", 0), Out("monitor", 0) };
        Assert.Equal(new[] { "speakers", "jbl" }, VolumeMix.Places(outputs, "speakers", null, heard, T0).Select(o => o.Id));

        // A pause between songs keeps the row; ten seconds of silence take it away.
        var quiet = new[] { Out("jbl", 0), Out("speakers", 0), Out("airpods", 0), Out("monitor", 0) };
        Assert.Equal(2, VolumeMix.Places(quiet, "speakers", null, heard, T0.AddSeconds(5)).Count);
        Assert.Equal(new[] { "speakers" }, VolumeMix.Places(quiet, "speakers", null, heard, T0.AddSeconds(11)).Select(o => o.Id));
    }

    [Fact]
    public void The_output_muted_for_a_paired_device_gets_no_row()
    {
        var outputs = new[] { Out("speakers", 0), Out("jbl", 0.2f) };
        Assert.Equal(new[] { "jbl" }, VolumeMix.Places(outputs, "speakers", "speakers", new(), T0).Select(o => o.Id));
    }

    [Fact]
    public void The_slider_for_all_keeps_the_balance_and_survives_zero()
    {
        double[] levels = { 40, 80, 20 };
        Assert.Equal(80, VolumeMix.Master(levels));
        var weights = VolumeMix.Weights(levels);
        Assert.Equal(new double[] { 20, 40, 10 }, VolumeMix.Apply(weights, 40));
        Assert.Equal(new double[] { 0, 0, 0 }, VolumeMix.Apply(weights, 0));
        Assert.Equal(new double[] { 50, 100, 25 }, VolumeMix.Apply(weights, 100)); // same weights: back up in proportion
        Assert.Equal(new double[] { 30, 30 }, VolumeMix.Apply(VolumeMix.Weights(new double[] { 0, 0 }), 30)); // all silent: equal
    }

    [Fact]
    public void A_moved_row_sends_its_value_but_a_shown_one_does_not()
    {
        var moved = new List<double>();
        var ch = new VolumeChannel { Key = "jbl" };
        ch.Moved = c => moved.Add(c.Level);
        ch.Show(30);
        ch.Level = 55;
        Assert.Equal(new double[] { 55 }, moved);
    }

    [Fact]
    public void A_drag_opens_the_volume_control_once_not_per_step()
    {
        int opened = 0;
        var set = new List<float>();
        var controls = new LevelControls(_ => { opened++; return set.Add; });
        for (int i = 0; i < 40; i++) controls.Set("jbl", i / 100f, T0.AddMilliseconds(i * 16)); // a drag of 0.6 s
        Assert.Equal(1, opened);
        Assert.Equal(40, set.Count);
        controls.Set("jbl", 0.5f, T0.AddSeconds(10)); // later: opened fresh (the device may have changed)
        Assert.Equal(2, opened);
        controls.Clear();
        controls.Set("jbl", 0.5f, T0.AddSeconds(10.1));
        Assert.Equal(3, opened);
    }

    [Fact]
    public void A_device_that_cannot_be_opened_is_skipped()
    {
        var controls = new LevelControls(_ => null);
        controls.Set("gone", 0.5f, T0); // no exception
    }

    [Theory]
    [InlineData("{\"vol\":0.4}", 0.4)]
    [InlineData("{\"vol\":1}", 1.0)]
    [InlineData("{\"vol\":7}", 1.0)]
    [InlineData("{\"vol\":-1}", 0.0)]
    public void Volume_headers_are_read_tolerantly(string json, double level) =>
        Assert.Equal(level, LinkAudioProtocol.ReadVolume(LinkAudioProtocol.ReadHeader(LinkAudioProtocol.HeaderFrame(JsonNode.Parse(json)!.AsObject()))));

    [Fact]
    public void Headers_without_a_volume_are_none()
    {
        Assert.Null(LinkAudioProtocol.ReadVolume(new JsonObject { ["ok"] = true }));
        Assert.Null(LinkAudioProtocol.ReadVolume(new JsonObject { ["vol"] = "laut" }));
        Assert.Null(LinkAudioProtocol.ReadVolume(null));
        Assert.Equal(0.5, LinkAudioProtocol.ReadVolume(LinkAudioProtocol.ReadHeader(LinkAudioProtocol.VolumeFrame(0.5))));
    }

    [Fact]
    public void A_volume_change_glides_across_the_block_without_a_click()
    {
        var block = Enumerable.Repeat(1f, 2 * 960).ToArray();
        AudioConvert.Gain(block, 1f, 0.2f);
        Assert.Equal(0.2f, block[^1], 3);
        Assert.Equal(block[0], block[1]); // left and right alike
        double maxStep = 0;
        for (int i = 2; i < block.Length; i += 2) maxStep = Math.Max(maxStep, Math.Abs(block[i] - block[i - 2]));
        Assert.True(maxStep < 0.001, $"step {maxStep}");
        Assert.True(Math.Abs(block[0] - 1f) < 0.001); // starts where it was

        var same = new[] { 0.5f, -0.5f };
        AudioConvert.Gain(same, 1f, 1f);
        Assert.Equal(new[] { 0.5f, -0.5f }, same);
    }

    // ---------- the rows in the audio card ----------

    private sealed class FakeTargets : IVolumeTargets
    {
        public readonly List<string> Calls = new();
        public void SetOutput(string id, float level) => Calls.Add(FormattableString.Invariant($"{id}={level:0.00}"));
        public void Unmute(string id) => Calls.Add($"unmute {id}");
        public void SetRemote(double level) => Calls.Add(FormattableString.Invariant($"remote={level:0.00}"));
        public void SetIncoming(double level) => Calls.Add(FormattableString.Invariant($"incoming={level:0.00}"));
    }

    private static (OutputVolumeService S, FakeTargets T) Service(params VolumeRow[] rows)
    {
        var t = new FakeTargets();
        var s = new OutputVolumeService(_ => null, t);
        s.Sync(rows.ToList());
        return (s, t);
    }

    [Fact]
    public void The_slider_for_all_moves_the_outputs_and_the_laptop_in_proportion()
    {
        var (s, t) = Service(new VolumeRow("speakers", "Lautsprecher", 40), new VolumeRow("link:pc", "Laptop", 80, Remote: true));
        Assert.True(s.HasMaster);
        Assert.Equal(80, s.Master);
        s.Master = 40;
        Assert.Equal(new[] { "speakers=0.20", "remote=0.40" }, t.Calls);
    }

    [Fact]
    public void The_other_devices_sound_here_has_its_own_slider_outside_the_one_for_all()
    {
        // The receiver: its speakers and the sound from the PC, which plays through those speakers.
        var (s, t) = Service(new VolumeRow("speakers", "Lautsprecher", 50), new VolumeRow("from:PC", "PC", 60, Remote: true, Incoming: true));
        Assert.False(s.HasMaster); // one output only: nothing to move together
        Assert.True(s.IsVisible);  // but the row for the sound from the PC is there
        s.Channels.Single(c => c.IsIncoming).Level = 30;
        Assert.Equal(new[] { "incoming=0.30" }, t.Calls);
    }

    [Fact]
    public void An_older_device_shows_a_note_and_is_left_out_of_the_slider_for_all()
    {
        var (s, t) = Service(new VolumeRow("speakers", "Lautsprecher", 40), new VolumeRow("link:pc", "Laptop", 100, Remote: true, Note: "ab 1.1.13"));
        var laptop = s.Channels.Single(c => c.IsRemote);
        Assert.False(laptop.CanSet);
        Assert.False(s.HasMaster);
        Assert.True(s.IsVisible);
        laptop.Level = 20; // cannot happen through the hidden slider; still nothing is sent
        Assert.Empty(t.Calls);
    }

    [Fact]
    public void Moving_a_muted_output_unmutes_it_but_the_slider_for_all_does_not()
    {
        var (s, t) = Service(new VolumeRow("speakers", "Lautsprecher", 40), new VolumeRow("jbl", "JBL", 30, Muted: true));
        s.Master = 20;
        Assert.DoesNotContain(t.Calls, c => c.StartsWith("unmute"));
        Assert.True(s.Channels[1].IsMuted);
        t.Calls.Clear();
        s.Channels[1].Level = 50;
        Assert.Equal(new[] { "unmute jbl", "jbl=0.50" }, t.Calls);
        Assert.False(s.Channels[1].IsMuted);
    }

    [Theory]
    [InlineData(3u, "Lautsprecher (Realtek)", true)]  // headphones
    [InlineData(5u, "Lautsprecher (Headset)", true)]  // headset
    [InlineData(1u, "Kopfhörer (Monitor)", false)]   // the form factor wins over the name
    [InlineData(null, "Kopfhörer (AirPods)", true)]  // no form factor: the name
    [InlineData(null, "Lautsprecher (JBL Flip 6)", false)]
    public void Headphones_come_from_the_form_factor(uint? formFactor, string name, bool headphones) =>
        Assert.Equal(headphones, VolumeMix.IsHeadphones(formFactor, name));

    [Fact]
    public void The_volume_per_paired_device_is_kept_in_the_settings()
    {
        var s = new AppSettings();
        s.LinkAudioVolumes["0123456789ABCDEF"] = 0.35;
        Assert.Equal(0.35, AppSettings.FromJson(s.ToJson()).LinkAudioVolumes["0123456789ABCDEF"]);
        Assert.Empty(AppSettings.FromJson("{}").LinkAudioVolumes); // older settings files
    }

    [Fact]
    public void Only_a_receiver_that_says_so_plays_the_volume()
    {
        Assert.True(LinkAudioProtocol.AnswerHasVolume(new JsonObject { ["ok"] = true, ["volume"] = true }));
        Assert.False(LinkAudioProtocol.AnswerHasVolume(new JsonObject { ["ok"] = true })); // 1.1.12 and older
    }
}
