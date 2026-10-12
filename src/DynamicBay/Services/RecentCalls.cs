using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>One caller with all their recent calls folded into one line ("3 Anrufe, 1 verpasst").</summary>
public sealed partial class CallEntry : ObservableObject
{
    /// <summary>Name or number as the call notification showed it.</summary>
    public string Who { get; init; } = "";
    /// <summary>Digits and "+" only; empty when the notification had a name without number.</summary>
    public string Number { get; init; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Detail))] private int _count = 1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Detail))] private int _missed;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Detail))] private DateTime _last;
    /// <summary>The last call was missed (an "incoming" and a "missed" notification of one call count once).</summary>
    internal bool LastMissed { get; set; }
    public bool HasNumber => Number.Length > 0;
    public bool IsMissed => Missed > 0;
    /// <summary>"3 Anrufe · 1 verpasst · 14:02" (time of the last one; the weekday when it is older than today).</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (Count > 1) parts.Add(Loc.German ? $"{Count} Anrufe" : $"{Count} calls");
            if (Missed > 0) parts.Add(Loc.German ? $"{Missed} verpasst" : $"{Missed} missed");
            parts.Add(Last.Date == DateTime.Today ? Last.ToString("HH:mm") : Last.ToString("ddd HH:mm", Loc.German ? new System.Globalization.CultureInfo("de-DE") : System.Globalization.CultureInfo.InvariantCulture));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// The last calls of the linked phone, one line per caller, to call back from the "Kontakte" widget. Read from the call
/// notifications Smartphone-Link shows, so it needs no Microsoft account; kept on this PC only (calls.json).
/// </summary>
public sealed partial class RecentCalls : ObservableObject
{
    private static string FilePath => Path.Combine(AppSettings.Folder, "calls.json");
    private const int Max = 12;
    private bool _demo;

    public ObservableCollection<CallEntry> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty = true;

    public RecentCalls(bool load = true)
    {
        Items.CollectionChanged += (_, _) => IsEmpty = Items.Count == 0;
        if (load) Load();
    }

    /// <summary>A call notification arrived: true when it was a call and was added.</summary>
    public bool Add(NotificationItem n)
    {
        if (ParseCall(n.AppId, n.Title, n.Body) is not { } call) return false;
        Record(call.Who, call.Number, call.Missed, n.Time == default ? DateTime.Now : n.Time);
        Save();
        return true;
    }

    /// <summary>Folds the call into the caller's line (same number, else same name) and moves it to the top.</summary>
    internal void Record(string who, string number, bool missed, DateTime time)
    {
        var e = Items.FirstOrDefault(c => number.Length > 0 ? SameNumber(c.Number, number) : c.Number.Length == 0 && string.Equals(c.Who, who, StringComparison.OrdinalIgnoreCase));
        if (e is null)
        {
            e = new CallEntry { Who = who, Number = number, Count = 0, Last = time };
        }
        else
        {
            // The same toast is read once per id, but a ringing call can come as "incoming" and then "missed" a moment later.
            if ((time - e.Last).Duration() < TimeSpan.FromSeconds(90) && e.Count > 0)
            {
                if (missed && !e.LastMissed) { e.Missed++; e.LastMissed = true; }
                MoveTop(e);
                return;
            }
            Items.Remove(e);
        }
        e.Count++;
        if (missed) e.Missed++;
        e.LastMissed = missed;
        if (time > e.Last) e.Last = time;
        Items.Insert(0, e);
        while (Items.Count > Max) Items.RemoveAt(Items.Count - 1);
    }

    private void MoveTop(CallEntry e)
    {
        int i = Items.IndexOf(e);
        if (i > 0) Items.Move(i, 0);
    }

    /// <summary>+49 176 … and 0176 … are the same number.</summary>
    internal static bool SameNumber(string a, string b)
    {
        static string Core(string n) => n.StartsWith("+49") ? "0" + n[3..] : n.StartsWith("0049") ? "0" + n[4..] : n;
        return a.Length > 0 && Core(a) == Core(b);
    }

