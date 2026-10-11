using System.Collections.ObjectModel;

namespace DynamicBay.Core;

/// <summary>
/// Brings a bound list to a new state with as few changes as possible: unchanged rows stay (no new templates, no
/// layout of the whole page), changed ones are replaced in place. Clear and Add rebuilt every row on each update.
/// </summary>
public static class CollectionSync
{
    /// <returns>Number of changes made (0: the list already matched).</returns>
    public static int Apply<T>(ObservableCollection<T> list, IReadOnlyList<T> target, Func<T, string> key, Func<T, T, bool> same)
    {
        int changes = 0;
        var wanted = new HashSet<string>(target.Select(key));
        for (int i = list.Count - 1; i >= 0; i--)
            if (!wanted.Contains(key(list[i]))) { list.RemoveAt(i); changes++; }
        for (int i = 0; i < target.Count; i++)
        {
            var t = target[i];
            string k = key(t);
            int at = -1;
            for (int j = i; j < list.Count; j++) if (key(list[j]) == k) { at = j; break; }
            if (at < 0) { list.Insert(i, t); changes++; continue; }
            if (at != i) { list.Move(at, i); changes++; }
            if (!same(list[i], t)) { list[i] = t; changes++; }
        }
        return changes;
    }
}
