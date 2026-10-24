namespace DynamicBay.Core;

/// <summary>
/// One line for a collapsed focus card in the settings: what this focus switches, in the order of the card
/// ("Nicht stören · Insel aus · Teams: Beschäftigt · Panel aus, beim Ende aus"). Pure, so it is tested without a window.
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
        if (p.TeamsStatus.Length > 0) parts.Add("Teams: " + teamsLabel(p.TeamsStatus));
        if (panelShown)
        {
            string? during = p.Panel switch
            {
                "dimmen" => de ? $"Panel {p.PanelPercent} %" : $"Panel {p.PanelPercent} %",
                "aus" => de ? "Panel aus" : "Panel off",
                _ => null,
            };
            string? end = p.PanelEnd == "aus" ? (de ? "Panel aus, wenn er endet" : "panel off when it ends") : null;
            if (during is not null && end is not null) parts.Add(during + (de ? ", aus, wenn er endet" : ", off when it ends"));
            else if (during is not null) parts.Add(during);
            else if (end is not null) parts.Add(end);
        }
        return parts.Count == 0 ? (de ? "Schaltet nichts" : "Switches nothing") : string.Join(" · ", parts);
    }
}
