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
        // Times in the PC's own zone (Graph takes Windows zone names): all-day events then keep their date.
        string prefer = $"outlook.timezone=\"{TimeZoneInfo.Local.Id}\"";
        var pages = await Task.WhenAll(calendars.Where(c => !hidden.Contains(c.Id)).Select(async cal =>
            (cal, data: await account.GetAsync($"me/calendars/{Uri.EscapeDataString(cal.Id)}/calendarView?{range}&{select}", prefer))));
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

    /// <summary>
    /// Times come in local time (Prefer: outlook.timezone = the PC's zone); all-day events take the date as written
    /// ("2026-10-12T00:00:00"), never shifted by a time zone.
    /// </summary>
    internal static DateTime Time(JsonNode? t, bool allDay)
    {
        string text = t?["dateTime"]?.ToString() ?? "";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (allDay && text.Length >= 10 && DateTime.TryParseExact(text[..10], "yyyy-MM-dd", inv, System.Globalization.DateTimeStyles.None, out var day))
            return day;
        if (!DateTime.TryParse(text, inv, System.Globalization.DateTimeStyles.None, out var dt)) return DateTime.Now;
        // A zone the PC does not share (only if Graph ignored the Prefer header): convert from it.
        if (t?["timeZone"]?.ToString() is { Length: > 0 } zone && zone != TimeZoneInfo.Local.Id && dt.Kind == DateTimeKind.Unspecified)
        {
            try { return TimeZoneInfo.ConvertTime(dt, TimeZoneInfo.FindSystemTimeZoneById(zone), TimeZoneInfo.Local); }
            catch { }
        }
        return allDay ? dt.Date : DateTime.SpecifyKind(dt, DateTimeKind.Local);
    }

    /// <summary>Outlook's "auto" colour has no hex value.</summary>
    private static string? NullIfAuto(string? hex) => string.IsNullOrWhiteSpace(hex) ? null : hex;
}
