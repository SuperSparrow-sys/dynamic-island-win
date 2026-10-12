using DynamicBay.Core;
using DynamicBay.Island;

namespace DynamicBay.Tests;

/// <summary>"Je Bildschirm-Anordnung merken": place and shape come back when a monitor is plugged in or out.</summary>
public class DockProfileTests
{
    private const string Laptop = @"\\.\DISPLAY1:1920x1200*";
    private const string Docked = @"\\.\DISPLAY1:1920x1200|\\.\DISPLAY2:2560x1440*";

    /// <summary>What IslandManager does when the user changes something: remember it for the current displays.</summary>
    private static void Remember(AppSettings s, string key) => s.DockProfileMap[key] = DockProfile.From(s);

    [Fact]
    public void Notch_on_the_monitor_and_island_on_the_laptop_come_back()
    {
        var s = new AppSettings { ShapeStyle = IslandShape.Island, Edge = IslandEdge.Top, Along = 0.5 };
        IslandManager.SwitchProfile(s, Laptop, arrangementChanged: false); // start: laptop alone

        // Monitor plugged in: new arrangement, starts from the laptop's look; the user picks the notch there.
        Assert.Null(IslandManager.SwitchProfile(s, Docked, arrangementChanged: true));
        s.ShapeStyle = IslandShape.Notch;
        s.Along = 0.3;
        Remember(s, Docked);

        // Unplugged: the laptop gets its island back, plugged in again: the notch.
        Assert.NotNull(IslandManager.SwitchProfile(s, Laptop, arrangementChanged: true));
        Assert.Equal((IslandShape.Island, 0.5), (s.ShapeStyle, s.Along));
        IslandManager.SwitchProfile(s, Docked, arrangementChanged: true);
        Assert.Equal((IslandShape.Notch, 0.3), (s.ShapeStyle, s.Along));
    }

    [Fact]
    public void Profiles_from_before_keep_the_shape_and_learn_it()
    {
        var s = new AppSettings { ShapeStyle = IslandShape.Notch, Along = 0.5 };
        s.DockProfileMap[Laptop] = new DockProfile { Edge = IslandEdge.Top, Along = 0.2 }; // saved by 1.1.3: no shape
        IslandManager.SwitchProfile(s, Laptop, arrangementChanged: true);
        Assert.Equal((IslandShape.Notch, 0.2), (s.ShapeStyle, s.Along));
        Assert.Equal(IslandShape.Notch, s.DockProfileMap[Laptop].Shape);
    }

    [Fact]
    public void Nothing_changes_at_the_start_or_when_switched_off()
    {
        var s = new AppSettings { ShapeStyle = IslandShape.Island };
        s.DockProfileMap[Docked] = new DockProfile { Shape = IslandShape.Notch };
        Assert.Null(IslandManager.SwitchProfile(s, Docked, arrangementChanged: false));
        Assert.Equal(IslandShape.Island, s.ShapeStyle);
        s.DockProfiles = false;
        Assert.Null(IslandManager.SwitchProfile(s, Docked, arrangementChanged: true));
        Assert.Equal(IslandShape.Island, s.ShapeStyle);
    }

    [Fact]
    public void Shape_survives_the_settings_file()
    {
        var s = new AppSettings();
        s.DockProfileMap[Docked] = new DockProfile { Shape = IslandShape.Notch, Monitor = @"\\.\DISPLAY2" };
        s.DockProfileMap[Laptop] = new DockProfile();
        var back = AppSettings.FromJson(s.ToJson());
        Assert.Equal(IslandShape.Notch, back.DockProfileMap[Docked].Shape);
        Assert.Null(back.DockProfileMap[Laptop].Shape);
    }
}
