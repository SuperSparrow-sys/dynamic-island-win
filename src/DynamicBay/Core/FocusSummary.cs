namespace DynamicBay.Core;

/// <summary>
/// One line under an automation's name in the settings: what it switches, in the order of the card
/// ("Nicht stören · Insel aus · Teams: Beschäftigt · Panel aus, schnell"). Pure, so it is tested without a window.
/// </summary>
public static class FocusSummary
{
    public static string Of(FocusProfile p, bool panelShown, Func<string, string> teamsLabel)
    {
        bool de = Loc.German;
        var parts = new List<string>();
        if (p.Quiet) parts.Add(de ? "Nicht stören" : "Do not disturb");
        if (p.HideText) parts.Add(de ? "Mitteilungen ohne Text" : "Notifications without text");
        if (p.HideIsland) parts.Add(de ? "Insel aus" : "Island hidden");
        if (p.MutedApps.Count > 0) parts.Add(de ? $"{p.MutedApps.Count} {(p.MutedApps.Count == 1 ? "App" : "Apps")} stumm" : $"{p.MutedApps.Count} muted {(p.MutedApps.Count == 1 ? "app" : "apps")}");
        if (p.HiddenWidgets.Count > 0) parts.Add(de ? $"{p.HiddenWidgets.Count} Widget{(p.HiddenWidgets.Count == 1 ? "" : "s")} aus" : $"{p.HiddenWidgets.Count} widget{(p.HiddenWidgets.Count == 1 ? "" : "s")} hidden");
        if (p.TeamsStatus.Length > 0) parts.Add("Teams: " + teamsLabel(p.TeamsStatus) + (p.TeamsOnlyIfAvailable ? (de ? ", nur statt grün" : ", only instead of green") : ""));
        if (panelShown)
        {
            string? panel = (p.Trigger, p.Panel) switch
            {
                (Triggers.Home, "plan") => de ? "Panel nach Wochenplan" : "panel follows the plan",
                (Triggers.Away, "aus") => (de ? "Panel aus" : "panel off") + (p.PanelDelay > 0 ? (de ? $" nach {p.PanelDelay / 60.0:0.#} min" : $" after {p.PanelDelay / 60.0:0.#} min") : ""),
                (Triggers.Home or Triggers.Away, _) => null,
                (_, "dimmen") => $"Panel {p.PanelPercent} %",
                (_, "aus") => de ? "Panel aus" : "panel off",
                (_, "an") => de ? "Panel an" : "panel on",
                _ => null,
            };
            if (panel is not null) parts.Add(panel + (p.PanelFade > 0 && p.Trigger is not (Triggers.Home or Triggers.Away) ? (de ? ", schnell" : ", quick") : ""));
        }
        return parts.Count == 0 ? (de ? "Schaltet nichts" : "Switches nothing") : string.Join(" · ", parts);
    }
}
