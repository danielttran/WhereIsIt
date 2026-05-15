using System;
using System.Collections.Generic;

namespace WhereIsIt.App.Services;

/// <summary>
/// In-memory tally of how many times each result row has been opened from
/// WhereIsIt. Path lookup is case-insensitive to match Windows file-system
/// semantics. Persistence lives in <see cref="AppSettingsService"/>.
/// </summary>
public sealed class RunCountService
{
    private readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

    public void Increment(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        // The dictionary's comparer is OrdinalIgnoreCase already, so passing
        // the raw path is enough — no ToLowerInvariant allocation needed.
        counts[path] = counts.TryGetValue(path, out var n) ? n + 1 : 1;
    }

    public int Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        return counts.TryGetValue(path, out var n) ? n : 0;
    }

    public void Load(IDictionary<string, int> data)
    {
        counts.Clear();
        foreach (var (k, v) in data)
        {
            if (string.IsNullOrEmpty(k) || v <= 0) continue;
            counts[k] = v;
        }
    }

    public Dictionary<string, int> Snapshot() => new(counts, StringComparer.OrdinalIgnoreCase);
}
