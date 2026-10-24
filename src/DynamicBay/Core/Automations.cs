namespace DynamicBay.Core;

/// <summary>Turning the focus profiles of 1.1.12–1.1.16 into automations (settings v13). Pure, so it is tested without a window.</summary>
public static class Automations
{
    /// <summary>
    /// The profile named like the presence report ("Zuhause") becomes "Zu Hause": its own switches stay, the panel
    /// follows the weekly plan; an "off when it ends" becomes the automation "Unterwegs" (panel off). A camera
    /// automation is added when there is none. Everything else stays an iPhone focus, in its order.
    /// </summary>
    public static void Migrate(AppSettings s)
    {
        var home = s.FocusProfiles.FirstOrDefault(p => p.Trigger == Triggers.Focus && s.IsPresenceName(p.Name));
        if (home is not null && !s.FocusProfiles.Any(p => p.Trigger == Triggers.Home))
        {
            bool offWhenGone = home.PanelEnd == "aus";
            home.Trigger = Triggers.Home;
            home.Name = Loc.German ? "Zu Hause" : "At home";
            home.Panel = offWhenGone || home.Panel == "plan" ? "plan" : "";
            home.PanelEnd = "";
            int at = s.FocusProfiles.IndexOf(home);
            if (offWhenGone && !s.FocusProfiles.Any(p => p.Trigger == Triggers.Away))
                s.FocusProfiles.Insert(at + 1, new FocusProfile { Name = Loc.German ? "Unterwegs" : "Away", Trigger = Triggers.Away, Panel = "aus" });
        }
        if (!s.FocusProfiles.Any(p => p.Trigger == Triggers.Camera))
        {
            int at = s.FocusProfiles.TakeWhile(p => p.Trigger is Triggers.Home or Triggers.Away).Count();
            s.FocusProfiles.Insert(at, new FocusProfile
            {
                Name = Loc.German ? "Kamera" : "Camera", Trigger = Triggers.Camera,
                Panel = s.AnzeigetafelEnabled ? "aus" : "", PanelFade = 10,
            });
        }
    }
}
