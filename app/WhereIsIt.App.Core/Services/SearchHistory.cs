using System.Collections.Generic;
using System.Linq;

namespace WhereIsIt.App.Services;

/// <summary>
/// In-memory MRU list of recent search queries with a cursor for up/down recall.
/// Newest entry is at index 0. Adding an existing entry moves it to the top
/// (no consecutive duplicates).
/// </summary>
public sealed class SearchHistory
{
    private readonly List<string> entries = new();
    private readonly int maxEntries;
    private int cursor = -1;   // -1 = no recall in progress

    public SearchHistory(int maxEntries = 50)
    {
        this.maxEntries = maxEntries < 1 ? 1 : maxEntries;
    }

    public IReadOnlyList<string> Entries => entries;

    public void Load(IEnumerable<string> existing)
    {
        entries.Clear();
        foreach (var s in existing)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (entries.Count >= maxEntries) break;
            entries.Add(s);
        }
        cursor = -1;
    }

    public void Add(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        var idx = entries.FindIndex(e => e == query);
        if (idx >= 0) entries.RemoveAt(idx);

        entries.Insert(0, query);

        while (entries.Count > maxEntries)
            entries.RemoveAt(entries.Count - 1);

        cursor = -1;
    }

    /// <summary>
    /// Moves the cursor one step toward older entries and returns that entry.
    /// Returns null when the history is empty.
    /// </summary>
    public string? RecallPrev()
    {
        if (entries.Count == 0) return null;
        if (cursor + 1 < entries.Count) cursor++;
        return entries[cursor];
    }

    /// <summary>
    /// Moves the cursor toward newer entries; returns null once past newest.
    /// </summary>
    public string? RecallNext()
    {
        if (entries.Count == 0 || cursor <= 0)
        {
            cursor = -1;
            return null;
        }
        cursor--;
        return entries[cursor];
    }

    public void ResetCursor() => cursor = -1;

    public string[] Snapshot() => entries.ToArray();
}
