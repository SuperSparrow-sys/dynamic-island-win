using DynamicBay.Core;

namespace DynamicBay.Tests;

/// <summary>The one line under a collapsed focus card: what the focus switches, in the order of the card.</summary>
[Collection("Loc")]
public class FocusSummaryTests
{
    private static string Teams(string s) => s == "Busy" ? "Beschäftigt" : s;

    [Fact]
    public void Says_what_the_focus_switches()
    {
        var p = new FocusProfile { Name = "Schule", Quiet = true, HideText = true, TeamsStatus = "Busy", Panel = "dimmen", PanelPercent = 25, PanelFade = 10 };
        p.MutedApps.Add("WhatsApp");
        p.HiddenWidgets.Add("media");
        p.HiddenWidgets.Add("calendar");
        Assert.Equal("Nicht stören · Mitteilungen ohne Text · 1 App stumm · 2 Widgets aus · Teams: Beschäftigt · Panel 25 %, schnell",
            FocusSummary.Of(p, panelShown: true, Teams));
    }

    [Fact]
    public void Leaves_out_the_board_when_it_is_not_set_up_and_says_when_nothing_happens()
    {
        var home = new FocusProfile { Name = "Zu Hause", Trigger = Triggers.Home, Panel = "plan" };
        Assert.Equal("Panel nach Wochenplan", FocusSummary.Of(home, panelShown: true, Teams));
        Assert.Equal("Schaltet nichts", FocusSummary.Of(home, panelShown: false, Teams));
        Assert.Equal("Panel aus nach 1 min", FocusSummary.Of(new FocusProfile { Trigger = Triggers.Away, Panel = "aus" }, true, Teams));
        Assert.Equal("Teams: Offline, nur statt grün", FocusSummary.Of(new FocusProfile { TeamsStatus = "Offline", TeamsOnlyIfAvailable = true }, false, Teams));
        Assert.Equal("Panel aus", FocusSummary.Of(new FocusProfile { Trigger = Triggers.Away, Panel = "aus", PanelDelay = 0 }, true, Teams));
        Assert.Equal("Insel aus · Panel aus", FocusSummary.Of(new FocusProfile { HideIsland = true, Panel = "aus" }, true, Teams));
    }
}
