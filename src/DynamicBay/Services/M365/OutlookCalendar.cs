using System.Text.Json.Nodes;

namespace DynamicBay.Services.M365;

/// <summary>Outlook / Exchange calendars of the Microsoft account (all calendars, each with its colour).</summary>
public static class OutlookCalendar
{
    public static async Task<(List<CalendarEvent> events, List<CalendarInfo> calendars)> FetchAsync(
        MicrosoftAccount account, DateTime from, DateTime to, ICollection<string> hidden)
    {
        var cals = await account.GetAsync("me/calendars?$select=id,name,hexColor,isDefaultCalendar&$top=50")
                   ?? throw new InvalidOperationException(Core.Loc.German ? "Microsoft ist nicht verbunden" : "Microsoft is not connected");
        var calendars = (cals["value"]?.AsArray() ?? new JsonArray())
            .Select(c => new CalendarInfo(c!["id"]!.ToString(), c["name"]?.ToString() ?? "", NullIfAuto(c["hexColor"]?.ToString())))
            .ToList();
        string range = $"startDateTime={Uri.EscapeDataString(from.ToUniversalTime().ToString("o"))}&endDateTime={Uri.EscapeDataString(to.ToUniversalTime().ToString("o"))}";
        const string select = "$select=subject,start,end,isAllDay,location,isCancelled,onlineMeeting,onlineMeetingUrl,bodyPreview&$top=250";
        var pages = await Task.WhenAll(calendars.Where(c => !hidden.Contains(c.Id)).Select(async cal =>
            (cal, data: await account.GetAsync($"me/calendars/{Uri.EscapeDataString(cal.Id)}/calendarView?{range}&{select}", "outlook.timezone=\"UTC\""))));
        var events = new List<CalendarEvent>();
        foreach (var (cal, data) in pages)
        {
            foreach (var ev in data?["value"]?.AsArray() ?? new JsonArray())
            {
                if (ev is null || ev["isCancelled"]?.GetValue<bool>() == true) continue;
                bool allDay = ev["isAllDay"]?.GetValue<bool>() == true;
                DateTime s = Time(ev["start"], allDay), e = Time(ev["end"], allDay);
                events.Add(new CalendarEvent
                {
                    Title = ev["subject"]?.ToString() ?? "", Start = s, End = e, AllDay = allDay,
                    Location = ev["location"]?["displayName"]?.ToString(),
                    Color = cal.Color,
                    // Teams meetings carry their join link in onlineMeeting; older events in onlineMeetingUrl or the text.
                    Meeting = MeetingLinks.Find(ev["onlineMeeting"]?["joinUrl"]?.ToString(), ev["onlineMeetingUrl"]?.ToString(),
                                                ev["location"]?["displayName"]?.ToString(), ev["bodyPreview"]?.ToString()),
                });
            }
        }
        return (events, calendars);
    }

    /// <summary>Times come in UTC (Prefer: outlook.timezone="UTC"); all-day events are dates at midnight.</summary>
    private static DateTime Time(JsonNode? t, bool allDay)
    {
        var dt = DateTime.Parse(t?["dateTime"]?.ToString() ?? DateTime.UtcNow.ToString("o"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        return allDay ? dt.Date : DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
    }

    /// <summary>Outlook's "auto" colour has no hex value.</summary>
    private static string? NullIfAuto(string? hex) => string.IsNullOrWhiteSpace(hex) ? null : hex;
}
