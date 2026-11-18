using System.Globalization;
using System.Text;

namespace DynamicBay.Core;

/// <summary>One name a search entry can be found by, prepared once: normalized text, its words and their initials.</summary>
public sealed record SearchKey(string Text, string[] Words, string Initials)
{
    public static SearchKey Of(string name)
    {
        var words = SearchMath.Words(name);
        return new SearchKey(SearchMath.Normalize(name), words, string.Concat(words.Select(w => w[0])));
    }
}

/// <summary>Something the search can find: an app, a settings page, a system tool. <see cref="Id"/> counts launches.</summary>
public sealed record SearchCandidate(string Id, string Name, SearchKey[] Keys)
{
    public static SearchCandidate Of(string id, string name, params string[] alsoKnownAs) =>
        new(id, name, alsoKnownAs.Prepend(name).Where(n => n.Length > 0).Select(SearchKey.Of).ToArray());
}

/// <summary>A line of the task table: phrases (normalized) that stand for the apps in <see cref="Targets"/> (normalized).</summary>
public sealed record SynonymRule(string[] Phrases, string[] Targets);

/// <summary>
/// The search's rules without a window: matching names (start, word starts, initials, typos), the task table
/// ("bild bearbeiten" → Paint) and the rank with launches counted locally.
/// </summary>
public static class SearchMath
{
    /// <summary>Lower case without accents (ä → a, ß → ss), anything not a letter or digit becomes one space.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Replace("ß", "ss").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Words of a name, also split where a capital follows a small letter ("ModbusMaster" → modbus, master).</summary>
    public static string[] Words(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) sb.Append(' ');
            sb.Append(name[i]);
        }
        return Normalize(sb.ToString()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// How well <paramref name="query"/> (normalized) fits a name: 1000 exact, 900 start of the name, 800 start of a word,
    /// 760 every query word starts a word, 700 initials ("vsc"), 500 inside, below 400 with a typo; 0 = no match.
    /// </summary>
    public static int Score(string query, SearchKey key)
    {
        if (query.Length == 0 || key.Text.Length == 0) return 0;
        if (key.Text == query) return 1000;
        if (key.Text.StartsWith(query, StringComparison.Ordinal)) return 900 - Math.Min(key.Text.Length - query.Length, 50);
        var parts = query.Split(' ');
        if (parts.Length == 1 && key.Words.Any(w => w.StartsWith(query, StringComparison.Ordinal))) return 800;
        int prefix = 0, fuzzy = 0;
        foreach (var part in parts)
        {
            int best = key.Words.Length == 0 ? 0 : key.Words.Max(w => WordMatch(part, w));
            if (best == 0) { prefix = fuzzy = -1; break; }
            if (best == 2) prefix++; else fuzzy++;
        }
        if (parts.Length > 1 && prefix == parts.Length) return 760;
        string compact = query.Replace(" ", "");
        if (compact.Length >= 2 && key.Initials.Length >= 2 && key.Initials.StartsWith(compact, StringComparison.Ordinal)) return 700;
        if (query.Length >= 3 && (key.Text.Contains(query, StringComparison.Ordinal) || key.Text.Replace(" ", "").Contains(compact, StringComparison.Ordinal))) return 500;
        if (fuzzy > 0) return 400 - 40 * fuzzy;
        return 0;
    }

    /// <summary>2: the word starts with the part; 1: it does with a typo (parts of 4 letters and more); 0: no.</summary>
    private static int WordMatch(string part, string word)
    {
        if (word.StartsWith(part, StringComparison.Ordinal)) return 2;
        if (part.Length < 4) return 0;
        int allowed = part.Length >= 7 ? 2 : 1;
        // Against the word's start one letter shorter or longer too: "spotfy" is "spotify" with one letter missing.
        for (int len = part.Length - 1; len <= part.Length + 1; len++)
            if (len <= word.Length && len >= 3 && Distance(part, word[..len]) <= allowed) return 1;
        return 0;
    }

    /// <summary>Edits between two strings: insert, delete, replace, swap two neighbours (optimal string alignment).</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "documentation", "dokumentation", "readme", "faq", "faqs", "help", "hilfe", "manual", "handbuch", "benutzerhandbuch",
        "anwenderhandbuch", "website", "forums", "wiki",
    };
    private static readonly string[] NoisePhrases = { "release notes", "revision history", "on the web", "im internet", "example scripts", "was ist neu" };

    /// <summary>Start menu entries that are documents, not programs ("VPN Documentation", "Git Release Notes"): left out of the search.</summary>
    public static bool IsNoise(string name)
    {
        var words = Words(name);
        if (words.Any(NoiseWords.Contains)) return true;
        string text = " " + string.Join(' ', words) + " ";
        return NoisePhrases.Any(p => text.Contains(" " + p + " ", StringComparison.Ordinal));
    }

    // ---------- task table ----------

    /// <summary>
    /// Reads the table: one line per task, "phrase, phrase = App; App"; # starts a comment. Lines without "=" or
    /// without apps are skipped.
    /// </summary>
    public static List<SynonymRule> ParseSynonyms(string text)
    {
        var rules = new List<SynonymRule>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var phrases = line[..eq].Split(',').Select(Normalize).Where(p => p.Length > 0).Distinct().ToArray();
            var targets = line[(eq + 1)..].Split(';').Select(Normalize).Where(t => t.Length > 0).Distinct().ToArray();
            if (phrases.Length > 0 && targets.Length > 0) rules.Add(new SynonymRule(phrases, targets));
        }
        return rules;
    }

    /// <summary>How well a query fits a phrase of the table: 650 equal, 620 the query holds it, 560 typed so far, 450 typo.</summary>
    public static int PhraseScore(string query, string phrase)
    {
        if (query == phrase) return 650;
        var words = query.Split(' ');
        if (phrase.Split(' ').All(p => words.Contains(p))) return 620;
        if (query.Length >= 3 && phrase.StartsWith(query, StringComparison.Ordinal)) return 560;
        if (query.Length >= 5 && Distance(query, phrase) <= (phrase.Length >= 8 ? 2 : 1)) return 450;
        return 0;
    }

    /// <summary>Apps the table offers for a query, best first: (normalized name start, score). Earlier apps in a line rank higher.</summary>
    public static List<(string Target, int Score)> Expand(string query, IReadOnlyList<SynonymRule> rules)
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            int score = rule.Phrases.Max(p => PhraseScore(query, p));
            if (score == 0) continue;
            for (int i = 0; i < rule.Targets.Length; i++)
            {
                int s = score - i;
                if (!found.TryGetValue(rule.Targets[i], out int had) || had < s) found[rule.Targets[i]] = s;
            }
        }
        return found.Select(kv => (kv.Key, kv.Value)).OrderByDescending(t => t.Value).ToList();
    }

    /// <summary>A table entry names an app by the start of its name, whole words only: "python" is "Python 3.13", not "IDLE (Python 3.13)".</summary>
    public static bool TargetMatches(string target, SearchKey key) =>
        key.Text == target || key.Text.StartsWith(target + " ", StringComparison.Ordinal);

    // ---------- rank ----------

    /// <summary>Launched often, ranked higher: about +40 at 1 launch, +120 at 7, at most +150.</summary>
    public static int Boost(int launches) => launches <= 0 ? 0 : (int)Math.Min(150, 40 * Math.Log2(1 + launches));

    /// <summary>The best <paramref name="max"/> candidates for a query, by name, by the task table and by launches.</summary>
    public static List<(SearchCandidate Item, int Score)> Rank(string query, IReadOnlyList<SearchCandidate> items,
        IReadOnlyList<SynonymRule> rules, Func<string, int> launches, int max)
    {
        string q = Normalize(query);
        if (q.Length == 0) return new();
        var targets = Expand(q, rules);
        var hits = new List<(SearchCandidate, int)>();
        foreach (var item in items)
        {
            int best = 0;
            foreach (var key in item.Keys)
            {
                best = Math.Max(best, Score(q, key));
                foreach (var (target, score) in targets)
                    if (score > best && TargetMatches(target, key)) best = score;
            }
            if (best > 0) hits.Add((item, best + Boost(launches(item.Id))));
        }
        return hits.OrderByDescending(h => h.Item2).ThenBy(h => h.Item1.Name.Length).ThenBy(h => h.Item1.Name, StringComparer.CurrentCultureIgnoreCase)
                   .Take(max).ToList();
    }
}
