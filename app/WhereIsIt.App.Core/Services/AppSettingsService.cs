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
    private readonly object writeGate = new();

    // In-memory cache of the parsed settings file. SaveXxx methods all need
    // the full current state (settings.json is one document) — without this
    // cache, every Save reloads and re-parses the JSON from disk.
    private AppSettings? cached;
    private long revision;

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
        catch (JsonException)
        {
            // Malformed JSON would silently wipe bookmarks/history/run-counts/
            // scope-roots if we just returned defaults — move the bad file
            // aside so the user (or a later schema-migrating Load) can recover.
            // I/O failures intentionally escape: treating an access or sharing
            // failure as corruption could later overwrite a valid settings file.
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
            var currentRevision = ++revision;
            WriteToDisk(cached, currentRevision);
            // Clear while still holding `gate`: a partial SaveXxx must not
            // update the cache between the durable write and this reset.
            ClearDirty();
        }
    }

    private void WriteToDisk(AppSettings settings, long snapshotRevision)
    {
        // Debounced writes intentionally clone outside `gate` so callers never
        // block on disk I/O. Serialise the actual file swap separately: without
        // this lock a background flush and a synchronous Save could race over
        // the same .tmp path and allow an older snapshot to win.
        lock (writeGate)
        {
            // A newer cache snapshot may have been committed while this
            // background write waited for the file lock. Never let stale data
            // overwrite the newer durable save.
            if (snapshotRevision != Interlocked.Read(ref revision)) return;

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
                // Fallback if Replace fails (e.g. filesystem limitation): plain overwrite.
                // Let Copy failures escape so synchronous saves report failure
                // and background saves can retain their dirty state. Cleanup is
                // best-effort once the destination contains the complete JSON.
                File.Copy(tmp, settingsPath, overwrite: true);
                try { File.Delete(tmp); } catch { }
            }
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
            revision++;
        }
        ScheduleFlush();
    }

    public void SaveBookmarks(Bookmark[] bookmarks)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.Bookmarks = bookmarks;
            revision++;
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
            revision++;
        }
        ScheduleFlush();
    }

    public void SaveRunCounts(System.Collections.Generic.Dictionary<string, int> counts)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.RunCounts = counts;
            revision++;
        }
        ScheduleFlush();
    }

    public void SaveRunDates(System.Collections.Generic.Dictionary<string, long> dates)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.RunDates = dates;
            revision++;
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
            var currentRevision = ++revision;
            WriteToDisk(cached, currentRevision);
            ClearDirty();
        }
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
        long snapshotRevision;
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
            snapshotRevision = revision;
        }
        try { WriteToDisk(snapshot, snapshotRevision); }
        catch
        {
            // Preserve durability intent: Dispose() retries the newest dirty
            // snapshot even if no further user action occurs after this fault.
            lock (flushGate) dirty = true;
        }
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
        // Always write the latest cached snapshot synchronously. A timer callback
        // may already have claimed `dirty` and be blocked in WriteToDisk, so checking
        // only the flag can let Dispose return before the newest state is durable.
        // Blocking here is acceptable — the app is exiting.
        AppSettings? snapshot = null;
        long snapshotRevision = 0;
        lock (gate)
        {
            lock (flushGate) dirty = false;
            if (cached is not null)
            {
                snapshot = Clone(cached);
                snapshotRevision = revision;
            }
        }
        if (snapshot is not null)
        {
            try { WriteToDisk(snapshot, snapshotRevision); } catch { }
        }
    }
}
