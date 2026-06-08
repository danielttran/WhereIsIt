using System;
using System.Collections.Generic;

namespace WhereIsIt.App.Services;

/// <summary>
/// In-memory tally of how many times each result row has been opened from
/// WhereIsIt, plus the last time each was opened. Path lookup is
/// case-insensitive to match Windows file-system semantics. Persistence lives
/// in <see cref="AppSettingsService"/>.
///
/// Reads (<see cref="Get"/>/<see cref="GetLastRun"/>) run on the engine's
/// result-watcher thread for <c>rc:</c>/<c>dr:</c> filtering while
/// <see cref="Increment"/> runs on the UI thread, so all access is guarded.
/// </summary>
public sealed class RunCountService
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
    // Last-run timestamps as Win32 UTC FILETIME-style DateTimeOffsets, kept in a
    // parallel map so the run-date filter (dr:) has data without a row schema change.
    private readonly Dictionary<string, DateTimeOffset> lastRun = new(StringComparer.OrdinalIgnoreCase);

    public void Increment(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (gate)
        {
            counts[path] = counts.TryGetValue(path, out var n) ? n + 1 : 1;
            lastRun[path] = DateTimeOffset.UtcNow;
        }
    }

    public int Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        lock (gate) return counts.TryGetValue(path, out var n) ? n : 0;
    }

    /// <summary>Last time the path was opened from WhereIsIt, or
    /// <c>default</c> if it has never been opened.</summary>
    public DateTimeOffset GetLastRun(string path)
    {
        if (string.IsNullOrEmpty(path)) return default;
        lock (gate) return lastRun.TryGetValue(path, out var d) ? d : default;
    }

    public void Load(IDictionary<string, int> data)
    {
        lock (gate)
        {
            counts.Clear();
            foreach (var (k, v) in data)
            {
                if (string.IsNullOrEmpty(k) || v <= 0) continue;
                counts[k] = v;
            }
        }
    }

    /// <summary>Restores persisted last-run timestamps (stored as UTC ticks).</summary>
    public void LoadRunDates(IDictionary<string, long> data)
    {
        lock (gate)
        {
            lastRun.Clear();
            foreach (var (k, ticks) in data)
            {
                if (string.IsNullOrEmpty(k) || ticks <= 0) continue;
                try { lastRun[k] = new DateTimeOffset(ticks, TimeSpan.Zero); }
                catch { /* skip out-of-range ticks */ }
            }
        }
    }

    public Dictionary<string, int> Snapshot()
    {
        lock (gate) return new(counts, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Snapshot of last-run timestamps as UTC ticks for persistence.</summary>
    public Dictionary<string, long> SnapshotRunDates()
    {
        lock (gate)
        {
            var copy = new Dictionary<string, long>(lastRun.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in lastRun) copy[k] = v.UtcDateTime.Ticks;
            return copy;
        }
    }
}
