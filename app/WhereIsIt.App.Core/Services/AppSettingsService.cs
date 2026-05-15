using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace WhereIsIt.App.Services;

/// <summary>
/// Settings persistence. The in-memory cache is updated synchronously so
/// callers (and any following <see cref="Load"/>) see changes instantly,
/// while the actual disk write for the high-frequency helpers is pushed onto
/// a coalescing background flusher — the UI thread never blocks on file I/O
/// (run-counts fire on every file open, history on every Enter).
///
/// <see cref="Save(AppSettings)"/> and <see cref="SaveLastSessionTabs"/> stay
/// synchronous: they are infrequent and durability-sensitive (settings dialog
/// commit, shutdown tab snapshot).
/// </summary>
public sealed class AppSettingsService : IDisposable
{
    private readonly string settingsPath;
    private readonly object gate = new();

    // In-memory cache of the parsed settings file. SaveXxx methods all need
    // the full current state (settings.json is one document) — without this
    // cache, every Save reloads and re-parses the JSON from disk.
    private AppSettings? cached;

    // Background coalescing flusher. A burst of run-count / history writes
    // collapses into a single debounced disk write off the UI thread.
    private readonly object flushGate = new();
    private Timer? flushTimer;
    private bool dirty;
    private bool disposed;
    private static readonly TimeSpan FlushDebounce = TimeSpan.FromMilliseconds(750);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettingsService(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhereIsIt", "settings.json");
    }

    public AppSettings Load()
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            return Clone(cached);
        }
    }

    private AppSettings LoadFromDisk()
    {
        if (!File.Exists(settingsPath)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch
        {
            // Corrupt JSON would silently wipe bookmarks/history/run-counts/
            // scope-roots if we just returned defaults — move the bad file
            // aside so the user (or a later schema-migrating Load) can recover.
            TryBackupCorruptSettings();
            return new AppSettings();
        }
    }

    private void TryBackupCorruptSettings()
    {
        try
        {
            var bak = settingsPath + ".bak." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(settingsPath, bak);
        }
        catch { /* best-effort; never throw out of Load */ }
    }

    /// <summary>Synchronous, durable full save (settings dialog commit, tests).</summary>
    public void Save(AppSettings settings)
    {
        lock (gate)
        {
            cached = Clone(settings);
            WriteToDisk(cached);
        }
        // We just wrote the complete latest state, so any pending debounced
        // write is now redundant.
        ClearDirty();
    }

    private void WriteToDisk(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(settingsPath)!;
        Directory.CreateDirectory(dir);
        // Atomic write: serialise to a sibling .tmp file, then File.Replace so
        // a crash mid-write doesn't leave a half-written settings.json that
        // would then be backed up as corrupt on next Load.
        var tmp = settingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
        try
        {
            if (File.Exists(settingsPath)) File.Replace(tmp, settingsPath, destinationBackupFileName: null);
            else File.Move(tmp, settingsPath);
        }
        catch
        {
            // Fallback if Replace fails (e.g. cross-volume tmp): plain overwrite.
            try { File.Copy(tmp, settingsPath, overwrite: true); File.Delete(tmp); } catch { }
        }
    }

    private static AppSettings Clone(AppSettings src)
    {
        // Cheap defensive copy so external callers can mutate the returned
        // AppSettings without polluting our cached snapshot before they call
        // Save back, and so the background flusher serialises a consistent
        // snapshot. Collections are short so JSON round-trip is fine.
        var json = JsonSerializer.Serialize(src, JsonOpts);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
    }

    // ── background-flushed, never blocks the caller on disk I/O ──────────

    /// <summary>
    /// Persists just the search-history field, leaving other settings (scope roots
    /// in particular) untouched. Safe to call after every history-modifying event.
    /// </summary>
    public void SaveSearchHistory(string[] history)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.SearchHistory = history;
        }
        ScheduleFlush();
    }

    public void SaveBookmarks(Bookmark[] bookmarks)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.Bookmarks = bookmarks;
        }
        ScheduleFlush();
    }

    public void SaveColumnVisibility(bool showCreated, bool showAccessed, bool showRunCount)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.ShowCreatedColumn  = showCreated;
            cached.ShowAccessedColumn = showAccessed;
            cached.ShowRunCountColumn = showRunCount;
        }
        ScheduleFlush();
    }

    public void SaveRunCounts(System.Collections.Generic.Dictionary<string, int> counts)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.RunCounts = counts;
        }
        ScheduleFlush();
    }

    /// <summary>Synchronous: called once on shutdown where durability matters.</summary>
    public void SaveLastSessionTabs(string[] tabs)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.LastSessionTabs = tabs ?? [];
            WriteToDisk(cached);
        }
        ClearDirty();
    }

    private void ScheduleFlush()
    {
        lock (flushGate)
        {
            if (disposed) return;
            dirty = true;
            // One-shot debounce timer; a burst of writes re-arms it so the
            // whole burst collapses into a single disk write.
            if (flushTimer is null)
                flushTimer = new Timer(_ => FlushIfDirty(), null, FlushDebounce, Timeout.InfiniteTimeSpan);
            else
                flushTimer.Change(FlushDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void ClearDirty()
    {
        lock (flushGate) dirty = false;
    }

    private void FlushIfDirty()
    {
        AppSettings snapshot;
        lock (gate)
        {
            lock (flushGate)
            {
                if (!dirty) return;
                dirty = false;
            }
            if (cached is null) return;
            // Clone under the lock so the snapshot is consistent, but serialise
            // + write to disk OUTSIDE the lock so a slow disk never stalls a
            // UI-thread SaveXxx that just needs to update the cache.
            snapshot = Clone(cached);
        }
        try { WriteToDisk(snapshot); }
        catch { /* best-effort; the next change or Dispose will retry */ }
    }

    public void Dispose()
    {
        lock (flushGate)
        {
            if (disposed) return;
            disposed = true;
            flushTimer?.Dispose();
            flushTimer = null;
        }
        // Final synchronous flush so a debounced write in flight isn't lost on
        // shutdown. Blocking here is acceptable — the app is exiting.
        AppSettings? snapshot = null;
        lock (gate)
        {
            bool needWrite;
            lock (flushGate) { needWrite = dirty; dirty = false; }
            if (needWrite && cached is not null) snapshot = Clone(cached);
        }
        if (snapshot is not null)
        {
            try { WriteToDisk(snapshot); } catch { }
        }
    }
}
