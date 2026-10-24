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
        var p = new FocusProfile { Name = "Schule", Quiet = true, HideText = true, TeamsStatus = "Busy", Panel = "dimmen", PanelPercent = 25, PanelEnd = "aus" };
        p.MutedApps.Add("WhatsApp");
        p.HiddenWidgets.Add("media");
        p.HiddenWidgets.Add("calendar");
        Assert.Equal("Nicht stören · Mitteilungen ohne Text · 1 App stumm · 2 Widgets aus · Teams: Beschäftigt · Panel 25 %, aus, wenn er endet",
            FocusSummary.Of(p, panelShown: true, Teams));
    }

    [Fact]
    public void Leaves_out_the_board_when_it_is_not_set_up_and_says_when_nothing_happens()
    {
        var home = new FocusProfile { Name = "Zuhause", PanelEnd = "aus" };
        Assert.Equal("Panel aus, wenn er endet", FocusSummary.Of(home, panelShown: true, Teams));
        Assert.Equal("Schaltet nichts", FocusSummary.Of(home, panelShown: false, Teams));
        Assert.Equal("Insel aus · Panel aus", FocusSummary.Of(new FocusProfile { HideIsland = true, Panel = "aus" }, true, Teams));
    }
}