    private static readonly string[] MissedWords = { "verpasst", "missed", "entgangen" };
    private static readonly string[] CallWords = { "anruf", "call", "ruft an", "calling" };

    /// <summary>
    /// A phone call notification of Smartphone-Link: the caller (name or number), the number when there is one, and
    /// whether it was missed. Null for everything else (also Teams calls: they are called back in Teams).
    /// </summary>
    internal static (string Who, string Number, bool Missed)? ParseCall(string appId, string title, string body)
    {
        if (!appId.Contains("YourPhone", StringComparison.OrdinalIgnoreCase)) return null;
        var texts = new[] { title }.Concat(body.Split('\n')).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        bool calling = appId.Contains("YourPhoneCalling", StringComparison.OrdinalIgnoreCase);
        bool mentionsCall = texts.Any(t => CallWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));
        if (!calling && !mentionsCall) return null;
        bool missed = texts.Any(t => MissedWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));
        // A phone app's own notification that only mentions a call ("Anruf in Abwesenheit" from WhatsApp …) has no number: not ours.
        string number = texts.Select(NumberIn).FirstOrDefault(n => n.Length > 0) ?? "";
        if (!calling && number.Length == 0 && !missed) return null;
        string who = texts.FirstOrDefault(t => !CallWords.Concat(MissedWords).Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase))) ?? number;
        if (who.Length == 0) return null;
        return (who, number, missed);
    }

    [GeneratedRegex(@"\+?\d[\d \-/()]{5,}\d")]
    private static partial Regex NumberPattern();

    /// <summary>The phone number in a text, digits and a leading "+" only (empty when there is none).</summary>
    internal static string NumberIn(string text)
    {
        var m = NumberPattern().Match(text);
        if (!m.Success) return "";
        string n = new string(m.Value.Where(ch => char.IsDigit(ch) || ch == '+').ToArray());
        return n.Count(char.IsDigit) >= 6 ? n : "";
    }

    /// <summary>
    /// Calls back through the app Windows uses for phone numbers (Smartphone-Link or any other "tel:" app); without a
    /// number Smartphone-Link opens on its calls.
    /// </summary>
    [RelayCommand]
    private void CallBack(CallEntry? c)
    {
        if (c is null) return;
        string target = c.HasNumber ? "tel:" + c.Number : @"shell:AppsFolder\Microsoft.YourPhone_8wekyb3d8bbwe!App";
        try
        {
            if (c.HasNumber) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else Process.Start("explorer.exe", target);
        }
        catch (Exception ex) { Log.Error("Call back", ex); }
    }

    private sealed record Stored(string Who, string Number, int Count, int Missed, DateTime Last);

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var s in JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(FilePath)) ?? new())
                if (!string.IsNullOrWhiteSpace(s.Who))
                    Items.Add(new CallEntry { Who = s.Who, Number = s.Number ?? "", Count = Math.Max(1, s.Count), Missed = s.Missed, Last = s.Last });
        }
        catch (Exception ex) { Log.Error("Calls load", ex); }
    }

    private void Save()
    {
        if (_demo) return;
        try
        {
            var list = Items.Select(c => new Stored(c.Who, c.Number, c.Count, c.Missed, c.Last)).ToList();
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }), backup: false);
        }
        catch (Exception ex) { Log.Error("Calls save", ex); }
    }

    /// <summary>For design snapshots only.</summary>
    public void LoadDemo()
    {
        _demo = true;
        Items.Clear();
        Items.Add(new CallEntry { Who = "+49 176 41511149", Number = "+4917641511149", Count = 5, Missed = 2, Last = DateTime.Today.AddHours(19.7) });
        Items.Add(new CallEntry { Who = "Chung", Number = "", Count = 1, Last = DateTime.Today.AddDays(-2).AddHours(11) });
    }
}
