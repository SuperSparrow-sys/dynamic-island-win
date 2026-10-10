using System.IO;
using System.Net;
using System.Text.Json.Nodes;
using DynamicBay.Services;
using DynamicBay.Services.Calendars;
using DynamicBay.Services.M365;
using DynamicBay.Services.Scripting;

namespace DynamicBay.Tests;

/// <summary>Scripts reach the internet, never this PC or the local network.</summary>
public class ScriptNetworkGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.9.10")]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.178.1")]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("100.64.0.1")]      // carrier-grade NAT (Tailscale)
    [InlineData("100.127.255.255")]
    [InlineData("224.0.0.251")]     // multicast
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")] // IPv4 inside IPv6
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("2002:c0a8:0101::1")] // 6to4 of 192.168.1.1
    [InlineData("64:ff9b::a00:1")]    // NAT64 of 10.0.0.1
    public void Blocks_local_and_private_addresses(string ip) => Assert.True(ScriptEngine.IsBlockedAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]   // just outside 172.16/12
    [InlineData("100.128.0.1")]  // just outside 100.64/10
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Allows_public_addresses(string ip) => Assert.False(ScriptEngine.IsBlockedAddress(IPAddress.Parse(ip)));
}

/// <summary>What reaches a command line or the shell from hooks and the synced folder.</summary>
public class ClaudeInputTests
{
    [Theory]
    [InlineData("8a6be841-6f22-40ad-87ff-7d8b7f489572", true)]
    [InlineData("8A6BE841-6F22-40AD-87FF-7D8B7F489572", true)]
    [InlineData("x & powershell -c calc", false)]
    [InlineData("8a6be841-6f22-40ad-87ff-7d8b7f489572 & calc", false)]
    [InlineData("8a6be8416f2240ad87ff7d8b7f489572", false)] // only the dashed form
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Session_ids_must_be_guids(string? id, bool ok) => Assert.Equal(ok, ClaudeService.IsSessionId(id));

    [Theory]
    [InlineData("https://claude.ai/code/session_01AbC-x_9", true)]
    [InlineData("https://claude.ai/code/session_X/sub", true)]
    [InlineData("http://claude.ai/code/session_X", false)]
    [InlineData("https://claude.ai.evil.com/code/x", false)]
    [InlineData("https://claude.ai/code/x?y=1", false)]
    [InlineData("https://claude.ai/code/x\n", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("C:\\Windows\\System32\\calc.exe", false)]
    [InlineData(null, false)]
    public void Remote_links_only_to_claude_ai_code(string? url, bool ok) => Assert.Equal(ok, ClaudeService.SafeRemoteUrl(url) is not null);

    [Fact]
    public void Hook_folders_without_shell_characters_only()
    {
        string temp = Path.GetTempPath();
        Assert.Equal(temp, ClaudeService.SafeCwd(temp));
        Assert.Equal("", ClaudeService.SafeCwd(temp + "\" & calc & \""));
        Assert.Equal("", ClaudeService.SafeCwd("C:\\does-not-exist-" + Guid.NewGuid().ToString("N")));
        Assert.Equal("", ClaudeService.SafeCwd(null));
    }
}

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.0.16", "1.0.15", true)]
    [InlineData("1.0.16", "1.0.16-beta", true)]   // the release beats its pre-release
    [InlineData("1.0.16-beta", "1.0.16", false)]
    [InlineData("1.0.16-beta.2", "1.0.16-beta.1", true)]
    [InlineData("1.0.16-rc.1", "1.0.16-beta.9", true)]
    [InlineData("1.0.16-beta", "1.0.15", true)]
    [InlineData("1.0.16", "1.0.16", false)]
    [InlineData("1.0.16", "1.0.16.0", false)]      // missing parts count as 0
    [InlineData("1.0.16.1", "1.0.16", true)]       // four parts
    [InlineData("1.0.16.2", "1.0.16.10", false)]
    [InlineData("1.0.16+build.5", "1.0.16", false)] // build metadata is ignored
    [InlineData("garbage", "1.0.0", false)]
    public void Compares_versions(string candidate, string current, bool newer) => Assert.Equal(newer, UpdateCheck.IsNewer(candidate, current));
}

/// <summary>All-day events and due dates keep their day in every time zone.</summary>
public class DateFixTests
{
    [Fact]
    public void All_day_event_keeps_the_date_from_the_string()
    {
        var start = JsonNode.Parse("""{ "dateTime": "2026-10-12T00:00:00.0000000", "timeZone": "UTC" }""");
        Assert.Equal(new DateTime(2026, 10, 12), OutlookCalendar.Time(start, allDay: true));
    }

    [Fact]
    public void Timed_event_in_the_local_zone_stays_as_written()
    {
        var start = JsonNode.Parse($$"""{ "dateTime": "2026-10-12T09:30:00.0000000", "timeZone": "{{TimeZoneInfo.Local.Id}}" }""");
        var t = OutlookCalendar.Time(start, allDay: false);
        Assert.Equal(new DateTime(2026, 10, 12, 9, 30, 0), t);
        Assert.Equal(DateTimeKind.Local, t.Kind);
    }

    [Fact]
    public void Due_date_at_midnight_is_that_day()
    {
        var due = JsonNode.Parse("""{ "dateTime": "2026-10-12T00:00:00.0000000", "timeZone": "UTC" }""");
        Assert.Equal(new DateTime(2026, 10, 12), TodoService.DueDate(due));
    }

    [Fact]
    public void Due_date_with_a_time_is_converted_to_local()
    {
        var due = JsonNode.Parse("""{ "dateTime": "2026-10-12T12:00:00.0000000", "timeZone": "UTC" }""");
        var expected = DateTime.SpecifyKind(new DateTime(2026, 10, 12, 12, 0, 0), DateTimeKind.Utc).ToLocalTime().Date;
        Assert.Equal(expected, TodoService.DueDate(due));
        Assert.Null(TodoService.DueDate(null));
    }
}

/// <summary>The CalDAV password only goes to the server (and iCloud's partitions), only over https.</summary>
public class CalDavCredentialTests
{
    [Theory]
    [InlineData("https://caldav.icloud.com/", "https://caldav.icloud.com/123/principal/", true)]
    [InlineData("https://caldav.icloud.com/", "https://p42-caldav.icloud.com:443/123/calendars/", true)]
    [InlineData("https://caldav.icloud.com/", "https://evil.example.com/steal", false)]
    [InlineData("https://caldav.icloud.com/", "https://icloud.com.evil.net/", false)]
    [InlineData("https://caldav.icloud.com/", "http://caldav.icloud.com/", false)]
    [InlineData("http://dav.example.org/", "http://dav.example.org/cal/", false)]
    [InlineData("https://example.com/dav/", "https://other.com/", false)]
    [InlineData("https://example.com/dav/", "https://example.com/dav/cal/", true)]
    public void Sends_credentials_only_where_it_should(string server, string target, bool ok) =>
        Assert.Equal(ok, CalDavClient.MaySendCredentials(new Uri(server), new Uri(target)));
}
