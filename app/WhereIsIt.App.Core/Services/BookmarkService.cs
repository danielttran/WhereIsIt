using System.Collections.Generic;
using System.Linq;

namespace WhereIsIt.App.Services;

public sealed record Bookmark(string Name, string Query);

/// <summary>
/// Named saved-query catalogue. Names are case-sensitive and unique;
/// re-adding an existing name overwrites the stored query.
/// </summary>
public sealed class BookmarkService
{
    private readonly List<Bookmark> items = new();

    public IReadOnlyList<Bookmark> Items => items;

    public void Add(string name, string query)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        var idx = items.FindIndex(b => b.Name == trimmed);
        var entry = new Bookmark(trimmed, query ?? string.Empty);
        if (idx >= 0) items[idx] = entry;
        else items.Add(entry);
    }

    public void Remove(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        items.RemoveAll(b => b.Name == trimmed);
    }

    public void Load(IEnumerable<Bookmark> entries)
    {
        items.Clear();
        foreach (var b in entries)
        {
            if (b is null || string.IsNullOrWhiteSpace(b.Name)) continue;
            items.Add(b);
        }
    }

    public Bookmark[] Snapshot() => items.ToArray();
}
