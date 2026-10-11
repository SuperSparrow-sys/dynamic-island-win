using DynamicBay.Core;
using DynamicBay.Settings;

namespace DynamicBay.Tests;

/// <summary>After an import (no passwords or tokens) or a refused login the account gets "Anmelden", keeping its calendars.</summary>
public class CalendarSignInTests
{
    [Theory]
    [InlineData(CalendarKind.Google, false, "", true)]                                       // imported: no token yet
    [InlineData(CalendarKind.ICloud, false, "", true)]                                       // imported: no password yet
    [InlineData(CalendarKind.Google, true, "Anmeldung abgelaufen, bitte neu anmelden.", true)]
    [InlineData(CalendarKind.ICloud, true, "Anmeldung abgelehnt, bitte Benutzername und App-Passwort prüfen.", true)]
    [InlineData(CalendarKind.ICloud, true, "OK · 4 Termine in 48 h", false)]
    [InlineData(CalendarKind.Google, true, "", false)]                                       // not synced yet, has a token
    [InlineData(CalendarKind.Ics, false, "Server nicht erreichbar.", false)]
    [InlineData(CalendarKind.Microsoft, false, "", false)]
    public void Sign_in_button_only_where_a_sign_in_is_missing_or_refused(CalendarKind kind, bool hasSecret, string status, bool expected) =>
        Assert.Equal(expected, SettingsWindow.NeedsSignIn(kind, hasSecret, status));
}
